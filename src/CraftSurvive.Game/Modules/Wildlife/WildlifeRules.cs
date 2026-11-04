using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Sky;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>What a herd reads of the world each update.</summary>
/// <param name="Player">The player's planar position (world X, Z), or null when there is no player to sense (down, or away in a dungeon).</param>
/// <param name="Water">Where the herd drinks, if its range has water the product found; without it, drinking is grazing.</param>
/// <param name="Allowed">Ground a herd may head for (the product's town sanctuary keeps them out); null allows all.</param>
internal readonly record struct HerdFacts(Vector2? Player, WorldTime Time, ulong Seed, Vector2? Water = null, Func<Vector2, bool>? Allowed = null);

/// <summary>
/// Wildlife behaviour as Engine-free rules (#10567), beside <see cref="CreatureBehaviorRules"/>: the
/// product reads facts, these rules decide every member's stage and velocity, and the product
/// applies the velocities to bodies and publishes appearances.
///
/// - **Day:** a herd keeps its kind's schedule (rest, graze, drift, drink). The leader heads for a
///   goal drawn in the home range from the world seed, so a herd's day is the same each time it is
///   woken; the others keep their slots beside the leader and their spacing from each other.
/// - **Wariness:** each animal notices the player at its kind's notice distance and looks; inside
///   the act distance, after looking, it warns; still pressed after warning, it responds: flees,
///   stands, charges or hunkers. A struck animal responds at once.
/// - **Alarm:** while any animal looks, the leader waits, so the herd keeps together under watch. A
///   warning or response spreads through the herd at a pace from where it was raised.
///   A charging kind's partners display rather than charge. The herd calms only when its nearest
///   animal is clear of the player, and a scattered herd regroups on the leader before calming.
/// </summary>
internal static class WildlifeRules
{
    /// <summary>Draws in [0, 1) are taken at this resolution.</summary>
    private const int DrawScale = 1_000_000;

    /// <summary>A planar speed below this is standing still.</summary>
    private const float StillSpeed = 1e-3f;

    /// <summary>Slots ring out from the leader at this share of the spacing times the square root of their order.</summary>
    private const double SlotRingShare = 0.75;

    /// <summary>The golden angle: successive slots turn by it, so a herd of any size is evenly spread.</summary>
    private static readonly double GoldenAngle = Math.PI * (3 - Math.Sqrt(5));

    /// <summary>A straggling animal is pulled back toward its herd's centre at this share of its distance a second while it flees.</summary>
    private const float FleeCohesionShare = 0.2f;

    /// <summary>Decides one update for an awake herd.</summary>
    internal static void Decide(WildlifeHerd herd, HerdFacts facts, WildlifeCatalog catalog, double seconds)
    {
        ArgumentNullException.ThrowIfNull(herd);
        ArgumentNullException.ThrowIfNull(catalog);
        if (herd.Members.Count == 0) return;
        WildlifeBehaviour behaviour = catalog.Behaviour;

        // Read: how far each animal stands from the player, and how far the nearest does.
        double nearest = double.PositiveInfinity;
        foreach (WildlifeMember member in herd.Members)
        {
            member.PlayerDistance = facts.Player is Vector2 player ? Vector2.Distance(member.Position, player) : double.PositiveInfinity;
            nearest = Math.Min(nearest, member.PlayerDistance);
        }

        foreach (WildlifeMember member in herd.Members)
        {
            Sense(member, herd.Kind, behaviour, nearest, seconds);
        }

        Spread(herd, behaviour, seconds);
        if (facts.Player is Vector2 from)
        {
            Vector2 away = herd.Centre - from;
            herd.FleeHeading = away.LengthSquared() > 0 ? Vector2.Normalize(away) : Facing(herd.Leader.Yaw);
        }

        Plan(herd, facts, catalog);
        Steer(herd, facts, behaviour, seconds);
    }

