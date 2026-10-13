using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The one water rule (#9877): the sea everywhere, raised by a river over its channel or a lake over its
/// basin, at fractional heights; the body is the one whose surface stands highest over the ground.
/// </summary>
internal static class WaterRuleChecks
{
    internal static void Run()
    {
        MapSample land = new(40, 0.5, 0.5, 0, 0);
        Check.That(WaterRules.Surface(land) == WaterRules.SeaSurface && WaterRules.Body(land, 40) == WaterBody.None,
            "dry land has only the sea's level under it, and no water over it");
        Check.That(WaterRules.Body(land with { Elevation = 1.2 }, 1.2) == WaterBody.Sea, "ground below the sea's level is under the sea");
        MapSample river = land with { RiverSurface = 37.35 };
        Check.That(WaterRules.Surface(river) == 37.35 && WaterRules.Body(river, 36.1) == WaterBody.River && WaterRules.Body(river, 37.4) == WaterBody.None,
            "a river stands at its own fractional surface over its channel");
        MapSample lake = land with { LakeSurface = 622.61 };
        Check.That(WaterRules.Surface(lake) == 622.61 && WaterRules.Body(lake, 586.4) == WaterBody.Lake && WaterRules.Body(lake, 623) == WaterBody.None,
            "a lake stands at its level over its basin, and its shore above the level is dry");
        MapSample both = land with { RiverSurface = 50.2, LakeSurface = 50.8 };
        Check.That(WaterRules.Surface(both) == 50.8 && WaterRules.Body(both, 49) == WaterBody.Lake, "where a river enters a lake, the lake's level stands over it");

        // The near water's square and the far field's water meet at the waterline on every edge (#9877 review): the far
        // field leaves its water out only strictly inside, so its vertices on any edge keep the water's height.
        (double X, double Z) centre = (10, 10);
        const double Outer = 112;
        int radius = NearWaterSquare.TileRadius(Outer);
        double low = -radius * NearWaterSquare.TileMetres, high = (radius + 1) * NearWaterSquare.TileMetres;
        bool edges = true;
        foreach ((double x, double z) in new[] { (low, 30.0), (high, 30.0), (30.0, low), (30.0, high) })
            edges &= !NearWaterSquare.CoversInside(centre, Outer, x, z);
        Check.That(edges && NearWaterSquare.Covers(centre, Outer, low, 30) && NearWaterSquare.CoversInside(centre, Outer, low + 0.01, 30)
            && NearWaterSquare.CoversInside(centre, Outer, high - 0.01, 30) && !NearWaterSquare.Covers(centre, Outer, high, 30),
            "the far field keeps its water on all four edges of the near square and leaves it out only inside");
    }
}
