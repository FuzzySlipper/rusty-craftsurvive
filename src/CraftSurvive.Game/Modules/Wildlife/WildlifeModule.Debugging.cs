using System.Globalization;
using System.Numerics;
using System.Text;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// Creature behaviour debugging for herd animals (#10926): the wildlife owns its herds, so it places, finds, reads and holds
/// them; the creature tools select across hunters and herd animals and ask it. Herd animals are named <c>herd:&lt;id&gt;</c>
/// there, their ids being the wildlife's own.
/// </summary>
internal sealed partial class WildlifeModule
{
    /// <summary>The animal the player last killed, so a watch can tell a kill from a herd let go.</summary>
    private int? lastKilled;

    /// <summary>A test's herd finds each animal's floor from this far above the height asked for, and as far below it.</summary>
    private const double SpawnFloorReachMetres = 1.5d;

    /// <summary>The ways a herd placed by a test starts.</summary>
    internal static readonly string[] HerdStarts = ["default", "rest", "idle", "flee"];

    /// <summary>
    /// Test aid (#10926): places a herd of <paramref name="size"/> of a herd kind with its leader at a world point, each animal
    /// on the floor near that point's height (a cave floor too), in no population. It starts as its schedule has it
    /// (<c>default</c>), resting or grazing in place for the schedule's current period (<c>rest</c>, <c>idle</c>), or fleeing
    /// the player now (<c>flee</c>: pressed, it runs whatever its kind does). Returns the herd's readout and its animals' ids.
    /// </summary>
    internal (string Readout, IReadOnlyList<int> Ids) SpawnAt(string kindId, Vector3 at, int size, string start)
    {
        if (!started) return ("wildlife: not started", []);
        if (!Catalog.TryKind(kindId, out WildlifeKind kind)) return ($"no kind '{kindId}'; kinds: {string.Join(",", Catalog.Kinds.Select(k => k.Id))}", []);
        if (kind.Pressed == PressedResponse.Hunt) return ($"{kindId} hunts: the creature rules place it", []);
        if (!HerdStarts.Contains(start)) return ($"a herd starts {string.Join(", ", HerdStarts)}, not '{start}': herds do not hunt", []);
        Vector2 point = new(at.X, at.Z);
        if (Excluded(point)) return (string.Create(CultureInfo.InvariantCulture, $"({point.X:F0}, {point.Y:F0}) is in a town's sanctuary: no herd stands there"), []);
        size = size > 0 ? size : (kind.GroupMinimum + kind.GroupMaximum) / 2;
        if (awake.Count >= Catalog.Waking.MostAwakeHerds) Release(awake.OrderByDescending(herd => Vector2.Distance(herd.Leader.Position, Here)).First());
        WildlifeKind shown = start == "flee" ? kind with { Pressed = PressedResponse.Flee } : kind;
        WildlifeHerd herd = WakeAt(Ledger.Place(kind, point, size), shown, point, size, near: at.Y);
        switch (start)
        {
            case "rest":
                WildlifeRules.Begin(herd, WildlifeActivity.Rest, clock());
                break;
            case "idle":
                WildlifeRules.Begin(herd, WildlifeActivity.Graze, clock());
                break;
            case "flee":
                foreach (WildlifeMember member in herd.Members) member.Enter(Wariness.Respond);
                herd.Alarm = Wariness.Respond;
                herd.AlarmOrigin = Here;
                break;
        }

        return (Readout(herd), [.. herd.Members.Select(member => member.Id)]);
    }

    /// <summary>Every awake herd animal above water, for a test aid choosing one: its id, kind and feet.</summary>
    internal IEnumerable<(int Id, string Kind, Vector3 Feet)> HerdMembers() =>
        awake.SelectMany(herd => herd.Members.Where(member => !member.Under)
            .Select(member => (member.Id, herd.Kind.Id, new Vector3(member.Position.X, FeetOf(member), member.Position.Y))));

    /// <summary>An awake herd animal by id and kind: an id reused by another kind is not it.</summary>
    private (WildlifeHerd Herd, WildlifeMember Member)? FindMember(int id, string kind)
    {
        foreach (WildlifeHerd herd in awake.Where(herd => herd.Kind.Id == kind))
        {
            if (herd.Members.FirstOrDefault(member => member.Id == id) is WildlifeMember member) return (herd, member);
        }

        return null;
    }

