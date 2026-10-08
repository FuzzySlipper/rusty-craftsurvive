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
    internal static CreatureStrike? Step(
        Creature creature,
        CreatureSense sense,
        Vector3 playerWorld,
        bool playerCanBeHit,
        ProductStep time,
        double sightScale,
        Vector2 waypoint)
    {
        ArgumentNullException.ThrowIfNull(creature);
        CreatureKind kind = creature.Kind;
        double distance = PlanarDistance(creature.Position, playerWorld);
        PerceptionFacts facts = new(sense.PlayerVisible, distance, creature.Combat.Health);
        BehaviorTuning tuning = kind.Tuning with { SightRange = kind.Tuning.SightRange * sightScale };
        creature.Behavior = CreatureBehaviorRules.Step(tuning, creature.Behavior, facts, time.Step);

        if (creature.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking
            && distance > kind.HaltDistanceMetres)
        {
            Vector2 offset = waypoint - creature.Position;
            double leg = offset.Length();
            double travel = Math.Min(Math.Min(kind.PursueSpeedMetresPerSecond * time.ElapsedSeconds, distance - kind.HaltDistanceMetres), leg);
            if (leg > 0)
            {
                creature.Position += offset * (float)(travel / leg);
            }

            distance = PlanarDistance(creature.Position, playerWorld);
        }

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
