namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>
/// A combatant's durable state: what it has left, what protects it, and - once it
/// is down - when it is due back. Position and appearance are the Engine's
/// business and are deliberately absent here.
/// </summary>
internal readonly record struct CombatantState(
    int Health,
    int MaximumHealth,
    DefenceProfile Defence,
    long DownAtTick)
{
    internal bool IsDown => Health <= 0;

    internal static CombatantState Fresh(int maximumHealth, DefenceProfile defence) =>
        new(maximumHealth, maximumHealth, defence, DownAtTick: -1);
}

/// <summary>What a defeated creature leaves behind.</summary>
internal readonly record struct EncounterReward(ExperienceAward Experience, LootDrop[] Drops);

internal static class EncounterResolutionRules
{
    /// <summary>
    /// One blow from an already-drawn roll. A blow against a creature that is
    /// already down is refused rather than resolved, so a dead creature cannot be
    /// farmed for experience or drops.
    /// </summary>
    internal static (CombatantState Target, AttackOutcome Outcome) Strike(
        int roll,
        AttackProfile attack,
        CombatantState target,
        long tick)
    {
        if (target.IsDown)
        {
            AttackOutcome refused = new(false, false, roll, 0, target.Defence.Evasion, 0, 0, AttackResult.TargetDown);
            return (target, refused);
        }

        AttackOutcome outcome = CombatRules.Resolve(roll, attack, target.Defence);
        if (!outcome.Hit)
        {
            return (target, outcome);
        }

        int remaining = Math.Max(target.Health - outcome.Damage, 0);
        CombatantState struck = target with
        {
            Health = remaining,
            DownAtTick = remaining == 0 ? tick : target.DownAtTick,
        };

        return (struck, outcome);
    }

    /// <summary>
    /// Brings a downed combatant back once its delay has elapsed, at full health.
    /// A combatant that is still standing is returned untouched.
    /// </summary>
    internal static CombatantState Respawn(long tick, CombatantState state, long respawnDelayTicks)
    {
        if (!state.IsDown || state.DownAtTick < 0)
        {
            return state;
        }

        return tick - state.DownAtTick >= respawnDelayTicks
            ? state with { Health = state.MaximumHealth, DownAtTick = -1 }
            : state;
    }

    /// <summary>
    /// What killing one creature is worth. The award is a combat award by
    /// construction, so it can only ever arrive through the source progression
    /// accepts, and the drops come from the table under the caller's keyed draw.
    /// </summary>
    internal static EncounterReward Reward(int experience, LootTable table, string scope, LootRules.Draw draw)
    {
        if (experience <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(experience), experience, "A defeat must be worth experience.");
        }

        return new EncounterReward(new ExperienceAward(ExperienceSource.Combat, experience), LootRules.Roll(table, scope, draw));
    }
}
