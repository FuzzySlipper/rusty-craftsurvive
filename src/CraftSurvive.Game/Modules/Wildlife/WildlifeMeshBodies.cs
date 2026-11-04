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
    private readonly Func<double, double, float> groundAt;
    private readonly ulong seed;
    private readonly Dictionary<string, Opened> opened = [];
    private readonly OwnedBodies<Body> bodies = new();
    private readonly CyclingSlots slots = new(WildlifePresentation.MostMembers);
    private readonly List<Appearance> retiring = [];

    /// <summary>What planting feet has cost since the last readout: calls, and the time in sampling the pose, casting for the ground and setting the pose.</summary>
    private (long Calls, double Sample, double Casts, double Pose) planting;

    /// <summary>How many bodies have been retired, for readouts.</summary>
    private long retired;

    /// <summary>The body's one material slot, and how rough its paint is, as the mesh's own material has it.</summary>
    private const uint BodySlot = 0;
    private const float PaintRoughness = 0.9f;

    private static readonly Color White = new(1, 1, 1, 1);

    private readonly Dictionary<string, (RenderResource Texture, Material Material)> paints = [];

    private sealed record Opened(RenderResource Mesh, WildlifeRig Rig, (uint Root, uint Mid, uint End)[] Legs, string[] Joints)
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
    }

    internal WildlifeMeshBodies(IEngineContext engine, ProductContent content, WorldFrame frame, WildlifeAnimation animation, Func<double, double, float> groundAt, ulong seed)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.animation = animation;
        this.groundAt = groundAt ?? throw new ArgumentNullException(nameof(groundAt));
        this.seed = seed;
    }

    internal WildlifeAnimation Animation => animation;

    /// <summary>The fact that draws an animal this update.</summary>
    /// <param name="fallen">It is dead: it plays its death once and stays where it fell.</param>
    /// <param name="plantWithin">How near it must be for its feet to be planted.</param>
    internal AppearanceFact Pose(WildlifeHerd? herd, WildlifeKind kind, WildlifeMember member, float feet, double distance, double seconds, double plantWithin, bool fallen)
    {
        Body body = bodies.Take(member, () => Create(kind, member), Retire);
        body.Tier = WildlifeClips.Tier(distance, body.Tier, animation);
        Play(body, herd, member, seconds, fallen);
        Sit(body, kind, member, feet, seconds);
        float scale = kind.Mesh!.Scale * WildlifeClips.Scale(member, kind);
        // The mesh faces +Z; yaw 0 faces north (-Z) and a quarter turn east (+X). A rising front turns the nose up: a negative turn about the mesh's own X.
        Quaternion lift = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -body.Pitch);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI - member.Yaw);
        Vector3 world = new(member.Position.X, body.Height, member.Position.Y);
        Transform placement = new(frame.ToLocal(world.X, world.Y, world.Z), Quaternion.Concatenate(lift, turn), new Vector3(scale));
        Plant(body, placement, world, scale, plant: !fallen && distance <= plantWithin);
        return new AppearanceFact(body.ObjectId, false, 0, placement, body.Appearance, Visible: body.Tier != WildlifeTier.Hidden, RenderLayer.Scene, ShadowCasting.Cast);
    }

    /// <summary>Starts, changes, holds or resumes the clip the animal's state calls for.</summary>
    private void Play(Body body, WildlifeHerd? herd, WildlifeMember member, double seconds, bool fallen)
    {
        WildlifeRig rig = body.Kind.Rig;
        bool held = body.Tier >= WildlifeTier.Held;
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

        if (herd is null) return;
        WildlifeClips.Advance(member, herd, rig, animation, seconds);
        float rate = WildlifeClips.Rate(member.Clip, member.Velocity.Length(), rig, animation, WildlifeClips.Scale(member, herd.Kind));
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
        body.Playing = role;
        body.Rate = rate;
        body.Paused = false;
    }

    /// <summary>Sits the body on the ground under its hind and fore feet: every update near, now and then further out, not at all once its pose is held.</summary>
    private void Sit(Body body, WildlifeKind kind, WildlifeMember member, float feet, double seconds)
    {
        float wantedHeight = feet, wantedPitch = 0;
        bool casts = body.Tier == WildlifeTier.Full || (body.Tier == WildlifeTier.Play && ++body.SinceSitting >= animation.PitchEveryUpdates) || !body.Sitting;
        if (body.Tier >= WildlifeTier.Held && body.Sitting) casts = false;
        if (casts)
        {
            body.SinceSitting = 0;
            Vector2 facing = WildlifeRules.Facing(member.Yaw);
            float scale = kind.Mesh!.Scale * WildlifeClips.Scale(member, kind);
            double hind = body.Kind.Rig.Rig.HindMetres * scale, fore = body.Kind.Rig.Rig.ForeMetres * scale;
            float hindGround = groundAt(member.Position.X + (facing.X * hind), member.Position.Y + (facing.Y * hind));
            float foreGround = groundAt(member.Position.X + (facing.X * fore), member.Position.Y + (facing.Y * fore));
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
    private void Plant(Body body, Transform placement, Vector3 world, float scale, bool plant)
    {
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
        (TwoBoneIk[] chains, _) = CreatureFeet.Chains(pose, body.Kind.Legs, scale, world, placement.Translation, groundAt, (float)animation.MostFootMetres);
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
            return new Body(open, slot, objectId, appearance, engine.Animation.CreateInstance(new(appearance, objectId)));
        }

        // The young share the adult's mesh and every clip; only the paint and the head's size are theirs.
        WildlifeYoung young = kind.Young!;
        engine.Animation.UpdateAnimatedMeshMaterials(new(appearance, new[] { new MeshMaterialBinding(BodySlot, YoungPaint(kind.Id, young)) }));
        Tint(appearance, member);
        AnimationInstance instance = engine.Animation.CreateInstance(new(appearance, objectId));
        JointOverride[] overrides = [JointOverride.LocalScale(open.Joint(young.HeadJoint), young.HeadScale, 1)];
        engine.Animation.SetPose(new(instance, ReadOnlyMemory<TwoBoneIk>.Empty, overrides));
        return new Body(open, slot, objectId, appearance, instance) { Overrides = overrides };
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
        if (kind.Young is { } young) YoungPaint(kind.Id, young);
    }

    private Opened Open(WildlifeKind kind)
    {
        if (opened.TryGetValue(kind.Id, out Opened? open)) return open;
        WildlifeMesh mesh = kind.Mesh!;
        RenderResource resource;
        using (ContentReference reference = engine.Content.OpenReference(new(mesh.File)))
            resource = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
        WildlifeRig rig = WildlifeRig.For(mesh, CreatureManifest.Parse(content.ReadText(mesh.Manifest)));
        string[] joints = [.. engine.Animation.ReadJoints(resource).ToArray().Select(joint => joint.Id)];
        (uint, uint, uint)[] legs = [.. rig.Rig.Legs.Select(leg =>
            ((uint)Array.IndexOf(joints, leg.Root), (uint)Array.IndexOf(joints, leg.Mid), (uint)Array.IndexOf(joints, leg.End)))];
        return opened[kind.Id] = new Opened(resource, rig, legs, joints);
    }

    /// <summary>Retires every body not owned by these animals. Call before the snapshot built without them.</summary>
    internal void Keep(HashSet<WildlifeMember> owners) => bodies.Keep(owners, Retire);

    /// <summary>The instance goes now, before the snapshot that drops its fact; its appearance after that snapshot.</summary>
    private void Retire(Body body)
    {
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
        foreach (Body body in bodies.All) body.Published = drawn;
        foreach (Appearance appearance in retiring) appearance.Dispose();
        retiring.Clear();
    }

    /// <summary>How the bodies stand, and what planting has cost since the last readout (which it resets).</summary>
    internal string Readout()
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"mostPitch={bodies.All.Select(b => Math.Abs(b.Pitch) * 180 / MathF.PI).DefaultIfEmpty().Max():F1} meshes={bodies.Count} full={bodies.All.Count(b => b.Tier == WildlifeTier.Full)} play={bodies.All.Count(b => b.Tier == WildlifeTier.Play)} held={bodies.All.Count(b => b.Tier == WildlifeTier.Held)} hidden={bodies.All.Count(b => b.Tier == WildlifeTier.Hidden)} planted={bodies.All.Count(b => b.Planted)} steepest={bodies.All.Select(b => Math.Abs(b.Pitch)).DefaultIfEmpty(0).Max() * 180 / MathF.PI:F1} retired={retired} plants={planting.Calls} sampleMs={planting.Sample:F1} castMs={planting.Casts:F1} poseMs={planting.Pose:F1}");
        planting = default;
        return text;
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
    }
}
