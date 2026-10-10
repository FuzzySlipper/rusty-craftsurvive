using System.Numerics;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The bounds of a designed continent (#9817): the frontier just past the passes' crest, and the open sea.
/// One rule answers for every path, so these hold it on the published continent: where it lies, and that
/// map routing never crosses it. Walking, arrival and the debug teleport ask the same rule live.
/// </summary>
internal static class FrontierChecks
{
    /// <summary>How far past a pass's saddle, toward the mainland, the frontier must already be behind the expedition.</summary>
    private const double PastSaddleMetres = 6000;

    /// <summary>A design whose pass or site lies beyond its own frontier is refused.</summary>
    internal static void DesignsKeepTheirWayOpen()
    {
        ContinentDesign design = ContinentDesign.For(MapScale.DefaultContinentalSize)!;
        Check.That(design.Frontier.Count >= 3, "the frontier peninsula draws its frontier");
        Check.That(design.Passes.All(pass => !design.BeyondFrontier(pass.At[0], pass.At[1]))
            && design.Sites.All(site => !design.BeyondFrontier(site.At[0], site.At[1])),
            "every pass's saddle and reserved site stands on the frontier's near side");
        DesignLand mainland = design.Land.Single(land => land.Forbidden);
        Check.That(design.BeyondFrontier(0.5, -0.95) && mainland.SignedDistance(0.5, -0.95) > 0, "the mainland's heart lies beyond the frontier");

        bool refused;
        try
        {
            _ = (design with { Passes = [.. design.Passes, new DesignPass { Name = "A pass too far", At = [0.5, -0.95] }] }).Validate();
            refused = false;
        }
        catch (InvalidDataException) { refused = true; }

        Check.That(refused, "a design with a pass beyond its frontier is refused");
    }

    internal static void Continent(WorldMap map)
    {
        ContinentDesign design = ContinentDesign.For(map.Configuration.Size)!;
        FrontierBound bound = FrontierBound.For(map) ?? throw new InvalidOperationException("A designed continent has bounds.");
        Check.That(ReferenceEquals(bound, FrontierBound.For(map)), "a continent's bounds are built once");

        FrontierReport reach = DesignTopology.Reach(map.Grid, i => map.Fields.Elevation[i] >= GenerationConstants.WaterLevel, design, bound.Sites);
        Check.That(reach.Holds, $"the expedition reaches the passes and the towns and none of the mainland's country: {reach}");

        // The frontier: the passes are walked to, a little past them is not, nor is the mainland.
        foreach (DesignPass pass in design.Passes)
        {
            double x = pass.At[0] * map.Radius, z = pass.At[1] * map.Radius;
            Check.That(bound.At(x, z) == BoundSide.Open, $"{pass.Name}'s saddle is inside the bounds");
            Check.That(bound.At(x, z - PastSaddleMetres) == BoundSide.BeyondFrontier, $"{PastSaddleMetres / 1000:F0} km past {pass.Name}, toward the mainland, is beyond the frontier");
        }

        Check.That(bound.At(0.5 * map.Radius, -0.95 * map.Radius) == BoundSide.BeyondFrontier, "the mainland is beyond the frontier");

        // The towns stand on dry ground inside the bounds, near where they were drawn.
        Check.That(bound.Sites.Count == design.Sites.Count, $"every reserved site stands somewhere ({bound.Sites.Count} of {design.Sites.Count})");
        foreach (PlacedSite site in bound.Sites)
        {
            DesignSite drawn = design.Sites.Single(s => s.Name == site.Name);
            double away = Math.Sqrt(Math.Pow(site.X - (drawn.At[0] * map.Radius), 2) + Math.Pow(site.Z - (drawn.At[1] * map.Radius), 2));
            Check.That(bound.Open(site.X, site.Z) && map.Sample(site.X, site.Z).Elevation >= GenerationConstants.WaterLevel && away < 20_000,
                $"{site.Name} stands on dry ground inside the bounds, {away / 1000:F1} km from where it was drawn");
        }

        // The sea: a short swim from any shore is allowed, the open sea is not, nor anything off the map.
        Check.That(bound.At(-0.98 * map.Radius, 0.98 * map.Radius) == BoundSide.OpenSea, "the far ocean is the open sea");
        Check.That(bound.At(map.Radius * 1.5, 0) != BoundSide.Open, "off the map is outside the bounds");
        (Vector2 shore, Vector2 sea) = ShoreAndSea(map);
        Vector2 offshore = Vector2.Lerp(shore, sea, 0.5f);
        Check.That(bound.At(offshore.X, offshore.Y) == BoundSide.Open && bound.SeaMetres(offshore.X, offshore.Y) < FrontierBound.OpenSeaMetres,
            $"half a lattice step off a shore is inside the bounds ({bound.SeaMetres(offshore.X, offshore.Y):F0} m out)");

        // Map routing never crosses the bounds: no passable node lies past them, and routes stop short.
        TravelCostModel cost = new(map);
        int crossed = 0;
        for (int i = 0; i < map.Grid.Count; i++)
            if (cost.Passable(i) && !bound.Open(map.Grid.X(i), map.Grid.Z(i))) crossed++;
        Check.That(crossed == 0, $"no map node past the bounds is passable ({crossed})");

        Vector2 home = Vector2.Zero;
        foreach (PlacedSite town in bound.Sites)
        {
            TravelRoute? route = TravelRouter.Plan(cost, home, new((float)town.X, (float)town.Z), town.Name);
            Check.That(route is not null && route.Points.All(p => bound.Open(p.X, p.Y)), $"a route from the arrival area reaches {town.Name} inside the bounds");
        }

        TravelRoute? beyond = TravelRouter.Plan(cost, home, new(0.5f * (float)map.Radius, -0.95f * (float)map.Radius), "the mainland");
        Check.That(beyond is null || beyond.Points.All(p => bound.Open(p.X, p.Y)),
            $"a route toward the mainland stops on the near side ({(beyond is null ? "no route" : $"ends at {beyond.Points[^1].X:F0},{beyond.Points[^1].Y:F0}")})");
    }

    /// <summary>A land node with sea beside it, and that sea node, for the shore's swim.</summary>
    private static (Vector2 Shore, Vector2 Sea) ShoreAndSea(WorldMap map)
    {
        MapGrid grid = map.Grid;
        for (int z = 1; z < grid.Segments; z++)
        for (int x = 1; x < grid.Segments; x++)
        {
            int i = (z * grid.Side) + x;
            if (map.Fields.Elevation[i] < GenerationConstants.WaterLevel || map.Fields.Elevation[i + 1] >= GenerationConstants.WaterLevel) continue;
            if (!map.Contains(grid.X(i), grid.Z(i)) || ContinentDesign.For(map.Configuration.Size)!.BeyondFrontier(grid.X(i) / grid.Radius, grid.Z(i) / grid.Radius)) continue;
            return (new((float)grid.X(i), (float)grid.Z(i)), new((float)grid.X(i + 1), (float)grid.Z(i + 1)));
        }

        throw new InvalidOperationException("A continent has a shore.");
    }
}
