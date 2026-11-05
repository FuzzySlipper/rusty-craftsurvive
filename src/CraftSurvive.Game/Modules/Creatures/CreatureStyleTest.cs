using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// A placeable creature kind: its mesh, its clips, its legs as two-bone IK chains, and where its
/// hind and fore feet stand along its body.
/// </summary>
internal sealed record CreatureStyleKind(
    string Name,
    string Mesh,
    float Scale,
    IReadOnlyDictionary<string, CreatureClip> Clips,
    IReadOnlyList<(string Root, string Mid, string End)> Legs,
    double HindMetres,
    double ForeMetres);

/// <summary>
/// The creature style test (#10568), a test aid rather than wildlife: places a creature kind built by
/// art/creatures, plays its clips, walks it round a circle at its clips' own speed, pitches it to the
/// ground and plants its feet with two-bone IK. Live-debug commands drive it, for captures.
///
/// Two drivers: an Engine graph with a trigger per clip (the path wildlife will take), or direct
/// playback that samples each clip at a time the product sets. Either can be stepped: the pose then
/// advances in whole steps of 1/fps seconds rather than every update, to judge a retro rate.
/// </summary>
internal sealed class CreatureStyleTest : IDisposable
{
    private const string KindsPath = "creatures/style-kinds.json";
    private const string IdleClip = "idle";
    private const uint GraphVersion = 1;
    private const uint TickMilliseconds = 50;
    /// <summary>Blend between graph states over this many controller ticks (0.3 s).</summary>
    private const uint BlendTicks = 6;
    private const int UnitSpeedMilli = 1000;
    private const string TriggerPrefix = "to-";

    /// <summary>Gaits walk round a circle this wide, so a capture keeps the creature in view.</summary>
    private const double CircleMetres = 9;

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly WorldFrame frame;
    private readonly Func<double, double, double> groundAt;
    private readonly Func<Vector3> playerWorld;
    private readonly Dictionary<string, (RenderResource Mesh, AnimationGraph Graph, string[] Joints)> opened = [];
    private readonly List<Placed> placed = [];
    private readonly List<Placed> retiring = [];
    private Dictionary<string, CreatureStyleKind>? kinds;
    private int nextId;

    /// <summary>What driving the placed creatures has cost each update (playback, placement, feet), since the last readout.</summary>
    private (double Total, int Count, double Worst) advanceMs;

