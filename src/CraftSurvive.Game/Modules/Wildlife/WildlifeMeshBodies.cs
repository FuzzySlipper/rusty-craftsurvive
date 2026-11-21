using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// The animated bodies of kinds that have a mesh (#10569). It holds each animal's Engine handles and
/// what it last told them, and decides nothing: <see cref="WildlifeClips"/> says what to show.
///
/// Clips run on the Engine's direct playback, not a graph: one call when the clip changes and none
/// otherwise, with its loop mode, rate and fade. A graph would need about twenty product ticks an
/// animal a second, and it loops one-shot clips (#10568).
///
/// An instance is disposed before the snapshot that drops its fact, and its appearance after that
/// snapshot, as the Engine requires.
/// </summary>
internal sealed class WildlifeMeshBodies : IDisposable
{
    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly WorldFrame frame;
    private readonly WildlifeAnimation animation;
    /// <summary>The ground under a point near a height in a body's space (#10876): the overworld's, a cave's floor under a body in a cave, or a separate space's floor.</summary>
    private readonly Func<Creatures.CreatureSpace?, double, double, double, float> groundAt;

    /// <summary>The physics world the dead of a space fall in (#10876: each space its own).</summary>
    private readonly Func<Creatures.CreatureSpace?, WildlifeRagdolls> ragdollsIn;
    private readonly ulong seed;
    private readonly Dictionary<string, Opened> opened = [];
    private readonly OwnedBodies<Body> bodies = new();
    private readonly CyclingSlots slots = new(WildlifePresentation.MostMembers);
    private readonly List<Appearance> retiring = [];

    /// <summary>The shells and blades bodies wear and hold (#10956), each piece opened once.</summary>
    private readonly WildlifeWornPieces worn;

    /// <summary>What planting feet has cost since the last readout: calls, and the time in sampling the pose, casting for the ground and setting the pose.</summary>
    private (long Calls, double Sample, double Casts, double Pose) planting;

    /// <summary>How many bodies have been retired, for readouts.</summary>
    private long retired;

    /// <summary>The breath's sprite, the dust puff, opened when a body first breathes (#10882).</summary>
    private RenderResourceReference? breathSprite;

    /// <summary>Breaths let out and refused, and the last refusal, for readouts.</summary>
    private long breathsLet, breathsRefused;
    private string breathFailure = "none";

    /// <summary>Where the last breath left a mouth, the way it went, and the body's place and facing, for readouts.</summary>
    private string lastBreath = "none";

    /// <summary>Separates one body's breaths' seeds from another's.</summary>
    private const ulong BreathSalt = 0x9E37_79B9_7F4A_7C15UL;

    /// <summary>The body's one material slot, and how rough its paint is, as the mesh's own material has it.</summary>
    private const uint BodySlot = 0;
    private const float PaintRoughness = 0.9f;

    private static readonly Color White = new(1, 1, 1, 1);

    private readonly Dictionary<string, (RenderResource Texture, Material Material)> paints = [];

    private sealed record Opened(RenderResource Mesh, WildlifeRig Rig, (uint Root, uint Mid, uint End)[] Legs, string[] Joints, WildlifeRagdollRig? Ragdoll)
    {
        internal uint Joint(string name)
        {
            int index = Array.IndexOf(Joints, name);
            return index >= 0 ? (uint)index : throw new InvalidDataException($"the mesh has no joint '{name}'");
        }
    }

    private sealed class Body(Opened kind, int slot, ulong objectId, Appearance appearance, AnimationInstance instance)
    {
        internal Opened Kind { get; } = kind;
        internal int Slot { get; } = slot;
        internal ulong ObjectId { get; } = objectId;
        internal Appearance Appearance { get; } = appearance;
        internal AnimationInstance Instance { get; } = instance;
        internal WildlifeTier Tier { get; set; }
        internal WildlifeClipRole? Playing { get; set; }
        internal float Rate { get; set; }
        internal bool Paused { get; set; }
        internal bool Published { get; set; }

        /// <summary>What every pose of this body carries: a young animal's larger head.</summary>
        internal JointOverride[] Overrides { get; init; } = [];
        internal bool Planted { get; set; }
        internal Vector3 PlantedAt { get; set; }
        internal Quaternion PlantedTurn { get; set; }
        internal WildlifeClipRole? PlantedClip { get; set; }
        internal float Height { get; set; }
        internal float Pitch { get; set; }
        internal bool Sitting { get; set; }
        internal int SinceSitting { get; set; }

        /// <summary>Its body in the physics world, once it has fallen as one.</summary>
        internal DynamicsRagdoll? Ragdoll { get; set; }

