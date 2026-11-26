using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// How wary an animal is of the player, in rising order: calm or regrouping after a scare, looking,
/// warning, responding as its kind does when pressed.
/// </summary>
internal enum Wariness
{
    Calm,
    Regroup,
    Look,
    Warn,
    Respond,
}

/// <summary>One animal of an awake herd: where it stands, where it keeps to, and how wary it is.</summary>
internal sealed class WildlifeMember
{
    internal WildlifeMember(int id, Vector2 position, Vector2 slot, CombatantState combat)
    {
        Id = id;
        Position = position;
        Slot = slot;
        Combat = combat;
    }

    /// <summary>Unique among awake animals; the Engine-side body and appearance are keyed by it.</summary>
    internal int Id { get; }

    /// <summary>World X and Z.</summary>
    internal Vector2 Position { get; set; }

    /// <summary>Where it keeps to beside the leader, as an offset from the leader's position.</summary>
    internal Vector2 Slot { get; set; }

    /// <summary>Its facing: 0 faces north (-Z), a quarter turn east (+X).</summary>
    internal float Yaw { get; set; }

    internal Wariness Stage { get; set; }

    /// <summary>How long it has been at its stage.</summary>
    internal double StageSeconds { get; set; }

    /// <summary>How far it stood from the player when last sensed; infinity when there was no player.</summary>
    internal double PlayerDistance { get; set; } = double.PositiveInfinity;

    /// <summary>The planar velocity the rules decided this update; the product moves its body by it.</summary>
    internal Vector2 Velocity { get; set; }

    internal CombatantState Combat { get; set; }

    /// <summary>A young animal (#10600): smaller and weaker, darting about an adult rather than keeping a slot.</summary>
    internal bool Young { get; init; }

    /// <summary>Its own size against its kind's, a little either side of 1 (#10601).</summary>
    internal float Size { get; init; } = 1;

    /// <summary>Its own coat as red, green and blue factors over the paint, a little either side of 1.</summary>
    internal Vector3 Coat { get; init; } = Vector3.One;

    /// <summary>Where a young animal is darting to, and how long it stands or runs for before it picks again.</summary>
    internal Vector2? DartGoal { get; set; }

    internal double DartSeconds { get; set; }

    /// <summary>How many darts it has made: each new one is drawn afresh.</summary>
    internal int DartSerial { get; set; }

    /// <summary>Whether it is walking back to its slot: it leaves only when well out of place and stops only when there.</summary>
    internal bool Following { get; set; }

    /// <summary>The clip its body shows, and how long it has shown it.</summary>
    internal WildlifeClipRole Clip { get; set; } = WildlifeClipRole.Idle;

    internal double ClipSeconds { get; set; }

    /// <summary>How far over its feet its body is drawn, in metres: a drop-sloth hanging from the ceiling or falling from it (#10877).</summary>
    internal float Lift { get; set; }

    /// <summary>
    /// The look its rules have it wear now, over the one its body picked (#10961): a brood bearer's pods, split once its
    /// threat has released its brood. Null wears its own.
    /// </summary>
    internal string? WornLook { get; set; }

    /// <summary>
    /// A clip its rules name over the one its role plays (#10963): a fixture's hold, its recovery, a vessel's count of
    /// spent vessels. Null plays the role's.
    /// </summary>
    internal string? NamedClip { get; set; }

    /// <summary>Joints its rules have it lose (#10963: a vault filter's cut lobes), drawn shrunk to nothing.</summary>
    internal IReadOnlyList<string> Lost { get; set; } = [];

    /// <summary>
    /// How much stolen light its held piece's light holds (#10966: an implement-carrier's core while it drinks the lantern),
    /// 0 to 1: at 1 it burns as bright as in its threat. Null while no drink holds it, and then its clip decides
    /// (<see cref="CoreGlow"/>).
    /// </summary>
    internal float? HeldGlow { get; set; }

