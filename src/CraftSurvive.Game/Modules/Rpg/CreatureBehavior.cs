namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>What a creature wants from the player.</summary>
internal enum CreatureDisposition
{
    Hostile = 0,
    Neutral = 1,
}

internal enum CreatureState
{
    Idle = 0,
    Alert = 1,
    Pursuing = 2,
    Attacking = 3,
    Dead = 4,
}

/// <summary>What the creature's senses reported this step.</summary>
internal readonly record struct PerceptionFacts(
    bool PlayerVisible,
    double DistanceToPlayer,
    int OwnHealth);

/// <summary>A creature's standing numbers, decided by its kind rather than by its state.</summary>
internal readonly record struct BehaviorTuning(
    CreatureDisposition Disposition,
    double SightRange,
    double AttackRange,
    long AttackCooldownTicks)
{
    internal static BehaviorTuning Hostile => new(CreatureDisposition.Hostile, SightRange: 48.0, AttackRange: 2.5, AttackCooldownTicks: 60);

    internal static BehaviorTuning Neutral => new(CreatureDisposition.Neutral, SightRange: 24.0, AttackRange: 2.5, AttackCooldownTicks: 60);
}

internal readonly record struct CreatureBehaviorState(
    CreatureState State,
    long LastAttackTick)
{
    internal static CreatureBehaviorState Spawned => new(CreatureState.Idle, LastAttackTick: -1);

    internal bool CanAttack(long tick, BehaviorTuning tuning) =>
        State == CreatureState.Attacking && (LastAttackTick < 0 || tick - LastAttackTick >= tuning.AttackCooldownTicks);

    internal CreatureBehaviorState AfterAttack(long tick) => this with { LastAttackTick = tick };
}

/// <summary>
/// A creature's behaviour as a small state machine over sensed facts. The facts
/// are inputs rather than queries, so the machine is testable without an Engine,
/// and the adapter that feeds it - perception, navigation, animation - stays thin
/// and does no deciding of its own.
/// </summary>
internal static class CreatureBehaviorRules
{
    internal const int DeathHealth = 0;

    internal static CreatureBehaviorState Step(
        BehaviorTuning tuning,
        CreatureBehaviorState state,
        PerceptionFacts facts,
        long tick)
    {
        // Death is terminal: nothing a dead creature senses changes its state.
        if (state.State == CreatureState.Dead || facts.OwnHealth <= DeathHealth)
        {
            return state with { State = CreatureState.Dead };
        }

        // A neutral creature watches and warns, but never closes or strikes.
        if (tuning.Disposition == CreatureDisposition.Neutral)
        {
            bool watching = facts.PlayerVisible && facts.DistanceToPlayer <= tuning.SightRange;
            return state with { State = watching ? CreatureState.Alert : CreatureState.Idle };
        }

        if (!facts.PlayerVisible)
        {
            return state with { State = CreatureState.Idle };
        }

        if (facts.DistanceToPlayer > tuning.SightRange)
        {
            // Out of sight is out of mind, even with the player technically visible.
            return state with { State = CreatureState.Idle };
        }

        if (facts.DistanceToPlayer <= tuning.AttackRange)
        {
            return state with { State = CreatureState.Attacking };
        }

        return state with { State = state.State == CreatureState.Idle ? CreatureState.Alert : CreatureState.Pursuing };
    }
}
