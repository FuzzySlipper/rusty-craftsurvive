using System.Globalization;
using System.Numerics;
using System.Text;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// Creature behaviour debugging (#10926): spawn any kind where it is wanted, select one creature, inspect it whole, watch
/// what it does and why, and override it. Everything here is a test aid: with nothing selected, watched or overridden the
/// creatures behave exactly as they would without it.
/// </summary>
internal sealed partial class CreatureModule
{
    /// <summary>A test aid's hold on one creature (#10926): a state, a clip, a point to go to, or everything frozen.</summary>
    private sealed class CreatureOverride(string kind)
    {
        /// <summary>The kind held: an id reused by another kind is not held.</summary>
        internal string Kind { get; } = kind;

        internal CreatureState? State { get; set; }
        internal WildlifeClipRole? Clip { get; set; }
        internal Vector3? GoTo { get; set; }
        internal bool Frozen { get; set; }

        internal bool Empty => State is null && Clip is null && GoTo is null && !Frozen;

        internal string Readout() => string.Join(",", new[]
        {
            State is CreatureState s ? $"state={s}" : null,
            Clip is WildlifeClipRole c ? $"clip={c}" : null,
            GoTo is Vector3 g ? string.Create(CultureInfo.InvariantCulture, $"goto={g.X:F1},{g.Z:F1}") : null,
            Frozen ? "frozen" : null,
        }.Where(part => part is not null));
    }

    private readonly Dictionary<int, CreatureOverride> overrides = [];

    /// <summary>The creature the player last defeated, so a watch can tell a kill from a creature let go.</summary>
    private Creature? lastDefeated;

    /// <summary>
    /// The creature selected for inspection: its id and kind as shown, and the creature itself, so one given the same id
    /// later is not taken for it (R10926-4). A herd animal's id is
    /// the wildlife's own, apart from the hunters' (shown as <c>herd:&lt;id&gt;</c>).
    /// </summary>
    private (int Id, string Kind, bool Herd, object Animal)? selected;

    /// <summary>What the senses gave this update, kept for inspection.</summary>
    private HashSet<int> lastSeeing = [], lastAlerted = [];

    /// <summary>How long a creature's watch lasts, in steps, and its log: each change with its update step and why.</summary>
    private long watchUntil;
    private readonly Queue<string> watchLog = new();
    private WatchFacts? watched;

    /// <summary>The most entries a watch keeps; the oldest go first.</summary>
    private const int MostWatchEntries = 400;

    /// <summary>How near a creature must stand to the view's line, and how far, to be the one looked at.</summary>
    private const double LookedAtDegrees = 12, LookedAtMetres = 60;

    /// <summary>What a watch compares from one update to the next.</summary>
    private sealed record WatchFacts(string State, string Clip, string Route, bool Sees, bool Alerted, int Health, bool Awake);

    // ---------------------------------------------------------------- spawn