    /// <summary>
    /// How bright a held piece's light burns (#10957, #10966), 0 to 1 of the way to its threat's share: what a drink holds
    /// while one does, so a core brightens through the raise with the lantern it drinks, and otherwise full in its threat;
    /// a strike always flares it.
    /// </summary>
    internal static float CoreGlow(WildlifeClipRole? playing, float? held) => playing switch
    {
        WildlifeClipRole.Bite => 1f,
        WildlifeClipRole.Threat => held is float drink ? Math.Clamp(drink, 0f, 1f) : 1f,
        _ => Math.Clamp(held ?? 0f, 0f, 1f),
    };

    /// <summary>Making for the water: having set off, it goes in, however clear of the player it has got.</summary>
    internal bool ToWater { get; set; }

    /// <summary>How long since it began to slip into the water; null while it is on land.</summary>
    internal double? DiveSeconds { get; set; }

    /// <summary>Gone under: not drawn, and out of reach, until it comes up where it went in.</summary>
    internal bool Under { get; set; }

    /// <summary>How much of a hop onto a ledge is left, and how long since the last.</summary>
    internal double HopSeconds { get; set; }

    internal double SinceHop { get; set; } = double.PositiveInfinity;

    /// <summary>Struck since the last decision: it answers as its kind does when pressed.</summary>
    internal bool Struck { get; set; }

    /// <summary>
    /// Test aid (#10926): what a live-debug override holds this animal to - a wariness, a run from the player, a point to
    /// walk to, a clip, or everything frozen (no rules, no move, its playback paused). Null for every animal the rules alone
    /// drive, which is every animal outside a test.
    /// </summary>
    internal WildlifeHold? Hold { get; set; }

    internal void Enter(Wariness stage)
    {
        Stage = stage;
        StageSeconds = 0;
    }
}

/// <summary>An animal that has been killed, left where it fell for a while: it holds its id until it goes.</summary>
internal sealed class WildlifeFallen(WildlifeKind kind, WildlifeMember member, Vector2 blow)
{
    internal WildlifeKind Kind { get; } = kind;

    internal WildlifeMember Member { get; } = member;

    /// <summary>The way the killing blow drove it, as a unit along the ground.</summary>
    internal Vector2 Blow { get; } = blow;

    internal double Seconds { get; set; }

    /// <summary>The space it lies in (#10876): its position is that space's world metres.</summary>
    internal Creatures.ICreatureGround? Space { get; init; }
}

/// <summary>
/// An awake herd: its kind, its home range, the day it is keeping, its alarm and its members, the
/// first of which leads. A herd is woken from its <see cref="HerdRecord"/> and forgotten when it
/// sleeps; only the record (how many are left) outlives it.
/// </summary>
internal sealed class WildlifeHerd
{
    internal WildlifeHerd(HerdRecord record, WildlifeKind kind, IEnumerable<WildlifeMember> members)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        Members = [.. members];
    }

    internal HerdRecord Record { get; }

    internal string Key => Record.Key;

    internal WildlifeKind Kind { get; }

    internal Vector2 Home => Record.Home;

    internal List<WildlifeMember> Members { get; }

    internal WildlifeMember Leader => Members[0];

    /// <summary>The adult nearest a point; the leader, who is always grown, when the others have gone.</summary>
    internal WildlifeMember NearestAdult(Vector2 point)
    {
        WildlifeMember nearest = Leader;
        float best = float.MaxValue;
        foreach (WildlifeMember member in Members)
        {
            float distance = Vector2.DistanceSquared(member.Position, point);
            if (member.Young || distance >= best) continue;
            best = distance;
            nearest = member;
        }

        return nearest;
    }

    /// <summary>The step of the day being kept (day × steps a day + step); -1 before the first decision.</summary>
    internal long Period { get; set; } = -1;

    internal WildlifeActivity Activity { get; set; }

    /// <summary>Where the leader is heading for the activity.</summary>
    internal Vector2 Goal { get; set; }

    /// <summary>How many goals the activity has reached; each new one is drawn afresh.</summary>
    internal int GoalSerial { get; set; }

    /// <summary>How long the herd has been grazing, which walks it on and stands it to feed in turn.</summary>
    internal double GrazeSeconds { get; set; }

    /// <summary>The highest stage the alarm has reached, where it was raised, and how long it has been spreading.</summary>
    internal Wariness Alarm { get; set; }

    internal Vector2 AlarmOrigin { get; set; }

    internal double AlarmSeconds { get; set; }

    /// <summary>Which way the herd bolts when it flees: away from the player from where it stands.</summary>
    internal Vector2 FleeHeading { get; set; }

    internal Vector2 Centre
    {
        get
        {
            Vector2 sum = Vector2.Zero;
            foreach (WildlifeMember member in Members) sum += member.Position;
            return Members.Count == 0 ? Home : sum / Members.Count;
        }
    }

    /// <summary>
    /// Takes a member out (killed). When the leader goes the next one leads, and every slot is
    /// re-based on it, so the herd keeps its shape around the new leader.
    /// </summary>
    internal void Remove(WildlifeMember member)
    {
        bool leading = Members.Count > 0 && Members[0] == member;
        Members.Remove(member);
        if (!leading || Members.Count == 0) return;
        // The next grown animal leads; only an orphan's herd is led by the young.
        int next = Math.Max(0, Members.FindIndex(other => !other.Young));
        (Members[0], Members[next]) = (Members[next], Members[0]);
        Vector2 offset = Members[0].Slot;
        foreach (WildlifeMember other in Members) other.Slot -= offset;
    }
}

