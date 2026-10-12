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
        Console.WriteLine($"lakes (seed {Seed}): " + string.Join("; ", lakes.Select(l =>
            $"{l.Kind} {l.AreaSquareKilometres:F0} km2 {l.MaximumDepth:F0} m{(l.Closed ? " closed" : "")}")));
    }
}
