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

    /// <summary>Lying still where it lives (#10876): it holds until the player comes within its wake distance.</summary>
    Resting = 5,

    /// <summary>It has lost the player and looks for them where they were last seen (#10876), for its search's while.</summary>
    Searching = 6,
}

/// <summary>What the creature's senses reported this step.</summary>
internal readonly record struct PerceptionFacts(
    bool PlayerVisible,
    double DistanceToPlayer,
    int OwnHealth);

/// <summary>A creature's standing numbers, decided by its kind rather than by its state.</summary>
/// <param name="WakeRange">A kind that rests (#10876) lies still until the player is seen this near; 0 for one that never rests.</param>
/// <param name="SearchTicks">How long a kind that searches (#10876) looks for a player it has lost; 0 for one that gives up at once.</param>
internal readonly record struct BehaviorTuning(
    CreatureDisposition Disposition,
    double SightRange,
    double AttackRange,
    long AttackCooldownTicks,
    double WakeRange = 0,
    long SearchTicks = 0)
{
    /// <summary>Whether a creature of this kind rests when it has nothing to hunt.</summary>
    internal bool Rests => WakeRange > 0;

    /// <summary>What a creature with nothing to hunt goes back to: resting, for a kind that rests.</summary>
    internal CreatureState Calm => Rests ? CreatureState.Resting : CreatureState.Idle;

    internal static BehaviorTuning Hostile => new(CreatureDisposition.Hostile, SightRange: 48.0, AttackRange: 2.5, AttackCooldownTicks: 60);

    internal static BehaviorTuning Neutral => new(CreatureDisposition.Neutral, SightRange: 24.0, AttackRange: 2.5, AttackCooldownTicks: 60);
}

/// <param name="SearchUntilTick">While searching, the tick its search ends.</param>
internal readonly record struct CreatureBehaviorState(
    CreatureState State,
    long LastAttackTick,
    long SearchUntilTick = -1)
{
    internal static CreatureBehaviorState Spawned => new(CreatureState.Idle, LastAttackTick: -1);

    /// <summary>A new creature of a kind: lying at rest where it lives, for a kind that rests.</summary>
    internal static CreatureBehaviorState SpawnedFor(BehaviorTuning tuning) => new(tuning.Calm, LastAttackTick: -1);

    internal bool CanAttack(long tick, BehaviorTuning tuning) => State == CreatureState.Attacking && CooledDown(tick, tuning);

    /// <summary>Whether its last attack's cooldown has run out (or it never attacked), whatever it is doing now.</summary>
    internal bool CooledDown(long tick, BehaviorTuning tuning) => LastAttackTick < 0 || tick - LastAttackTick >= tuning.AttackCooldownTicks;

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

        // A resting creature holds still (#10876) until it sees the player within its wake distance.
        if (state.State == CreatureState.Resting)
        {
            bool woken = facts.PlayerVisible && facts.DistanceToPlayer <= Math.Min(tuning.WakeRange, tuning.SightRange);
            return woken ? state with { State = CreatureState.Alert } : state;
        }

        // Out of sight is out of mind, even with the player technically visible: unless the kind searches (#10876), when a
        // hunt that loses the player looks for them for a while before it gives up.
        if (!facts.PlayerVisible || facts.DistanceToPlayer > tuning.SightRange)
        {
            if (state.State is CreatureState.Alert or CreatureState.Pursuing or CreatureState.Attacking && tuning.SearchTicks > 0)
            {
                return state with { State = CreatureState.Searching, SearchUntilTick = tick + tuning.SearchTicks };
            }

            if (state.State == CreatureState.Searching && tick < state.SearchUntilTick)
            {
                return state;
            }

            return state with { State = tuning.Calm, SearchUntilTick = -1 };
        }

        if (facts.DistanceToPlayer <= tuning.AttackRange)
        {
            return state with { State = CreatureState.Attacking };
        }

        // A search that finds the player again takes up the hunt where it was.
        return state with { State = state.State == CreatureState.Idle ? CreatureState.Alert : CreatureState.Pursuing, SearchUntilTick = -1 };
    }
}
