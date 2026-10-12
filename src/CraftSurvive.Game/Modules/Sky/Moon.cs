using System.Numerics;

namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// The moon's light and look as rules (#9806); <see cref="WorldClock"/> keeps its calendar.
/// - Brightness follows the phase and the moon's height. A half moon still lights the way and a
///   full moon high in a clear sky is easy to walk by; a new moon leaves only the night's fill.
/// - The moon rises large and warm, whitening as it climbs.
/// </summary>
internal static class Moon
{
    /// <summary>A full moon's light, high in a clear sky (the sun's is 1.5).</summary>
    internal const float FullIntensity = 0.28f;

    /// <summary>The lit share is raised to this: a half moon gives about a third of a full one's light.</summary>
    private const double PhaseFalloff = 1.5d;

    /// <summary>Low in the sky the light crosses more air: this share of it is left at the horizon, all of it from this height (a sine) up.</summary>
    private const double LowShare = 0.35d, ClearAbove = 0.3d;

    /// <summary>The moon fades in as it rises and out as it sets, between these elevations (sines), as its disc crosses the horizon.</summary>
    internal const double RisesFrom = -0.02d, RisenAt = 0.03d;

    /// <summary>Its light high in the sky (cool, as moonlight reads) and at the horizon (warm, through more air); it whitens by this height.</summary>
    private static readonly Vector3 HighColour = new(0.6f, 0.7f, 1f);
    private static readonly Vector3 LowColour = new(1f, 0.7f, 0.42f);
    private const double WhiteAbove = 0.35d;

    /// <summary>Its disc in degrees, high and at the horizon: a rising moon looks large.</summary>
    internal const float DiscDegrees = 1.6f, LowDiscDegrees = 2.6f;

    /// <summary>How much of a full moon's light there is now, 0 to 1: phase, height and whether it is up at all.</summary>
    internal static float Share(WorldTime time)
    {
        double elevation = WorldClock.MoonElevation(time);
        double phase = Math.Pow(WorldClock.MoonLit(time), PhaseFalloff);
        double height = LowShare + ((1d - LowShare) * Smooth(elevation, 0d, ClearAbove));
        return (float)(phase * height * Smooth(elevation, RisesFrom, RisenAt));
    }

    /// <summary>The moon's light now, in a clear sky.</summary>
    internal static float Intensity(WorldTime time) => FullIntensity * Share(time);

    /// <summary>The moon's colour at a height (as a sine): warm at the horizon, whitening as it climbs.</summary>
    internal static Vector3 Colour(double elevation) => Vector3.Lerp(LowColour, HighColour, (float)Smooth(elevation, 0d, WhiteAbove));

    /// <summary>The disc's size at a height: larger low.</summary>
    internal static float Disc(double elevation) => LowDiscDegrees + ((DiscDegrees - LowDiscDegrees) * (float)Smooth(elevation, 0d, WhiteAbove));

    private static double Smooth(double value, double from, double to)
    {
        double t = Math.Clamp((value - from) / (to - from), 0d, 1d);
        return t * t * (3d - (2d * t));
    }
}
