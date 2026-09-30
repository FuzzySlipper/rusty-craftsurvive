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
    internal static CreatureStrike? Step(
        Creature creature,
        CreatureSense sense,
        Vector3 playerWorld,
        bool playerCanBeHit,
        ProductStep time)
    {
        ArgumentNullException.ThrowIfNull(creature);
        CreatureKind kind = creature.Kind;
        double distance = PlanarDistance(creature.Position, playerWorld);
        PerceptionFacts facts = new(sense.PlayerVisible, distance, creature.Combat.Health);
        creature.Behavior = CreatureBehaviorRules.Step(kind.Tuning, creature.Behavior, facts, time.Step);

        if (creature.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking
            && distance > kind.HaltDistanceMetres)
        {
            double travel = Math.Min(kind.PursueSpeedMetresPerSecond * time.ElapsedSeconds, distance - kind.HaltDistanceMetres);
            double scale = travel / distance;
            creature.Position = new Vector2(
                (float)(creature.Position.X + ((playerWorld.X - creature.Position.X) * scale)),
                (float)(creature.Position.Y + ((playerWorld.Z - creature.Position.Y) * scale)));
            distance -= travel;
        }

        if (!playerCanBeHit
            || !creature.Behavior.CanAttack(time.Step, kind.Tuning)
            || distance > kind.ReachMetres)
        {
            return null;
        }

        creature.Behavior = creature.Behavior.AfterAttack(time.Step);
        return new CreatureStrike(creature.Id, kind.Attack);
    }
}
