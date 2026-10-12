using System.Diagnostics;
using System.Globalization;
using System.Text;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// The river census (#9874): over many seeds, how big a world's rivers are in real flow, which river
/// mouths could hold the start's sea port, and how much of the network a boat could use.
/// <list type="bullet">
/// <item>Flow: the map's discharge is rain-weighted area (rain is relative, about 1 over land), so one
/// runoff figure, millimetres a year per unit of rain, turns it into cubic metres a second. That figure
/// is the world's water budget.</item>
/// <item>A port: a river mouth on the playable peninsula, out of the neck, in calm climate, sheltered
/// from the open sea (few bearings from it reach open water far out).</item>
/// <item>Navigable: flow at or above a canoe's or a river boat's least.</item>
/// </list>
/// </summary>
internal static class RiverCensus
{
    private const double SecondsPerYear = 365.25 * 24 * 3600;

    /// <summary>Runoff per unit of relative rain (mm a year): the game's own water budget unless asked otherwise.</summary>
    private const double DefaultRunoffMillimetres = WaterBudget.RunoffMetresPerYear * 1000;

    /// <summary>Least flows (m³/s): a canoe most of the year, a laden river boat, and the start port's great river.</summary>
    private const double DefaultCanoeFlow = 5, DefaultBoatFlow = 40, DefaultGreatFlow = 150;

    /// <summary>A mouth is sheltered when at most this share of bearings from it reaches open sea this far out.</summary>
    private const double ShelterOpenShare = 0.3, OpenSeaMetres = 60_000;
    private const int ShelterBearings = 32;

    /// <summary>Calm climate for the start port: temperature (0 polar, 1 tropical) within these.</summary>
    private const double CalmCoolest = 0.4, CalmWarmest = 0.9;

    /// <summary>Rain-weighted catchment (km²) below which a mouth is not counted at all.</summary>
    private const double CountedCatchment = 50;

    private sealed record Mouth(int Node, double X, double Z, double Catchment, double Flow, double Open, double Temperature,
        bool Playable, bool Neck)
    {
        internal bool Sheltered => Open <= ShelterOpenShare;
        internal bool Calm => Temperature is >= CalmCoolest and <= CalmWarmest;
        internal bool PortCandidate => Playable && !Neck && Calm;
    }

    private sealed record SeedResult(ulong Seed, double Seconds, double LandKm2, List<Mouth> Mouths, double CanoeKm, double BoatKm,
        Mouth? ShelteredPort, Mouth? CalmRiver, double LargestCatchment, Basin? PortBasin);

    /// <summary>
    /// The port river's own network: how far a canoe and a river boat can go on it, the junctions of two
    /// canoe rivers on it (river-town sites), and the head of navigation for a boat and a canoe, in
    /// kilometres along the river from the mouth.
    /// </summary>
    private sealed record Basin(double CanoeKm, double BoatKm, int CanoeJunctions, int BoatJunctions, double BoatHeadKm, double CanoeHeadKm);