    /// <summary>
    /// Wakes a herd of <paramref name="count"/> animals with its leader at a point and the rest in
    /// their slots, calm and fresh; each takes the next id.
    /// </summary>
    internal static WildlifeHerd Wake(HerdRecord record, WildlifeKind kind, WildlifeCatalog catalog, ulong seed, Vector2 at, int count, Func<int> nextId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(nextId);
        return new WildlifeHerd(record, kind, Enumerable.Range(0, count).Select(order =>
        {
            Vector2 slot = Slot(order, kind, catalog.Behaviour, seed, record.Key);
            return new WildlifeMember(nextId(), at + slot, slot, CombatantState.Fresh(kind.MaximumHealth, kind.Defence))
            {
                Yaw = (float)(2 * Math.PI * Unit(seed, string.Create(CultureInfo.InvariantCulture, $"wildlife:{record.Key}:yaw:{order}"))),
            };
        }));
    }

    /// <summary>Moves every member by its decided velocity: the rule lane's stand-in for the product's bodies.</summary>
    internal static void Integrate(WildlifeHerd herd, double seconds)
    {
        foreach (WildlifeMember member in herd.Members)
        {
            member.Position += member.Velocity * (float)seconds;
        }
    }

    /// <summary>One animal's own senses: escalate on its own distance, calm on the herd's nearest.</summary>
    private static void Sense(WildlifeMember member, WildlifeKind kind, WildlifeBehaviour behaviour, double herdNearest, double seconds)
    {
        member.StageSeconds += seconds;
        WildlifeSenses senses = kind.Senses;
        double distance = member.PlayerDistance;
        double release = behaviour.ReleaseShare;
        if (member.Struck)
        {
            member.Struck = false;
            member.Enter(Wariness.Respond);
            return;
        }

        switch (member.Stage)
        {
            case Wariness.Calm:
            case Wariness.Regroup:
                if (distance <= senses.NoticeMetres) member.Enter(Wariness.Look);
                else if (member.Stage == Wariness.Regroup && member.StageSeconds >= behaviour.RegroupSeconds) member.Enter(Wariness.Calm);
                break;
            case Wariness.Look:
                if (herdNearest > senses.NoticeMetres * release) member.Enter(Wariness.Calm);
                else if (distance <= senses.ActMetres && member.StageSeconds >= senses.LookSeconds) member.Enter(Wariness.Warn);
                break;
            case Wariness.Warn:
                if (herdNearest > senses.ActMetres * release) member.Enter(Wariness.Look);
                else if (distance <= senses.ActMetres && member.StageSeconds >= senses.WarnSeconds) member.Enter(Wariness.Respond);
                break;
            case Wariness.Respond:
                switch (kind.Pressed)
                {
                    case PressedResponse.Flee:
                        if (herdNearest >= senses.NoticeMetres * behaviour.FleeUntilNoticeShare) member.Enter(Wariness.Regroup);
                        break;
                    case PressedResponse.Stand:
                        if (herdNearest > senses.ActMetres * release) member.Enter(Wariness.Look);
                        break;
                    case PressedResponse.DisplayCharge:
                        // A charge is short: it ends at contact or after its time, and the animal displays again.
                        if (herdNearest > senses.ActMetres * release) member.Enter(Wariness.Look);
                        else if (member.StageSeconds >= behaviour.ChargeSeconds || distance <= ContactMetres(kind, behaviour)) member.Enter(Wariness.Warn);
                        break;
                    case PressedResponse.Hunker:
                        if (herdNearest > senses.NoticeMetres * release) member.Enter(Wariness.Regroup);
                        break;
                }

                break;
        }
    }

    /// <summary>How close a charge comes: the kind's front end at the contact distance from the player.</summary>
    internal static double ContactMetres(WildlifeKind kind, WildlifeBehaviour behaviour) =>
        behaviour.ContactMetres + (kind.Body.LengthMetres / 2);

