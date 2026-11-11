using System.Globalization;
using System.Text;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// How a continent's walked rivers sit in the land (#10180): cross-sections of the channel where the
/// walking terrain is sampled, and a census of small reaches whose surface stands above the dry ground
/// beside them (an embankment), whose bed is cut far below what the river's size asks (a trench), or
/// whose banks stand far above the water (a gorge).
/// </summary>
internal static class ChannelRun
{
    /// <summary>Cross-sections reach this far either side of the centreline (m), sampled every two metres.</summary>
    private const int SectionHalfMetres = 24;
    /// <summary>Sections are taken this far apart along the reach (m).</summary>
    private const double SectionSpacing = 24;
    /// <summary>The sections either side of the point asked for.</summary>
    private const int SectionsEachWay = 3;
    /// <summary>The census looks at reaches whose channel is at most this wide (m).</summary>
    private const double SmallWidth = 8;
    /// <summary>Banks are judged this far beyond the channel's edge (m).</summary>
    private const double BankProbe = 4;
    /// <summary>The census samples each reach every this many metres along it.</summary>
    private const double CensusSpacing = 48;
    /// <summary>A sample stands on an embankment when the ground beyond its bank lies this far below the surface (m).</summary>
    private const double PerchedMetres = 0.5;
    /// <summary>A sample is trenched when its centre is deeper than its depth asks by this (m).</summary>
    private const double TrenchedMetres = 1.5;
    /// <summary>A sample runs in a gorge when even its lower bank stands this far above the surface (m).</summary>
    private const double IncisedMetres = 4;
    /// <summary>A shaped reach's points stand about this far apart (m).</summary>
    private const double ShapedSpacing = 6;

