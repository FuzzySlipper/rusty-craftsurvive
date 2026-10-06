using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>continent [seed] [size] [out]: generate a continent and write its fields for inspection (#9549).</summary>
internal static class ContinentDump
{
    internal static void Run(string[] args)
    {
        ulong seed = args.Length > 1 ? ulong.Parse(args[1]) : 777;
        int size = args.Length > 2 ? int.Parse(args[2]) : MapScale.DefaultContinentalSize;
        string output = args.Length > 3 ? args[3] : "continent.bin";
        Stopwatch clock = Stopwatch.StartNew();
        WorldMap map = WorldMapGenerator.Generate(new TerrainConfiguration(seed, size));
        double ms = clock.Elapsed.TotalMilliseconds;
        MapGrid grid = map.Grid;
        using BinaryWriter writer = new(File.Create(output));
        writer.Write(grid.Side);
        for (int i = 0; i < grid.Count; i++)
        {
            MapSample sample = map.Node(i);
            writer.Write((float)sample.Elevation);
            writer.Write((byte)WorldMap.Biome(sample));
            writer.Write((byte)(MapRivers.CatchmentSquareKilometres(grid, map.Fields.Discharge[i]) >= map.Scale.SourceCatchment ? 1 : 0));
        }
        var land = Enumerable.Range(0, grid.Count).Where(i => map.Fields.Elevation[i] >= GenerationConstants.WaterLevel).ToArray();
        var biomes = land.GroupBy(i => WorldMap.Biome(map.Node(i))).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {100.0 * g.Count() / land.Length:F0}%");
        Console.WriteLine(FormattableString.Invariant(
            $"continent seed={seed} size={size / 1000.0:F0} km spacing={grid.Spacing:F0} m nodes={grid.Count} generateMs={ms:F0} land={100.0 * land.Length / grid.Count:F0}% peak={land.Max(i => map.Fields.Elevation[i]):F0} m rivers={map.Rivers.Reaches.Count} sites={map.Sites.Count} fingerprint={map.Fingerprint:x16}"));
        Console.WriteLine("biomes: " + string.Join(", ", biomes));
    }
}
