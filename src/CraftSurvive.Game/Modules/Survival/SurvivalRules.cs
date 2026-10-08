namespace CraftSurvive.Game.Modules.Survival;

/// <summary>
/// The player's survival tracks: how fed they are and how much air they hold, how wet and chilled
/// the weather has left them (0..1, #9741), plus the progress toward the next point of health
/// regained or lost, carried between updates.
/// </summary>
internal readonly record struct SurvivalState(
    double Satiety,
    double Breath,
    double RegainProgress,
    double StarveProgress,
    double DrownProgress,
    double SinceHurtSeconds,
    double Wetness = 0d,
    double Chill = 0d,
    double ExposureProgress = 0d)
{
    internal static SurvivalState Fresh => new(SurvivalRules.MaximumSatiety, SurvivalRules.MaximumBreathSeconds, 0d, 0d, 0d, 0d);
}

/// <summary>
/// What the weather does to someone out in it (#9741, Den <c>design/weather-and-environment</c>): how
/// fast it wets and chills them and how much it wounds, per game hour, and whether they are under
/// cover. Under cover nothing wets or wounds, and chill comes only in part.
/// </summary>
internal readonly record struct WeatherExposure(double Wetting, double Chill, double Harm, bool Sheltered)
{
    internal static WeatherExposure None => new(0d, 0d, 0d, true);
}

/// <summary>What the player is doing this update, as survival needs to know it.</summary>
internal readonly record struct SurvivalFacts(int Health, int MaximumHealth, bool HeadSubmerged, bool Sprinting, bool Hurt, double HungerFactor = 1, WeatherExposure Weather = default);

