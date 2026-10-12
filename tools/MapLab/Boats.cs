using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// River journeys against walking (#9879): on each seed's continent, from every river town down to the
/// sea port and back up, by raft (down only) and by canoe, each against the same journey on foot, at
/// the rivers' mean stage. The boat waits at the start's nearest open water.
/// </summary>
internal static class BoatCensus
{
    private const int Width = 1100;

    /// <summary>A settlement stands off the floodplain: its boat waits at the open water within this distance.</summary>
    private const double SettlementWaterMetres = 3000;

    internal static int Run(Dictionary<string, List<string>> options, int size, ContinentDesign? design, string output)
    {
        Directory.CreateDirectory(output);
        StringBuilder text = new();
        void Line(string line)
        {
            Console.WriteLine(line);
            text.AppendLine(line);
        }

        List<double> raftGain = [], downGain = [], upGain = [];
        foreach (ulong seed in TownCensus.Seeds(options["boats"][0]))
        {
            TerrainConfiguration configuration = new(seed, size);
            WorldMap map = new(configuration, MapSimulation.Run(configuration, MapRecipe.Default, design));
            MapDrainage drainage = MapRegions.For(map).Drainage;
            TravelCostModel land = new(map);
            IReadOnlyList<Settlement> settlements = Settlements.Choose(map, design, drainage);
            if (settlements.FirstOrDefault(s => s.Kind == SettlementKind.Port) is not Settlement port) { Line($"seed {seed}: no port"); continue; }
            Line(Invariant($"seed {seed}: port {port.Name} at ({port.X / 1000:F0}, {port.Z / 1000:F0}) km"));
            List<(Vector2 From, Vector2 To, TravelRoute? Route)> drawn = [];
            foreach (Settlement town in settlements.Where(s => s.Kind != SettlementKind.Port && s.Reach >= 0))
            {
                Vector2 at = new((float)town.X, (float)town.Z), harbour = new((float)port.X, (float)port.Z);
                Stopwatch clock = Stopwatch.StartNew();
                RiverPlan raft = Journey(land, drainage.Network, Boats.Raft, at, harbour, port.Name);
                RiverPlan down = Journey(land, drainage.Network, Boats.Canoe, at, harbour, port.Name);
                RiverPlan up = Journey(land, drainage.Network, Boats.Canoe, harbour, at, town.Name);
                double ms = clock.Elapsed.TotalMilliseconds / 3;
                Line(Invariant($"  {town.Name} ({Vector2.Distance(at, harbour) / 1000:F0} km): walk {raft.WalkingHours:F0} h; raft down {Hours(raft)}; canoe down {Hours(down)}; canoe up {Hours(up)} (walk {up.WalkingHours:F0} h); {ms:F0} ms a plan"));
                if (raft.Route is not null) raftGain.Add(raft.WalkingHours / raft.RiverHours);
                if (down.Route is not null) downGain.Add(down.WalkingHours / down.RiverHours);
                upGain.Add(up.Route is null ? 1 : up.WalkingHours / up.RiverHours);
                drawn.Add((at, harbour, down.Route));
                drawn.Add((harbour, at, up.Route));
            }

            Draw(map, drainage.Network, drawn).Save(Path.Combine(output, $"boats-{seed}.png"));
        }

        static string Median(List<double> values) => values.Count == 0 ? "-" : Invariant($"{values.Order().ElementAt(values.Count / 2):F2}x");
        Line($"faster than walking (median): raft down {Median(raftGain)} over {raftGain.Count} journeys; canoe down {Median(downGain)} over {downGain.Count}; canoe up {Median(upGain)} over {upGain.Count} (1x where walking wins)");
        File.WriteAllText(Path.Combine(output, "boats.txt"), text.ToString());
        return 0;
    }

    /// <summary>A journey with the boat at the start's nearest open water.</summary>
    private static RiverPlan Journey(TravelCostModel land, WaterNetwork network, BoatSpec boat, Vector2 from, Vector2 to, string name)
    {
        Vector2 boatAt = RiverRouter.NearestOpenWater(land, network, boat, null, from, SettlementWaterMetres) ?? from;
        return RiverRouter.Plan(land, network, boat, boatAt, null, from, to, name);
    }

    private static string Hours(RiverPlan plan) => plan.Route is TravelRoute route
        ? Invariant($"{plan.RiverHours:F0} h ({plan.WalkingHours / plan.RiverHours:F1}x, {Afloat(route) / 1000:F0} km afloat)")
        : Invariant($"no ({plan.Reason})");

    private static double Afloat(TravelRoute route)
    {
        double metres = 0;
        for (int i = 0; i < route.LegHours.Length; i++)
            if (route.Afloat(i)) metres += Vector2.Distance(route.Points[i], route.Points[i + 1]);
        return metres;
    }

    /// <summary>The land by height, the canoe rivers, and each journey: water legs blue, walked legs orange.</summary>
    private static Image Draw(WorldMap map, WaterNetwork network, List<(Vector2 From, Vector2 To, TravelRoute? Route)> journeys)
    {
        Image image = new(Width, Width);
        double radius = map.Radius;
        (int X, int Y) Pixel(double x, double z) => ((int)((x + radius) / (2 * radius) * (Width - 1)), (int)((z + radius) / (2 * radius) * (Width - 1)));
        for (int py = 0; py < Width; py++)
        for (int px = 0; px < Width; px++)
        {
            double x = (px / (double)(Width - 1) * 2 * radius) - radius, z = (py / (double)(Width - 1) * 2 * radius) - radius;
            MapSample s = map.Broad(x, z);
            double shade = Math.Clamp(s.Elevation / 2500, 0, 1);
            image.Set(px, py, s.Elevation < GenerationConstants.WaterLevel ? (0.1, 0.16, 0.25) : (0.25 + 0.4 * shade, 0.3 + 0.35 * shade, 0.22 + 0.35 * shade));
        }

        foreach (Reach reach in network.Reaches.Where(r => r.Flow >= Boats.LeastFlow))
            foreach (int node in reach.Nodes)
            {
                (int x, int y) = Pixel(network.Grid.X(node), network.Grid.Z(node));
                image.Set(Math.Clamp(x, 0, Width - 1), Math.Clamp(y, 0, Width - 1), reach.Class == Navigability.None ? (0.8, 0.3, 0.3) : (0.35, 0.55, 0.85));
            }

        foreach ((_, _, TravelRoute? route) in journeys)
        {
            if (route is null) continue;
            for (int i = 0; i + 1 < route.Points.Length; i++)
            {
                (double, double, double) colour = route.Afloat(i) ? (0.4, 0.9, 1) : (1, 0.6, 0.2);
                (int x0, int y0) = Pixel(route.Points[i].X, route.Points[i].Y);
                (int x1, int y1) = Pixel(route.Points[i + 1].X, route.Points[i + 1].Y);
                int steps = Math.Max(1, Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
                for (int k = 0; k <= steps; k++)
                {
                    int x = x0 + ((x1 - x0) * k / steps), y = y0 + ((y1 - y0) * k / steps);
                    for (int d = -1; d <= 1; d++) image.Set(Math.Clamp(x + d, 0, Width - 1), Math.Clamp(y, 0, Width - 1), colour);
                }
            }
        }

        return image;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
