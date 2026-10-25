namespace CraftSurvive.Game.Modules.Water;

/// <summary>The near water's mesh spacing and how waves fade into the shore; the waves themselves are <see cref="SeaWaves"/>.</summary>
internal static class WaterWaveSettings
{
    internal const int MeshSpacingMetres = 1;
    internal const float ShoreClearance = 0.15f, FullDepth = 2f;

    /// <summary>How far under its own ground a dry shore corner lower than its water's surface is drawn (metres).</summary>
    internal const double ShoreSink = 0.1;

    /// <summary>Pin dry and frozen vertices; ease in waves and opacity over the shallow bed.</summary>
    internal static float Coverage(double depth, bool ice)
    {
        if (ice) return 0;
        float t = (float)Math.Clamp((depth - ShoreClearance) / (FullDepth - ShoreClearance), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
