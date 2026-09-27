namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>What the player's death means in play: the other half of "win or die".</summary>
internal enum PlayerDefeatOutcome
{
    Alive,
    Defeated,
    RespawnReady,
}

/// <summary>The player's own health and defeat accounting, owned by the product.</summary>
internal readonly record struct PlayerDefeatState(int Health, int MaximumHealth, int Defeats, long RespawnTick)
{
    internal static PlayerDefeatState Full(int maximumHealth) =>
        new(maximumHealth, maximumHealth, 0, RespawnTick: -1);
}

/// <summary>
/// The rules for taking lethal damage and for coming back, so an encounter can be
/// lost as well as won.
/// </summary>
internal static class PlayerDefeatRules
{
    internal const int RespawnHealthPercent = 50;
    internal const long RespawnDelayTicks = 200;

    internal static bool IsDefeated(int health) => health <= 0;

    internal static PlayerDefeatState Strike(PlayerDefeatState state, int damage, long tick)
    {
        // A defeated player absorbs nothing. Without this, every further hit counts
        // another defeat and moves the respawn schedule, so a corpse is "killed"
        // every frame and never comes back - which is exactly what the live chase
        // did before this guard.
        if (state.Health <= 0)
        {
            return state;
        }

        int health = Math.Max(0, state.Health - Math.Max(0, damage));
        return health > 0
            ? state with { Health = health }
            : state with { Health = 0, Defeats = state.Defeats + 1, RespawnTick = tick + RespawnDelayTicks };
    }

    internal static bool CanRespawn(PlayerDefeatState state, long tick) =>
        state.Health <= 0 && state.RespawnTick >= 0 && tick >= state.RespawnTick;

    internal static PlayerDefeatState Respawn(PlayerDefeatState state) => state with
    {
        Health = Math.Max(1, state.MaximumHealth * RespawnHealthPercent / 100),
        RespawnTick = -1,
    };

    internal static PlayerDefeatOutcome Outcome(PlayerDefeatState state, long tick) =>
        state.Health > 0 ? PlayerDefeatOutcome.Alive
        : CanRespawn(state, tick) ? PlayerDefeatOutcome.RespawnReady
        : PlayerDefeatOutcome.Defeated;
}