    /// <summary>
    /// Test aid (#10926): spawns <paramref name="count"/> of any hunter or herd kind, in no population, where asked:
    /// <c>here</c>, <c>ahead:&lt;metres&gt;</c> (along the view, on the floor there), <c>at:&lt;x&gt;,&lt;y&gt;,&lt;z&gt;</c>
    /// (on the floor under that point, in a cave too) or <c>home:&lt;key&gt;</c> (a cave home's floor). A hunter starts
    /// <c>rest</c>, <c>idle</c>, <c>hunt</c> (alert to the player) or as its kind does (<c>default</c>); a herd starts
    /// <c>default</c>, <c>rest</c>, <c>idle</c> (grazing) or <c>flee</c> (running from the player now), each animal on the floor
    /// near the point's height, caves included. Returns the new creatures' ids (a herd animal's as <c>herd:&lt;id&gt;</c>).
    /// </summary>
    internal string Spawn(string kindId, int count, string where, string start)
    {
        if (Wildlife is null) return "no wildlife";
        if (Where(where) is not (Vector3 at, string placed)) return $"where '{where}' is not here, ahead:<metres>, at:<x>,<y>,<z> or home:<key>";
        count = Math.Clamp(count, 1, MostSpawned);
        if (Wildlife.HunterKinds.FirstOrDefault(kind => kind.Id == kindId) is WildlifeKind hunter)
        {
            HashSet<int> before = [.. roster.All.Select(creature => creature.Id)];
            if (PlayerSpace is not CreatureSpace space) return "no creatures live in the space the player is in";
            HerdRecord record = new($"{hunter.Id}@spawn#{++hunterRegions}", hunter.Id, new Vector2(at.X, at.Z), count) { Floor = at.Y };
            PlaceCaveHunter(record, hunter, at + Vector3.UnitY, count, space);
            int[] made = [.. roster.All.Select(creature => creature.Id).Where(id => !before.Contains(id))];
            CreatureState? state = start switch
            {
                "rest" => CreatureState.Resting,
                "idle" => CreatureState.Idle,
                "hunt" => CreatureState.Alert,
                _ => null,
            };
            foreach (int id in made)
            {
                if (state is CreatureState s && roster.TryGet(id, out Creature creature)) creature.Behavior = creature.Behavior with { State = s };
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"spawned {made.Length} of {count} {hunter.Id} {placed}, {(state is CreatureState shown ? shown.ToString() : "as its kind starts")}: ids=[{string.Join(",", made)}]{(made.Length < count ? $"; {lastEvent}" : "")}");
        }

        // Herds live in the walking session, its caves included; a separate space has none.
        if (PlayerSpace != walking) return $"{kindId} is a herd kind: herds live in the walking session (its caves too), not in {PlayerSpace?.Name ?? "no space"}";
        (string woken, IReadOnlyList<int> ids) = Wildlife.SpawnAt(kindId, at, count, start);
        return ids.Count == 0 ? woken : $"spawned {ids.Count} {kindId} {placed}, {start}: ids=[{string.Join(",", ids.Select(id => $"herd:{id}"))}]; {woken}";
    }

    /// <summary>The most creatures one spawn makes.</summary>
    private const int MostSpawned = 12;

    /// <summary>A spawn's place in world metres (on the floor there, looked for from a little above), and how it reads.</summary>
    private (Vector3 At, string Placed)? Where(string where)
    {
        Vector3 feet = player.WorldFeetPosition;
        string[] parts = where.Split(':', 2);
        switch (parts[0])
        {
            case "here":
                return (feet, "here");
            case "ahead" when parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double metres):
            {
                Vector3 forward = player.AimForward with { Y = 0 };
                forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : -Vector3.UnitZ;
                Vector3 point = feet + (forward * (float)metres);
                float floor = SpaceFloor(point.X, point.Z, feet.Y) ?? feet.Y;
                return (point with { Y = floor }, string.Create(CultureInfo.InvariantCulture, $"{metres:F1} m ahead"));
            }

            case "at" when parts.Length == 2 && parts[1].Split(',') is [string xs, string ys, string zs]
                && double.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(ys, NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                && double.TryParse(zs, NumberStyles.Float, CultureInfo.InvariantCulture, out double z):
            {
                float floor = SpaceFloor(x, z, y) ?? (float)y;
                return (new Vector3((float)x, floor, (float)z), string.Create(CultureInfo.InvariantCulture, $"at {x:F1},{floor:F1},{z:F1}"));
            }

            case "home" when parts.Length == 2 && Wildlife?.CaveHomes.FirstOrDefault(home => home.Key == parts[1]) is HerdRecord home:
            {
                float floor = SpaceFloor(home.Home.X, home.Home.Y, home.Floor ?? feet.Y) ?? (home.Floor ?? feet.Y);
                return (new Vector3(home.Home.X, floor, home.Home.Y), $"at {home.Key}");
            }

            default:
                return null;
        }
    }