    /// <summary>How a selected herd animal went, once it is no longer awake: killed by the player, or let go with its herd.</summary>
    internal string HowGone(int id) => lastKilled == id ? "killed by the player" : "released";

    /// <summary>One line naming a herd animal, or null when it is gone.</summary>
    internal string? DescribeMember(int id, string kind) => FindMember(id, kind) is (WildlifeHerd herd, WildlifeMember member)
        ? string.Create(CultureInfo.InvariantCulture,
            $"id=herd:{member.Id} kind={herd.Kind.Id} at={member.Position.X:F1},{FeetOf(member):F1},{member.Position.Y:F1} state={member.Stage}/{herd.Activity}")
        : null;

    /// <summary>
    /// Test aid (#10926): a herd animal whole - its herd and place in it; wariness, activity and alarm; what it senses and how
    /// it answers when pressed; where it is going and how it moves; its body; the clip it wants and why, and what its mesh
    /// plays; any hold.
    /// </summary>
    internal string InspectMember(int id, string kind)
    {
        if (FindMember(id, kind) is not (WildlifeHerd herd, WildlifeMember member)) return $"herd:{id} ({kind}) is gone";
        StringBuilder text = new();
        void Part(string name, string value) => text.Append(name).Append(": ").Append(value).Append(" | ");
        WildlifeKind k = herd.Kind;
        Part("creature", string.Create(CultureInfo.InvariantCulture,
            $"id=herd:{member.Id} kind={k.Id} herd={herd.Key} of {herd.Members.Count} leader={member == herd.Leader} young={member.Young} size={member.Size:F2} hp={member.Combat.Health}/{member.Combat.MaximumHealth} home={herd.Home.X:F0},{herd.Home.Y:F0}"));
        Part("state", string.Create(CultureInfo.InvariantCulture,
            $"{member.Stage} for {member.StageSeconds:F1}s activity={herd.Activity}{(herd.HeldActivity is null ? "" : " (held)")} herdAlarm={herd.Alarm} alarmFor={herd.AlarmSeconds:F1}s following={member.Following} regroupOrFlee={(member.Stage is Wariness.Respond or Wariness.Regroup)}"));
        Part("perception", string.Create(CultureInfo.InvariantCulture,
            $"d={member.PlayerDistance:F2} notice={k.Senses.NoticeMetres:F0}m act={k.Senses.ActMetres:F0}m pressed={k.Pressed} answers={WildlifeRules.Response(member, k)} struck={member.Struck}"));
        bool stepped = bodies.TryGetValue(member.Id, out MemberBody? body) && body.Body is not null;
        Part("route", string.Create(CultureInfo.InvariantCulture,
            $"goal={herd.Goal.X:F1},{herd.Goal.Y:F1} dGoal={Vector2.Distance(member.Position, herd.Goal):F1} dart={(member.DartGoal is Vector2 dart ? $"{dart.X:F1},{dart.Y:F1}" : "none")} moves={(stepped ? "body" : "glide")} toWater={member.ToWater} under={member.Under}"));
        Part("body", string.Create(CultureInfo.InvariantCulture,
            $"feet={member.Position.X:F2},{FeetOf(member):F2},{member.Position.Y:F2} speed={member.Velocity.Length():F2} yaw={Math.IEEERemainder(member.Yaw * 180 / Math.PI, 360):F0} lift={member.Lift:F2}"));
        Part("animation", k.Mesh is null
            ? $"no mesh (posed boxes) shows={member.Clip}"
            : $"wants={WildlifeClips.Want(member, herd, RigOf(k))} ({member.Stage}, {herd.Activity}, {(member.Velocity.Length() < 0.05f ? "standing" : "moving")}) shows={member.Clip} {BodyReadout(member)}");
        Part("override", member.Hold is WildlifeHold hold && !hold.Empty ? hold.Readout() + (herd.HeldActivity is WildlifeActivity a ? $",herd={a}" : "") : "none");
        return text.ToString().TrimEnd(' ', '|');
    }

