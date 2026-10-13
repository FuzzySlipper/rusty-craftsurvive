namespace CraftSurvive.Game.Modules.Water;

/// <summary>Visual wave limits and shore coverage; neither changes the gameplay water surface.</summary>
internal static class WaterWaveSettings
{
    internal const int MeshSpacingMetres = 1;
    internal const float DefaultAmplitude = 0.45f, MaximumAmplitude = 0.6f;
    internal const float DefaultChoppiness = 0.9f, MaximumChoppiness = 1.1f;
    internal const float ShoreClearance = 0.15f, FullDepth = 2f, EdgeFadeMetres = 24f;

    /// <summary>Pin dry and frozen vertices; ease in waves and opacity over the shallow bed.</summary>
    internal static float Coverage(double depth, bool ice)
    {
        if (ice) return 0;
        float t = (float)Math.Clamp((depth - ShoreClearance) / (FullDepth - ShoreClearance), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
