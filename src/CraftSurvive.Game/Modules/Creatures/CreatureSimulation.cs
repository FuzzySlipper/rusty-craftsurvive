using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.World;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>What a creature's senses reported this update: the Engine's sight answer.</summary>
internal readonly record struct CreatureSense(bool PlayerVisible, double PerceivedDistance);

/// <summary>A creature's blow on the player this update, if it struck.</summary>
internal readonly record struct CreatureStrike(int CreatureId, int Damage);

/// <summary>
/// One creature's update as pure policy: decide its state from what it sensed, move it on Engine
/// step time, and strike when its behaviour allows. Movement and reach are planar - creatures
/// stand on the ground - while sight comes from the Engine's perceived distance.
/// </summary>
internal static class CreatureSimulation
{
    internal static double PlanarDistance(Vector2 creature, Vector3 player)
    {
        double dx = player.X - creature.X;
        double dz = player.Z - creature.Y;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// Advances one creature. Returns the blow it lands, if any; the caller applies it to the
    /// player's vitals. A player who cannot be hit - down, or in grace after a respawn - draws no
    /// attack and costs the creature no cooldown.
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
        PerceptionFacts facts = new(sense.PlayerVisible, sense.PerceivedDistance, creature.Combat.Health);
        creature.Behavior = CreatureBehaviorRules.Step(kind.Tuning, creature.Behavior, facts, time.Step);

        double distance = PlanarDistance(creature.Position, playerWorld);
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
            || distance > kind.AttackReachMetres)
        {
            return null;
        }

        creature.Behavior = creature.Behavior.AfterAttack(time.Step);
        return new CreatureStrike(creature.Id, kind.AttackDamage);
    }
}
