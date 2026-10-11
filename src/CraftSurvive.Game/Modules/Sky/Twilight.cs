namespace CraftSurvive.Game.Modules.Sky;

/// <summary>The sky's base panoramas: day, night, and the twilight between them at dawn and at dusk, vivid or muted (#9805).</summary>
internal enum SkyPanorama
{
    Day,
    Night,
    DawnGlowVivid,
    DawnGlowMuted,
    DawnAfterglowVivid,
    DawnAfterglowMuted,
    DuskGlowVivid,
    DuskGlowMuted,
    DuskAfterglowVivid,
    DuskAfterglowMuted,
}

/// <summary>How a dawn or dusk looks: spectacular, or grey and quiet.</summary>
internal enum TwilightMood
{
    Vivid,
    Muted,
}

/// <summary>The two panoramas the sky shows now and how far it has gone from the first toward the second.</summary>
internal readonly record struct SkyBlend(SkyPanorama From, SkyPanorama To, float Amount);

/// <summary>
/// Sunrise and sunset as rules (#9805). The sun keeps one bearing all year, so each twilight is
/// painted onto world-fixed panoramas (<c>scripts/make-twilight-sky.py</c>):
/// - glow: the sun on the horizon;
/// - afterglow: the sun a few degrees under it.
///
/// The sky steps from day through the glow and the afterglow to night, blending only neighbours, so
/// each step meets the next without a jump. Each dawn and dusk rolls its own mood from the day.
/// </summary>
internal static class Twilight
{
    /// <summary>The sun's elevation (as a sine) where the sky is the glow panorama alone: the sun on the horizon.</summary>
    internal const double GlowAt = 0d;

    /// <summary>Where the sky is the afterglow alone: the sun about 4.6° under the horizon.</summary>
    internal const double AfterglowAt = -0.08d;

    /// <summary>
    /// The day panorama alone from full day, and the night's from where twilight starts, so the day
    /// and night ends meet the clock's own daylight blend exactly.
    /// </summary>
    internal const double DayAbove = WorldClock.FullDayAt;
    internal const double NightBelow = WorldClock.TwilightStartsAt;

    /// <summary>
    /// How the day gives way to the glow: the share is raised to this power, so the sky stays the
    /// day's until the sun is low and then warms quickly.
    /// </summary>
    private const double GlowOnset = 2d;

    /// <summary>A roll below this makes a muted dawn or dusk; above it, a vivid one.</summary>
    internal const float MutedBelow = 0.35f;

    /// <summary>Separates the dawn's roll from the dusk's on the same day.</summary>
    private const ulong DawnSalt = 0x5A11E5D0A7E5UL, DuskSalt = 0xD05C0FF5E7UL;

    /// <summary>Whether the sun is rising: the morning half of the day, midnight to noon.</summary>
    internal static bool Rising(double dayFraction) => dayFraction < 0.5d;

    /// <summary>
    /// How spectacular a dawn or dusk is, 0 to 1. It is rolled from the day number, so it is the same
    /// every time that twilight is shown.
    /// </summary>
    internal static float Vividness(long day, bool dawn)
    {
        ulong z = unchecked((ulong)day * 0x9E3779B97F4A7C15UL) + (dawn ? DawnSalt : DuskSalt);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (float)((z >> 11) * (1d / (1UL << 53)));
    }

    internal static TwilightMood Mood(float vividness) => vividness < MutedBelow ? TwilightMood.Muted : TwilightMood.Vivid;

    /// <summary>The sky's base for a moment of a day at a vividness: two neighbouring panoramas and the share of the second.</summary>
    internal static SkyBlend Blend(double dayFraction, float vividness)
    {
        double elevation = WorldClock.SunElevation(dayFraction);
        bool dawn = Rising(dayFraction);
        TwilightMood mood = Mood(vividness);
        SkyPanorama glow = Panorama(dawn, afterglow: false, mood);
        SkyPanorama afterglow = Panorama(dawn, afterglow: true, mood);
        if (elevation >= DayAbove) return new(SkyPanorama.Day, glow, 0f);
        if (elevation >= GlowAt) return new(SkyPanorama.Day, glow, (float)Math.Pow(Share(elevation, DayAbove, GlowAt), GlowOnset));
        if (elevation >= AfterglowAt) return new(glow, afterglow, (float)Ease(Share(elevation, GlowAt, AfterglowAt)));
        if (elevation >= NightBelow) return new(afterglow, SkyPanorama.Night, (float)Ease(Share(elevation, AfterglowAt, NightBelow)));
        return new(afterglow, SkyPanorama.Night, 1f);
    }

    /// <summary>The twilight panorama for a side of the day, a stage and a mood.</summary>
    internal static SkyPanorama Panorama(bool dawn, bool afterglow, TwilightMood mood) => (dawn, afterglow, mood) switch
    {
        (true, false, TwilightMood.Vivid) => SkyPanorama.DawnGlowVivid,
        (true, false, TwilightMood.Muted) => SkyPanorama.DawnGlowMuted,
        (true, true, TwilightMood.Vivid) => SkyPanorama.DawnAfterglowVivid,
        (true, true, TwilightMood.Muted) => SkyPanorama.DawnAfterglowMuted,
        (false, false, TwilightMood.Vivid) => SkyPanorama.DuskGlowVivid,
        (false, false, TwilightMood.Muted) => SkyPanorama.DuskGlowMuted,
        (false, true, TwilightMood.Vivid) => SkyPanorama.DuskAfterglowVivid,
        _ => SkyPanorama.DuskAfterglowMuted,
    };

    /// <summary>How far a value has gone from one end of a span toward the other, 0 to 1.</summary>
    private static double Share(double value, double from, double to) => Math.Clamp((value - from) / (to - from), 0d, 1d);

    private static double Ease(double t) => t * t * (3d - (2d * t));
}
