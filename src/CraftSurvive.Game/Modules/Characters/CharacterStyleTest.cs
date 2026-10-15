using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Characters;

/// <summary>How a placed traveller moves: standing, walking a circle, or talking where it stands.</summary>
internal enum TravellerMode
{
    Idle,
    Walk,
    Talk,
}

/// <summary>
/// One kind of placeable character: its mesh, its own clip pack, its faces, its default scale and
/// its posture. The #10014 travellers are three kinds; the #10028 cast adds one per townsperson.
/// </summary>
internal sealed record CharacterKind(
    string Name,
    string Mesh,
    string Pack,
    Func<string, int, string> FacePath,
    int[] FaceSizes,
    int DefaultFaceSize,
    string? DenseBody,
    float Scale,
    float StoopDegrees);

/// <summary>
/// The character style test (#10014), a test aid rather than shipped content: one human traveller in
/// two proportion variants, animated with UAL's generic humanoid clips and given a texture-swap face.
/// Live-debug commands place travellers in the world for captures; nothing else creates them.
///
/// The clip pack is attached to each variant through the Engine's clip-pack association, and each
/// traveller plays through an Engine animation graph (idle, walk, talk) its controller selects. The face
/// is its own material slot: blinks, mouth movement while talking and a smile are whole small face
/// textures swapped in by replacing that slot's material.
/// </summary>
internal sealed class CharacterStyleTest : IDisposable
{
    /// <summary>
    /// Each variant's own copy of the trimmed UAL clip pack: on pair 0a5dfab7b318 a second mesh
    /// associated with the same pack bytes draws in its bind pose (rusty-engine #10018).
    /// </summary>
    private const string ClipPackPath = "characters/traveller/ual-clips-{0}.glb";
    /// <summary>The framework-check cast (#10028), written by art/characters/mpfb/build_cast.py.</summary>
    private const string CastManifest = "characters/cast/cast.json";
    private const int CastFaceSize = 128;
    private const string ClipPackProducer = "Quaternius Universal Animation Library (UAL1 Standard), trimmed by art/characters/make_clip_pack.py";
    private const string ClipPackLicense = "CC0-1.0";
    private const string MeshPath = "characters/traveller/traveller-{0}.glb";
    private const string FacePath = "characters/traveller/face-{0}-{1}.png";
    /// <summary>The body at twice the crunch rule's density, on the same UVs as the GLB's own 32 texels/m.</summary>
    private const string DenseBodyPath = "characters/traveller/body-{0}-64.png";
    internal static readonly int[] BodyDensities = [32, 64];
    private const int GlbBodyDensity = 32;
    internal static readonly string[] TravellerVariants = ["chunky", "stylised", "bold"];
    internal static readonly int[] FaceSizes = [32, 64, 128];
    private const int DefaultFaceSize = 64;

    /// <summary>The body and face material slots, in assemble_character.py's slot order.</summary>
    private const uint BodySlot = 0;
    private const uint FaceSlot = 1;
    private const float BodyRoughness = 0.85f;
    private const float FaceRoughness = 0.7f;
    private static readonly Color White = new(1, 1, 1, 1);

    // UAL1's Idle_Loop, Walk_Loop and Idle_Talking_Loop, renamed to the Engine's lowercase clip ids.
    private const string IdleClip = "idle";
    private const string WalkClip = "walk";
    private const string TalkClip = "talk";
    private const string WalkingParameter = "walking";
    private const string TalkingParameter = "talking";
    private const uint GraphVersion = 1;
    private const uint TickMilliseconds = 50;
    /// <summary>Blend between states over this many controller ticks (0.3 s).</summary>
    private const uint BlendTicks = 6;
    private const float BlendSeconds = 0.3f;
    private const int UnitSpeedMilli = 1000;

    /// <summary>The fitted travellers stand about 2 m tall on UAL's skeleton; this brings them to about 1.73 m.</summary>
    internal const float TravellerScale = 0.86f;

    /// <summary>A stoop bends the spine forward by these shares of its angle, on top of every clip; the head turns back up by the last.</summary>
    private static readonly (string Joint, float Share)[] Stoop = [("spine_02", 0.35f), ("spine_03", 0.35f), ("neck_01", 0.3f), ("Head", -0.6f)];

    /// <summary>The walk clip covers about 1.2 m a second at full size (stride measured from Walk_Loop).</summary>
    private const double WalkMetresPerSecondAtUnitScale = 1.2;
    private const double WalkCircleMetres = 3;

