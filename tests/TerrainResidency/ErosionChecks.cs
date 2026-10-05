using System.Diagnostics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>Simulated geography: drainage reaches the sea, rivers descend and connect, climate and biomes cohere.</summary>
internal static class ErosionChecks
{
    internal static void Run()
    {
        // Priority-flood: a closed depression fills to its spill height and then drains.
        {
            MapGrid grid = MapGrid.For(16 * (int)MapGrid.TargetSpacing);
            double[] height = new double[grid.Count];
            bool[] outlet = new bool[grid.Count];
            for (int i = 0; i < grid.Count; i++)
            {
                double x = grid.X(i), z = grid.Z(i);
                height[i] = 10 + Math.Abs(x) / 100 - 5 * Math.Exp(-(x * x + z * z) / 20000);
                outlet[i] = grid.IsEdge(i) && i % grid.Side == 0;
            }
            double[] rain = Enumerable.Repeat(1d, grid.Count).ToArray();
            MapFlow flow = MapFlow.Route(grid, height, outlet, rain, MapFlow.FillGradient);
            Check.That(Enumerable.Range(0, grid.Count).All(i => outlet[i] || height[flow.Receiver[i]] < height[i]),
                "every filled node drains through a strictly lower receiver");
            Check.That(Math.Abs(Enumerable.Range(0, grid.Count).Where(i => outlet[i]).Sum(i => flow.Discharge[i]) - grid.Count) < 1e-6,
                "all rain leaves through an outlet; no closed basin swallows it");
        }

        foreach (ulong seed in new ulong[] { TerrainConstants.DefaultSeed, 1, 12345 })
        {
            TerrainConfiguration configuration = new(seed, TerrainConstants.DefaultSize);
            Stopwatch watch = Stopwatch.StartNew();
            WorldMap map = new(configuration, MapSimulation.Run(configuration));
            double generateMs = watch.Elapsed.TotalMilliseconds;
            MapBiome[] biomes = Enumerable.Range(0, map.Grid.Count).Select(i => WorldMap.Biome(map.Node(i))).ToArray();
            int distinct = biomes.Where(b => b != MapBiome.Sea).Distinct().Count();
            Console.WriteLine($"seed={seed} spacing={map.Spacing:F1} generateMs={generateMs:F0} reaches={map.Rivers.Reaches.Count} biomes={distinct} "
                + $"relief={map.Fields.Elevation.Min():F1}..{map.Fields.Elevation.Max():F1} fingerprint={map.Fingerprint:x16}");
            Check.That(distinct >= 6, "a default-size world spans many environments");
            Check.That(map.Rivers.Reaches.Count >= 10, "a default-size world has a river network");

            HashSet<(double, double)> starts = map.Rivers.Reaches.Select(r => (Math.Round(r[0].X, 3), Math.Round(r[0].Z, 3))).ToHashSet();
            foreach (RiverPoint[] reach in map.Rivers.Reaches)
            {
                for (int k = 1; k < reach.Length; k++)
                    Check.That(reach[k].Surface <= reach[k - 1].Surface + 1e-9, "a river's surface never rises downstream");
                RiverPoint mouth = reach[^1];
                bool joins = starts.Contains((Math.Round(mouth.X, 3), Math.Round(mouth.Z, 3)));
                int node = (int)Math.Round((mouth.Z + map.Radius) / map.Spacing) * map.Side + (int)Math.Round((mouth.X + map.Radius) / map.Spacing);
                bool sea = map.Fields.Elevation[node] < GenerationConstants.WaterLevel
                    || !map.Contains(mouth.X + map.Spacing, mouth.Z) || !map.Contains(mouth.X - map.Spacing, mouth.Z)
                    || !map.Contains(mouth.X, mouth.Z + map.Spacing) || !map.Contains(mouth.X, mouth.Z - map.Spacing);
                Check.That(joins || sea, "every reach ends in another reach, the sea or the border");
            }

            // Channel centrelines are resolved below their water and are free of local noise.
            TerrainRecipe recipe = configuration.CreateRecipe(new TestDraws(seed), map);
            RiverPoint wide = map.Rivers.Reaches.SelectMany(r => r.Skip(r.Length / 3).Take(r.Length / 3))
                .Where(p => p.Surface > GenerationConstants.WaterLevel + 2).MaxBy(p => p.HalfWidth);
            MapSample centre = map.Sample(wide.X, wide.Z);
            Check.That(centre.InRiver && centre.Detail == 0 && centre.Elevation < centre.RiverSurface,
                "a river centreline is a quiet channel below its water surface");
            TerrainChunkGenerator generator = new(recipe);
            long waterTop = (long)Math.Floor(centre.RiverSurface);
            TerrainChunk chunk = generator.Generate(new VoxelAddress((long)wide.X, waterTop, (long)wide.Z).Chunk, new(seed, []));
            Check.That(chunk.Materials.Span.Contains((ushort)BlockId.Water), "local terrain holds water in an inland river above the sea");

            // Climate follows terrain: the wettest and driest land differ, and both extremes exist.
            Check.That(biomes.Contains(MapBiome.Desert) || biomes.Contains(MapBiome.Shrubland) || biomes.Contains(MapBiome.ColdSteppe),
                "every world has dry country");
            Check.That(biomes.Any(b => b is MapBiome.TemperateForest or MapBiome.BorealForest or MapBiome.Rainforest),
                "every world has forest");
            Check.That(biomes.Contains(MapBiome.IceField) || biomes.Contains(MapBiome.Tundra), "every world has frozen country");

            // Restoring the saved fields rebuilds the same rivers.
            WorldMap restored = new(configuration, map.Fields);
            Check.That(restored.Fingerprint == map.Fingerprint && restored.Rivers.Reaches.Count == map.Rivers.Reaches.Count
                && restored.Rivers.Reaches.Zip(map.Rivers.Reaches).All(p => p.First.SequenceEqual(p.Second)),
                "rivers are rebuilt identically from saved fields");
        }

        // Larger worlds hold more geography at the same spacing rather than a stretched copy.
        WorldMap small = WorldMapGenerator.Generate(new(12345, 4096));
        WorldMap large = WorldMapGenerator.Generate(new(12345, 16384));
        Check.That(large.Grid.Count > small.Grid.Count * 10 && Math.Abs(large.Spacing - small.Spacing) < 1,
            "world extent adds nodes at a fixed spacing");
    }
}
