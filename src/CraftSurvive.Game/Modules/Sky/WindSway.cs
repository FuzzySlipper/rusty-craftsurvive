using System.Numerics;

namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// The scene wind the grass, bushes and trees sway in (owner, 2026-10-13: "movement is good but it might
/// need to be calibrated to a lower range ... some offset cycling so it feels a bit less uniform").
/// <list type="bullet">
/// <item>Its strength runs from a light breeze in still weather to a gale's with the weather's wind channel.</item>
/// <item>Its gust share rises with the wind.</item>
/// <item>Over the seconds it rises and falls in slow lulls and gusts and veers a little about the weather's
/// flow. These are sums of a few slow waves of unrelated periods, so the pattern never visibly repeats.</item>
/// </list>
/// The Engine phases each plant's lean by where it stands; this varies the whole scene's wind over time.
/// Engine-free.
/// </summary>
internal static class WindSway
{
    /// <summary>The Engine wind strength in still weather and in a full gale (the weather's wind channel 0 and 1).</summary>
    internal const float CalmStrength = 0.45f, GaleStrength = 1.6f;

    /// <summary>The share of the lean that comes and goes in the Engine's own gusts, still and in a gale.</summary>
    internal const float CalmGust = 0.35f, GaleGust = 0.75f;

    /// <summary>How far the strength swings either way in the lulls and gusts, as a share of itself.</summary>
    internal const float Swing = 0.35f;

    /// <summary>How far the wind veers either way about the weather's flow (degrees).</summary>
    internal const float VeerDegrees = 12f;

    /// <summary>The lulls and gusts: periods (seconds) and weights; the weights sum to one.</summary>
    private static readonly (double Period, double Weight, double Phase)[] Gusts = [(13.7, 0.5, 0.0), (23.3, 0.3, 1.9), (47.9, 0.2, 4.2)];

    /// <summary>The veering: periods (seconds) and weights; the weights sum to one.</summary>
    private static readonly (double Period, double Weight, double Phase)[] Veers = [(31.1, 0.6, 0.7), (71.3, 0.4, 3.1)];

    /// <summary>The scene wind's strength for the weather's wind channel (0 to 1) at a time (seconds).</summary>
    internal static float Strength(float windChannel, double seconds)
    {
        float steady = CalmStrength + ((GaleStrength - CalmStrength) * Math.Clamp(windChannel, 0f, 1f));
        return steady * (1f + (Swing * (float)Wave(Gusts, seconds)));
    }

    /// <summary>The gust share for the weather's wind channel.</summary>
    internal static float Gust(float windChannel) => CalmGust + ((GaleGust - CalmGust) * Math.Clamp(windChannel, 0f, 1f));

    /// <summary>The wind's direction over the ground at a time: the weather's flow, veered a little.</summary>
    internal static Vector2 Direction(Vector2 flow, double seconds)
    {
        double angle = VeerDegrees * Math.PI / 180 * Wave(Veers, seconds);
        float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
        return new((flow.X * c) - (flow.Y * s), (flow.X * s) + (flow.Y * c));
    }

    /// <summary>A sum of slow waves, -1 to 1.</summary>
    private static double Wave((double Period, double Weight, double Phase)[] waves, double seconds)
    {
        double sum = 0;
        foreach ((double period, double weight, double phase) in waves) sum += weight * Math.Sin((2 * Math.PI * seconds / period) + phase);
        return sum;
    }
}