    /// <summary>
    /// Raises the herd's alarm to the highest warning or response any animal reached by itself, and
    /// spreads it from where it was raised at the behaviour's pace. A charging kind spreads only its
    /// display: one animal charges, the others warn.
    /// </summary>
    private static void Spread(WildlifeHerd herd, WildlifeBehaviour behaviour, double seconds)
    {
        WildlifeMember? raiser = null;
        foreach (WildlifeMember member in herd.Members)
        {
            if (member.Stage >= Wariness.Warn && (raiser is null || member.Stage > raiser.Stage)) raiser = member;
        }

        if (raiser is null)
        {
            herd.Alarm = herd.Members.Any(member => member.Stage == Wariness.Look) ? Wariness.Look : Wariness.Calm;
            herd.AlarmSeconds = 0;
            return;
        }

        if (herd.Alarm < Wariness.Warn)
        {
            herd.AlarmOrigin = raiser.Position;
            herd.AlarmSeconds = 0;
        }
        else
        {
            herd.AlarmSeconds += seconds;
        }

        herd.Alarm = raiser.Stage;
        Wariness cap = herd.Kind.Pressed == PressedResponse.DisplayCharge ? Wariness.Warn : Wariness.Respond;
        Wariness spread = herd.Alarm < cap ? herd.Alarm : cap;
        double reach = behaviour.AlarmSpreadMetresPerSecond * herd.AlarmSeconds;
        foreach (WildlifeMember member in herd.Members)
        {
            if (member.Stage < spread && Vector2.Distance(member.Position, herd.AlarmOrigin) <= reach) member.Enter(spread);
        }
    }

    /// <summary>Keeps the herd's day: a new step of the schedule sets a new activity and goal; a reached goal draws the next.</summary>
    private static void Plan(WildlifeHerd herd, HerdFacts facts, WildlifeCatalog catalog)
    {
        WildlifeSchedule schedule = herd.Kind.Schedule;
        int step = schedule.StepAt(facts.Time.DayFraction);
        long period = (facts.Time.Day * schedule.Steps.Length) + step;
        if (period != herd.Period)
        {
            herd.Period = period;
            herd.Activity = schedule.Steps[step].Activity;
            herd.GoalSerial = 0;
            herd.Goal = Goal(herd, facts, catalog);
            return;
        }

        bool roaming = herd.Activity is WildlifeActivity.Graze or WildlifeActivity.Drift
            || (herd.Activity == WildlifeActivity.Drink && facts.Water is null);
        if (roaming && Vector2.Distance(herd.Leader.Position, herd.Goal) <= catalog.Behaviour.GoalArrivalMetres)
        {
            herd.GoalSerial++;
            herd.Goal = Goal(herd, facts, catalog);
        }
    }

    private static Vector2 Goal(WildlifeHerd herd, HerdFacts facts, WildlifeCatalog catalog) => herd.Activity switch
    {
        WildlifeActivity.Rest => herd.Leader.Position,
        WildlifeActivity.Drink when facts.Water is Vector2 water => water,
        _ => RangePoint(herd.Key, herd.Home, herd.Kind, herd.Period, herd.GoalSerial, facts.Seed, catalog.Placement.HomeAttempts, facts.Allowed),
    };