/// <summary>Why health changed in a survival step.</summary>
internal enum SurvivalHarm
{
    None,
    Starving,
    Drowning,
    Exposure,
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
    bool DrowningHurts,
    bool WeatherHurts = true);

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

    /// <summary>No one sleeps with an awake hostile creature this close.</summary>
    internal const double RestSafetyMetres = 24d;

    /// <summary>A respawned player comes back at least this fed, and with full breath.</summary>
    internal const double RespawnSatiety = 50d;

    /// <summary>Seconds of play in a game hour: weather rates are per game hour.</summary>
    private const double SecondsPerHour = Sky.WorldClock.DaySeconds / 24d;

    /// <summary>
    /// Wetness (#9741): it dries this much a game hour out of the wet, faster under cover; going under
    /// water soaks completely. Being wet chills a little, and wet and cold both make the body burn food.
    /// </summary>
    internal const double DryInTheOpenPerHour = 0.3d, DryUnderCoverPerHour = 0.6d;
    internal const double WetChillPerHour = 0.08d, ChillUnderCoverShare = 0.25d, ChillRecoveryPerHour = 0.3d;
    internal const double WetHunger = 0.25d, ChillHunger = 0.75d;

    /// <summary>A full stomach lasts about one and a half in-game days at Normal.</summary>
    private const double NormalFullStomachSeconds = 1.5d * 20d * 60d;

    internal static SurvivalTuning Tuning(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Gentle => new(MaximumSatiety / (NormalFullStomachSeconds * 2d), 2d, double.PositiveInfinity, double.PositiveInfinity, 0.75d, false, false, WeatherHurts: false),
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
        (double wetness, double chill, double exposure, int exposed) = Weathered(state, facts with { Weather = tuning.WeatherHurts ? facts.Weather : facts.Weather with { Harm = 0d } }, seconds);
        double weatherHunger = 1d + (WetHunger * wetness) + (ChillHunger * chill);
        double hunger = tuning.HungerPerSecond * (facts.Sprinting ? SprintHungerFactor : 1d) * Math.Max(0d, facts.HungerFactor) * weatherHunger * seconds;
        double satiety = Math.Max(0d, state.Satiety - hunger);
        double breath = facts.HeadSubmerged
            ? Math.Max(0d, state.Breath - (tuning.BreathLostPerSecond * seconds))
            : Math.Min(MaximumBreathSeconds, state.Breath + (BreathRegainedPerSecond * seconds));
        double sinceHurt = facts.Hurt ? 0d : state.SinceHurtSeconds + seconds;

        // Drowning, then starving, take health; neither accrues while its cause is absent.
        double drown = breath <= 0d && tuning.DrowningHurts ? state.DrownProgress + (seconds / tuning.SecondsPerPointDrowned) : 0d;
        double starve = satiety <= 0d && tuning.StarvingHurts ? state.StarveProgress + (seconds / tuning.SecondsPerPointStarved) : 0d;
        int drowned = (int)Math.Floor(drown);
        // Starving never takes the last point, after what water and weather took this step.
        int starved = Math.Min((int)Math.Floor(starve), Math.Max(0, facts.Health - drowned - exposed - 1));
        drown -= drowned;
        starve -= (int)Math.Floor(starve);

        // A fed, calm, hurt player regains health, and pays for it in food.
        bool regaining = satiety >= RegainAboveSatiety && sinceHurt >= CalmSecondsBeforeRegaining
            && drowned + exposed == 0 && facts.Health < facts.MaximumHealth
            // Nothing mends out in weather that wounds (#9741): it must be sheltered from, not outlasted.
            && !(facts.Weather.Harm > 0d && !facts.Weather.Sheltered);
        double regain = regaining ? state.RegainProgress + (seconds / tuning.SecondsPerPointRegained) : 0d;
        int regained = Math.Min((int)Math.Floor(regain), facts.MaximumHealth - facts.Health);
        regain -= Math.Floor(regain);
        satiety = Math.Max(0d, satiety - (regained * SatietyPerPointRegained));

        int lost = drowned + starved + exposed;
        SurvivalHarm cause = drowned > 0 ? SurvivalHarm.Drowning : exposed > 0 ? SurvivalHarm.Exposure : starved > 0 ? SurvivalHarm.Starving : SurvivalHarm.None;
        return new SurvivalStep(new SurvivalState(satiety, breath, regain, starve, drown, lost > 0 ? 0d : sinceHurt, wetness, chill, exposure), regained, lost, cause);
    }

    /// <summary>
    /// The weather on the body over some seconds (#9741): out in it, wetting soaks and harm wounds
    /// (whole points, the fraction carried); under cover it dries and nothing wounds; under water
    /// soaks at once. Chill rises with the weather's cold (a quarter of it under cover) and a little
    /// with being wet, and the body warms back at a steady rate.
    /// </summary>
    private static (double Wetness, double Chill, double ExposureProgress, int Wounds) Weathered(SurvivalState state, SurvivalFacts facts, double seconds)
    {
        double hours = seconds / SecondsPerHour;
        WeatherExposure weather = facts.Weather;
        bool open = !weather.Sheltered;
        double wetness = facts.HeadSubmerged ? 1d
            : open && weather.Wetting > 0d ? state.Wetness + (weather.Wetting * hours)
            : state.Wetness - ((open ? DryInTheOpenPerHour : DryUnderCoverPerHour) * hours);
        wetness = Math.Clamp(wetness, 0d, 1d);
        double cold = (open ? weather.Chill : weather.Chill * ChillUnderCoverShare) + (WetChillPerHour * wetness);
        double chill = Math.Clamp(state.Chill + ((cold - ChillRecoveryPerHour) * hours), 0d, 1d);
        double exposure = open && weather.Harm > 0d ? state.ExposureProgress + (weather.Harm * hours) : state.ExposureProgress;
        int wounds = Math.Min((int)Math.Floor(exposure), Math.Max(0, facts.Health));
        return (wetness, chill, exposure - Math.Floor(exposure), wounds);
    }

    /// <summary>A rest is advanced in slices this long, so health regained early is paid for by the food on hand then.</summary>
    internal const double RestSliceSeconds = 5d;

    /// <summary>
    /// Sleeping through some seconds: the same rules as staying awake and calm for that long, taken
    /// a slice at a time, so a rest regains health while there is food and costs the food it uses,
    /// and never drowns anyone.
    /// </summary>
    /// <param name="meal">Eats from what is carried, if anything, after each slice: food on hand is eaten as hunger
    /// comes, so it is the food missing, not the food held, that hurts.</param>
    internal static SurvivalStep Rest(SurvivalState state, int health, int maximumHealth, Difficulty difficulty, double seconds,
        Func<SurvivalState, SurvivalState>? meal = null) =>
        Slices(state with { SinceHurtSeconds = CalmSecondsBeforeRegaining, Breath = MaximumBreathSeconds }, health, maximumHealth, difficulty, seconds, meal, 1d);

    /// <summary>
    /// A march on the map: the ordinary rules over those seconds, a slice at a time, never sprinting or
    /// submerged, eating as hunger comes when there is food to eat. Hard weather makes the march hungrier
    /// (<paramref name="hungerFactor"/>, #9739).
    /// </summary>
    internal static SurvivalStep March(SurvivalState state, int health, int maximumHealth, Difficulty difficulty, double seconds,
        Func<SurvivalState, SurvivalState>? meal = null, double hungerFactor = 1d) =>
        Slices(state, health, maximumHealth, difficulty, seconds, meal, hungerFactor);

    private static SurvivalStep Slices(SurvivalState resting, int health, int maximumHealth, Difficulty difficulty, double seconds,
        Func<SurvivalState, SurvivalState>? meal, double hungerFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        int regained = 0;
        int lost = 0;
        SurvivalHarm cause = SurvivalHarm.None;
        for (double slept = 0d; slept < seconds; slept += RestSliceSeconds)
        {
            int now = health + regained - lost;
            SurvivalStep slice = Advance(resting, new SurvivalFacts(now, maximumHealth, HeadSubmerged: false, Sprinting: false, Hurt: false, hungerFactor),
                difficulty, Math.Min(RestSliceSeconds, seconds - slept));
            resting = meal?.Invoke(slice.State) ?? slice.State;
            regained += slice.Regained;
            lost += slice.Lost;
            cause = slice.Cause == SurvivalHarm.None ? cause : slice.Cause;
        }

        return new SurvivalStep(resting, regained, lost, cause);
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
        Wetness = 0d,
        Chill = 0d,
        ExposureProgress = 0d,
    };
}