    private const double BlinkSeconds = 0.13;
    private const double BlinkGapMinimumSeconds = 2.2;
    private const double BlinkGapRangeSeconds = 3.6;
    private const double SyllableOpenMinimumSeconds = 0.09;
    private const double SyllableOpenRangeSeconds = 0.12;
    private const double SyllableClosedMinimumSeconds = 0.06;
    private const double SyllableClosedRangeSeconds = 0.12;
    /// <summary>Talk pauses between phrases: after this many syllables, the mouth rests a while.</summary>
    private const int PhraseSyllablesMinimum = 4;
    private const int PhraseSyllablesRange = 6;
    private const double PhrasePauseMinimumSeconds = 0.4;
    private const double PhrasePauseRangeSeconds = 0.6;

    internal static readonly string[] Expressions = ["neutral", "blink", "talk", "smile"];

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly WorldFrame frame;
    private Dictionary<string, CharacterKind>? kinds;
    private readonly Func<double, double, double> groundAt;
    private readonly Func<Vector3> playerWorld;
    private readonly Dictionary<string, (RenderResource Mesh, AnimationGraph Graph)> variants = [];
    private readonly Dictionary<string, Material> faces = [];
    private readonly Dictionary<string, Material> denseBodies = [];
    private readonly List<RenderResource> textures = [];
    private readonly List<Traveller> travellers = [];
    private readonly List<Traveller> retiring = [];
    private readonly List<RenderResource> clipPacks = [];
    private int nextId;

    /// <summary>
    /// Whether new characters play through an Engine graph and controller, or through direct playback.
    /// On pair 0a5dfab7b318 only one controller-driven character animates at a time: each controller's
    /// projector numbers its animation entity 1 (rusty-engine #10019), so a crowd uses direct playback.
    /// </summary>
    internal bool DirectPlayback { get; set; }

    internal CharacterStyleTest(IEngineContext engine, ProductContent content, WorldFrame frame, Func<double, double, double> groundAt, Func<Vector3> playerWorld)
    {
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.groundAt = groundAt ?? throw new ArgumentNullException(nameof(groundAt));
    }

    /// <summary>The travellers for this update's appearance snapshot.</summary>
    internal IEnumerable<AppearanceFact> Facts => travellers.Select(Fact);

    /// <summary>Every placeable kind: the three travellers, then the cast from its manifest.</summary>
    internal IReadOnlyDictionary<string, CharacterKind> Kinds => kinds ??= ReadKinds();

    private Dictionary<string, CharacterKind> ReadKinds()
    {
        Dictionary<string, CharacterKind> all = [];
        foreach (string v in TravellerVariants)
        {
            all[v] = new(v, Format(MeshPath, v), Format(ClipPackPath, v), (e, size) => Format(FacePath, e, size), FaceSizes, DefaultFaceSize,
                Format(DenseBodyPath, v), TravellerScale, 0);
        }

        using JsonDocument cast = JsonDocument.Parse(content.ReadText(CastManifest));
        foreach (JsonElement c in cast.RootElement.GetProperty("characters").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            Dictionary<string, string> faceSet = c.GetProperty("faces").EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetString()!);
            float stoop = c.GetProperty("posture").TryGetProperty("stoop", out JsonElement s) ? s.GetSingle() : 0;
            all[name] = new(name, c.GetProperty("mesh").GetString()!, c.GetProperty("pack").GetString()!, (e, _) => faceSet[e], [CastFaceSize], CastFaceSize,
                null, c.GetProperty("scale").GetSingle(), stoop);
        }

