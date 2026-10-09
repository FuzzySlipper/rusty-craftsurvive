namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>Where a known place's silhouette is drawn, by its distance from the player.</summary>
internal enum LandmarkBand
{
    /// <summary>Not drawn: within the near ground (its own structure stands there) or out of range.</summary>
    None,

    /// <summary>In the world, on the far field's ground.</summary>
    World,

    /// <summary>In the backdrop, on the map's ground.</summary>
    Backdrop,
}

/// <summary>
/// The pure rules of known places on the horizon (H3 #9781): which layer a place is drawn in, how
/// much larger than life its silhouette is drawn so it stays readable, and how lit home's windows are.
/// </summary>
internal static class HorizonLandmarkRules
{
    /// <summary>Places are drawn out to this far from the player.</summary>
    internal const double RangeMetres = 40_000;
    /// <summary>A silhouette is at least this tall as seen from the player.</summary>
    internal const double MinimumDegrees = 1.0;
    /// <summary>Lights show as the daylight falls from the first value to the second, full by then.</summary>
    internal const double LightsFrom = 0.45, LightsFull = 0.15;

    /// <param name="nearEdge">The near ground's edge from the player: its own structure is built within it.</param>
    /// <param name="reach">The far field's reach from the player: past it the backdrop is the ground.</param>
    internal static LandmarkBand Band(double distance, double nearEdge, double reach) =>
        distance < nearEdge || distance > RangeMetres ? LandmarkBand.None
        : distance >= reach ? LandmarkBand.Backdrop
        : LandmarkBand.World;

    /// <summary>How many times life size a silhouette of <paramref name="heightMetres"/> is drawn at a distance: never less than one.</summary>
    internal static double Grow(double distance, double heightMetres, double minimumDegrees = MinimumDegrees) =>
        Math.Max(1, distance * Math.Tan(minimumDegrees * Math.PI / 180) / heightMetres);

    /// <summary>How lit home's windows are at a daylight (0 night to 1 day): none by day, fully by night.</summary>
    internal static double Lit(double daylight)
    {
        double t = Math.Clamp((Math.Clamp(daylight, 0, 1) - LightsFrom) / (LightsFull - LightsFrom), 0, 1);
        return t * t * (3 - (2 * t));
    }
}