    internal static int Run(Dictionary<string, List<string>> options, int size, ContinentDesign? design, string output)
    {
        ulong seed = ulong.Parse(options["channel"][0], CultureInfo.InvariantCulture);
        TerrainConfiguration configuration = new(seed, size);
        WorldMap map = new(configuration, MapSimulation.Run(configuration, MapRecipe.Default, design));
        MapRegions regions = MapRegions.For(map);
        MapDrainage drainage = regions.Drainage;
        Directory.CreateDirectory(output);
        StringBuilder text = new();
        void Line(string line)
        {
            Console.WriteLine(line);
            text.AppendLine(line);
        }

        double Ground(double x, double z, MapSample g) =>
            Math.Max(g.Elevation + RegionalTerrain.Relief(configuration.Contract.GeographyNoiseSeed, g, x, z), GenerationConstants.MinimumTerrainHeight);

        if (options.ContainsKey("lakes"))
            foreach (Lake lake in map.Lakes)
                Line(Invariant($"lake {lake.Id} {lake.Kind} level {lake.Level:F1} spill {lake.Spill:F1} closed {lake.Closed} nodes {lake.Nodes.Length}"));
        foreach (string at in options.GetValueOrDefault("at", []))
        {
            string[] parts = at.Split(',');
            double ax = double.Parse(parts[0], CultureInfo.InvariantCulture), az = double.Parse(parts[1], CultureInfo.InvariantCulture);
            if (regions.RiverNear(ax, az) is not RiverInfluence near || near.Reach < 0)
            {
                Line(Invariant($"no walked river near {ax:F0},{az:F0}"));
                continue;
            }
            RiverPoint[] points = drainage.Shaped(near.Reach);
            int closest = 0;
            for (int k = 1; k < points.Length; k++)
                if (Square(points[k].X - ax) + Square(points[k].Z - az) < Square(points[closest].X - ax) + Square(points[closest].Z - az)) closest = k;
            Reach reach = drainage.Network.Reaches[near.Reach];
            Line(Invariant($"reach {near.Reach} near {ax:F0},{az:F0}: flow {reach.Flow:F2} m3/s width {reach.Width:F1} m gradient {reach.Gradient:F4} points {points.Length}"));
            foreach (int node in reach.Nodes)
            {
                // The network's surface at its own nodes, against the relief it was eroded from and the tile's ground.
                double x = drainage.Grid.X(node), z = drainage.Grid.Z(node);
                double relief = RegionRelief.At(map, x, z, RegionRelief.ResolvedOctaves(MapDrainage.Spacing)).Height;
                Line(Invariant($"  node {node} at {x:F0},{z:F0}: network {drainage.ElevationAt(node):F1} continent {map.Broad(x, z).Elevation:F1} relief {relief:F1} filled {drainage.AggradationAt(node):F1} tile {regions.Blended(x, z).Elevation:F1} lake {map.LakeLevelAt(x, z):F1}"));
            }
            int step = Math.Max(1, (int)Math.Round(SectionSpacing / ShapedSpacing));
            for (int s = -SectionsEachWay; s <= SectionsEachWay; s++)
            {
                int k = Math.Clamp(closest + s * step, 1, points.Length - 2);
                RiverPoint p = points[k];
                double tx = points[k + 1].X - points[k - 1].X, tz = points[k + 1].Z - points[k - 1].Z, tl = Math.Sqrt(tx * tx + tz * tz);
                double nx = -tz / tl, nz = tx / tl;
                Line(Invariant($"  section at {p.X:F0},{p.Z:F0}: surface {p.Surface:F2} halfwidth {p.HalfWidth:F2} depth {p.Depth:F2}"));
                StringBuilder broad = new("    broad "), ground = new("    ground"), water = new("    water ");
                for (int d = -SectionHalfMetres; d <= SectionHalfMetres; d += 2)
                {
                    double x = p.X + nx * d, z = p.Z + nz * d;
                    MapSample g = regions.Sample(x, z);
                    broad.Append(Invariant($" {regions.Blended(x, z).Elevation,6:F1}"));
                    ground.Append(Invariant($" {Ground(x, z, g),6:F1}"));
                    water.Append(g.InRiver ? Invariant($" {g.RiverSurface,6:F1}") : g.InLake ? Invariant($" L{g.LakeSurface,5:F1}") : "      -");
                }
                Line(broad.ToString());
                Line(ground.ToString());
                Line(water.ToString());
            }
        }

        if (options.TryGetValue("around", out List<string>? around))
        {
            // A census of the small reaches within a radius of a point: how many of their samples stand on an
            // embankment, sit in a trench or run in a gorge, and what lies under the perched ones.
            string[] parts = around[0].Split(',');
            double cx = double.Parse(parts[0], CultureInfo.InvariantCulture), cz = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double radius = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 8000;
            int incised = 0;
            double worstIncision = 0;
            (double X, double Z) worstIncisionAt = default;
            int samples = 0, perched = 0, trenched = 0, perchedFilled = 0, perchedLake = 0, lakeSamples = 0;
            List<double> overRefined = [], overTile = [], raises = [];
            List<(double Perch, double X, double Z, int Reach)> dry = [];
            double worstPerch = 0, worstTrench = 0;
            (double X, double Z) worstPerchAt = default, worstTrenchAt = default;
            foreach (int r in drainage.ReachesWithin(cx - radius, cz - radius, cx + radius, cz + radius))
            {
                if (drainage.Network.Reaches[r].Width > SmallWidth) continue;
                RiverPoint[] points = drainage.Shaped(r);
                double since = CensusSpacing;
                for (int k = 1; k + 1 < points.Length; k++)
                {
                    since += ShapedSpacing;
                    RiverPoint p = points[k];
                    if (since < CensusSpacing || Square(p.X - cx) + Square(p.Z - cz) > radius * radius) continue;
                    since = 0;
                    double tx = points[k + 1].X - points[k - 1].X, tz = points[k + 1].Z - points[k - 1].Z, tl = Math.Sqrt(tx * tx + tz * tz);
                    if (tl == 0) continue;
                    double nx = -tz / tl, nz = tx / tl;
                    MapSample centre = regions.Sample(p.X, p.Z);
                    if (!centre.InRiver || centre.RiverReach != r) continue;
                    samples++;
                    bool lake = !double.IsNegativeInfinity(map.LakeLevelAt(p.X, p.Z));
                    if (lake) lakeSamples++;
                    // The lower bank decides whether water would spill over it.
                    double bank = double.MaxValue, bankBroad = double.MaxValue;
                    foreach (int side in (int[])[-1, 1])
                    {
                        double bx = p.X + nx * side * (p.HalfWidth + BankProbe), bz = p.Z + nz * side * (p.HalfWidth + BankProbe);
                        MapSample beside = regions.Sample(bx, bz);
                        // A bank under a lake standing at the river's level is the lake's shore water, not an embankment.
                        double held = beside.InLake && beside.LakeSurface >= centre.RiverSurface - PerchedMetres ? beside.LakeSurface : double.NegativeInfinity;
                        bank = Math.Min(bank, Math.Max(Ground(bx, bz, beside), held));
                        bankBroad = Math.Min(bankBroad, regions.Blended(bx, bz).Elevation);
                    }
                    double perch = centre.RiverSurface - bank;
                    // A gorge: even the lower bank stands far above the water.
                    double incision = -perch;
                    if (incision > IncisedMetres) incised++;
                    if (incision > worstIncision) { worstIncision = incision; worstIncisionAt = (p.X, p.Z); }
                    if (perch > PerchedMetres)
                    {
                        perched++;
                        if (lake) perchedLake++;
                        else dry.Add((perch, p.X, p.Z, r));
                        int node = drainage.Grid.Nearest(p.X, p.Z);
                        double raised = drainage.AggradationAt(node);
                        if (raised > PerchedMetres) perchedFilled++;
                        overRefined.Add(centre.RiverSurface - RegionRelief.At(map, p.X, p.Z, RegionRelief.Octaves).Height);
                        overTile.Add(centre.RiverSurface - bankBroad);
                        raises.Add(raised);
                    }
                    if (perch > worstPerch) { worstPerch = perch; worstPerchAt = (p.X, p.Z); }
                    // Under a lake at the river's level the depth is the lake's, not a trench.
                    double trench = lake && centre.LakeSurface >= centre.RiverSurface - PerchedMetres ? 0 : centre.RiverSurface - Ground(p.X, p.Z, centre) - p.Depth;
                    if (trench > TrenchedMetres) trenched++;
                    if (trench > worstTrench) { worstTrench = trench; worstTrenchAt = (p.X, p.Z); }
                }
            }
            Line(Invariant($"small reaches (<= {SmallWidth} m) within {radius:F0} m of {cx:F0},{cz:F0}: {samples} samples, perched {perched} ({Share(perched, samples):P1}, bank {PerchedMetres} m below the surface {BankProbe} m out), trenched {trenched} ({Share(trenched, samples):P1}, centre {TrenchedMetres} m deeper than asked)"));
            Line(Invariant($"  incised: {incised} ({Share(incised, samples):P1}, lower bank {IncisedMetres} m above the surface {BankProbe} m out); worst {worstIncision:F1} m at {worstIncisionAt.X:F0},{worstIncisionAt.Z:F0}"));
            Line(Invariant($"  in a lake's basin: {lakeSamples} samples, {perchedLake} perched ({Share(perchedLake, perched):P1} of perched)"));
            Line(Invariant($"  perched where the drainage node stands above its own relief: {perchedFilled} ({Share(perchedFilled, perched):P1} of perched)"));
            static string Q(List<double> v) { v.Sort(); return v.Count == 0 ? "-" : Invariant($"p10 {v[v.Count / 10]:F1} p50 {v[v.Count / 2]:F1} p90 {v[v.Count * 9 / 10]:F1}"); }
            Line(Invariant($"  perched: surface over the refined relief {Q(overRefined)}; over the tile's blended bank {Q(overTile)}; drainage node over its own relief {Q(raises)}"));
            foreach (var d in dry.OrderByDescending(d => d.Perch).Take(5))
                Line(Invariant($"  perched away from lakes: {d.Perch:F1} m at {d.X:F0},{d.Z:F0} reach {d.Reach}"));
            Line(Invariant($"  worst perch {worstPerch:F1} m at {worstPerchAt.X:F0},{worstPerchAt.Z:F0}; worst trench {worstTrench:F1} m at {worstTrenchAt.X:F0},{worstTrenchAt.Z:F0}"));
        }

        File.WriteAllText(Path.Combine(output, Invariant($"channels-{seed}.txt")), text.ToString());
        return 0;
    }

    private static double Share(int part, int whole) => whole == 0 ? 0 : (double)part / whole;
    private static double Square(double v) => v * v;
    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
