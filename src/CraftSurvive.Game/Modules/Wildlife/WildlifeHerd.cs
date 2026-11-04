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

    /// <summary>Struck since the last decision: it answers as its kind does when pressed.</summary>
    internal bool Struck { get; set; }

    internal void Enter(Wariness stage)
    {
        Stage = stage;
        StageSeconds = 0;
    }
}

/// <summary>An animal that has been killed, left where it fell for a while: it holds its id until it goes.</summary>
internal sealed class WildlifeFallen(WildlifeKind kind, WildlifeMember member)
{
    internal WildlifeKind Kind { get; } = kind;

    internal WildlifeMember Member { get; } = member;

    internal double Seconds { get; set; }
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