    /// <summary>The floor under a point in the player's space, looked for from a little above it.</summary>
    private float? SpaceFloor(double x, double z, double near) =>
        (PlayerSpace ?? walking).FloorBelow(x, z, near + SpawnLookAboveMetres, SpawnLookAboveMetres * 2);

    private const double SpawnLookAboveMetres = 1.5;

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// Test aid (#10926): selects one creature for inspection, watching and overriding: by id, <c>nearest</c>,
    /// <c>nearest:&lt;kind&gt;</c>, or <c>looked-at</c> (the one nearest the view's line, within some degrees of it).
    /// </summary>
    internal string Select(string which)
    {
        Vector3 eye = player.WorldPosition;
        Vector2 at = new(eye.X, eye.Z);
        // Hunters and herd animals alike: which creature, and the point a label or a look aims at.
        List<Candidate> awake = [.. roster.All.Where(creature => creature.Awake)
            .Select(creature => new Candidate(creature.Id, creature.Kind.Name, false, creature, new Vector3(creature.Position.X, StandingAt(creature), creature.Position.Y)))];
        if (Wildlife is not null) awake.AddRange(Wildlife.HerdMembers().Select(member => new Candidate(member.Pick.Id, member.Pick.Kind, true, member.Pick.Animal, member.Feet)));
        Candidate? chosen = which switch
        {
            "nearest" => Nearest(awake),
            "looked-at" => LookedAt(awake),
            _ when which.StartsWith("nearest:", StringComparison.Ordinal) => Nearest(awake.Where(c => c.Kind == which[8..])),
            _ when which.StartsWith("herd:", StringComparison.Ordinal) && int.TryParse(which[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int member)
                => awake.Where(c => c.Herd && c.Id == member).Select(c => (Candidate?)c).FirstOrDefault(),
            _ when int.TryParse(which, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                => awake.Where(c => !c.Herd && c.Id == id).Select(c => (Candidate?)c).FirstOrDefault(),
            _ => null,
        };
        if (chosen is not Candidate pick) return $"no creature for '{which}' (id, herd:<id>, nearest, nearest:<kind>, looked-at)";
        selected = (pick.Id, pick.Kind, pick.Herd, pick.Animal);
        watched = null;
        return $"selected {Selected().Line}";

        Candidate? Nearest(IEnumerable<Candidate> among) =>
            among.Select(c => (Candidate?)c).MinBy(c => Vector2.Distance(new Vector2(c!.Value.Feet.X, c.Value.Feet.Z), at));
    }

    /// <summary>A creature a selection could take: its id and kind, whether it is a herd animal, the creature itself, its feet.</summary>
    private readonly record struct Candidate(int Id, string Kind, bool Herd, object Animal, Vector3 Feet);

    private Candidate? LookedAt(IEnumerable<Candidate> creatures)
    {
        Vector3 eye = player.WorldPosition + (Vector3.UnitY * 1.6f);
        Vector3 forward = player.AimForward;
        if (forward.LengthSquared() < 1e-6f) return null;
        forward = Vector3.Normalize(forward);
        return creatures
            .Select(c => (Creature: c, To: c.Feet + (Vector3.UnitY * 0.8f) - eye))
            .Where(c => c.To.Length() <= LookedAtMetres && c.To.Length() > 0.1f)
            .Select(c => (c.Creature, Degrees: Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(c.To), forward), -1, 1)) * 180 / Math.PI))
            .Where(c => c.Degrees <= LookedAtDegrees)
            .OrderBy(c => c.Degrees)
            .Select(c => (Candidate?)c.Creature)
            .FirstOrDefault();
    }

    /// <summary>The selected herd animal, as the wildlife takes it; null when a hunter or nothing is selected.</summary>
    private HerdPick? SelectedHerd => selected is (int id, string kind, true, object animal) ? new HerdPick(id, kind, animal) : null;

    /// <summary>
    /// The selected creature while it is still the one selected (a hunter, or null with <c>Herd</c> for a herd animal still
    /// awake), a line naming it, and why not otherwise.
    /// </summary>
    private (Creature? Creature, bool Herd, string Line, string Why) Selected()
    {
        if (selected is not (int id, string kind, bool herd, object animal)) return (null, false, "", "nothing selected: craft.creature.select <id|herd:id|nearest|nearest:kind|looked-at>");
        if (herd)
        {
            return Wildlife?.DescribeMember(new HerdPick(id, kind, animal)) is string line ? (null, true, line, "") : (null, false, "", $"creature herd:{id} ({kind}) is gone");
        }

        // The very creature selected: one given its id since, of any kind, is another (R10926-4).
        return !roster.TryGet(id, out Creature creature) || !ReferenceEquals(creature, animal) ? (null, false, "", $"creature {id} ({kind}) is gone")
            : (creature, false, Describe(creature), "");
    }

    /// <summary>Whether the selection names a creature still here.</summary>
    private static bool Present((Creature? Creature, bool Herd, string Line, string Why) chosen) => chosen.Creature is not null || chosen.Herd;

    private string Describe(Creature creature) => string.Create(CultureInfo.InvariantCulture,
        $"id={creature.Id} kind={creature.Kind.Name} at={creature.Position.X:F1},{StandingAt(creature):F1},{creature.Position.Y:F1} state={creature.Behavior.State}");

    // ---------------------------------------------------------------- inspection

    /// <summary>Test aid (#10926): the selected creature, all of it: kind and home, state and timers, perception, route, body and animation.</summary>
    internal string Inspect()
    {
        var chosen = Selected();
        if (chosen.Herd && SelectedHerd is HerdPick inspected) return Wildlife!.InspectMember(inspected);
        if (chosen.Creature is not Creature creature) return chosen.Why;
        Vector3 playerFeet = player.WorldFeetPosition;
        Vector3 feet = Feet(creature);
        double distance = CreatureSimulation.PlanarDistance(creature.Position, player.WorldPosition);
        BehaviorTuning tuning = creature.Kind.Tuning;
        hunters.TryGetValue(creature.Id, out Hunter? hunter);
        StringBuilder text = new();
        void Part(string name, string value) => text.Append(name).Append(": ").Append(value).Append(" | ");
        Part("creature", FormattableString.Invariant(
            $"id={creature.Id} kind={creature.Kind.Name} space={SpaceOf(creature).Name} awake={creature.Awake} hp={creature.Combat.Health}/{creature.Combat.MaximumHealth}")
            + (hunter is null ? " (no hunter record)" : FormattableString.Invariant($" home={hunter.Record.Key} floor={(hunter.Record.Floor is float f ? f.ToString("F1", CultureInfo.InvariantCulture) : "ground")} upwind={(hunter.Record.Upwind is Vector2 u ? HunterRules.HeadingDegrees(u).ToString("F0", CultureInfo.InvariantCulture) : "none")} pack={(hunter.Kind.Hunt?.Pack is null ? "no" : "yes")} side={hunter.Side} flankBlocked={hunter.FlankBlocked} ringBlocked={hunter.RingBlocked}")));
        CreatureBehaviorState behaviour = creature.Behavior;
        Part("state", FormattableString.Invariant(
            $"{behaviour.State} lastAttackTick={behaviour.LastAttackTick} canAttack={behaviour.CanAttack(step, tuning)} searchLeft={(behaviour.SearchUntilTick < 0 ? "none" : $"{Math.Max(0, behaviour.SearchUntilTick - step) / HunterRules.StepsPerSecond:F1}s")}")
            + (hunter is null ? "" : FormattableString.Invariant($" display={hunter.ThreatSeconds:F2}s threatened={hunter.Threatened} bite={hunter.BiteSeconds:F2}s blowIn={(hunter.Blow is null ? "none" : $"{hunter.BlowInSeconds:F2}s")} hurt={hunter.HurtSeconds:F2}s alarm={(packAlarms.TryGetValue(hunter.Record.Key, out double alarm) ? $"{alarm:F1}s" : "none")}")));
        Part("perception", string.Create(CultureInfo.InvariantCulture,
            $"sees={lastSeeing.Contains(creature.Id)} alerted={lastAlerted.Contains(creature.Id)} d={distance:F2} sight={tuning.SightRange:F0}m x{SightScale(creature, lastAlerted):F2} wake={tuning.WakeRange:F0}m x{WakeShare(hunter, creature, player.WorldPosition):F2} reach={tuning.AttackRange:F1}m unseenAid={step < unseenUntil}"));
        Part("route", string.Create(CultureInfo.InvariantCulture,
            $"outcome={creature.RouteOutcome} waypoint={(creature.Waypoint is Vector2 w ? $"{w.X:F1},{w.Y:F1}" : "none")} path={(creature.Path is Vector2[] p ? $"{creature.PathIndex}/{p.Length}" : "none")} stuck={creature.StuckSteps} backoff={Math.Max(0, creature.BackoffUntil - step)} lastSeen={(creature.LastSeen is Vector3 seen ? $"{seen.X:F1},{seen.Y:F1},{seen.Z:F1}" : "none")} grid={NavigationOf(creature).GridId} why={NavigationOf(creature).Diagnose(feet)}"));
        if (hunter is not null)
        {
            WildlifeMember member = hunter.Member;
            Vector2 toPlayer = new Vector2(playerFeet.X, playerFeet.Z) - creature.Position;
            Part("body", string.Create(CultureInfo.InvariantCulture,
                $"feet={feet.X:F2},{feet.Y:F2},{feet.Z:F2} standing={StandingAt(creature):F2} lift={member.Lift:F2} ceiling={(hunter.CeilingLift is float c ? c.ToString("F1", CultureInfo.InvariantCulture) : "none")} hangs={hunter.Hangs} speed={member.Velocity.Length():F2} yaw={Degrees(member.Yaw):F0} toPlayer={HunterRules.HeadingDegrees(toPlayer):F0}"));
            WildlifeRig rig = Wildlife!.RigOf(hunter.Kind);
            double runsAbove = (rig.Clip(WildlifeClipRole.Walk).Speed + rig.Clip(WildlifeClipRole.Run).Speed) / 2 * member.Size;
            (WildlifeClipRole want, string why) = HunterRules.WantWhy(hunter, behaviour.State, member.Velocity.Length(), runsAbove);
            Part("animation", $"wants={want} ({why}) shows={member.Clip} {Wildlife.BodyReadout(member)}");
        }

        Part("override", overrides.TryGetValue(creature.Id, out CreatureOverride? held) && !held.Empty ? held.Readout() : "none");
        return text.ToString().TrimEnd(' ', '|');
    }

    private static double Degrees(float radians) => Math.IEEERemainder(radians * 180 / Math.PI, 360);

    // ---------------------------------------------------------------- watching

    /// <summary>
    /// Test aid (#10926): watches the selected creature for some seconds (0 stops), logging each change of its state, clip,
    /// route outcome, sight, alarm, health or wakefulness at the update it happened, with the facts that went with it.
    /// <c>read</c> gives the log so far.
    /// </summary>
    internal string Watch(string seconds)
    {
        if (seconds == "read") return watchLog.Count == 0 ? "nothing logged" : string.Join(" | ", watchLog);
        if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double length)) return "watch <seconds> or watch read";
        var chosen = Selected();
        if (!Present(chosen)) return chosen.Why;
        watchUntil = length <= 0 ? 0 : step + (long)Math.Ceiling(length * HunterRules.StepsPerSecond);
        watchLog.Clear();
        watched = null;
        return length <= 0 ? "watch stopped" : string.Create(CultureInfo.InvariantCulture, $"watching {chosen.Line} until step {watchUntil}");
    }

    /// <summary>Logs what changed for the watched creature this update.</summary>
    private void WatchUpdate()
    {
        if (step >= watchUntil) return;
        var chosen = Selected();
        if (!Present(chosen))
        {
            // Its end is logged once: killed (its last health seen), or let go with its space, its herd or the encounter.
            if (watched is WatchFacts last && selected is (_, _, bool herd, object animal))
            {
                string how = herd ? Wildlife!.HowGone(SelectedHerd!.Value) : ReferenceEquals(lastDefeated, animal) ? "killed by the player" : "released";
                watchLog.Enqueue(string.Create(CultureInfo.InvariantCulture, $"step {step}: gone - {how} (last {last.State}, hp {last.Health}; {chosen.Why})"));
                watched = null;
            }

            return;
        }

        WatchFacts now;
        string context;
        if (chosen.Creature is Creature creature)
        {
            hunters.TryGetValue(creature.Id, out Hunter? hunter);
            now = new(creature.Behavior.State.ToString(), hunter?.Member.Clip.ToString() ?? "none", creature.RouteOutcome, lastSeeing.Contains(creature.Id), lastAlerted.Contains(creature.Id), creature.Combat.Health, creature.Awake);
            double distance = CreatureSimulation.PlanarDistance(creature.Position, player.WorldPosition);
            context = string.Create(CultureInfo.InvariantCulture,
                $"d={distance:F1} sees={now.Sees} wake={creature.Kind.Tuning.WakeRange * WakeShare(hunter, creature, player.WorldPosition):F1}m sight={creature.Kind.Tuning.SightRange * SightScale(creature, lastAlerted):F0}m{(hunter is not null && packAlarms.ContainsKey(hunter.Record.Key) ? " packAlarm" : "")}{(overrides.TryGetValue(creature.Id, out CreatureOverride? held) && !held.Empty ? $" override[{held.Readout()}]" : "")}");
        }
        else if (SelectedHerd is HerdPick watching && Wildlife!.MemberFacts(watching) is var (state, clip, route, sees, alerted, health, facts))
        {
            now = new(state, clip, route, sees, alerted, health, true);
            context = facts;
        }
        else
        {
            return;
        }

        if (watched is WatchFacts was && was != now)
        {
            List<string> changes = [];
            if (was.State != now.State) changes.Add($"state {was.State}->{now.State}");
            if (was.Clip != now.Clip) changes.Add($"clip {was.Clip}->{now.Clip}");
            if (was.Route != now.Route) changes.Add($"route {was.Route}->{now.Route}");
            if (was.Sees != now.Sees) changes.Add(now.Sees ? "saw the player" : "lost sight of the player");
            if (was.Alerted != now.Alerted) changes.Add(now.Alerted ? "alerted" : "no longer alerted");
            if (was.Health != now.Health) changes.Add($"hp {was.Health}->{now.Health}");
            if (was.Awake != now.Awake) changes.Add(now.Awake ? "awake" : "asleep (its floor let go)");
            watchLog.Enqueue(string.Create(CultureInfo.InvariantCulture, $"step {step}: {string.Join(", ", changes)} ({context})"));
            while (watchLog.Count > MostWatchEntries) watchLog.Dequeue();
        }

        watched = now;
    }

    // ---------------------------------------------------------------- overrides

    /// <summary>
    /// Test aid (#10926): holds the selected creature: <c>state &lt;rest|idle|alert|pursue|search|attack&gt;</c>,
    /// <c>clip &lt;role&gt;</c>, <c>goto &lt;x&gt;,&lt;z&gt;</c> (it walks there as a search does), <c>freeze</c> (no rules, no move,
    /// its clip held), <c>health &lt;n&gt;</c>, or <c>release</c> (everything let go). Shown in its inspection and watch.
    /// </summary>
    internal string Force(string what, string value)
    {
        var chosen = Selected();
        if (chosen.Herd && SelectedHerd is HerdPick forced) return Wildlife!.ForceMember(forced, what, value);
        if (chosen.Creature is not Creature creature) return chosen.Why;
        CreatureOverride held = overrides.TryGetValue(creature.Id, out CreatureOverride? kept) && kept.Kind == creature.Kind.Name
            ? kept
            : overrides[creature.Id] = new CreatureOverride(creature.Kind.Name);
        hunters.TryGetValue(creature.Id, out Hunter? hunter);
        switch (what)
        {
            case "state" when ParseState(value) is CreatureState state:
                held.State = state;
                break;
            case "clip" when Enum.TryParse(value, ignoreCase: true, out WildlifeClipRole role):
                held.Clip = role;
                break;
            case "goto" when value.Split(',') is [string xs, string zs]
                && double.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(zs, NumberStyles.Float, CultureInfo.InvariantCulture, out double z):
                held.GoTo = new Vector3((float)x, SpaceFloor(x, z, StandingAt(creature)) ?? StandingAt(creature), (float)z);
                break;
            case "freeze":
                held.Frozen = true;
                // Its body is held too: the mesh pauses its playback and keeps its seat and feet (R10926-3).
                if (hunter is not null) hunter.Member.Hold = new WildlifeHold { Frozen = true };
                break;
            case "health" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int health):
                creature.Combat = creature.Combat with { Health = Math.Clamp(health, 1, creature.Combat.MaximumHealth) };
                if (held.Empty) overrides.Remove(creature.Id);
                return $"{Describe(creature)} health set to {creature.Combat.Health}/{creature.Combat.MaximumHealth}";
            case "release":
                overrides.Remove(creature.Id);
                if (hunter is not null) hunter.Member.Hold = null;
                // A creature sent somewhere searched without end: its search ends now, and the rules take it from here.
                if (creature.Behavior.SearchUntilTick == long.MaxValue) creature.Behavior = creature.Behavior with { SearchUntilTick = step };
                return $"released {Describe(creature)}";
            default:
                return $"force {what} {value}: for a hunter, state <rest|idle|alert|pursue|search|attack> (hunters do not flee: a herd animal does), clip <role>, goto <x>,<z>, freeze, health <n> or release";
        }

        if (held.Empty) overrides.Remove(creature.Id);
        return $"{Describe(creature)} held: {held.Readout()}";
    }

    private static CreatureState? ParseState(string name) => name switch
    {
        "rest" => CreatureState.Resting,
        "idle" => CreatureState.Idle,
        "alert" => CreatureState.Alert,
        "pursue" => CreatureState.Pursuing,
        "search" => CreatureState.Searching,
        "attack" => CreatureState.Attacking,
        _ => null,
    };

    /// <summary>Whether a test aid has frozen the creature: no rules, no move, its clip held.</summary>
    private bool Frozen(Creature creature) => overrides.TryGetValue(creature.Id, out CreatureOverride? held) && held.Frozen && held.Kind == creature.Kind.Name;

    /// <summary>The state a test aid holds the creature in; a creature sent somewhere searches its way there.</summary>
    private CreatureState? ForcedState(Creature creature) => overrides.TryGetValue(creature.Id, out CreatureOverride? held) && held.Kind == creature.Kind.Name
        ? held.State ?? (held.GoTo is null ? null : CreatureState.Searching)
        : null;

    /// <summary>Before routing: a creature sent somewhere looks for it as a searcher looks for where it last saw the player.</summary>
    private void SteerOverrides()
    {
        // A hold outlives its creature only until the id goes or is taken by another kind.
        foreach (int gone in overrides.Where(entry => !roster.TryGet(entry.Key, out Creature c) || c.Kind.Name != entry.Value.Kind).Select(entry => entry.Key).ToArray())
            overrides.Remove(gone);
        foreach ((int id, CreatureOverride held) in overrides)
        {
            if (held.GoTo is Vector3 to && roster.TryGet(id, out Creature creature) && creature.Kind.Name == held.Kind)
            {
                creature.LastSeen = to;
                creature.Behavior = creature.Behavior with { State = CreatureState.Searching, SearchUntilTick = long.MaxValue };
            }
        }
    }

    /// <summary>After its clip is chosen: a held clip wins.</summary>
    private void HoldClip(Creature creature, Hunter hunter)
    {
        if (overrides.TryGetValue(creature.Id, out CreatureOverride? held) && held.Clip is WildlifeClipRole clip) hunter.Member.Clip = clip;
    }

    // ---------------------------------------------------------------- labels

    /// <summary>Whether creatures carry labels on the Engine's inspection overlay: off, the selected one, or all awake.</summary>
    private string labels = "off";

    private const string LabelSet = "craft.creatures";

    /// <summary>
    /// Test aid (#10926): labels over creatures on the Engine's inspection overlay (shown while <c>engine.inspect.overlay
    /// show</c> is on, in headless snapshots too): <c>off</c>, <c>selected</c> or <c>all</c>. Each says its id, state, clip
    /// and route outcome.
    /// </summary>
    /// <summary>A test aid (#10956): the look shell-wearing creatures made from now on wear.</summary>
    internal string Look(string look) => Wildlife is null ? "no wildlife" : Wildlife.ForceWornLook(look);

    internal string Labels(string mode)
    {
        if (mode is not ("off" or "selected" or "all")) return "labels off|selected|all";
        labels = mode;
        if (mode == "off") engine.Graphics.PublishOverlaySet(new OverlaySetRequest(LabelSet, [], []));
        return $"creature labels {mode}; the Engine draws them while engine.inspect.overlay show is on";
    }

    private static readonly Color LabelColour = new(1f, 0.9f, 0.3f, 1f);
    private static readonly Color SelectedColour = new(0.3f, 1f, 0.5f, 1f);

    /// <summary>The labels for this update, in the player's space.</summary>
    private void PublishLabels(CreatureSpace? here)
    {
        if (labels == "off" || here is null) return;
        object? chosenAnimal = selected?.Animal;
        int? chosen = selected is (int id, _, false, _) && Selected().Creature is not null ? id : null;
        List<OverlayItem> items = [];
        foreach (Creature creature in roster.All.Where(c => c.Awake && SpaceOf(c) == here && (labels == "all" || c.Id == chosen)))
        {
            hunters.TryGetValue(creature.Id, out Hunter? hunter);
            float above = hunter is null ? LabelAboveMetres : (float)hunter.Kind.Body.HeightMetres + LabelGapMetres + hunter.Member.Lift;
            Vector3 at = here.ToLocal(creature.Position.X, StandingAt(creature) + above, creature.Position.Y);
            string text = $"{creature.Id} {creature.Kind.Name} {creature.Behavior.State} {hunter?.Member.Clip.ToString() ?? ""} {creature.RouteOutcome}"
                + (overrides.TryGetValue(creature.Id, out CreatureOverride? held) && !held.Empty ? $" [{held.Readout()}]" : "");
            items.Add(OverlayItem.Label(new OverlayPlace(OverlaySpace.World, 0, 0), at, text, creature.Id == chosen ? SelectedColour : LabelColour, true));
        }

        // Herd animals live in the walking session (#10926).
        if (Wildlife is not null && here == walking)
        {
            foreach ((object animal, Vector3 at, string text) in Wildlife.MemberLabels(animal => labels == "all" || ReferenceEquals(animal, chosenAnimal), LabelGapMetres))
            {
                items.Add(OverlayItem.Label(new OverlayPlace(OverlaySpace.World, 0, 0), here.ToLocal(at.X, at.Y, at.Z), text, ReferenceEquals(animal, chosenAnimal) ? SelectedColour : LabelColour, true));
            }
        }

        engine.Graphics.PublishOverlaySet(new OverlaySetRequest(LabelSet, [.. items], []));
    }

    private const float LabelAboveMetres = 2f, LabelGapMetres = 0.4f;
}
