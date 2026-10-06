using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// regions [seed] [x m] [z m] [out]: refine the four region tiles meeting nearest a continental point and
/// write a raster across their shared corner, with build times, determinism and seam figures (#9550).
/// </summary>
internal static class RegionProbe
{
    private const double RasterStep = 16;
    private const double RasterHalf = 6144;

    internal static void Run(string[] args)
    {
        ulong seed = args.Length > 1 ? ulong.Parse(args[1]) : 12345;
        double px = args.Length > 2 && args[2] is not ("rugged" or "far") ? double.Parse(args[2]) : 0, pz = args.Length > 3 ? double.Parse(args[3]) : 0;
        string output = args.Length > 4 ? args[4] : "regions.bin";
        WorldMap continent = WorldMapGenerator.Generate(new TerrainConfiguration(seed, MapScale.DefaultContinentalSize));
        MapRegions regions = new(continent);
        if (args.Length > 2 && args[2] == "far")
        {
            // The land farthest from the origin in each quadrant: where a precision audit walks (#9551).
            foreach ((int sx, int sz) in (ReadOnlySpan<(int, int)>)[(1, 1), (1, -1), (-1, 1), (-1, -1)])
            {
                int best = Enumerable.Range(0, continent.Grid.Count)
                    .Where(i => continent.Grid.X(i) * sx > 0 && continent.Grid.Z(i) * sz > 0 && continent.Node(i).Elevation > GenerationConstants.WaterLevel + 5)
                    .MaxBy(i => Math.Min(Math.Abs(continent.Grid.X(i)), Math.Abs(continent.Grid.Z(i))));
                Console.WriteLine(FormattableString.Invariant($"far ({sx},{sz}): ({continent.Grid.X(best):F0}, {continent.Grid.Z(best):F0}) elevation={continent.Node(best).Elevation:F0} {WorldMap.Region(continent.Node(best))}"));
            }
            return;
        }
        Stopwatch network = Stopwatch.StartNew();
        MapDrainage drainage = regions.Drainage;
        Console.WriteLine(FormattableString.Invariant($"drainage nodes={drainage.Grid.Count} reaches={drainage.ReachCount} buildMs={network.Elapsed.TotalMilliseconds:F0}"));
        if (args.Length > 2 && args[2] == "rugged")
        {
            // The most rugged land node away from the coast stands in for mountain country.
            int best = Enumerable.Range(0, continent.Grid.Count).Where(i => continent.Node(i).Elevation > 200)
                .MaxBy(i => continent.Node(i).Detail * continent.Node(i).Elevation);
            (px, pz) = (continent.Grid.X(best), continent.Grid.Z(best));
            Console.WriteLine(FormattableString.Invariant($"rugged point ({px:F0}, {pz:F0}) detail={continent.Node(best).Detail:F2} elevation={continent.Node(best).Elevation:F0}"));
        }
        (int X, int Z) own = MapRegions.TileAt(px, pz);
        double cornerX = (own.X + 0.5) * MapRegions.TileSpacing, cornerZ = (own.Z + 0.5) * MapRegions.TileSpacing;
        foreach ((int X, int Z) tile in (ReadOnlySpan<(int, int)>)[(own.X, own.Z), (own.X + 1, own.Z), (own.X, own.Z + 1), (own.X + 1, own.Z + 1)])
        {
            Stopwatch clock = Stopwatch.StartNew();
            WorldMap map = regions.Tile(tile).Map;
            Console.WriteLine(FormattableString.Invariant($"tile {tile} buildMs={clock.Elapsed.TotalMilliseconds:F0} rivers={regions.Tile(tile).Rivers.Reaches.Count} fingerprint={map.Fingerprint:x16}"));
        }
        WorldMap again = new MapRegions(continent).Tile(own).Map;
        Console.WriteLine(FormattableString.Invariant($"deterministic={again.Fingerprint == regions.Tile(own).Map.Fingerprint}"));

        int side = (int)(2 * RasterHalf / RasterStep) + 1;
        double[] h = new double[side * side];
        using (BinaryWriter writer = new(File.Create(output)))
        {
            writer.Write(side);
            for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                double x = cornerX - RasterHalf + i * RasterStep, z = cornerZ - RasterHalf + j * RasterStep;
                MapSample s = regions.Sample(x, z);
                MapSample c = continent.Sample(x, z);
                h[j * side + i] = s.Elevation;
                writer.Write((float)s.Elevation);
                writer.Write((float)c.Elevation);
                writer.Write((byte)WorldMap.Biome(s));
                writer.Write((byte)(s.InRiver ? 1 : 0));
            }
        }
        // Steepest metre-scale step across each seam versus away from it, sampled at one metre.
        double seam = 0, away = 0;
        for (double t = -RasterHalf; t <= RasterHalf; t += 8)
        {
            seam = Math.Max(seam, Step(regions, cornerX, cornerZ + t, 1, 0));
            seam = Math.Max(seam, Step(regions, cornerX + t, cornerZ, 0, 1));
            away = Math.Max(away, Step(regions, cornerX - MapRegions.TileSpacing / 2, cornerZ + t, 1, 0));
            away = Math.Max(away, Step(regions, cornerX + t, cornerZ - MapRegions.TileSpacing / 2, 0, 1));
        }
        double deviation = Math.Sqrt(Enumerable.Range(0, side * side).Average(k =>
        {
            double x = cornerX - RasterHalf + k % side * RasterStep, z = cornerZ - RasterHalf + k / side * RasterStep;
            double d = h[k] - continent.Sample(x, z).Elevation;
            return d * d;
        }));
        // Rivers crossing lines parallel to each seam, from well inside one tile to well inside the next:
        // a tributary that broke at the seam would show as a count that changes across it.
        double[] offsets = [-900, -450, 0, 450, 900];
        Console.WriteLine("riverCrossings x-seam: " + string.Join(" ", offsets.Select(o => Crossings(regions, cornerX + o, cornerZ, 0, 1))));
        Console.WriteLine("riverCrossings z-seam: " + string.Join(" ", offsets.Select(o => Crossings(regions, cornerX, cornerZ + o, 1, 0))));
        // Rivers at the seams: each tile's share of the network must answer identically where both reach.
        int agreed = 0, disagreed = 0;
        RegionTile ownTile = regions.Tile(own), east = regions.Tile((own.X + 1, own.Z)), south = regions.Tile((own.X, own.Z + 1));
        for (double t = -RasterHalf; t <= RasterHalf; t += 1)
        {
            foreach ((RegionTile other, double x, double z) in (ReadOnlySpan<(RegionTile, double, double)>)[(east, cornerX, cornerZ + t), (south, cornerX + t, cornerZ)])
            {
                RiverInfluence? a = ownTile.Rivers.Nearest(x, z), b = other.Rivers.Nearest(x, z);
                if (a is null && b is null) continue;
                if (a == b) agreed++; else disagreed++;
            }
        }
        Console.WriteLine($"seamRiverSamples agreed={agreed} disagreed={disagreed}");
        Stopwatch sampling = Stopwatch.StartNew();
        const int Samples = 200_000;
        double sink = 0;
        for (int k = 0; k < Samples; k++) sink += regions.Sample(cornerX - 3000 + k % 500 * 12, cornerZ - 3000 + k / 500 * 15).Elevation;
        Console.WriteLine(FormattableString.Invariant(
            $"seamMaxStep={seam:F3} m/m interiorMaxStep={away:F3} m/m rmsFromContinent={deviation:F1} m sampleUs={sampling.Elapsed.TotalMicroseconds / Samples:F2} ({sink > 0})"));
    }

    private static int Crossings(MapRegions regions, double x, double z, int dx, int dz)
    {
        int crossings = 0;
        bool wet = false;
        for (double t = -RasterHalf; t <= RasterHalf; t += 2)
        {
            bool now = regions.Sample(x + t * dx, z + t * dz).InRiver;
            if (now && !wet) crossings++;
            wet = now;
        }
        return crossings;
    }

    private static double Step(MapRegions regions, double x, double z, int dx, int dz)
    {
        double steepest = 0;
        for (int k = -40; k < 40; k++)
        {
            MapSample a = regions.Sample(x + k * dx, z + k * dz), b = regions.Sample(x + (k + 1) * dx, z + (k + 1) * dz);
            double step = Math.Abs(b.Elevation - a.Elevation);
            steepest = Math.Max(steepest, step);
        }
        return steepest;
    }
}