/// <summary>
/// A live-debug override's hold on one animal (#10926). The herd rules, the clip choice and the mesh body each read it, so a
/// held animal does what it is held to and says so; nothing reads it for an animal without one.
/// </summary>
internal sealed class WildlifeHold
{
    /// <summary>The wariness it stays at, whatever its senses say.</summary>
    internal Wariness? Stage { get; set; }

    /// <summary>Pressed, it runs from the player whatever its kind does.</summary>
    internal bool Flee { get; set; }

    /// <summary>It rests where it stands, as at its herd's rest, while its herd keeps to its own day (R10926-5).</summary>
    internal bool Rest { get; set; }

    /// <summary>A point it walks to, and stands at once there.</summary>
    internal Vector2? GoTo { get; set; }

    /// <summary>The clip it plays, whatever its state calls for.</summary>
    internal WildlifeClipRole? Clip { get; set; }

    /// <summary>No rules, no move, its clip's playback paused and its pose held.</summary>
    internal bool Frozen { get; set; }

    internal bool Empty => Stage is null && !Flee && !Rest && GoTo is null && Clip is null && !Frozen;

    internal string Readout() => string.Join(",", new[]
    {
        Stage is Wariness stage ? $"stage={stage}" : null,
        Flee ? "flee" : null,
        Rest ? "rest" : null,
        GoTo is Vector2 to ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"goto={to.X:F1},{to.Y:F1}") : null,
        Clip is WildlifeClipRole clip ? $"clip={clip}" : null,
        Frozen ? "frozen" : null,
    }.Where(part => part is not null));
}

/// <summary>
/// A herd animal a test aid picked (#10926): its id and kind as shown, and the animal itself, so an id given out again to
/// another animal is never taken for it (R10926-4).
/// </summary>
internal readonly record struct HerdPick(int Id, string Kind, object Animal)
{
    /// <summary>The very animal picked, with its herd, while it is awake among these herds; null once it is gone.</summary>
    internal static (WildlifeHerd Herd, WildlifeMember Member)? Find(IEnumerable<WildlifeHerd> herds, HerdPick pick)
    {
        foreach (WildlifeHerd herd in herds.Where(herd => herd.Kind.Id == pick.Kind))
        {
            if (herd.Members.FirstOrDefault(member => ReferenceEquals(member, pick.Animal)) is WildlifeMember member) return (herd, member);
        }

        return null;
    }
}
