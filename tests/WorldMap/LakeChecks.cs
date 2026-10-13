using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// Lakes (#9876): on since generator 31; with the recipe off a world holds none. With them, a continent
/// holds open and closed lakes, found the same way from its saved fields every time - an
/// open one full to its spill with an outlet, a closed one at the level where evaporation balances its
/// inflow and a sink to routing - each a storage node of the water network.
/// </summary>
internal static class LakeChecks
{
    private const ulong Seed = 13;

    /// <summary>
    /// The lake rules by themselves (#9876 review): a river crossing a lake is split into the reach that
    /// enters it, the one under it and the one that leaves; a closed lake fed at two separate sinks takes in
    /// both, and stands where evaporation from it balances their sum.
    /// </summary>
    internal static void Rules()
    {
        List<int[]> crossing = MapDrainage.SplitAtLakes([[0, 1, 2, 3, 4, 5, 6, 7, 8, 9]], n => n is >= 3 and <= 6 ? 0 : -1);
        Check.That(crossing.Count == 3 && crossing[0].SequenceEqual([0, 1, 2, 3]) && crossing[1].SequenceEqual([3, 4, 5, 6]) && crossing[2].SequenceEqual([6, 7, 8, 9]),
            $"a river crossing a lake becomes the reach in, the reach under it and the reach out, joined at its shores ({string.Join(" | ", crossing.Select(p => string.Join(",", p)))})");
        Check.That(MapDrainage.SplitAtLakes([[0, 1, 2]], _ => -1).Single().SequenceEqual([0, 1, 2])
            && MapDrainage.SplitAtLakes([[0, 1, 2, 3]], n => n >= 2 ? 0 : -1).Select(p => p.Length).SequenceEqual([3, 2]),
            "a river clear of lakes is unchanged, and one ending in a lake ends under it");

        // A bowl with a high rim, fed at two separate sink cells of ten nodes' discharge each.
        MapGrid grid = new(16, 1000, 8000);
        float[] elevation = new float[grid.Count], discharge = new float[grid.Count], temperature = new float[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            double r = Math.Sqrt((grid.X(i) * grid.X(i)) + (grid.Z(i) * grid.Z(i)));
            // The sea around the edge drains everything outside the rim.
            elevation[i] = grid.IsEdge(i) ? 0 : (float)(50 + (450 * Math.Min(1, r / 6000 * (r / 6000))));
            discharge[i] = 1;
            temperature[i] = 1;
        }

        int a = grid.Nearest(-1000, 0), b = grid.Nearest(1000, 0);
        discharge[a] = discharge[b] = 10;
        HashSet<int> sinks = [a, b];
        Lake both = MapLakes.Find(grid, elevation, discharge, temperature, null, sinks).Single();
        Lake larger = MapLakes.Find(grid, elevation, discharge, temperature, null).Single();
        double cellKm2 = grid.Spacing * grid.Spacing / 1e6;
        Check.That(both.Closed && Math.Abs(both.InflowFlow - WaterBudget.Flow(20 * cellKm2)) < 1e-9 && Math.Abs(larger.InflowFlow - WaterBudget.Flow(10 * cellKm2)) < 1e-9,
            $"a closed lake takes in every sink its water stops at ({both.InflowFlow:F3} m3/s), not just the largest ({larger.InflowFlow:F3})");
        double Loss(double area) => both.EvaporationMetres * area * 1e6 / (365.25 * 24 * 3600);
        Check.That(Loss(both.AreaSquareKilometres) <= both.InflowFlow + Loss(cellKm2) && Loss(both.AreaSquareKilometres + cellKm2) >= both.InflowFlow - Loss(cellKm2)
            && both.AreaSquareKilometres > larger.AreaSquareKilometres,
            $"it stands where evaporation from it balances both ({both.AreaSquareKilometres:F0} km2 against {larger.AreaSquareKilometres:F0} km2 for one)");
    }

