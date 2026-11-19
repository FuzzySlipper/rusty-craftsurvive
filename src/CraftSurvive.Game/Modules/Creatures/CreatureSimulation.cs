using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.World;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>What a creature's senses reported this update: whether the Engine says it sees the player.</summary>
internal readonly record struct CreatureSense(bool PlayerVisible);

/// <summary>A creature's swing at the player this update; the combat rules decide whether it lands.</summary>
internal readonly record struct CreatureStrike(int CreatureId, AttackProfile Attack);

/// <summary>
/// One creature's update as pure policy: decide its state from what it sensed, move it on Engine
/// step time, and swing when its behaviour allows.
///
/// Every distance decision - sight range, attack range, halting, reach - uses the planar distance
/// between the creature and the player, because both stand on the ground. The Engine answers only
/// whether the creature can see the player at all.
/// </summary>
internal static class CreatureSimulation
{
    /// <summary>A searching creature goes at this share of its hunting pace.</summary>
    internal const double SearchPaceShare = 0.5;

    /// <summary>
    /// The radius the Engine's visibility query is given. Sight range is a planar rule decided by
    /// the behaviour, so the Engine must never reject a target inside it: its radius covers the
    /// planar sight range at the greatest height difference two things in the world can have.
    /// </summary>
    internal static double EngineSightRadius(double planarSightRange, double maximumHeightDifference) =>
        Math.Sqrt((planarSightRange * planarSightRange) + (maximumHeightDifference * maximumHeightDifference));

    /// <summary>
    /// Where a pursuer heads: the waypoint the Engine routed, or - for any other answer, whether no
    /// route, a start outside the published grid or a refused query - its own position, so it waits.
    /// </summary>
    internal static Vector2 WaypointOrWait(Vector2 position, Vector2? routed) => routed ?? position;

    /// <summary>
    /// Whether a pursuer that meant to walk came no nearer where it is heading (#9737). It is measured from
    /// where it last stood, so a player moving about neither hides a blocked body nor makes a walking one
    /// look blocked; one at its halt has stopped on purpose.
    /// </summary>
    /// <param name="from">Where it stood last step; null when it was not walking then, or has a fresh path.</param>
    /// <param name="progressMetres">Closing by less than this is not progress, and this near its halt it has arrived.</param>
    internal static bool NotClosing(Vector2? from, Vector2 position, Vector2 heading, double playerDistance, double haltDistance, float progressMetres) =>
        from is Vector2 before
        && playerDistance > haltDistance + progressMetres
        && Vector2.Distance(before, heading) - Vector2.Distance(position, heading) < progressMetres;

    /// <summary>A swing reaches a creature whose feet stand within this height of the player's: on the player's own floor, a step or a ledge about it.</summary>
    internal const double MeleeHeightMetres = 2.5;

    /// <summary>
    /// The creature a swing goes for (R10876-2): of those it can reach in height (feet within <see cref="MeleeHeightMetres"/>
    /// of the player's), one already attacking first, then the nearest across the ground. A creature on another floor, however
    /// near across the ground, is never a target and never takes a swing from one that is. Returns the creature and how far
    /// across the ground it stands, or null when none stands within reach in height.
    /// </summary>
    /// <param name="candidates">The awake creatures in the player's space, each with the height of its feet.</param>
    internal static (Creature Creature, double Distance)? MeleeTarget(IEnumerable<(Creature Creature, float Feet)> candidates, Vector3 playerFeet)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        (Creature Creature, double Distance)? best = null;
        foreach ((Creature creature, float feet) in candidates)
        {
            if (Math.Abs(feet - playerFeet.Y) > MeleeHeightMetres) continue;
            double distance = PlanarDistance(creature.Position, playerFeet);
            bool engaged = creature.Behavior.State == CreatureState.Attacking;
            bool bestEngaged = best?.Creature.Behavior.State == CreatureState.Attacking;
            if (best is null || (engaged && !bestEngaged) || (engaged == bestEngaged && distance < best.Value.Distance)) best = (creature, distance);
        }

