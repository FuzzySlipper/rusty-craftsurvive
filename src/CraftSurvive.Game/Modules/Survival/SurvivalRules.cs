namespace CraftSurvive.Game.Modules.Survival;

/// <summary>
/// The player's survival tracks: how fed they are and how much air they hold, plus the progress
/// toward the next point of health regained or lost, carried between updates.
/// </summary>
internal readonly record struct SurvivalState(
    double Satiety,
    double Breath,
    double RegainProgress,
    double StarveProgress,
    double DrownProgress,
    double SinceHurtSeconds)
{
    internal static SurvivalState Fresh => new(SurvivalRules.MaximumSatiety, SurvivalRules.MaximumBreathSeconds, 0d, 0d, 0d, 0d);
}

/// <summary>What the player is doing this update, as survival needs to know it.</summary>
internal readonly record struct SurvivalFacts(int Health, int MaximumHealth, bool HeadSubmerged, bool Sprinting, bool Hurt);

/// <summary>Why health changed in a survival step.</summary>
internal enum SurvivalHarm
{
    None,
    Starving,
    Drowning,
}

/// <summary>One survival step: the new tracks, the health to give back, and the health to take and why.</summary>
internal readonly record struct SurvivalStep(SurvivalState State, int Regained, int Lost, SurvivalHarm Cause);

/// <summary>How a difficulty tunes survival. Rates are per second of play.</summary>
internal readonly record struct SurvivalTuning(
    double HungerPerSecond,
    double SecondsPerPointRegained,
    double SecondsPerPointStarved,
    double SecondsPerPointDrowned,
    double BreathLostPerSecond,
    bool StarvingHurts,
    bool DrowningHurts);

/// <summary>
/// The rules of hunger, air and recovery, sized for expeditions rather than for a farm. Hunger
/// drains with time (faster while sprinting); a fed player regains health once they have not been
/// hurt for a while, and each point regained costs food; an empty stomach drains health but never
/// below one point, so hunger presses an expedition without ending it alone. Air runs out while the
/// head is under water and drowning can kill. The difficulty tunes every rate.
/// </summary>
internal static class SurvivalRules
{
    internal const double MaximumSatiety = 100d;

    /// <summary>How long the player can hold their breath, in seconds.</summary>
    internal const double MaximumBreathSeconds = 20d;

    /// <summary>Air comes back four times faster than it goes.</summary>
    internal const double BreathRegainedPerSecond = 4d;

    /// <summary>A player must be at least this fed to regain health.</summary>
    internal const double RegainAboveSatiety = 40d;

    /// <summary>The food one point of regained health costs.</summary>
    internal const double SatietyPerPointRegained = 1.5d;

    /// <summary>How long after a hurt health begins to come back.</summary>
    internal const double CalmSecondsBeforeRegaining = 5d;

    /// <summary>Sprinting burns food this many times faster.</summary>
    internal const double SprintHungerFactor = 2d;

    /// <summary>A respawned player comes back at least this fed, and with full breath.</summary>
    internal const double RespawnSatiety = 50d;

    /// <summary>A full stomach lasts about one and a half in-game days at Normal.</summary>
    private const double NormalFullStomachSeconds = 1.5d * 20d * 60d;

    internal static SurvivalTuning Tuning(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Gentle => new(MaximumSatiety / (NormalFullStomachSeconds * 2d), 2d, double.PositiveInfinity, double.PositiveInfinity, 0.75d, false, false),
        Difficulty.Harsh => new(MaximumSatiety / (NormalFullStomachSeconds / 1.5d), 8d, 6d, 1d, 1.5d, true, true),
        _ => new(MaximumSatiety / NormalFullStomachSeconds, 4d, 10d, 1.5d, 1d, true, true),
    };

    /// <summary>Advances survival by some seconds of play.</summary>
    internal static SurvivalStep Advance(SurvivalState state, SurvivalFacts facts, Difficulty difficulty, double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        if (facts.Health <= 0)
        {
            // A defeated player's tracks wait for the respawn.
            return new SurvivalStep(state, 0, 0, SurvivalHarm.None);
        }

        SurvivalTuning tuning = Tuning(difficulty);
        double hunger = tuning.HungerPerSecond * (facts.Sprinting ? SprintHungerFactor : 1d) * seconds;
        double satiety = Math.Max(0d, state.Satiety - hunger);
        double breath = facts.HeadSubmerged
            ? Math.Max(0d, state.Breath - (tuning.BreathLostPerSecond * seconds))
            : Math.Min(MaximumBreathSeconds, state.Breath + (BreathRegainedPerSecond * seconds));
        double sinceHurt = facts.Hurt ? 0d : state.SinceHurtSeconds + seconds;

        // Drowning, then starving, take health; neither accrues while its cause is absent.
        double drown = breath <= 0d && tuning.DrowningHurts ? state.DrownProgress + (seconds / tuning.SecondsPerPointDrowned) : 0d;
        double starve = satiety <= 0d && tuning.StarvingHurts ? state.StarveProgress + (seconds / tuning.SecondsPerPointStarved) : 0d;
        int drowned = (int)Math.Floor(drown);
        int starved = Math.Min((int)Math.Floor(starve), Math.Max(0, facts.Health - drowned - 1));
        drown -= drowned;
        starve -= (int)Math.Floor(starve);

        // A fed, calm, hurt player regains health, and pays for it in food.
        bool regaining = satiety >= RegainAboveSatiety && sinceHurt >= CalmSecondsBeforeRegaining
            && drowned == 0 && facts.Health < facts.MaximumHealth;
        double regain = regaining ? state.RegainProgress + (seconds / tuning.SecondsPerPointRegained) : 0d;
        int regained = Math.Min((int)Math.Floor(regain), facts.MaximumHealth - facts.Health);
        regain -= Math.Floor(regain);
        satiety = Math.Max(0d, satiety - (regained * SatietyPerPointRegained));

        int lost = drowned + starved;
        SurvivalHarm cause = drowned > 0 ? SurvivalHarm.Drowning : starved > 0 ? SurvivalHarm.Starving : SurvivalHarm.None;
        return new SurvivalStep(new SurvivalState(satiety, breath, regain, starve, drown, lost > 0 ? 0d : sinceHurt), regained, lost, cause);
    }

    /// <summary>Eating: food fills the stomach up to full.</summary>
    internal static SurvivalState Eat(SurvivalState state, double nourishment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nourishment);
        return state with { Satiety = Math.Min(MaximumSatiety, state.Satiety + nourishment) };
    }

    /// <summary>A respawned player: breath restored, and at least half fed.</summary>
    internal static SurvivalState Respawned(SurvivalState state) => state with
    {
        Satiety = Math.Max(state.Satiety, RespawnSatiety),
        Breath = MaximumBreathSeconds,
        RegainProgress = 0d,
        StarveProgress = 0d,
        DrownProgress = 0d,
        SinceHurtSeconds = 0d,
    };
}