    internal static void Run(WorldMap defaultContinent)
    {
        Check.That(defaultContinent.Lakes.Count > 0, $"the generator's own continent holds lakes ({defaultContinent.Lakes.Count})");

        // A point's lake is the one its level comes from, out to the shore (#9878: a lake freezes whole, with no unfrozen ring).
        int sampled = 0, disowned = 0;
        foreach (Lake lake in defaultContinent.Lakes)
            foreach (int node in lake.Basin)
                for (double fx = -0.45; fx <= 0.45; fx += 0.3)
                for (double fz = -0.45; fz <= 0.45; fz += 0.3)
                {
                    double x = defaultContinent.Grid.X(node) + (fx * defaultContinent.Spacing), z = defaultContinent.Grid.Z(node) + (fz * defaultContinent.Spacing);
                    double level = defaultContinent.LakeLevelAt(x, z);
                    if (double.IsNegativeInfinity(level)) continue;
                    sampled++;
                    if (defaultContinent.LakeAt(x, z) is not Lake owner || owner.Level != level) disowned++;
                }
        Check.That(sampled > 0 && disowned == 0, $"every point under a lake's level belongs to that lake ({disowned} of {sampled} not)");

        TerrainConfiguration configuration = new(Seed, MapScale.DefaultContinentalSize);
        MapRecipe off = MapRecipe.Default with { Lakes = MapRecipe.Default.Lakes with { Enabled = false } };
        Check.That(new WorldMap(configuration, MapSimulation.Run(configuration, off, ContinentDesign.For(configuration.Size))).Lakes.Count == 0,
            "with the recipe's lakes off, erosion leaves no basin deep enough to hold one");
        MapRecipe recipe = MapRecipe.Default;
        MapFields fields = MapSimulation.Run(configuration, recipe, ContinentDesign.For(configuration.Size));
        WorldMap map = new(configuration, fields);
        IReadOnlyList<Lake> lakes = map.Lakes;
        Check.That(lakes.Any(l => l.Closed) && lakes.Any(l => !l.Closed), $"a continent with lakes holds open and closed ones ({lakes.Count})");
        Check.That(new WorldMap(configuration, fields).Lakes.Select(l => (l.Kind, l.Level, l.Closed)).SequenceEqual(lakes.Select(l => (l.Kind, l.Level, l.Closed))),
            "lakes are found the same way from the saved fields every time");

        double cellKm2 = map.Grid.Spacing * map.Grid.Spacing / 1e6;
        bool open = true, closed = true, levels = true, wet = true;
        foreach (Lake lake in lakes)
        {
            wet &= lake.Nodes.Length > 0 && lake.Nodes.All(n => fields.Elevation[n] < lake.Level) && lake.Level <= lake.Spill + 1e-6;
            levels &= lake.LevelArea.Zip(lake.LevelArea.Skip(1)).All(p => p.Second.Level >= p.First.Level && p.Second.AreaSquareKilometres >= p.First.AreaSquareKilometres);
            if (lake.Closed)
            {
                double loss = lake.EvaporationMetres * lake.AreaSquareKilometres * 1e6 / (365.25 * 24 * 3600);
                closed &= lake.Outlet < 0 && lake.Level < lake.Spill && loss <= lake.InflowFlow + (lake.EvaporationMetres * cellKm2 * 1e6 / (365.25 * 24 * 3600));
            }
            else
            {
                open &= lake.Outlet >= 0 && Math.Abs(lake.Level - lake.Spill) < 1e-9;
            }
        }

        Check.That(wet, "a lake's nodes lie under its level, which is at most its spill");
        Check.That(levels, "a lake's area grows with its level");
        Check.That(open, "an open lake stands full to its spill and has an outlet");
        Check.That(closed, "a closed lake stands below its spill where evaporation from it takes its inflow, with no outlet");
        bool[] outlets = MapLakes.Outlets(map.Grid, fields.Elevation.Select(v => (double)v).ToArray(), lakes);
        Check.That(lakes.Where(l => l.Closed).All(l => l.Nodes.All(n => outlets[n])), "a closed lake is a sink to routing");

        MapDrainage drainage = new(map);
        WaterNetwork network = drainage.Network;
        Check.That(network.Lakes.Count == lakes.Count && network.Lakes.All(n => n.Nodes.Length > 0),
            "every lake is a storage node of the water network, on the drainage lattice");
        Check.That(network.Lakes.Where(n => n.Lake.Closed).All(n => n.Outflow < 0) && network.Lakes.Any(n => n.Inflows.Length > 0),
            "rivers flow into lakes, and a closed lake has no river flowing on");
        // The lake as storage between its rivers (#9876 review): what flows in, what lies under it and what flows on are separate reaches.
        Check.That(network.Lakes.All(n => !n.Inflows.Contains(n.Outflow) && !n.Interior.Contains(n.Outflow) && !n.Inflows.Intersect(n.Interior).Any()),
            "no reach is both a lake's inflow and its outflow, or under it and either");
        bool joined = true;
        int crossed = 0;
        foreach (LakeNode lake in network.Lakes.Where(n => n.Outflow >= 0 && n.Interior.Length > 0))
            foreach (int inflow in lake.Inflows)
            {
                // Down from where a river enters, the lake's own reaches come before the one that flows on.
                List<int> chain = [];
                for (int r = network.Reaches[inflow].Downstream; r >= 0 && chain.Count < network.Reaches.Count && r != lake.Outflow; r = network.Reaches[r].Downstream) chain.Add(r);
                if (chain.Count == 0 || !chain.All(lake.Interior.Contains)) continue;
                crossed++;
                joined &= network.Reaches[chain[^1]].Downstream == lake.Outflow;
            }

        Check.That(crossed > 0 && joined, $"a river entering a lake runs through the reaches under it to the one flowing on ({crossed} crossings)");
        Console.WriteLine($"lakes (seed {Seed}): " + string.Join("; ", lakes.Select(l =>
            $"{l.Kind} {l.AreaSquareKilometres:F0} km2 {l.MaximumDepth:F0} m{(l.Closed ? " closed" : "")}")));
    }
}