    /// <summary>What a watch compares for a herd animal from one update to the next, with the facts that go with a change; null when it is gone.</summary>
    internal (string State, string Clip, string Route, bool Sees, bool Alerted, int Health, string Context)? MemberFacts(int id, string kind)
    {
        if (FindMember(id, kind) is not (WildlifeHerd herd, WildlifeMember member)) return null;
        bool stepped = bodies.TryGetValue(member.Id, out MemberBody? body) && body.Body is not null;
        return ($"{member.Stage}/{herd.Activity}", member.Clip.ToString(), stepped ? "body" : "glide",
            member.PlayerDistance <= herd.Kind.Senses.NoticeMetres, herd.Alarm >= Wariness.Look, member.Combat.Health,
            string.Create(CultureInfo.InvariantCulture,
                $"d={member.PlayerDistance:F1} notice={herd.Kind.Senses.NoticeMetres:F0}m act={herd.Kind.Senses.ActMetres:F0}m answers={WildlifeRules.Response(member, herd.Kind)} speed={member.Velocity.Length():F2}{(member.Hold is WildlifeHold hold && !hold.Empty ? $" override[{hold.Readout()}]" : "")}"));
    }

    /// <summary>A herd animal's label over its body, and where: id, kind, wariness and activity, clip, any hold.</summary>
    internal IEnumerable<(int Id, Vector3 At, string Text)> MemberLabels(Func<int, bool> wanted, float gapMetres) =>
        awake.SelectMany(herd => herd.Members.Where(member => !member.Under && wanted(member.Id)).Select(member => (member.Id,
            new Vector3(member.Position.X, FeetOf(member) + (float)(herd.Kind.Body.HeightMetres * member.Size) + gapMetres, member.Position.Y),
            $"herd:{member.Id} {herd.Kind.Id} {member.Stage}/{herd.Activity} {member.Clip}" + (member.Hold is WildlifeHold hold && !hold.Empty ? $" [{hold.Readout()}]" : ""))));

    /// <summary>
    /// Test aid (#10926): holds a herd animal - <c>state &lt;rest|idle|look|warn|flee&gt;</c> (rest holds its herd at rest
    /// too; flee runs from the player whatever its kind does), <c>clip &lt;role&gt;</c>, <c>goto &lt;x&gt;,&lt;z&gt;</c> (it walks
    /// there), <c>freeze</c> (no rules, no move, playback paused), <c>health &lt;n&gt;</c>, or <c>release</c>.
    /// </summary>
    internal string ForceMember(int id, string kind, string what, string value)
    {
        if (FindMember(id, kind) is not (WildlifeHerd herd, WildlifeMember member)) return $"herd:{id} ({kind}) is gone";
        WildlifeHold hold = member.Hold ?? new WildlifeHold();
        switch (what)
        {
            case "state" when value is "rest" or "idle" or "look" or "warn" or "flee":
                hold.Stage = value switch
                {
                    "look" => Wariness.Look,
                    "warn" => Wariness.Warn,
                    "flee" => Wariness.Respond,
                    _ => Wariness.Calm,
                };
                hold.Flee = value == "flee";
                herd.HeldActivity = value == "rest" ? WildlifeActivity.Rest : herd.HeldActivity;
                if (value == "flee") herd.AlarmOrigin = Here;
                break;
            case "clip" when Enum.TryParse(value, ignoreCase: true, out WildlifeClipRole role):
                hold.Clip = role;
                break;
            case "goto" when value.Split(',') is [string xs, string zs]
                && double.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(zs, NumberStyles.Float, CultureInfo.InvariantCulture, out double z):
                hold.GoTo = new Vector2((float)x, (float)z);
                break;
            case "freeze":
                hold.Frozen = true;
                break;
            case "health" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int health):
                member.Combat = member.Combat with { Health = Math.Clamp(health, 1, member.Combat.MaximumHealth) };
                return $"{DescribeMember(id, kind)} health set to {member.Combat.Health}/{member.Combat.MaximumHealth}";
            case "release":
                member.Hold = null;
                herd.HeldActivity = null;
                return $"released {DescribeMember(id, kind)}";
            default:
                return $"force {what} {value}: for a herd animal, state <rest|idle|look|warn|flee>, clip <role>, goto <x>,<z>, freeze, health <n> or release";
        }

        member.Hold = hold.Empty ? null : hold;
        return $"{DescribeMember(id, kind)} held: {hold.Readout()}{(herd.HeldActivity is WildlifeActivity a ? $"; its herd held to {a}" : "")}";
    }
}