    internal CreatureStyleTest(IEngineContext engine, ProductContent content, WorldFrame frame, Func<double, double, double> groundAt, Func<Vector3> playerWorld)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.groundAt = groundAt ?? throw new ArgumentNullException(nameof(groundAt));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
    }

    /// <summary>Whether new creatures play through direct playback rather than an Engine graph.</summary>
    internal bool DirectPlayback { get; set; }

    /// <summary>The pose's step rate in frames a second; 0 is smooth.</summary>
    internal int StepsPerSecond { get; set; }

    internal bool PlantFeet { get; set; } = true;

    internal IReadOnlyDictionary<string, CreatureStyleKind> Kinds => kinds ??= ReadKinds();

    internal IEnumerable<AppearanceFact> Facts => placed.Select(p =>
        new AppearanceFact(p.ObjectId, false, 0, p.Placement, p.Appearance, Visible: true, RenderLayer.Scene, ShadowCasting.Cast));

    private Dictionary<string, CreatureStyleKind> ReadKinds()
    {
        Dictionary<string, CreatureStyleKind> all = [];
        using JsonDocument index = JsonDocument.Parse(content.ReadText(KindsPath));
        foreach (JsonElement k in index.RootElement.GetProperty("kinds").EnumerateArray())
        {
            string name = k.GetProperty("name").GetString()!;
            CreatureRig rig = CreatureManifest.Parse(content.ReadText(k.GetProperty("manifest").GetString()!));
            all[name] = new CreatureStyleKind(name, k.GetProperty("mesh").GetString()!, k.GetProperty("scale").GetSingle(), rig.Clips, rig.Legs,
                rig.HindMetres, rig.ForeMetres);
        }

        return all;
    }

    /// <summary>Places a creature metres east and south of the player, facing yaw degrees (0 north, 90 east).</summary>
    internal string PlaceNear(string kindName, double east, double south, double yawDegrees, float scale)
    {
        if (!Kinds.TryGetValue(kindName, out CreatureStyleKind? kind)) return $"unknown kind '{kindName}'; one of {string.Join('|', Kinds.Keys)}";
        (RenderResource mesh, AnimationGraph graph, string[] joints) = Open(kind);
        Vector3 player = playerWorld();
        int id = nextId++;
        ulong objectId = ProductIds.CreatureStyleBase + (ulong)id;
        Appearance appearance = engine.Animation.CreateAnimatedMeshAppearance(new(mesh));
        AnimationInstance instance = engine.Animation.CreateInstance(new(appearance, objectId));
        AnimationController? controller = DirectPlayback ? null : engine.Animation.CreateController(new(graph, instance, TickMilliseconds));
        Placed creature = new(id, objectId, kind, joints, appearance, instance, controller, player.X + east, player.Z + south, yawDegrees,
            scale > 0 ? scale : kind.Scale)
        {
            Clip = kind.Clips[IdleClip],
        };
        placed.Add(creature);
        Place(creature);
        return Readout();
    }

    /// <summary>Plays a clip on one creature, or every creature (id -1). Gaits walk the circle.</summary>
    internal string SetClip(int id, string clip)
    {
        foreach (Placed creature in Select(id))
        {
            if (!creature.Kind.Clips.TryGetValue(clip, out CreatureClip? next)) return $"unknown clip '{clip}'; one of {string.Join('|', creature.Kind.Clips.Keys)}";
            // A trigger no transition consumes stays set: fired for the state already playing, it
            // would pull the graph straight back here from the next state.
            if (next == creature.Clip) continue;
            creature.Clip = next;
            creature.ClipSeconds = 0;
            creature.ShownSeconds = -1;
            if (creature.Controller is not null) engine.Animation.FireTrigger(new(creature.Controller, TriggerPrefix + clip));
        }

        return Readout();
    }

    /// <summary>Advances every creature by host seconds: its clip time, its walk, its pose and its feet.</summary>
    internal void Advance(double seconds)
    {
        long started = Stopwatch.GetTimestamp();
        seconds = Math.Max(0, seconds);
        foreach (Placed creature in placed)
        {
            creature.Seconds += seconds;
            creature.ClipSeconds += seconds;
            // Only gaits have a ground speed.
            if (creature.Clip.Speed > 0)
                creature.WalkAngle += seconds * creature.Clip.Speed * creature.Scale / CircleMetres;
            // A stepped pose shows the clip only at whole steps; smooth shows it every update.
            double shown = StepsPerSecond > 0 ? Math.Floor(creature.ClipSeconds * StepsPerSecond) / StepsPerSecond : creature.ClipSeconds;
            if (shown != creature.ShownSeconds)
            {
                creature.ShownSeconds = shown;
                Drive(creature, shown);
            }

            Place(creature);
            if (creature.Published) Plant(creature);
        }

        if (placed.Count == 0) return;
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        advanceMs.Total += ms;
        advanceMs.Count++;
        advanceMs.Worst = Math.Max(advanceMs.Worst, ms);
    }

    /// <summary>Moves the clip to a time: graph ticks up to it (contiguous, as the Engine requires) or a direct sample.</summary>
    private void Drive(Placed creature, double clipSeconds)
    {
        if (creature.Controller is { } controller)
        {
            ulong due = (ulong)(creature.Seconds * 1000 / TickMilliseconds);
            if (StepsPerSecond > 0) due = (ulong)(Math.Floor(creature.Seconds * StepsPerSecond) / StepsPerSecond * 1000 / TickMilliseconds);
            while (creature.Tick < due) engine.Animation.Tick(new(controller, ++creature.Tick));
            return;
        }

        CreatureClip clip = creature.Clip;
        double fraction = clip.Loops ? (clipSeconds / clip.Seconds) % 1.0 : Math.Min(1.0, clipSeconds / clip.Seconds);
        engine.Animation.SetPlayback(new(creature.Instance, AnimationPlaybackKind.Sample, clip.Name, AnimationLoopMode.Once, 0, 1, false, 0, false,
            (float)fraction));
    }

    /// <summary>Where the creature stands: round its circle in a gait, pitched to the ground under its hind and fore feet.</summary>
    private void Place(Placed creature)
    {
        double yaw = creature.YawDegrees;
        double x = creature.X, z = creature.Z;
        if (creature.WalkAngle != 0)
        {
            // Round a circle from the placement, turning right: facing yaw y heads along (sin y, -cos y).
            double start = creature.YawDegrees * Math.PI / 180, heading = start + creature.WalkAngle;
            double cx = creature.X + (Math.Cos(start) * CircleMetres), cz = creature.Z + (Math.Sin(start) * CircleMetres);
            x = cx - (Math.Cos(heading) * CircleMetres);
            z = cz - (Math.Sin(heading) * CircleMetres);
            yaw = heading * 180 / Math.PI;
        }

        double radians = yaw * Math.PI / 180;
        (double fx, double fz) = (Math.Sin(radians), -Math.Cos(radians));
        double hind = creature.Kind.HindMetres * creature.Scale, fore = creature.Kind.ForeMetres * creature.Scale;
        double hindGround = groundAt(x + (fx * hind), z + (fz * hind));
        double foreGround = groundAt(x + (fx * fore), z + (fz * fore));
        // A two-legged body has one place its feet stand, so nothing to pitch it by.
        double span = fore - hind;
        double y = span > 0 ? hindGround + ((foreGround - hindGround) * (0 - hind) / span) : hindGround;
        double pitch = span > 0 ? Math.Atan2(foreGround - hindGround, span) : 0;
        // The mesh faces +Z; yaw 0 faces north (-Z) and 90 east (+X), as the camera's yaw does. A
        // rising front turns the nose up: a negative turn about the mesh's own X.
        Quaternion lift = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)-pitch);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)((180 - yaw) * Math.PI / 180));
        creature.World = new Vector3((float)x, (float)y, (float)z);
        creature.PitchDegrees = pitch * 180 / Math.PI;
        creature.Placement = new Transform(frame.ToLocal(x, y, z), Quaternion.Concatenate(lift, turn), new Vector3(creature.Scale));
    }

    /// <summary>
    /// Plants each foot on the ground under where the clip puts it, keeping the clip's own lift: a
    /// two-bone IK per leg toward that point, bending the way the leg already bends. Clips that fold
    /// or raise their legs (rearing, dying) keep their own pose.
    /// </summary>
    private void Plant(Placed creature)
    {
        if (!PlantFeet || !creature.Clip.PlantsFeet)
        {
            if (creature.Planted)
            {
                engine.Animation.SetPose(new(creature.Instance, ReadOnlyMemory<TwoBoneIk>.Empty, ReadOnlyMemory<JointOverride>.Empty));
                creature.Planted = false;
            }

            return;
        }

        ReadOnlySpan<JointPose> pose = engine.Animation.SampleJointPose(
            AnimationPoseSampleRequest.OfCurrentPlayback(creature.Instance, creature.Placement, applyPoseControls: false)).Span;
        if (pose.Length < creature.Joints.Length) return;
        (TwoBoneIk[] chains, double worst) = CreatureFeet.Chains(pose, creature.Legs, creature.Scale, creature.World, creature.Placement.Translation,
            (x, z) => (float)groundAt(x, z));
        creature.FootCorrection = worst;
        engine.Animation.SetPose(new(creature.Instance, chains, ReadOnlyMemory<JointOverride>.Empty));
        creature.Planted = true;
    }

    /// <summary>Removes every creature; appearances go after the next snapshot without them.</summary>
    internal string Clear()
    {
        foreach (Placed creature in placed)
        {
            creature.Controller?.Dispose();
            creature.Instance.Dispose();
            retiring.Add(creature);
        }

        placed.Clear();
        return Readout();
    }

    /// <summary>After a snapshot: placed creatures are now published (so their pose can be sampled), and removed ones can go.</summary>
    internal void AfterAppearanceSnapshot()
    {
        foreach (Placed creature in placed) creature.Published = true;
        foreach (Placed creature in retiring) creature.Appearance.Dispose();
        retiring.Clear();
    }

    /// <summary>Reports, and restarts the advance timing.</summary>
    internal string Readout()
    {
        string text = Report();
        advanceMs = default;
        return text;
    }

    private string Report()
    {
        IEnumerable<string> rows = placed.Select(c =>
        {
            AnimationControllerReadout controller = c.Controller is null ? default : engine.Animation.ReadController(c.Controller);
            return string.Create(CultureInfo.InvariantCulture,
                $"{c.Id}:{c.Kind.Name}:{c.Clip.Name}:at={c.World.X:F2},{c.World.Y:F2},{c.World.Z:F2}:pitch={c.PitchDegrees:F1}:scale={c.Scale:F2}:planted={c.Planted}:footCorrection={c.FootCorrection:F2}:driver={(c.Controller is null ? "direct" : "graph")}:state={controller.StateIndex}:tick={controller.ControllerTick}");
        });
        return string.Create(CultureInfo.InvariantCulture,
            $"step={StepsPerSecond};plant={PlantFeet};direct={DirectPlayback};creatures={placed.Count};advanceMs=mean {(advanceMs.Count > 0 ? advanceMs.Total / advanceMs.Count : 0):F3} worst {advanceMs.Worst:F3} over {advanceMs.Count};{string.Join(';', rows)}");
    }

    /// <summary>A kind's mesh facts and the clips the Engine assembled for it.</summary>
    internal string Clips(string kindName)
    {
        if (!Kinds.TryGetValue(kindName, out CreatureStyleKind? kind)) return $"unknown kind '{kindName}'";
        (RenderResource mesh, _, string[] joints) = Open(kind);
        AnimatedMeshInfo info = engine.Animation.ReadMeshInfo(mesh);
        string clips = string.Join(',', engine.Animation.ReadClips(mesh).ToArray().Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{c.Name}({c.DurationSeconds:F2}s)")));
        return string.Create(CultureInfo.InvariantCulture, $"{kindName}: {info};joints={joints.Length};clips={clips}");
    }

    public void Dispose()
    {
        Clear();
        AfterAppearanceSnapshot();
        foreach ((RenderResource mesh, AnimationGraph graph, _) in opened.Values)
        {
            graph.Dispose();
            mesh.Dispose();
        }
    }

    private IEnumerable<Placed> Select(int id) => id < 0 ? placed : placed.Where(c => c.Id == id);

    /// <summary>Opens a kind once: its mesh, and a graph with a state per clip and a trigger into each.</summary>
    private (RenderResource Mesh, AnimationGraph Graph, string[] Joints) Open(CreatureStyleKind kind)
    {
        if (opened.TryGetValue(kind.Name, out (RenderResource, AnimationGraph, string[]) open)) return open;
        RenderResource mesh;
        using (ContentReference reference = engine.Content.OpenReference(new(kind.Mesh)))
            mesh = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
        string[] joints = [.. engine.Animation.ReadJoints(mesh).ToArray().Select(j => j.Id)];
        AnimationGraph graph = engine.Animation.CreateGraph(new(mesh, $"creature-{kind.Name}", GraphVersion, IdleClip));
        string[] clips = [.. kind.Clips.Keys];
        foreach (string clip in clips)
        {
            engine.Animation.DefineParameter(new(graph, TriggerPrefix + clip, AnimationParameterKind.Trigger, 0, false));
            engine.Animation.DefineState(new(graph, clip, AnimationMotionKind.Clip, clip, "", "", 0, 0, UnitSpeedMilli));
        }

        foreach (string from in clips)
        {
            // Transitions leaving one state need distinct priorities.
            uint priority = 0;
            foreach (string to in clips.Where(c => c != from))
            {
                AnimationTransitionHandle transition = engine.Animation.DefineTransition(new(graph, $"{from}-{to}", from, to, priority++, BlendTicks));
                engine.Animation.DefineCondition(new(transition, AnimationConditionKind.TriggerSet, TriggerPrefix + to, 0, false));
            }
        }

        opened[kind.Name] = (mesh, graph, joints);
        return opened[kind.Name];
    }

    private sealed class Placed(int id, ulong objectId, CreatureStyleKind kind, string[] joints, Appearance appearance, AnimationInstance instance,
        AnimationController? controller, double x, double z, double yawDegrees, float scale)
    {
        internal int Id { get; } = id;
        internal ulong ObjectId { get; } = objectId;
        internal CreatureStyleKind Kind { get; } = kind;
        internal string[] Joints { get; } = joints;
        internal (uint Root, uint Mid, uint End)[] Legs { get; } = [.. kind.Legs.Select(l =>
            ((uint)Array.IndexOf(joints, l.Root), (uint)Array.IndexOf(joints, l.Mid), (uint)Array.IndexOf(joints, l.End)))];
        internal Appearance Appearance { get; } = appearance;
        internal AnimationInstance Instance { get; } = instance;
        internal AnimationController? Controller { get; } = controller;
        internal double X { get; } = x;
        internal double Z { get; } = z;
        internal double YawDegrees { get; } = yawDegrees;
        internal float Scale { get; } = scale;
        internal required CreatureClip Clip { get; set; }
        internal double Seconds { get; set; }
        internal double ClipSeconds { get; set; }
        internal double ShownSeconds { get; set; } = -1;
        internal double WalkAngle { get; set; }
        internal ulong Tick { get; set; }
        internal Vector3 World { get; set; }
        internal double PitchDegrees { get; set; }
        internal Transform Placement { get; set; }
        internal bool Published { get; set; }
        internal bool Planted { get; set; }
        internal double FootCorrection { get; set; }
    }
}