    internal static int Run(Dictionary<string, List<string>> options, int size, MapRecipe recipe, ContinentDesign? design, string output)
    {
        double runoff = Number(options, "runoff", DefaultRunoffMillimetres);
        double canoe = Number(options, "canoe", DefaultCanoeFlow), boat = Number(options, "boat", DefaultBoatFlow), great = Number(options, "great", DefaultGreatFlow);
        ulong[] seeds = [.. options["rivers"].SelectMany(SeedList)];
        Directory.CreateDirectory(output);
        SeedResult[] results = new SeedResult[seeds.Length];
        Parallel.For(0, seeds.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) }, k =>
            results[k] = Census(seeds[k], size, recipe, design, runoff, canoe, boat, output));

        StringBuilder table = new();
        void Line(string text)
        {
            Console.WriteLine(text);
            table.AppendLine(text);
        }

        Line(Invariant($"river census: size={size}m design={design?.Name ?? "none"} runoff={runoff:F0} mm/yr per unit rain; canoe>={canoe} boat>={boat} great>={great} m3/s; sheltered: <= {ShelterOpenShare:P0} of bearings open to {OpenSeaMetres / 1000:F0} km; calm: temperature {CalmCoolest}-{CalmWarmest}"));
        Line("seed       | s    | largest km2 | mouths canoe/boat/great | canoe km | boat km | best calm river mouth = port (m3/s, open) | port basin: canoe/boat km, junctions canoe/boat, head canoe/boat km | runoff for a great port river");
        foreach (SeedResult r in results)
        {
            List<Mouth> playable = [.. r.Mouths.Where(m => m.Playable)];
            string Port(Mouth? m) => m is null ? "none" : Invariant($"{m.Flow,6:F0} at ({m.X / 1000,5:F0},{m.Z / 1000,5:F0}) km, open {m.Open:P0}");
            double neededCalm = r.CalmRiver is null ? double.NaN : runoff * great / r.CalmRiver.Flow;
            Basin? b = r.PortBasin;
            string basin = b is null ? "none" : Invariant($"{b.CanoeKm,5:F0}/{b.BoatKm,4:F0} km, {b.CanoeJunctions,2}/{b.BoatJunctions,2}, head {b.CanoeHeadKm,4:F0}/{b.BoatHeadKm,4:F0} km");
            Line(Invariant($"{r.Seed,-10} | {r.Seconds,4:F1} | {r.LargestCatchment,11:F0} | {playable.Count(m => m.Flow >= canoe),6}/{playable.Count(m => m.Flow >= boat),4}/{playable.Count(m => m.Flow >= great),5}     | {r.CanoeKm,8:F0} | {r.BoatKm,7:F0} | {Port(r.CalmRiver),-41} | {basin,-58} | {neededCalm,5:F0} mm"));
        }

        int metCalm = results.Count(r => r.CalmRiver is { } p && p.Flow >= great);
        Line(Invariant($"seeds whose port river (the largest calm playable mouth) reaches {great} m3/s: {metCalm}/{results.Length}; sheltered ones among them: {results.Count(r => r.CalmRiver is { } p && p.Flow >= great && p.Sheltered)}"));
        double[] neededAll = [.. results.Where(r => r.CalmRiver is not null).Select(r => runoff * great / r.CalmRiver!.Flow).Order()];
        if (neededAll.Length > 0)
            Line(Invariant($"runoff the port river needs to reach {great} m3/s: median {neededAll[neededAll.Length / 2]:F0} mm, range {neededAll[0]:F0}-{neededAll[^1]:F0} mm"));
        Basin[] basins = [.. results.Where(r => r.PortBasin is not null).Select(r => r.PortBasin!)];
        if (basins.Length > 0)
        {
            double Median(Func<Basin, double> of) { double[] v = [.. basins.Select(of).Order()]; return v[v.Length / 2]; }
            Line(Invariant($"port basins: boat km median {Median(b => b.BoatKm):F0} (min {basins.Min(b => b.BoatKm):F0}), canoe km median {Median(b => b.CanoeKm):F0} (min {basins.Min(b => b.CanoeKm):F0}), canoe junctions median {Median(b => b.CanoeJunctions):F0} (min {basins.Min(b => b.CanoeJunctions)}), boat head median {Median(b => b.BoatHeadKm):F0} km (min {basins.Min(b => b.BoatHeadKm):F0})"));
        }
        File.WriteAllText(Path.Combine(output, "census.txt"), table.ToString());
        return 0;
    }

    private static SeedResult Census(ulong seed, int size, MapRecipe recipe, ContinentDesign? design, double runoff, double canoe, double boat, string output)
    {
        TerrainConfiguration configuration = new(seed, size);
        Stopwatch clock = Stopwatch.StartNew();
        MapFields fields = MapSimulation.Run(configuration, recipe, design);
        double seconds = clock.Elapsed.TotalSeconds;
        MapGrid grid = fields.Grid;
        int count = grid.Count, side = grid.Side;
        double[] height = [.. fields.Elevation.Select(v => (double)v)];
        bool[] outlet = MapFlow.Outlets(grid, height);
        bool[] sea = [.. height.Select(h => h < GenerationConstants.WaterLevel)];
        MapFlow flow = MapFlow.Route(grid, (double[])height.Clone(), outlet, [.. Enumerable.Repeat(1.0, count)], MapFlow.FillGradient);
        double cellKm2 = grid.Spacing * grid.Spacing / 1e6;
        double Catchment(int i) => fields.Discharge[i] * cellKm2;
        double Flow(int i) => Catchment(i) * 1e6 * runoff / 1000 / SecondsPerYear;
        (double X, double Z) Design(int i) => (grid.X(i) / grid.Radius, grid.Z(i) / grid.Radius);
        bool Playable(int i)
        {
            (double x, double z) = Design(i);
            return design is null || (design.LandAt(x, z) is not { Forbidden: true } && !design.BeyondFrontier(x, z));
        }

        double Open(int from)
        {
            int open = 0;
            for (int b = 0; b < ShelterBearings; b++)
            {
                double angle = 2 * Math.PI * b / ShelterBearings, dx = Math.Cos(angle), dz = Math.Sin(angle);
                bool clear = true;
                for (double d = grid.Spacing; d <= OpenSeaMetres && clear; d += grid.Spacing)
                {
                    double x = grid.X(from) + (dx * d), z = grid.Z(from) + (dz * d);
                    if (Math.Abs(x) > grid.Radius || Math.Abs(z) > grid.Radius) break;
                    clear = sea[grid.Nearest(x, z)];
                }

                if (clear) open++;
            }

            return open / (double)ShelterBearings;
        }

        List<Mouth> mouths = [];
        double land = 0, canoeKm = 0, boatKm = 0;
        for (int i = 0; i < count; i++)
        {
            if (sea[i]) continue;
            land += cellKm2;
            if (!Playable(i)) continue;
            double q = Flow(i);
            if (q >= canoe) canoeKm += flow.Distance[i] * grid.Spacing / 1000;
            if (q >= boat) boatKm += flow.Distance[i] * grid.Spacing / 1000;
            int r = flow.Receiver[i];
            if (r < 0 || !sea[r] || Catchment(i) < CountedCatchment) continue;
            (double x, double z) = Design(i);
            mouths.Add(new(i, grid.X(i), grid.Z(i), Catchment(i), q, Open(r), fields.Temperature[i], true,
                design?.InNeck(x, z) ?? false));
        }

        mouths.Sort((a, b) => b.Flow.CompareTo(a.Flow));
        Mouth? port = mouths.FirstOrDefault(m => m.PortCandidate && m.Sheltered);
        Mouth? calm = mouths.FirstOrDefault(m => m.PortCandidate);
        Basin? basin = calm is null ? null : PortBasin(grid, flow, Flow, calm.Node, canoe, boat);
        Draw(grid, height, sea, i => Playable(i), Flow, canoe, boat, mouths, port, calm, design)
            .Save(Path.Combine(output, $"rivers-{seed}.png"));
        double largest = Enumerable.Range(0, count).Where(i => !sea[i] && Playable(i)).Select(Catchment).DefaultIfEmpty(0).Max();
        return new(seed, seconds, land, mouths, canoeKm, boatKm, port, calm, largest, basin);
    }

    /// <summary>The network draining to one mouth, measured along its rivers.</summary>
    private static Basin PortBasin(MapGrid grid, MapFlow flow, Func<int, double> flowAt, int mouth, double canoe, double boat)
    {
        int count = grid.Count;
        bool[] inside = new bool[count];
        double[] along = new double[count];
        inside[mouth] = true;
        // Order runs downstream first, so a node's receiver is settled before it.
        foreach (int i in flow.Order)
        {
            int r = flow.Receiver[i];
            if (i == mouth || r < 0 || !inside[r]) continue;
            inside[i] = true;
            along[i] = along[r] + (flow.Distance[i] * grid.Spacing / 1000);
        }

        int[] canoeInflows = new int[count], boatInflows = new int[count];
        double canoeKm = 0, boatKm = 0, boatHead = 0, canoeHead = 0;
        for (int i = 0; i < count; i++)
        {
            if (!inside[i]) continue;
            double q = flowAt(i);
            double step = flow.Distance[i] * grid.Spacing / 1000;
            if (q >= canoe) { canoeKm += step; canoeHead = Math.Max(canoeHead, along[i]); if (i != mouth) canoeInflows[flow.Receiver[i]]++; }
            if (q >= boat) { boatKm += step; boatHead = Math.Max(boatHead, along[i]); if (i != mouth) boatInflows[flow.Receiver[i]]++; }
        }

        return new(canoeKm, boatKm, canoeInflows.Count(n => n >= 2), boatInflows.Count(n => n >= 2), boatHead, canoeHead);
    }

    /// <summary>The map: land by height (the forbidden side dimmed), rivers by class, port candidates ringed.</summary>
    private static Image Draw(MapGrid grid, double[] height, bool[] sea, Func<int, bool> playable, Func<int, double> flow, double canoe, double boat,
        List<Mouth> mouths, Mouth? port, Mouth? calm, ContinentDesign? design)
    {
        const int Scale = 2;
        int side = grid.Side;
        double top = Math.Max(1, height.Max());
        Image image = new(side * Scale, side * Scale);
        for (int i = 0; i < grid.Count; i++)
        {
            (double R, double G, double B) colour;
            if (sea[i]) colour = (0.08, 0.16, 0.32);
            else
            {
                double v = Math.Sqrt(Math.Clamp(height[i] / top, 0, 1));
                double dim = playable(i) ? 1 : 0.5;
                colour = ((0.3 + (0.6 * v)) * dim, (0.42 + (0.5 * v)) * dim, (0.25 + (0.6 * v)) * dim);
                double q = flow(i);
                if (q >= boat) colour = (0.1, 0.45, 1);
                else if (q >= canoe) colour = (0.35, 0.75, 1);
                else if (q >= canoe / 5) colour = (0.55, 0.7, 0.8);
            }

            int px = i % side * Scale, py = i / side * Scale;
            for (int dy = 0; dy < Scale; dy++)
            for (int dx = 0; dx < Scale; dx++)
                image.Set(px + dx, py + dy, colour);
        }

        if (design is not null && design.Neck.Count >= 3)
        {
            for (int k = 0; k < design.Neck.Count; k++)
            {
                double[] a = design.Neck[k], b = design.Neck[(k + 1) % design.Neck.Count];
                for (double t = 0; t <= 1; t += 0.002)
                    image.Set((int)((((a[0] + ((b[0] - a[0]) * t)) + 1) / 2) * side * Scale), (int)((((a[1] + ((b[1] - a[1]) * t)) + 1) / 2) * side * Scale), (1, 0.4, 0.9));
            }
        }

        foreach (Mouth m in mouths.Where(m => m.PortCandidate && m.Flow >= canoe))
            Ring(image, m.Node % side * Scale, m.Node / side * Scale, 6, m.Sheltered ? (1, 0.85, 0.2) : (1, 0.35, 0.25));
        if (calm is not null) Ring(image, calm.Node % side * Scale, calm.Node / side * Scale, 10, (1, 1, 1));
        if (port is not null) Ring(image, port.Node % side * Scale, port.Node / side * Scale, 14, (0.2, 1, 0.3));
        return image;
    }

    private static void Ring(Image image, int cx, int cy, int radius, (double, double, double) colour)
    {
        for (double a = 0; a < 2 * Math.PI; a += 0.5 / radius)
            for (int w = 0; w < 2; w++)
                image.Set(cx + (int)Math.Round((radius + w) * Math.Cos(a)), cy + (int)Math.Round((radius + w) * Math.Sin(a)), colour);
    }

    private static IEnumerable<ulong> SeedList(string text)
    {
        foreach (string part in text.Split(','))
        {
            string[] range = part.Split('-');
            if (range.Length == 2)
            {
                for (ulong s = ulong.Parse(range[0], CultureInfo.InvariantCulture); s <= ulong.Parse(range[1], CultureInfo.InvariantCulture); s++) yield return s;
            }
            else
            {
                yield return ulong.Parse(part, CultureInfo.InvariantCulture);
            }
        }
    }

    private static double Number(Dictionary<string, List<string>> options, string key, double fallback) =>
        options.TryGetValue(key, out List<string>? value) ? double.Parse(value[0], CultureInfo.InvariantCulture) : fallback;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
