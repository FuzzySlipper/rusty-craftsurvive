using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// Small rivers sit in the land (#10180), on the default continent. The walked network's water stands no
/// higher than an open lake's level over its bed, and never rises downstream. Around the arrival, a small
/// river's banks stand at its water rather than below it: a dry bank a few metres out is rarely lower than
/// the surface (an embankment), and never by much.
/// </summary>
internal static class RiverSeatChecks
{
    /// <summary>Small rivers are those whose channel is at most this wide (m).</summary>
    private const double SmallWidth = 8;
    /// <summary>Banks are judged this far beyond the channel's edge (m).</summary>
    private const double BankProbe = 4;
    /// <summary>Each reach is sampled this often along its centreline (m), within this radius of the arrival.</summary>
    private const double SampleSpacing = 48, Radius = 6000;
    /// <summary>A sample is perched where its lower dry bank lies this far below the water (m).</summary>
    private const double PerchedMetres = 0.5;
    /// <summary>Before #10180 an eighth to a quarter of small-river samples were perched, by up to 26 m on seed 13.</summary>
    private const double MostPerchedShare = 0.03, DeepestPerch = 2;
    /// <summary>Float storage of the network's surface (m).</summary>
    private const double LevelTolerance = 0.01;
    private const double ShapedSpacing = 6;

    internal static void Run(WorldMap continent)
    {
        MapRegions regions = MapRegions.For(continent);
        MapDrainage drainage = regions.Drainage;
        MapGrid grid = drainage.Grid;

        // Where a region tile lays an open lake's bed (the continent's ground below its level).
        int lakeNodes = 0, aboveLake = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            double x = grid.X(i), z = grid.Z(i);
            if (continent.LakeAt(x, z) is not { Closed: false } lake || continent.Broad(x, z).Elevation >= lake.Level) continue;
            lakeNodes++;
            if (drainage.ElevationAt(i) > lake.Level + LevelTolerance) aboveLake++;
        }
        Check.That(lakeNodes > 0 && aboveLake == 0,
            $"the network's water stands no higher than an open lake's level over its bed ({aboveLake} of {lakeNodes} nodes above)");

        int rising = 0;
        foreach (Reach reach in drainage.Network.Reaches)
            for (int k = 1; k < reach.Nodes.Length; k++)
                if (drainage.ElevationAt(reach.Nodes[k]) > drainage.ElevationAt(reach.Nodes[k - 1]) + LevelTolerance) rising++;
        Check.That(rising == 0, $"the network's water never rises downstream ({rising} steps rise)");

        ulong seed = continent.Configuration.Contract.GeographyNoiseSeed;
        double Ground(double x, double z, MapSample g) =>
            Math.Max(g.Elevation + RegionalTerrain.Relief(seed, g, x, z), GenerationConstants.MinimumTerrainHeight);
        int samples = 0, perched = 0;
        double deepest = 0;
        foreach (int r in drainage.ReachesWithin(-Radius, -Radius, Radius, Radius))
        {
            if (drainage.Network.Reaches[r].Width > SmallWidth) continue;
            RiverPoint[] points = drainage.Shaped(r);
            double since = SampleSpacing;
            for (int k = 1; k + 1 < points.Length; k++)
            {
                since += ShapedSpacing;
                RiverPoint p = points[k];
                if (since < SampleSpacing || (p.X * p.X) + (p.Z * p.Z) > Radius * Radius) continue;
                since = 0;
                double tx = points[k + 1].X - points[k - 1].X, tz = points[k + 1].Z - points[k - 1].Z, tl = Math.Sqrt((tx * tx) + (tz * tz));
                MapSample centre = regions.Sample(p.X, p.Z);
                if (tl == 0 || !centre.InRiver || centre.RiverReach != r) continue;
                samples++;
                double bank = double.MaxValue;
                foreach (int side in (int[])[-1, 1])
                {
                    double bx = p.X - (tz / tl * side * (p.HalfWidth + BankProbe)), bz = p.Z + (tx / tl * side * (p.HalfWidth + BankProbe));
                    MapSample beside = regions.Sample(bx, bz);
                    // A bank under a lake at the river's level is the lake's own water, not an embankment.
                    double held = beside.InLake && beside.LakeSurface >= centre.RiverSurface - PerchedMetres ? beside.LakeSurface : double.NegativeInfinity;
                    bank = Math.Min(bank, Math.Max(Ground(bx, bz, beside), held));
                }
                double perch = centre.RiverSurface - bank;
                if (perch > PerchedMetres) perched++;
                deepest = Math.Max(deepest, perch);
            }
        }
        double share = samples == 0 ? 1 : (double)perched / samples;
        Console.WriteLine($"river seats: {samples} small-river samples within {Radius / 1000:F0} km, perched {perched} ({share:P1}), deepest {deepest:F1} m; open-lake nodes {lakeNodes}");
        Check.That(samples > 500 && share <= MostPerchedShare && deepest <= DeepestPerch,
            $"small rivers sit in the land, not on embankments ({share:P1} perched, deepest {deepest:F1} m, of {samples})");
    }
}