        return all;
    }

    private static string Format(string pattern, params object[] values) => string.Format(CultureInfo.InvariantCulture, pattern, values);

    /// <summary>
    /// Places a character of a kind standing at a world column, facing yawDegrees (0 faces north, 90
    /// east), at a uniform scale (0 takes the kind's own).
    /// </summary>
    internal string Place(string kindName, double x, double z, double yawDegrees, float scale)
    {
        if (!Kinds.TryGetValue(kindName, out CharacterKind? kind)) return $"unknown kind '{kindName}'; one of {string.Join('|', Kinds.Keys)}";
        (RenderResource mesh, AnimationGraph graph) = Variant(kind);
        int id = nextId++;
        ulong objectId = ProductIds.CharacterStyleBase + (ulong)id;
        Appearance appearance = engine.Animation.CreateAnimatedMeshAppearance(new(mesh));
        AnimationInstance instance = engine.Animation.CreateInstance(new(appearance, objectId));
        AnimationController? controller = DirectPlayback ? null : engine.Animation.CreateController(new(graph, instance, TickMilliseconds));
        if (controller is null) Play(instance, IdleClip, restart: true);
        Traveller traveller = new(id, objectId, kind, appearance, instance, controller, x, z, yawDegrees, scale > 0 ? scale : kind.Scale, new Random(id + 10014))
        {
            FaceSize = kind.DefaultFaceSize,
        };
        travellers.Add(traveller);
        ShowFace(traveller, "neutral");
        if (kind.StoopDegrees != 0) ApplyPosture(traveller, mesh);
        return Readout();
    }

    /// <summary>
    /// A stoop as pose controls over the clips (JointOverride.AddLocalRotation): it bends whatever the
    /// clip plays, so one clip pack serves upright and stooped people alike.
    /// </summary>
    private void ApplyPosture(Traveller t, RenderResource mesh)
    {
        ReadOnlySpan<AnimationJointInfo> rig = engine.Animation.ReadJoints(mesh).Span;
        List<JointOverride> overrides = [];
        foreach ((string joint, float share) in Stoop)
        {
            uint index = AnimationJoints.IndexOf(rig, joint);
            if (index == uint.MaxValue) continue;
            float radians = t.Kind.StoopDegrees * share * MathF.PI / 180;
            overrides.Add(JointOverride.AddLocalRotation(index, Quaternion.CreateFromAxisAngle(Vector3.UnitX, radians), 1));
        }

        engine.Animation.SetPose(new(t.Instance, ReadOnlyMemory<TwoBoneIk>.Empty, overrides.ToArray(), false));
    }

    /// <summary>Places a traveller at an offset (metres east, south) from the player.</summary>
    internal string PlaceNear(string variant, double east, double south, double yawDegrees, float scale)
    {
        Vector3 player = playerWorld();
        return Place(variant, player.X + east, player.Z + south, yawDegrees, scale);
    }

    /// <summary>Direct playback: loop a clip, cross-fading from whatever played.</summary>
    private void Play(AnimationInstance instance, string clip, bool restart) =>
        engine.Animation.SetPlayback(new(instance, AnimationPlaybackKind.Play, clip, AnimationLoopMode.Repeat, 1, 1, restart, BlendSeconds, !restart, 0));

    /// <summary>Sets one traveller's mode, or every traveller's (id -1).</summary>
    internal string SetMode(int id, string mode)
    {
        if (!Enum.TryParse(mode, true, out TravellerMode parsed)) return $"unknown mode '{mode}'; idle|walk|talk";
        foreach (Traveller traveller in Select(id))
        {
            traveller.Mode = parsed;
            traveller.WalkAngle = 0;
            if (traveller.Controller is null)
            {
                Play(traveller.Instance, parsed switch { TravellerMode.Walk => WalkClip, TravellerMode.Talk => TalkClip, _ => IdleClip }, restart: false);
                continue;
            }

            engine.Animation.SetBool(new(traveller.Controller, WalkingParameter, parsed == TravellerMode.Walk));
            engine.Animation.SetBool(new(traveller.Controller, TalkingParameter, parsed == TravellerMode.Talk));
        }

        return Readout();
    }

    /// <summary>
    /// Pins a face (neutral|blink|talk|smile) for stills, or returns it to "auto" (blinks, and mouth
    /// movement while talking) or "smile" (auto over a smiling rest face). Size picks the texture density.
    /// </summary>
    internal string SetFace(int id, string face, int size)
    {
        if (face != "auto" && !Expressions.Contains(face)) return $"unknown face '{face}'; auto|{string.Join('|', Expressions)}";
        foreach (Traveller traveller in Select(id))
        {
            traveller.PinnedFace = face == "auto" ? null : face;
            traveller.FaceSize = traveller.Kind.FaceSizes.Contains(size) ? size : traveller.Kind.DefaultFaceSize;
            ShowFace(traveller, traveller.PinnedFace ?? "neutral");
        }

        return Readout();
    }

    /// <summary>Selects a traveller's body texture density in texels per metre (32, the GLB's own, or 64); id -1 for all.</summary>
    internal string SetBody(int id, int density)
    {
        if (!BodyDensities.Contains(density)) return $"unknown body density {density}; {string.Join('|', BodyDensities)}";
        foreach (Traveller traveller in Select(id).Where(t => t.Kind.DenseBody is not null))
        {
            traveller.BodyDensity = density;
            Bind(traveller);
        }

        return Readout();
    }

    /// <summary>
    /// Removes every traveller. Its controller and animation instance go at once (the Engine wants a
    /// controller gone before the snapshot that removes its target); its appearance goes after that
    /// snapshot (<see cref="AfterAppearanceSnapshot"/>).
    /// </summary>
    internal string Clear()
    {
        foreach (Traveller traveller in travellers)
        {
            traveller.Controller?.Dispose();
            traveller.Instance.Dispose();
            retiring.Add(traveller);
        }

        travellers.Clear();
        return Readout();
    }

    /// <summary>Moves walking travellers, ticks their controllers and swaps faces, on host seconds.</summary>
    internal void Advance(double seconds)
    {
        seconds = Math.Max(0, seconds);
        foreach (Traveller traveller in travellers)
        {
            traveller.Seconds += seconds;
            // Controller ticks are contiguous: every tick the host time has passed, in order.
            ulong due = (ulong)(traveller.Seconds * 1000 / TickMilliseconds);
            while (traveller.Controller is not null && traveller.Tick < due)
            {
                engine.Animation.Tick(new(traveller.Controller, ++traveller.Tick));
            }

            if (traveller.Mode == TravellerMode.Walk)
                traveller.WalkAngle += seconds * WalkMetresPerSecondAtUnitScale * traveller.Scale / WalkCircleMetres;
            AnimateFace(traveller);
        }
    }

    /// <summary>Releases removed travellers' appearances, after a snapshot without them.</summary>
    internal void AfterAppearanceSnapshot()
    {
        foreach (Traveller traveller in retiring)
        {
            traveller.Appearance.Dispose();
        }

        retiring.Clear();
    }

    internal string Readout()
    {
        IEnumerable<string> rows = travellers.Select(t =>
        {
            AnimationControllerReadout controller = t.Controller is null ? default : engine.Animation.ReadController(t.Controller);
            (double x, double y, double z, double yaw) = Pose(t);
            return string.Create(CultureInfo.InvariantCulture,
                $"{t.Id}:{t.Kind.Name}:{t.Mode}:body={t.BodyDensity}:face={t.Shown}@{t.FaceSize}{(t.PinnedFace is null ? "(auto)" : "")}:at={x:F2},{y:F2},{z:F2}:yaw={yaw:F0}:scale={t.Scale:F2}:state={controller.StateIndex}:clip={controller.ClipAIndex}:tick={controller.ControllerTick}");
        });
        Vector3 player = playerWorld();
        return string.Create(CultureInfo.InvariantCulture, $"player={player.X:F2},{player.Y:F2},{player.Z:F2};travellers={travellers.Count};{string.Join(';', rows)}");
    }

    /// <summary>A kind's mesh facts and its own clips (the attached pack's are validated when a graph runs).</summary>
    internal string Clips(string variant)
    {
        if (!Kinds.TryGetValue(variant, out CharacterKind? kind)) return $"unknown kind '{variant}'";
        RenderResource mesh = Variant(kind).Mesh;
        AnimatedMeshInfo info = engine.Animation.ReadMeshInfo(mesh);
        string clips = string.Join(',', engine.Animation.ReadClips(mesh).ToArray().Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{c.Name}({c.DurationSeconds:F2}s)")));
        return string.Create(CultureInfo.InvariantCulture, $"{variant}: {info};clips={clips}");
    }

    /// <summary>Releases everything; call <see cref="Clear"/> before the snapshot that withdraws the travellers.</summary>
    public void Dispose()
    {
        Clear();
        AfterAppearanceSnapshot();
        foreach (Material material in faces.Values.Concat(denseBodies.Values)) material.Dispose();
        foreach (RenderResource texture in textures) texture.Dispose();
        foreach ((RenderResource mesh, AnimationGraph graph) in variants.Values)
        {
            graph.Dispose();
            mesh.Dispose();
        }

        foreach (RenderResource pack in clipPacks) pack.Dispose();
    }

    private IEnumerable<Traveller> Select(int id) => id < 0 ? travellers : travellers.Where(t => t.Id == id);

    /// <summary>
    /// Opens a variant once: its mesh, then UAL's clip pack attached to it (before any appearance or
    /// graph holds the mesh, as the Engine requires), then its idle/walk/talk graph.
    /// </summary>
    private (RenderResource Mesh, AnimationGraph Graph) Variant(CharacterKind kind)
    {
        string name = kind.Name;
        if (variants.TryGetValue(name, out (RenderResource Mesh, AnimationGraph Graph) open)) return open;
        RenderResource clipPack;
        using (ContentReference pack = engine.Content.OpenReference(new(kind.Pack)))
            clipPack = engine.Animation.OpenAnimationClipPackFromContent(new(pack));
        clipPacks.Add(clipPack);

        RenderResource mesh;
        using (ContentReference reference = engine.Content.OpenReference(new(kind.Mesh)))
            mesh = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
        engine.Animation.AssociateAnimationClipPack(new(mesh, clipPack, ClipPackProducer, ClipPackLicense));

        AnimationGraph graph = engine.Animation.CreateGraph(new(mesh, $"traveller-{name}", GraphVersion, "idle"));
        engine.Animation.DefineParameter(new(graph, WalkingParameter, AnimationParameterKind.Bool, 0, false));
        engine.Animation.DefineParameter(new(graph, TalkingParameter, AnimationParameterKind.Bool, 0, false));
        foreach ((string state, string clip) in new[] { ("idle", IdleClip), ("walk", WalkClip), ("talk", TalkClip) })
            engine.Animation.DefineState(new(graph, state, AnimationMotionKind.Clip, clip, "", "", 0, 0, UnitSpeedMilli));
        // Transitions leaving one state need distinct priorities; talking outranks walking.
        Transition(graph, "idle", "talk", TalkingParameter, true, 0);
        Transition(graph, "idle", "walk", WalkingParameter, true, 1);
        Transition(graph, "walk", "talk", TalkingParameter, true, 0);
        Transition(graph, "walk", "idle", WalkingParameter, false, 1);
        Transition(graph, "talk", "idle", TalkingParameter, false, 0);
        variants[name] = (mesh, graph);
        return (mesh, graph);
    }

    private void Transition(AnimationGraph graph, string from, string to, string parameter, bool value, uint priority)
    {
        AnimationTransitionHandle transition = engine.Animation.DefineTransition(new(graph, $"{from}-{to}", from, to, priority, BlendTicks));
        engine.Animation.DefineCondition(new(transition, AnimationConditionKind.BoolEquals, parameter, 0, value));
    }

    /// <summary>
    /// Automatic faces: a blink every few seconds; while talking, the mouth opens and closes in
    /// syllables grouped into phrases. A pinned face holds still.
    /// </summary>
    private void AnimateFace(Traveller t)
    {
        if (t.PinnedFace is { } pinned && pinned != "smile")
        {
            ShowFace(t, pinned);
            return;
        }

        string rest = t.PinnedFace ?? "neutral";
        if (t.Seconds >= t.NextBlink + BlinkSeconds)
            t.NextBlink = t.Seconds + BlinkGapMinimumSeconds + (t.Random.NextDouble() * BlinkGapRangeSeconds);
        if (t.Seconds >= t.NextBlink)
        {
            ShowFace(t, "blink");
            return;
        }

        if (t.Mode != TravellerMode.Talk)
        {
            ShowFace(t, rest);
            return;
        }

        if (t.Seconds >= t.NextSyllable)
        {
            t.MouthOpen = !t.MouthOpen;
            if (t.MouthOpen && --t.SyllablesLeft <= 0)
            {
                t.MouthOpen = false;
                t.SyllablesLeft = PhraseSyllablesMinimum + t.Random.Next(PhraseSyllablesRange);
                t.NextSyllable = t.Seconds + PhrasePauseMinimumSeconds + (t.Random.NextDouble() * PhrasePauseRangeSeconds);
            }
            else
            {
                t.NextSyllable = t.Seconds + (t.MouthOpen
                    ? SyllableOpenMinimumSeconds + (t.Random.NextDouble() * SyllableOpenRangeSeconds)
                    : SyllableClosedMinimumSeconds + (t.Random.NextDouble() * SyllableClosedRangeSeconds));
            }
        }

        ShowFace(t, t.MouthOpen ? "talk" : rest);
    }

    /// <summary>Selects a face texture by replacing the face slot's material; only when it changes.</summary>
    private void ShowFace(Traveller t, string expression)
    {
        if (t.Shown == expression && t.ShownSize == t.FaceSize) return;
        t.Shown = expression;
        t.ShownSize = t.FaceSize;
        Bind(t);
    }

    /// <summary>The appearance's complete material selection: its face, and its body when not the GLB's own.</summary>
    private void Bind(Traveller t)
    {
        List<MeshMaterialBinding> bindings = [new(FaceSlot, Face(t.Kind.FacePath(t.Shown, t.ShownSize)))];
        if (t.BodyDensity != GlbBodyDensity && t.Kind.DenseBody is { } dense) bindings.Add(new(BodySlot, DenseBody(dense)));
        engine.Animation.UpdateAnimatedMeshMaterials(new(t.Appearance, bindings.ToArray()));
    }

    private Material DenseBody(string path)
    {
        if (denseBodies.TryGetValue(path, out Material? material)) return material;
        RenderResource texture = engine.Graphics.OpenResource(new(path, TextureFilter.Nearest, TextureWrap.Clamp)).Handle;
        textures.Add(texture);
        material = engine.Graphics.CreateMaterial(new MaterialRequest(White, texture, BodyRoughness, White, Vector3.Zero, 0, false));
        denseBodies[path] = material;
        return material;
    }

    private Material Face(string path)
    {
        if (faces.TryGetValue(path, out Material? material)) return material;
        // Nearest magnification, as the crunch rule asks of objects; the Engine gives nearest
        // textures no mipmaps yet (rusty-engine #10012).
        RenderResource texture = engine.Graphics.OpenResource(new(path, TextureFilter.Nearest, TextureWrap.Clamp)).Handle;
        textures.Add(texture);
        material = engine.Graphics.CreateMaterial(new MaterialRequest(White, texture, FaceRoughness, White, Vector3.Zero, 0, false));
        faces[path] = material;
        return material;
    }

    private (double X, double Y, double Z, double YawDegrees) Pose(Traveller t)
    {
        double x = t.X, z = t.Z, yaw = t.YawDegrees;
        if (t.Mode == TravellerMode.Walk || t.WalkAngle != 0)
        {
            // Around a circle from the placement, turning right: facing yaw y heads along (sin y, -cos y),
            // so the circle's points are centre - r (cos y, sin y).
            double start = t.YawDegrees * Math.PI / 180, heading = start + t.WalkAngle;
            double cx = t.X + (Math.Cos(start) * WalkCircleMetres), cz = t.Z + (Math.Sin(start) * WalkCircleMetres);
            x = cx - (Math.Cos(heading) * WalkCircleMetres);
            z = cz - (Math.Sin(heading) * WalkCircleMetres);
            yaw = heading * 180 / Math.PI;
        }

        return (x, groundAt(x, z), z, yaw);
    }

    private AppearanceFact Fact(Traveller t)
    {
        (double x, double y, double z, double yaw) = Pose(t);
        // The mesh faces +Z; yaw 0 faces north (-Z) and 90 east (+X), as the camera's yaw does.
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)((180 - yaw) * Math.PI / 180));
        return new AppearanceFact(t.ObjectId, false, 0, new Transform(frame.ToLocal(x, y, z), turn, new Vector3(t.Scale)),
            t.Appearance, Visible: true, RenderLayer.Scene, ShadowCasting.Cast);
    }

    private sealed class Traveller(int id, ulong objectId, CharacterKind kind, Appearance appearance, AnimationInstance instance,
        AnimationController? controller, double x, double z, double yawDegrees, float scale, Random random)
    {
        internal int Id { get; } = id;
        internal ulong ObjectId { get; } = objectId;
        internal CharacterKind Kind { get; } = kind;
        internal Appearance Appearance { get; } = appearance;
        internal AnimationInstance Instance { get; } = instance;
        internal AnimationController? Controller { get; } = controller;
        internal double X { get; } = x;
        internal double Z { get; } = z;
        internal double YawDegrees { get; } = yawDegrees;
        internal float Scale { get; } = scale;
        internal Random Random { get; } = random;
        internal TravellerMode Mode { get; set; }
        internal double WalkAngle { get; set; }
        internal double Seconds { get; set; }
        internal ulong Tick { get; set; }
        internal string? PinnedFace { get; set; }
        internal int FaceSize { get; set; } = DefaultFaceSize;
        internal int BodyDensity { get; set; } = GlbBodyDensity;
        internal string Shown { get; set; } = "";
        internal int ShownSize { get; set; }
        internal double NextBlink { get; set; } = 1.5;
        internal double NextSyllable { get; set; }
        internal bool MouthOpen { get; set; }
        internal int SyllablesLeft { get; set; } = PhraseSyllablesMinimum;
    }
}