        return best;
    }

    internal static double PlanarDistance(Vector2 creature, Vector3 player)
    {
        double dx = player.X - creature.X;
        double dz = player.Z - creature.Y;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// Advances one creature. Returns the swing it makes, if any; the caller resolves it through
    /// the combat rules against the player's defence. A player who cannot be hit - down, or in
    /// grace after a respawn - draws no swing and costs the creature no cooldown.
    /// </summary>
    /// <param name="sightScale">How much further than its kind's sight the creature sees now (1 by day).</param>
    /// <param name="waypoint">
    /// Where a pursuing creature walks next, in world X and Z: the navigation route's next point,
    /// or its own position to wait (see <see cref="WaypointOrWait"/>). A creature never walks
    /// straight at the player, so nothing but a route moves it past what is in between.
    /// </param>
    /// <param name="move">
    /// Moves the creature toward the waypoint by at most some metres over the update's seconds and says
    /// where it now stands (#9737: its body, stepped against the world's collision). It is called every
    /// update with time in it, with no metres when the creature is not walking, so a body with nothing under
    /// it still falls.
    /// Without one the creature is set down along the line, as a point that meets nothing.
    /// </param>
    /// <param name="wakeScale">The share of its kind's wake range a resting creature wakes at now (#10878: from downwind of a draft).</param>
    /// <param name="forced">A state a test aid holds the creature in (#10926), or null for the rules' own.</param>
    internal static CreatureStrike? Step(
        Creature creature,
        CreatureSense sense,
        Vector3 playerWorld,
        bool playerCanBeHit,
        ProductStep time,
        double sightScale,
        Vector2 waypoint,
        Func<Creature, Vector2, double, double, Vector2>? move = null,
        double wakeScale = 1,
        CreatureState? forced = null)
    {
        ArgumentNullException.ThrowIfNull(creature);
        CreatureKind kind = creature.Kind;
        double distance = PlanarDistance(creature.Position, playerWorld);
        PerceptionFacts facts = new(sense.PlayerVisible, distance, creature.Combat.Health);
        // A resting animal wakes nearer than its kind's range when the player comes from where it is not turned (#10878).
        BehaviorTuning tuning = kind.Tuning with { SightRange = kind.Tuning.SightRange * sightScale, WakeRange = kind.Tuning.WakeRange * wakeScale };
        creature.Behavior = CreatureBehaviorRules.Step(tuning, creature.Behavior, facts, time.Step);
        // A test aid's override (#10926) holds the state, whatever the rules decided.
        if (forced is CreatureState held) creature.Behavior = creature.Behavior with { State = held };

        double travel = 0;
        if (creature.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking
            && distance > kind.HaltDistanceMetres)
        {
            Vector2 offset = waypoint - creature.Position;
            double leg = offset.Length();
            travel = Math.Min(Math.Min(kind.PursueSpeedMetresPerSecond * time.ElapsedSeconds, distance - kind.HaltDistanceMetres), leg);
            if (move is null && leg > 0) creature.Position += offset * (float)(travel / leg);
        }
        else if (creature.Behavior.State == CreatureState.Searching)
        {
            // A search walks to where the player was last seen, slower than a hunt, and looks about there (#10876).
            Vector2 offset = waypoint - creature.Position;
            double leg = offset.Length();
            travel = Math.Min(kind.PursueSpeedMetresPerSecond * SearchPaceShare * time.ElapsedSeconds, leg);
            if (move is null && leg > 0) creature.Position += offset * (float)(travel / leg);
        }

        // An update the Engine admitted no steps to has no time to step a body through, and the Engine refuses one.
        if (move is not null && time.ElapsedSeconds > 0) creature.Position = move(creature, waypoint, travel, time.ElapsedSeconds);
        distance = PlanarDistance(creature.Position, playerWorld);

        // A blow needs a clear line as well as reach: a creature cannot strike through a wall it
        // stands against (#9734), and perception's casts meet built work as the session's collision.
        if (!playerCanBeHit
            || !sense.PlayerVisible
            || !creature.Behavior.CanAttack(time.Step, kind.Tuning)
            || distance > kind.ReachMetres)
        {
            return null;
        }

        creature.Behavior = creature.Behavior.AfterAttack(time.Step);
        return new CreatureStrike(creature.Id, kind.Attack);
    }
}
