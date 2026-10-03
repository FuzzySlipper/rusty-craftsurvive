using CraftSurvive.Game.Modules.Rpg;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// The one owner of the player's health and defeat. Creatures strike through it; it decides when
/// a defeated player may come back, and the player controller applies the respawn it requests.
/// Ticks are Engine simulation steps.
/// </summary>
internal sealed class PlayerVitals
{
    private readonly int maximumHealth;
    private PlayerDefeatState state;
    private long graceUntilStep = long.MinValue;

    internal PlayerVitals(int maximumHealth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHealth);
        this.maximumHealth = maximumHealth;
        state = PlayerDefeatState.Full(maximumHealth);
    }

    internal PlayerDefeatState State => state;

    internal bool IsDown => state.Health <= 0;

    internal bool IsInvulnerable(long step) => PlayerDefeatRules.IsInvulnerable(graceUntilStep, step);

    /// <summary>How many times this session health has been lost, from any harm: what the HUD flashes on.</summary>
    internal long HitsTaken { get; private set; }

    /// <summary>Applies a landed blow. A player in grace or already down absorbs nothing.</summary>
    internal PlayerDefeatState TakeHit(int damage, long step)
    {
        if (!IsInvulnerable(step))
        {
            int before = state.Health;
            state = PlayerDefeatRules.Strike(state, damage, step);
            HitsTaken += state.Health < before ? 1 : 0;
        }

        return state;
    }

    /// <summary>
    /// Brings a defeated player back once the rules allow it and starts their grace. Returns true
    /// when the caller must move the player home.
    /// </summary>
    internal bool TryRespawn(long step)
    {
        if (!PlayerDefeatRules.CanRespawn(state, step))
        {
            return false;
        }

        state = PlayerDefeatRules.Respawn(state);
        graceUntilStep = PlayerDefeatRules.GraceUntil(step);
        return true;
    }

    /// <summary>Gives back health, up to the maximum. A defeated player regains nothing until respawned.</summary>
    internal void Heal(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (!IsDown)
        {
            state = state with { Health = Math.Min(maximumHealth, state.Health + amount) };
        }
    }

    internal PlayerDefeatOutcome Outcome(long step) => PlayerDefeatRules.Outcome(state, step);

    internal int MaximumHealth => maximumHealth;

    /// <summary>
    /// Continues from a saved session: the health and defeats it ended with, and no grace or
    /// pending respawn, which are timed in the old session's steps.
    /// </summary>
    internal void Restore(int health, int defeats)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(health);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(health, maximumHealth);
        ArgumentOutOfRangeException.ThrowIfNegative(defeats);
        state = PlayerDefeatState.Full(maximumHealth) with { Health = health, Defeats = defeats };
        graceUntilStep = long.MinValue;
    }

    /// <summary>A fresh session: full health, no defeats, no grace.</summary>
    internal void Reset()
    {
        state = PlayerDefeatState.Full(maximumHealth);
        graceUntilStep = long.MinValue;
    }
}
