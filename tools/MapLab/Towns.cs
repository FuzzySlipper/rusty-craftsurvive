using System.Diagnostics;
using System.Globalization;
using System.Text;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// The network and settlement census (#9875): over many seeds of a continent, the walked drainage as
/// data (reaches by navigability and channel form, mouths, confluences, heads of navigation, floodplain)
/// and the settlements chosen on it - the start's sea port and the river towns up from it.
/// </summary>
internal static class TownCensus
{
    private const int Scale = 2;

    private sealed record Row(ulong Seed, double Seconds, WaterNetwork Network, IReadOnlyList<Settlement> Settlements, double FloodplainKm2);

    internal static int Run(Dictionary<string, List<string>> options, int size, ContinentDesign? design, string output)
    {
        ulong[] seeds = [.. options["towns"].SelectMany(Seeds)];
        Directory.CreateDirectory(output);
        Row[] rows = new Row[seeds.Length];
        // Each seed's drainage is itself parallel; a few seeds at once keep the machine busy without thrashing.
        Parallel.For(0, seeds.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 6) }, k =>
            rows[k] = Census(seeds[k], size, design, output));

        StringBuilder table = new();
        void Line(string text)
        {
            Console.WriteLine(text);
            table.AppendLine(text);
        }

        Line(Invariant($"network and settlement census: size={size}m design={design?.Name ?? "none"} budget={WaterBudget.RunoffMetresPerYear * 1000:F0} mm; canoe>={WaterNetwork.CanoeFlow} boat>={WaterNetwork.BoatFlow} great>={WaterNetwork.GreatFlow} m3/s; {Settlements.TownCount} towns, {Settlements.SpacingMetres / 1000:F0} km apart"));
        Line("seed   | s     | reaches | canoe/boat/great km | mouths c/b/g | confluences | heads boat/canoe | floodplain km2 | port m3/s @ km, shelter, flat (* not the largest flow) | towns (reason, m3/s, up the port river?)");
        foreach (Row r in rows)
        {
            WaterNetwork n = r.Network;
            double Km(Navigability c) => n.Reaches.Where(reach => reach.Class >= c).Sum(reach => reach.LengthMetres) / 1000;
            int Mouths(Navigability c) => n.Mouths.Count(m => m.Class >= c);
            int confluences = n.Confluences.Count(c => c.Inflows.Count(i => n.Reaches[i].Class >= Navigability.Canoe) >= 2);
            Settlement? port = r.Settlements.FirstOrDefault(s => s.Kind == SettlementKind.Port);
            string towns = string.Join("; ", r.Settlements.Where(s => s.Kind == SettlementKind.Town).Select(s =>
                Invariant($"{s.Name} {Short(s.Reason)} {n.Reaches[s.Reach].Flow:F0}{(port is not null && n.MouthOf(s.Reach) == port.Reach ? " up" : "")}")));
            Line(Invariant($"{r.Seed,-6} | {r.Seconds,5:F1} | {n.Reaches.Count,7} | {Km(Navigability.Canoe),5:F0}/{Km(Navigability.Boat),4:F0}/{Km(Navigability.Great),4:F0}      | {Mouths(Navigability.Canoe),3}/{Mouths(Navigability.Boat),2}/{Mouths(Navigability.Great),2}   | {confluences,11} | {n.BoatHeads.Count,4}/{n.CanoeHeads.Count,5}       | {r.FloodplainKm2,14:F0} | {(port is null ? "none" : Invariant($"{n.Reaches[port.Reach].Flow,4:F0} @ ({port.X / 1000,4:F0},{port.Z / 1000,4:F0}) sh {port.Shelter:F2} fl {port.Flat:F2}{(port.LargestFlow ? "" : " *")}")),-40} | {towns}"));
        }

        Line(Invariant($"ports that shelter and flat land chose over a larger river: {rows.Count(r => r.Settlements.Any(s => s.Kind == SettlementKind.Port && !s.LargestFlow))}/{rows.Length}"));
        int full = rows.Count(r => r.Settlements.Count(s => s.Kind == SettlementKind.Town) == Settlements.TownCount);
        double upShare = rows.Average(r =>
        {
            Settlement? port = r.Settlements.FirstOrDefault(s => s.Kind == SettlementKind.Port);
            Settlement[] towns = [.. r.Settlements.Where(s => s.Kind == SettlementKind.Town)];
            return port is null || towns.Length == 0 ? 0 : towns.Count(s => r.Network.MouthOf(s.Reach) == port.Reach) / (double)towns.Length;
        });
        Line(Invariant($"seeds with a port: {rows.Count(r => r.Settlements.Any(s => s.Kind == SettlementKind.Port))}/{rows.Length}; with all {Settlements.TownCount} towns: {full}/{rows.Length}; share of towns up the port's own river: {upShare:P0}"));
        foreach (SettlementReason reason in Enum.GetValues<SettlementReason>())
            Line(Invariant($"  towns at a {reason}: {rows.Sum(r => r.Settlements.Count(s => s.Kind == SettlementKind.Town && s.Reason == reason))}"));
        File.WriteAllText(Path.Combine(output, "towns.txt"), table.ToString());
        return 0;
    }

    private static Row Census(ulong seed, int size, ContinentDesign? design, string output)
    {
        TerrainConfiguration configuration = new(seed, size);
        Stopwatch clock = Stopwatch.StartNew();
        WorldMap map = new(configuration, MapSimulation.Run(configuration, MapRecipe.Default, design));
        MapDrainage drainage = new(map);
        IReadOnlyList<Settlement> settlements = Settlements.Choose(map, design, drainage);
        double seconds = clock.Elapsed.TotalSeconds;
        WaterNetwork network = drainage.Network;
        MapGrid grid = network.Grid;
        int plain = 0;
        for (int i = 0; i < grid.Count; i++) if (network.OnFloodplain(i)) plain++;
        Draw(map, drainage, settlements, design).Save(Path.Combine(output, $"towns-{seed}.png"));
        return new(seed, seconds, network, settlements, plain * grid.Spacing * grid.Spacing / 1e6);
    }

    /// <summary>The map at half the drainage lattice: land by height, floodplain tinted, rivers by class, settlements ringed.</summary>
    private static Image Draw(WorldMap map, MapDrainage drainage, IReadOnlyList<Settlement> settlements, ContinentDesign? design)
    {
        WaterNetwork network = drainage.Network;
        MapGrid grid = network.Grid;
        int side = grid.Side / Scale;
        double top = map.Fields.Elevation.Max();
        Image image = new(side, side);
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int i = (y * Scale * grid.Side) + (x * Scale);
            double h = drainage.ElevationAt(i);
            if (h < GenerationConstants.WaterLevel) { image.Set(x, y, (0.08, 0.16, 0.32)); continue; }
            double v = Math.Sqrt(Math.Clamp(h / top, 0, 1));
            double dim = design is not null && design.LandAt(grid.X(i) / map.Radius, grid.Z(i) / map.Radius) is { Forbidden: true } ? 0.5 : 1;
            (double R, double G, double B) colour = ((0.3 + (0.6 * v)) * dim, (0.42 + (0.5 * v)) * dim, (0.25 + (0.6 * v)) * dim);
            if (network.OnFloodplain(i)) colour = (colour.R * 0.75, colour.G * 0.85, colour.B * 1.1 + 0.08);
            image.Set(x, y, colour);
        }

        foreach (Reach reach in network.Reaches)
        {
            if (reach.Class == Navigability.None && reach.Flow < WaterNetwork.CanoeFlow / 2) continue;
            (double, double, double) colour = reach.Class switch
            {
                Navigability.Great => (0.05, 0.3, 1),
                Navigability.Boat => (0.15, 0.55, 1),
                Navigability.Canoe => (0.45, 0.8, 1),
                _ => (0.6, 0.62, 0.66),
            };
            foreach (int node in reach.Nodes)
            {
                if (drainage.ElevationAt(node) < GenerationConstants.WaterLevel) continue;
                image.Set(node % grid.Side / Scale, node / grid.Side / Scale, colour);
            }
        }

        foreach (Settlement s in settlements)
        {
            int px = (int)((s.X + map.Radius) / grid.Spacing / Scale), pz = (int)((s.Z + map.Radius) / grid.Spacing / Scale);
            Ring(image, px, pz, s.Kind == SettlementKind.Port ? 12 : 7, s.Kind == SettlementKind.Port ? (0.2, 1, 0.3) : (1, 0.85, 0.2));
        }

        return image;
    }

    private static void Ring(Image image, int cx, int cy, int radius, (double, double, double) colour)
    {
        for (double a = 0; a < 2 * Math.PI; a += 0.5 / radius)
            for (int w = 0; w < 2; w++)
                image.Set(cx + (int)Math.Round((radius + w) * Math.Cos(a)), cy + (int)Math.Round((radius + w) * Math.Sin(a)), colour);
    }

    private static string Short(SettlementReason reason) => reason switch
    {
        SettlementReason.Confluence => "conf",
        SettlementReason.BoatHead => "boathead",
        SettlementReason.CanoeHead => "canoehead",
        SettlementReason.Harbour => "harbour",
        _ => "mouth",
    };

    private static IEnumerable<ulong> Seeds(string text)
    {
        foreach (string part in text.Split(','))
        {
            string[] range = part.Split('-');
            if (range.Length == 2)
                for (ulong s = ulong.Parse(range[0], CultureInfo.InvariantCulture); s <= ulong.Parse(range[1], CultureInfo.InvariantCulture); s++) yield return s;
            else yield return ulong.Parse(part, CultureInfo.InvariantCulture);
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