        /// <summary>The space it stands or lies in (#10876); null for the walking session's frame.</summary>
        internal Creatures.CreatureSpace? Space { get; set; }

        /// <summary>Its death has begun, as a ragdoll or a clip: a body that could not fall at once plays its clip and stays with it.</summary>
        internal bool Fell { get; set; }

        /// <summary>How far through its clip it is, as a share of the clip's length: when to breathe (#10882).</summary>
        internal float ClipShare { get; set; }

        /// <summary>How many breaths it has let out, to seed each one apart.</summary>
        internal ulong Breaths { get; set; }

        /// <summary>The look it wears (#10956) and the pieces that make it up, each hung from a joint; none for a kind that wears nothing.</summary>
        internal string? WornLook { get; set; }

        internal WildlifeWornPieces.Part[] Worn { get; set; } = [];

        /// <summary>
        /// Whether its pieces hang from their joints: they are hung before the first snapshot that draws the body, and again
        /// after a snapshot that left it out (the map), which let go of them and their bindings.
        /// </summary>
        internal bool Hung { get; set; }

        /// <summary>The body's and its pieces' facts from its last pose, for hanging them before a snapshot.</summary>
        internal AppearanceFact[] WornFacts { get; set; } = [];
    }

    internal WildlifeMeshBodies(IEngineContext engine, ProductContent content, WorldFrame frame, WildlifeAnimation animation, Func<Creatures.CreatureSpace?, double, double, double, float> groundAt, ulong seed, Func<Creatures.CreatureSpace?, WildlifeRagdolls> ragdollsIn)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.animation = animation;
        this.groundAt = groundAt ?? throw new ArgumentNullException(nameof(groundAt));
        this.seed = seed;
        this.ragdollsIn = ragdollsIn ?? throw new ArgumentNullException(nameof(ragdollsIn));
        worn = new WildlifeWornPieces(engine, content);
    }

    internal WildlifeAnimation Animation => animation;

    /// <summary>The fact that draws an animal this update.</summary>
    /// <param name="fallen">It is dead: it falls as a ragdoll or plays its death, and stays where it fell.</param>
    /// <param name="blow">The way the killing blow drove it.</param>
    /// <param name="plantWithin">How near it must be for its feet to be planted.</param>
    /// <param name="space">The space it is in (#10876): its position and feet are that space's world metres; null for the walking session.</param>
    /// <param name="into">Where the facts of what it wears and holds go (#10956), beside the one returned for its body.</param>
    internal AppearanceFact Pose(WildlifeHerd? herd, WildlifeKind kind, WildlifeMember member, float feet, double distance, double seconds, double plantWithin, bool fallen, Vector2 blow = default,
        Creatures.CreatureSpace? space = null, ICollection<AppearanceFact>? into = null)
    {
        Body body = bodies.Take(member, () => Create(kind, member), Retire);
        body.Space = space;
        body.Tier = WildlifeClips.Tier(distance, body.Tier, animation);
        if (fallen && !body.Fell) Fall(body, kind, member, blow);
        if (body.Ragdoll is not null)
        {
            Follow(body, member);
        }
        else
        {
            Play(body, herd, kind, member, seconds, fallen);
            Sit(body, kind, member, feet, seconds);
        }

        float scale = kind.Mesh!.Scale * WildlifeClips.Scale(member, kind);
        // The mesh faces +Z; yaw 0 faces north (-Z) and a quarter turn east (+X). A rising front turns the nose up: a negative turn about the mesh's own X.
        // Hanging or falling (#10877) it is drawn over its feet, level, whatever the ground under it does.
        Quaternion lift = Quaternion.CreateFromAxisAngle(Vector3.UnitX, member.Lift > 0 ? 0f : -body.Pitch);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI - member.Yaw);
        Vector3 world = new(member.Position.X, body.Height + member.Lift, member.Position.Y);
        Transform placement = new(ToLocal(body, world), Quaternion.Concatenate(lift, turn), new Vector3(scale));
        Plant(body, placement, world, scale, plant: !fallen && distance <= plantWithin, frozen: !fallen && Frozen(member));
        if (!fallen) Breathe(body, kind, member, placement, scale, seconds);
        AppearanceFact fact = new(body.ObjectId, false, 0, placement, body.Appearance, Visible: body.Tier != WildlifeTier.Hidden, RenderLayer.Scene, ShadowCasting.Cast);
        Wear(body, fact, into);
        return fact;
    }

    /// <summary>
    /// The facts of what a body wears and holds (#10956): each piece a child of the body in its joint's frame. They join the
    /// snapshot as plain children; <see cref="BeforeSnapshot"/> hangs them from their joints, which later snapshots keep.
    /// </summary>
    private void Wear(Body body, AppearanceFact fact, ICollection<AppearanceFact>? into)
    {
        if (body.Worn.Length == 0) return;
        AppearanceFact[] pieces = [.. body.Worn.Select(part => new AppearanceFact(part.ObjectId, true, body.ObjectId, PieceFrame, part.Look, fact.Visible, RenderLayer.Scene, ShadowCasting.Cast))];
        body.WornFacts = [fact, .. pieces];
        foreach (AppearanceFact piece in pieces) into?.Add(piece);
    }

    /// <summary>
    /// Before a snapshot that draws the bodies (#10956 R10956-1): hangs the pieces of every body whose pieces are not hanging
    /// from their joints, a body new since the last snapshot or one a snapshot left out (the map) and so let go of with its
    /// pieces and their joint bindings. Each is published with its body in one change, as the Engine wants a parent there,
    /// from the facts its last pose made: the same facts the snapshot then carries, which keeps each piece where it hangs.
    /// A snapshot that leaves the bodies out hangs nothing.
    /// </summary>
    internal void BeforeSnapshot(bool drawn)
    {
        if (!drawn) return;
        foreach (Body body in bodies.All)
        {
            if (body.Hung || body.WornFacts.Length == 0) continue;
            engine.Graphics.PublishChanges(new(body.WornFacts, ReadOnlyMemory<ulong>.Empty, body.Worn.Select(part => new MeshJointAttachment(part.ObjectId, part.Joint)).ToArray()));
            body.Hung = true;
        }
    }

    /// <summary>A piece sits in its joint's own frame as make_shells.py wrote it.</summary>
    private static readonly Transform PieceFrame = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Lets a body fall if it has a ragdoll and a pose to fall from, holding its clip so that what the ragdoll leaves to the clips stays still.</summary>
    private void Fall(Body body, WildlifeKind kind, WildlifeMember member, Vector2 blow)
    {
        body.Fell = true;
        if (body.Kind.Ragdoll is not { } rig || !body.Published || !engine.Animation.ReadJointPose(body.Instance).Reported) return;
        float scale = kind.Mesh!.Scale * WildlifeClips.Scale(member, kind);
        body.Ragdoll = ragdollsIn(body.Space).Fall(body.Instance, rig, body.Kind.Joint, scale, blow);
        if (body.Playing is not null && !body.Paused)
            engine.Animation.SetPlayback(new(body.Instance, AnimationPlaybackKind.Pause, "", AnimationLoopMode.Once, 0, 0, false, 0, false, 0));
        body.Paused = true;
    }

    /// <summary>
    /// A fallen body's fact and record follow its hips, which is where the animal now is, for what
    /// bounds or distance it. Rest is not the end of it: a change in the ground under a body wakes it.
    /// </summary>
    private void Follow(Body body, WildlifeMember member)
    {
        Vector3 hips = ToWorld(body, engine.Dynamics.ReadRagdoll(body.Ragdoll!).Bones.Span[0].Translation);
        member.Position = new Vector2(hips.X, hips.Z);
    }

    /// <summary>Whether a test aid has frozen the animal (#10926): its playback paused, its seat and feet held.</summary>
    private static bool Frozen(WildlifeMember member) => member.Hold is { Frozen: true };

    /// <summary>Starts, changes, holds or resumes the clip the animal's state calls for.</summary>
    /// <param name="herd">Its herd, whose state picks the clip; null for a hostile animal, whose clip the creature rules have set.</param>
    private void Play(Body body, WildlifeHerd? herd, WildlifeKind kind, WildlifeMember member, double seconds, bool fallen)
    {
        WildlifeRig rig = body.Kind.Rig;
        // A test aid's freeze (#10926) pauses the playback as the held tier does, and resumes it on release; death ends it.
        bool held = body.Tier >= WildlifeTier.Held || (Frozen(member) && !fallen);
        if (held != body.Paused && body.Playing is not null)
        {
            body.Paused = held;
            engine.Animation.SetPlayback(new(body.Instance, held ? AnimationPlaybackKind.Pause : AnimationPlaybackKind.Resume, "", AnimationLoopMode.Once, 0, 0, false, 0, false, 0));
        }

        if (fallen)
        {
            member.Clip = WildlifeClipRole.Die;
            if (body.Playing != WildlifeClipRole.Die) Start(body, member, WildlifeClipRole.Die, 1, restart: true);
            return;
        }

        if (herd is not null) WildlifeClips.Advance(member, herd, rig, animation, seconds);
        float rate = WildlifeClips.Rate(member.Clip, member.Velocity.Length(), rig, animation, WildlifeClips.Scale(member, kind));
        if (body.Playing == member.Clip && Math.Abs(rate - body.Rate) < animation.RateStep) return;
        if (held && body.Playing is not null) return;
        Start(body, member, member.Clip, rate, restart: body.Playing != member.Clip);
    }

    private void Start(Body body, WildlifeMember member, WildlifeClipRole role, float rate, bool restart)
    {
        CreatureClip clip = body.Kind.Rig.Clip(role);
        // The stand role is one swipe, played over for as long as the animal is pressed.
        AnimationLoopMode loop = clip.Loops || role == WildlifeClipRole.Stand ? AnimationLoopMode.Repeat : AnimationLoopMode.Once;
        // A herd must not breathe in step: a looping clip starts at the animal's own point in it.
        float start = restart && clip.Loops ? WildlifeClips.Phase(member, seed) : 0;
        engine.Animation.SetPlayback(new(body.Instance, AnimationPlaybackKind.Play, clip.Name, loop, rate, 1, restart, (float)animation.FadeSeconds, true, start));
        if (restart) body.ClipShare = clip.Seconds > 0 ? (float)(start / clip.Seconds) : 0f;
        body.Playing = role;
        body.Rate = rate;
        body.Paused = false;
    }

    /// <summary>
    /// Lets out the kind's breath (#10882) where its clip crosses one of the shares the breath lists: a burst of haze from
    /// between the jaws as posed now, the way they point. Only a body drawn whole and playing breathes.
    /// </summary>
    private void Breathe(Body body, WildlifeKind kind, WildlifeMember member, Transform placement, float scale, double seconds)
    {
        if (kind.Mesh!.Breath is not WildlifeBreath breath || body.Playing is not WildlifeClipRole playing || body.Paused || !body.Published) return;
        CreatureClip clip = body.Kind.Rig.Clip(playing);
        if (clip.Seconds <= 0 || seconds <= 0) return;
        float from = body.ClipShare;
        float to = from + (float)(seconds * body.Rate / clip.Seconds);
        body.ClipShare = clip.Loops ? to % 1f : Math.Min(to, 1f);
        int count = breath.Crossed(playing, from, Math.Min(to, 1f)) + (clip.Loops && to > 1f ? breath.Crossed(playing, 0f, to - 1f) : 0);
        if (count == 0 || body.Tier == WildlifeTier.Hidden) return;
        ReadOnlySpan<JointPose> pose = engine.Animation.SampleJointPose(AnimationPoseSampleRequest.OfCurrentPlayback(body.Instance, placement, applyPoseControls: false)).Span;
        (Vector3 mouth, Vector3 forward) = WildlifeBreathLook.Mouth(breath, pose[(int)body.Kind.Joint(breath.UpperJoint)], pose[(int)body.Kind.Joint(breath.LowerJoint)], scale);
        breathSprite ??= OpenBreathSprite();
        lastBreath = string.Create(CultureInfo.InvariantCulture,
            $"mouth={mouth.X:F2},{mouth.Y:F2},{mouth.Z:F2} forward={forward.X:F2},{forward.Y:F2},{forward.Z:F2} at={placement.Translation.X:F2},{placement.Translation.Y:F2},{placement.Translation.Z:F2} facing={Vector3.Transform(Vector3.UnitZ, placement.Rotation).X:F2},{Vector3.Transform(Vector3.UnitZ, placement.Rotation).Z:F2}");
        for (int i = 0; i < count; i++)
        {
            ulong seed = unchecked(((ulong)body.ObjectId * BreathSalt) ^ ++body.Breaths);
            try
            {
                engine.Presentation.EmitParticles(WildlifeBreathLook.Describe(breath, mouth, forward, breathSprite.Value, seed, scale));
                breathsLet++;
            }
            catch (EngineCallException exception)
            {
                breathsRefused++;
                breathFailure = exception.Message;
            }
        }
    }

    private RenderResourceReference OpenBreathSprite()
    {
        RenderResourceInfo resource = engine.Graphics.OpenResource(new RenderResourceRequest(Feedback.Bursts.PuffContentPath));
        if (resource.Kind != RenderResourceKind.Texture || resource.ByteLength == 0)
            throw new InvalidOperationException($"the breath's sprite '{Feedback.Bursts.PuffContentPath}' must be a non-empty Engine texture.");
        return resource.Handle;
    }

    /// <summary>Sits the body on the ground under its hind and fore feet: every update near, now and then further out, not at all once its pose is held.</summary>
    private void Sit(Body body, WildlifeKind kind, WildlifeMember member, float feet, double seconds)
    {
        // A frozen body (#10926) keeps the height and pitch it had.
        if (Frozen(member) && body.Sitting) return;
        float wantedHeight = feet, wantedPitch = 0;
        bool casts = body.Tier == WildlifeTier.Full || (body.Tier == WildlifeTier.Play && ++body.SinceSitting >= animation.PitchEveryUpdates) || !body.Sitting;
        if (body.Tier >= WildlifeTier.Held && body.Sitting) casts = false;
        if (casts)
        {
            body.SinceSitting = 0;
            Vector2 facing = WildlifeRules.Facing(member.Yaw);
            float scale = kind.Mesh!.Scale * WildlifeClips.Scale(member, kind);
            double hind = body.Kind.Rig.Rig.HindMetres * scale, fore = body.Kind.Rig.Rig.ForeMetres * scale;
            float hindGround = groundAt(body.Space, member.Position.X + (facing.X * hind), member.Position.Y + (facing.Y * hind), feet);
            float foreGround = groundAt(body.Space, member.Position.X + (facing.X * fore), member.Position.Y + (facing.Y * fore), feet);
            (wantedHeight, wantedPitch) = WildlifeClips.Stance(feet, hindGround, foreGround, hind, fore, animation);
            float most = (float)(animation.PitchDegreesPerSecond * Math.PI / 180 * seconds);
            body.Pitch = body.Sitting ? body.Pitch + Math.Clamp(wantedPitch - body.Pitch, -most, most) : wantedPitch;
            body.Sitting = true;
            body.Height = wantedHeight;
            return;
        }

        // Between casts the body rides its feet.
        body.Height = feet;
    }

    /// <summary>Plants the feet near the player, while the clip keeps them on the ground; otherwise the clip's own pose stands.</summary>
    private void Plant(Body body, Transform placement, Vector3 world, float scale, bool plant, bool frozen)
    {
        // A frozen body (#10926) keeps its feet as they were planted, or not.
        if (frozen) return;
        CreatureClip clip = body.Playing is WildlifeClipRole role ? body.Kind.Rig.Clip(role) : body.Kind.Rig.Clip(WildlifeClipRole.Idle);
        if (!plant || body.Tier != WildlifeTier.Full || !clip.PlantsFeet)
        {
            if (body.Planted)
            {
                engine.Animation.SetPose(new(body.Instance, ReadOnlyMemory<TwoBoneIk>.Empty, body.Overrides));
                body.Planted = false;
            }

            return;
        }

        if (!body.Published) return;
        // Feet planted on the ground stay planted while the body stands as it stood in the same clip.
        if (body.Planted && body.PlantedClip == body.Playing
            && WildlifeClips.FeetHold(body.PlantedAt, body.PlantedTurn, placement.Translation, placement.Rotation, animation)) return;
        long began = Stopwatch.GetTimestamp();
        ReadOnlySpan<JointPose> pose = engine.Animation.SampleJointPose(
            AnimationPoseSampleRequest.OfCurrentPlayback(body.Instance, placement, applyPoseControls: false)).Span;
        long sampled = Stopwatch.GetTimestamp();
        (TwoBoneIk[] chains, _) = CreatureFeet.Chains(pose, body.Kind.Legs, scale, world, placement.Translation, (x, z) => groundAt(body.Space, x, z, world.Y), (float)animation.MostFootMetres);
        long cast = Stopwatch.GetTimestamp();
        engine.Animation.SetPose(new(body.Instance, chains, body.Overrides));
        long posed = Stopwatch.GetTimestamp();
        planting = (planting.Calls + 1, planting.Sample + Stopwatch.GetElapsedTime(began, sampled).TotalMilliseconds,
            planting.Casts + Stopwatch.GetElapsedTime(sampled, cast).TotalMilliseconds, planting.Pose + Stopwatch.GetElapsedTime(cast, posed).TotalMilliseconds);
        body.Planted = true;
        body.PlantedAt = placement.Translation;
        body.PlantedTurn = placement.Rotation;
        body.PlantedClip = body.Playing;
    }

    private Body Create(WildlifeKind kind, WildlifeMember member)
    {
        Opened open = Open(kind);
        // A placeholder's parts have the first numbers of each four: a mesh is a different kind of Engine node, so it never shares one.
        int slot = slots.Take();
        ulong objectId = ProductIds.WildlifeAppearanceBase + ((ulong)slot * WildlifePresentation.PartsPerMember) + WildlifePresentation.MeshPart;
        Appearance appearance = engine.Animation.CreateAnimatedMeshAppearance(new(open.Mesh));
        if (!member.Young)
        {
            Tint(appearance, member);
            return Dressed(new Body(open, slot, objectId, appearance, engine.Animation.CreateInstance(new(appearance, objectId))), kind, member);
        }

        // The young share the adult's mesh and every clip; only the paint and the head's size are theirs.
        WildlifeYoung young = kind.Young!;
        engine.Animation.UpdateAnimatedMeshMaterials(new(appearance, new[] { new MeshMaterialBinding(BodySlot, YoungPaint(kind.Id, young)) }));
        Tint(appearance, member);
        AnimationInstance instance = engine.Animation.CreateInstance(new(appearance, objectId));
        JointOverride[] overrides = [JointOverride.LocalScale(open.Joint(young.HeadJoint), young.HeadScale, 1)];
        engine.Animation.SetPose(new(instance, ReadOnlyMemory<TwoBoneIk>.Empty, overrides));
        return Dressed(new Body(open, slot, objectId, appearance, instance) { Overrides = overrides }, kind, member);
    }

    /// <summary>Gives a body of a kind that wears pieces (#10956) its look and the pieces of it.</summary>
    private Body Dressed(Body body, WildlifeKind kind, WildlifeMember member)
    {
        if (kind.Mesh!.Worn is not WildlifeWorn wears) return body;
        (body.WornLook, body.Worn) = worn.For(wears, member.Id, seed, body.Slot);
        return body;
    }

    /// <summary>A test aid (#10956): bodies made from now wear this look (any: each its own); says which looks the kinds have.</summary>
    internal string ForceLook(string look, IEnumerable<WildlifeKind> kinds)
    {
        worn.Forced = look is "any" or "" ? null : look;
        string[] offered = [.. kinds.Where(kind => kind.Mesh?.Worn is not null).SelectMany(kind => worn.Looks(kind.Mesh!.Worn!)).Distinct()];
        return $"look={worn.Forced ?? "any"} (bodies made from now); looks: {(offered.Length == 0 ? "none" : string.Join(",", offered))}";
    }

    /// <summary>Gives an appearance its animal's own coat, over the paint it shares with the rest of its kind.</summary>
    private void Tint(Appearance appearance, WildlifeMember member)
    {
        Color coat = new(member.Coat.X, member.Coat.Y, member.Coat.Z, 1);
        engine.Animation.UpdateAnimatedMeshMaterialFactors(new(appearance, new[]
        {
            new MeshMaterialFactors(BodySlot, false, default, false, Vector3.Zero, 0, true, coat),
        }));
    }

    /// <summary>A kind's paint for its young, opened once and shared by all of them.</summary>
    private Material YoungPaint(string kindId, WildlifeYoung young)
    {
        if (paints.TryGetValue(kindId, out (RenderResource, Material Material) paint)) return paint.Material;
        RenderResource texture = engine.Graphics.OpenResource(new(young.Texture, TextureFilter.Nearest, TextureWrap.Clamp)).Handle;
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(White, texture, PaintRoughness, White, Vector3.Zero, 0, false));
        paints[kindId] = (texture, material);
        return material;
    }

    /// <summary>Opens a kind's mesh and paint now, with the world, rather than as a hitch when its first herd wakes.</summary>
    internal void Prepare(WildlifeKind kind)
    {
        Open(kind);
        if (kind.Mesh?.Worn is { } wears) worn.Prepare(wears);
        if (kind.Young is { } young) YoungPaint(kind.Id, young);
    }

    /// <summary>Opens a small cave creature's mesh now, with the world (#10884).</summary>
    internal void Prepare(CaveAmbientKind kind) => Open(AmbientId(kind), kind.Mesh!);

    /// <summary>A small cave creature's kind shares the opened meshes' table with the hunters' and herds', under its own name.</summary>
    private static string AmbientId(CaveAmbientKind kind) => "ambient:" + kind.Id;

    /// <summary>
    /// The fact that draws a small cave creature with a mesh (#10884). It has no collider, no ragdoll and no planted feet:
    /// it stands, wanders or runs off by its speed, at the rate that keeps its gait on the ground, and casts no shadow.
    /// </summary>
    internal AppearanceFact PoseAmbient(CaveAmbientKind kind, WildlifeMember member, float feet, double distance, Creatures.CreatureSpace? space = null)
    {
        WildlifeMesh mesh = kind.Mesh!;
        Body body = bodies.Take(member, () => CreateAmbient(kind, member), Retire);
        body.Space = space;
        body.Tier = WildlifeClips.Tier(distance, body.Tier, animation);
        float speed = member.Velocity.Length();
        WildlifeClipRole role = speed >= AmbientRunShare * kind.FleeSpeed ? WildlifeClipRole.Run : speed > AmbientStillSpeed ? WildlifeClipRole.Walk : WildlifeClipRole.Idle;
        float scale = mesh.Scale * member.Size;
        float rate = WildlifeClips.Rate(role, speed, body.Kind.Rig, animation, scale);
        bool held = body.Tier >= WildlifeTier.Held;
        if (held != body.Paused && body.Playing is not null)
        {
            body.Paused = held;
            engine.Animation.SetPlayback(new(body.Instance, held ? AnimationPlaybackKind.Pause : AnimationPlaybackKind.Resume, "", AnimationLoopMode.Once, 0, 0, false, 0, false, 0));
        }

        if (!held && (body.Playing != role || Math.Abs(rate - body.Rate) >= animation.RateStep)) Start(body, member, role, rate, restart: body.Playing != role);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI - member.Yaw);
        Vector3 world = new(member.Position.X, feet, member.Position.Y);
        Transform placement = new(ToLocal(body, world), turn, new Vector3(scale));
        return new AppearanceFact(body.ObjectId, false, 0, placement, body.Appearance, Visible: body.Tier != WildlifeTier.Hidden, RenderLayer.Scene, ShadowCasting.None);
    }

    /// <summary>A small creature moving at least this share of its running speed is running.</summary>
    private const float AmbientRunShare = 0.6f;

    /// <summary>A small creature slower than this, metres a second, is standing.</summary>
    private const float AmbientStillSpeed = 0.03f;

    private Body CreateAmbient(CaveAmbientKind kind, WildlifeMember member)
    {
        Opened open = Open(AmbientId(kind), kind.Mesh!);
        int slot = slots.Take();
        ulong objectId = ProductIds.WildlifeAppearanceBase + ((ulong)slot * WildlifePresentation.PartsPerMember) + WildlifePresentation.MeshPart;
        Appearance appearance = engine.Animation.CreateAnimatedMeshAppearance(new(open.Mesh));
        return new Body(open, slot, objectId, appearance, engine.Animation.CreateInstance(new(appearance, objectId)));
    }

    /// <summary>A kind's clips, opening it if it is not open yet.</summary>
    internal WildlifeRig RigOf(WildlifeKind kind) => Open(kind).Rig;

    private Opened Open(WildlifeKind kind) => Open(kind.Id, kind.Mesh!);

    private Opened Open(string id, WildlifeMesh mesh)
    {
        if (opened.TryGetValue(id, out Opened? open)) return open;
        RenderResource resource;
        using (ContentReference reference = engine.Content.OpenReference(new(mesh.File)))
            resource = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
        WildlifeRig rig = WildlifeRig.For(mesh, CreatureManifest.Parse(content.ReadText(mesh.Manifest)));
        string[] joints = [.. engine.Animation.ReadJoints(resource).ToArray().Select(joint => joint.Id)];
        (uint, uint, uint)[] legs = [.. rig.Rig.Legs.Select(leg =>
            ((uint)Array.IndexOf(joints, leg.Root), (uint)Array.IndexOf(joints, leg.Mid), (uint)Array.IndexOf(joints, leg.End)))];
        WildlifeRagdollRig? ragdoll = mesh.Ragdoll is { } path ? WildlifeRagdollRig.Parse(content.ReadText(path)) : null;
        return opened[id] = new Opened(resource, rig, legs, joints, ragdoll);
    }

    /// <summary>A point of a body's space's world metres in that space's local frame.</summary>
    private Vector3 ToLocal(Body body, Vector3 world) => body.Space?.ToLocal(world) ?? frame.ToLocal(world);

    private Vector3 ToWorld(Body body, Vector3 local) => body.Space?.ToWorld(local) ?? frame.ToWorld(local);

    /// <summary>Retires every body not owned by these animals. Call before the snapshot built without them.</summary>
    internal void Keep(HashSet<WildlifeMember> owners) => bodies.Keep(owners, Retire);

    /// <summary>The instance goes now, before the snapshot that drops its fact; its appearance after that snapshot.</summary>
    private void Retire(Body body)
    {
        if (body.Ragdoll is { } ragdoll) ragdollsIn(body.Space).Release(ragdoll);
        body.Instance.Dispose();
        retiring.Add(body.Appearance);
        slots.Release(body.Slot);
        retired++;
    }

    /// <summary>
    /// After a snapshot the retired bodies' appearances can go. Bodies it drew can be sampled; one that
    /// left them out (the map) means they cannot be until they are drawn again.
    /// </summary>
    internal void AfterSnapshot(bool drawn)
    {
        foreach (Body body in bodies.All)
        {
            body.Published = drawn;
            // A snapshot that left the body out let go of its pieces' joint bindings with them (R10956-1).
            if (!drawn) body.Hung = false;
        }

        foreach (Appearance appearance in retiring) appearance.Dispose();
        retiring.Clear();
    }

    /// <summary>How the bodies stand, and what planting has cost since the last readout (which it resets).</summary>
    /// <summary>What an animal's mesh plays now (#10926): the clip and its rate, its detail tier, whether its feet are planted, its pitch.</summary>
    internal string BodyReadout(WildlifeMember member) => bodies.Find(member) is Body body
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"playing={body.Playing?.ToString() ?? "none"} look={body.WornLook ?? "-"} paused={body.Paused} rate={body.Rate:F2} tier={body.Tier} planted={body.Planted} pitch={body.Pitch * 180 / MathF.PI:F1} fell={body.Fell} {PoseReadout(body)}")
        : "no mesh body";

    /// <summary>
    /// A signature of the pose its playback gives now (#10926), from the joints' model-space rotations: two reads alike mean
    /// the clip has not moved on between them, as while the body is frozen; reads that differ mean it plays.
    /// </summary>
    private string PoseReadout(Body body)
    {
        if (!body.Published) return "pose=unpublished";
        ReadOnlySpan<JointPose> pose = engine.Animation.SampleJointPose(
            AnimationPoseSampleRequest.OfCurrentPlayback(body.Instance, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), applyPoseControls: false)).Span;
        double signature = 0;
        for (int i = 0; i < pose.Length; i++)
        {
            Quaternion turn = pose[i].Model.Rotation;
            signature += (i + 1) * ((turn.X * PoseWeightX) + (turn.Y * PoseWeightY) + (turn.Z * PoseWeightZ));
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"pose={signature:F5} joints={pose.Length}");
    }

    /// <summary>Weights that keep a pose signature from cancelling out across a joint's axes.</summary>
    private const double PoseWeightX = 1, PoseWeightY = 3, PoseWeightZ = 7;

    internal string Readout()
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"mostPitch={bodies.All.Select(b => Math.Abs(b.Pitch) * 180 / MathF.PI).DefaultIfEmpty().Max():F1} meshes={bodies.Count} full={bodies.All.Count(b => b.Tier == WildlifeTier.Full)} play={bodies.All.Count(b => b.Tier == WildlifeTier.Play)} held={bodies.All.Count(b => b.Tier == WildlifeTier.Held)} hidden={bodies.All.Count(b => b.Tier == WildlifeTier.Hidden)} planted={bodies.All.Count(b => b.Planted)} steepest={bodies.All.Select(b => Math.Abs(b.Pitch)).DefaultIfEmpty(0).Max() * 180 / MathF.PI:F1} retired={retired} plants={planting.Calls} sampleMs={planting.Sample:F1} castMs={planting.Casts:F1} poseMs={planting.Pose:F1} breaths={breathsLet} breathsRefused={breathsRefused} breathFailure={breathFailure} lastBreath=[{lastBreath}]");
        planting = default;
        return text + Lying();
    }

    /// <summary>How the ragdolls lie: at rest or not, and how far the deepest bone's surface has gone below the ground.</summary>
    private string Lying()
    {
        int resting = 0;
        float deepest = float.NegativeInfinity;
        Vector3 hips = default;
        foreach (Body body in bodies.All.Where(body => body.Ragdoll is not null))
        {
            DynamicsRagdollResult read = engine.Dynamics.ReadRagdoll(body.Ragdoll!);
            if (read.Resting) resting++;
            ReadOnlySpan<Transform> bones = read.Bones.Span;
            hips = ToWorld(body, bones[0].Translation);
            for (int index = 0; index < bones.Length; index++)
            {
                Vector3 at = ToWorld(body, bones[index].Translation);
                deepest = MathF.Max(deepest, groundAt(body.Space, at.X, at.Z, at.Y) - (at.Y - body.Kind.Ragdoll!.Bones[index].Radius));
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $" resting={resting} deepest={(float.IsInfinity(deepest) ? 0 : deepest):F2} hips={hips.X:F1},{hips.Y:F1},{hips.Z:F1}");
    }

    public void Dispose()
    {
        Keep([]);
        AfterSnapshot(drawn: false);
        foreach (Opened open in opened.Values) open.Mesh.Dispose();
        opened.Clear();
        foreach ((RenderResource texture, Material material) in paints.Values)
        {
            material.Dispose();
            texture.Dispose();
        }

        paints.Clear();
        worn.Dispose();
    }
}