    /// <summary>
    /// Where a dormant herd is when it wakes: the first goal of the schedule step it is in. Its day
    /// is drawn from the seed, so a herd wakes where its range puts it, not where it was left.
    /// </summary>
    internal static Vector2 WakePoint(HerdRecord record, WildlifeKind kind, WorldTime time, ulong seed, int attempts, Func<Vector2, bool>? allowed)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(kind);
        long period = (time.Day * kind.Schedule.Steps.Length) + kind.Schedule.StepAt(time.DayFraction);
        return RangePoint(record.Key, record.Home, kind, period, 0, seed, attempts, allowed);
    }

    /// <summary>A point in a herd's home range, drawn from the seed for a step of its day and a goal; the home if none drawn is allowed.</summary>
    internal static Vector2 RangePoint(string key, Vector2 home, WildlifeKind kind, long period, int serial, ulong seed, int attempts, Func<Vector2, bool>? allowed)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            string scope = string.Create(CultureInfo.InvariantCulture, $"wildlife:{key}:goal:{period}:{serial}:{attempt}");
            double angle = 2 * Math.PI * Unit(seed, scope + ":angle");
            // The square root spreads goals evenly over the range's area rather than bunching them at home.
            double radius = kind.Population.RangeMetres * Math.Sqrt(Unit(seed, scope + ":radius"));
            Vector2 point = home + new Vector2((float)(radius * Math.Sin(angle)), (float)(radius * -Math.Cos(angle)));
            if (allowed?.Invoke(point) != false) return point;
        }

        return home;
    }

    /// <summary>A keyed draw in [0, 1).</summary>
    internal static double Unit(ulong seed, string scope) => KeyedDraws.Draw(seed, scope, 0, DrawScale - 1) / (double)DrawScale;

    /// <summary>
    /// A member's slot beside the leader: rings of a sunflower, so neighbours stand about the
    /// spacing apart however many there are, with a little keyed jitter so a herd is not a lattice.
    /// </summary>
    internal static Vector2 Slot(int order, WildlifeKind kind, WildlifeBehaviour behaviour, ulong seed, string key)
    {
        if (order == 0) return Vector2.Zero;
        double radius = kind.SpacingMetres * SlotRingShare * Math.Sqrt(order);
        double angle = order * GoldenAngle;
        double jitter = kind.SpacingMetres * behaviour.SlotJitterShare;
        string scope = string.Create(CultureInfo.InvariantCulture, $"wildlife:{key}:slot:{order}");
        return new Vector2(
            (float)((radius * Math.Cos(angle)) + (jitter * (Unit(seed, scope + ":x") - 0.5))),
            (float)((radius * Math.Sin(angle)) + (jitter * (Unit(seed, scope + ":z") - 0.5))));
    }

    /// <summary>How fast the leader moves at an activity.</summary>
    private static double ActivitySpeed(WildlifeKind kind, WildlifeActivity activity) => activity switch
    {
        WildlifeActivity.Graze => kind.Speeds.GrazeDrift,
        WildlifeActivity.Drift or WildlifeActivity.Drink => kind.Speeds.Walk,
        _ => 0,
    };

    /// <summary>Decides every member's velocity and facing from its stage and the herd's day.</summary>
    private static void Steer(WildlifeHerd herd, HerdFacts facts, WildlifeBehaviour behaviour, double seconds)
    {
        WildlifeKind kind = herd.Kind;
        WildlifeMember leader = herd.Leader;
        double leaderSpeed = ActivitySpeed(kind, herd.Activity);
        Vector2 centre = herd.Centre;
        foreach (WildlifeMember member in herd.Members)
        {
            Vector2 velocity = Vector2.Zero;
            Vector2? face = null;
            bool shuffles = true;
            switch (member.Stage)
            {
                case Wariness.Look:
                case Wariness.Warn:
                    face = facts.Player;
                    shuffles = false;
                    break;
                case Wariness.Respond:
                    switch (kind.Pressed)
                    {
                        case PressedResponse.Flee:
                            velocity = herd.FleeHeading * (float)kind.Speeds.Flee;
                            Vector2 toCentre = centre - member.Position;
                            if (toCentre.Length() > kind.SpacingMetres * behaviour.CatchUpSpacings) velocity += toCentre * FleeCohesionShare;
                            velocity = Cap(velocity, kind.Speeds.Flee);
                            break;
                        case PressedResponse.DisplayCharge when facts.Player is Vector2 player:
                            velocity = Toward(member.Position, player, kind.Speeds.Flee, seconds);
                            break;
                        default:
                            face = facts.Player;
                            shuffles = false;
                            break;
                    }

                    break;
                default:
                    velocity = member == leader
                        ? LeaderVelocity(herd, behaviour, leaderSpeed, seconds)
                        : FollowerVelocity(member, leader, kind, behaviour, leaderSpeed, seconds);
                    break;
            }

            if (shuffles) velocity = Cap(velocity + Separation(member, herd, behaviour), Math.Max(kind.Speeds.Walk, velocity.Length()));
            member.Velocity = velocity;
            if (face is Vector2 target && target != member.Position) member.Yaw = Yaw(target - member.Position);
            else if (velocity.Length() > StillSpeed) member.Yaw = Yaw(velocity);
        }
    }

    private static Vector2 LeaderVelocity(WildlifeHerd herd, WildlifeBehaviour behaviour, double speed, double seconds)
    {
        // A scattered herd waits for its stragglers before it goes on, and a herd with heads up waits for
        // its watchers, so the herd does not walk away from the ones looking at the player.
        if (herd.Leader.Stage == Wariness.Regroup || herd.Alarm >= Wariness.Look || herd.Activity == WildlifeActivity.Rest) return Vector2.Zero;
        if (Vector2.Distance(herd.Leader.Position, herd.Goal) <= behaviour.GoalArrivalMetres) return Vector2.Zero;
        return Toward(herd.Leader.Position, herd.Goal, speed, seconds);
    }

    private static Vector2 FollowerVelocity(WildlifeMember member, WildlifeMember leader, WildlifeKind kind, WildlifeBehaviour behaviour,
        double leaderSpeed, double seconds)
    {
        Vector2 target = leader.Position + member.Slot;
        double distance = Vector2.Distance(member.Position, target);
        if (distance <= behaviour.SlotArrivalMetres) return Vector2.Zero;
        double speed = distance > kind.SpacingMetres * behaviour.CatchUpSpacings || member.Stage == Wariness.Regroup
            ? kind.Speeds.Walk
            : Math.Max(leaderSpeed, kind.Speeds.GrazeDrift);
        return Toward(member.Position, target, speed, seconds);
    }

    /// <summary>Steps apart from any herd-mate standing closer than the spacing allows, harder the closer it is.</summary>
    private static Vector2 Separation(WildlifeMember member, WildlifeHerd herd, WildlifeBehaviour behaviour)
    {
        double keep = herd.Kind.SpacingMetres * behaviour.SeparationShare;
        Vector2 push = Vector2.Zero;
        foreach (WildlifeMember other in herd.Members)
        {
            if (other == member) continue;
            Vector2 apart = member.Position - other.Position;
            float distance = apart.Length();
            if (distance >= keep) continue;
            // Two standing on one spot part along a direction drawn from their ids.
            Vector2 direction = distance > StillSpeed ? apart / distance : Facing(member.Id - other.Id);
            push += direction * (float)((1 - (distance / keep)) * herd.Kind.Speeds.Walk);
        }

        return push;
    }

    /// <summary>A velocity toward a point at a speed, never overshooting it within the update.</summary>
    private static Vector2 Toward(Vector2 from, Vector2 to, double speed, double seconds)
    {
        Vector2 offset = to - from;
        float distance = offset.Length();
        if (distance <= StillSpeed || speed <= 0 || seconds <= 0) return Vector2.Zero;
        return offset / distance * (float)Math.Min(speed, distance / seconds);
    }

    private static Vector2 Cap(Vector2 velocity, double speed)
    {
        float length = velocity.Length();
        return length > speed && length > 0 ? velocity * (float)(speed / length) : velocity;
    }

    /// <summary>The facing along a planar direction: 0 faces north (-Z), a quarter turn east (+X), as the player's look counts it.</summary>
    internal static float Yaw(Vector2 direction) => MathF.Atan2(direction.X, -direction.Y);

    /// <summary>The planar direction a facing looks along.</summary>
    internal static Vector2 Facing(float yaw) => new(MathF.Sin(yaw), -MathF.Cos(yaw));
}
