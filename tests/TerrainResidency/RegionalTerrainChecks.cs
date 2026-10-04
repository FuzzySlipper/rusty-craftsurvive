using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

internal static class RegionalTerrainChecks
{
    internal static void Run()
    {
        MapSample[] families = [new(20, 0.7, 0.1, 0.6, 0.5, 0), new(20, 0.6, 0.6, 0.6, 0.5, 0), new(20, 0.1, 0.5, 0.6, 0.5, 0)];
        foreach (ulong seed in new ulong[] { 1, 12345 })
        {
            double[][] relief = families.Select(_ => new double[4096]).ToArray();
            for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                double px = -512 + x * 2, pz = 192 + z * 2;
                for (int f = 0; f < families.Length; f++)
                {
                    double value = RegionalTerrain.Relief(seed, families[f], px, pz);
                    relief[f][z * 64 + x] = value;
                    Check.That(double.IsFinite(value) && Math.Abs(value) <= WorldMap.LocalReliefLimit, "regional relief respects finite map anchoring budget");
                    Check.That(RegionalTerrain.Relief(seed, families[f] with { Detail = 0 }, px, pz) == 0, "channel floor stays exact, including independent fine noise");
                    Check.That(RegionalTerrain.Relief(seed, families[f] with { Protection = 1 }, px, pz) == 0, "protected route and landmark elevation stays exact");
                    Check.That(Math.Abs(value - RegionalTerrain.Relief(seed, families[f], px + 0.00001, pz)) < 0.001, "local sampling has no chunk/lattice seam");
                }
            }
            for (int f = 0; f < families.Length; f++)
            {
                double[] values = relief[f];
                double rmsDifference = Math.Sqrt(values.Zip(relief[(f + 1) % families.Length]).Average(v => Math.Pow(v.First - v.Second, 2)));
                Check.That(rmsDifference > 1, "climate families differ in geometry with identical map height and texture-independent inputs");
                Check.That(values.Max() - values.Min() > 4, "each family has structural relief within one local patch");
                Console.WriteLine($"regional seed={seed} family={f} range={values.Max() - values.Min():F2} rmsDifference={rmsDifference:F2}");
            }
        }
        // Both climate boundaries interpolate geometry instead of switching recipes at labels.
        for (int i = 0; i <= 100; i++)
        {
            MapSample g = families[1] with { Temperature = i / 100d, Moisture = i / 100d };
            var weights = RegionalTerrain.Weights(g);
            Check.That(weights.Dry >= 0 && weights.Upland >= -1e-15 && weights.Frozen >= 0
                && Math.Abs(weights.Dry + weights.Upland + weights.Frozen - 1) < 1e-12, "continuous family weights partition unity");
            double value = RegionalTerrain.Relief(12345, g, -512, 256);
            Check.That(Math.Abs(value - RegionalTerrain.Relief(12345, g with { Temperature = g.Temperature + 1e-7, Moisture = g.Moisture + 1e-7 }, -512, 256)) < 0.001,
                "no elevation jump at a climatic family transition");
        }
        foreach (ulong seed in new ulong[] { 1, 12345 })
        {
            TerrainConfiguration configuration = new(seed, 8192);
            WorldMap map = WorldMapGenerator.Generate(configuration);
            WorldMap previous = WorldMapGenerator.Generate(configuration with { GeneratorVersion = 18 });
            Check.That(map.Nodes.SequenceEqual(previous.Nodes) && map.Drainage.SequenceEqual(previous.Drainage), "regional detail preserves all saved map and erosion fields across the recipe revision");
            TerrainRecipe recipe = configuration.CreateRecipe(new TestDraws(seed), map);
            Stopwatch watch = Stopwatch.StartNew();
            double checksum = 0;
            foreach (MapSite site in map.Sites)
            for (long z = (long)site.Z - 32; z < site.Z + 32; z++)
            for (long x = (long)site.X - 32; x < site.X + 32; x++)
            {
                MapSample sample = map.Sample(x, z);
                double height = recipe.ContinuousHeightAt(x, z);
                checksum += height;
                Check.That(Math.Abs(height - sample.Elevation) <= WorldMap.LocalReliefLimit, "generated local patch stays anchored to erosion map");
                if (sample.Detail == 0 || sample.Protection == 1)
                    Check.That(height == sample.Elevation, "actual protected and drained terrain is unchanged");
            }
            Console.WriteLine($"regional patches seed={seed} samples=12288 ms={watch.Elapsed.TotalMilliseconds:F2} checksum={checksum:F3}");
        }
    }
}
