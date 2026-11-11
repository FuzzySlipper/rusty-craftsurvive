using System.Diagnostics;
using System.Globalization;
using System.Text;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// Floods over a stretch of the year (#9880): one continent's river stage routed hour by hour, the
/// floodplains flooding where their reaches run over their banks. Finds the worst flood and draws its
/// floodplain from before the storm, through its peak, to the water's going down; charts the area under
/// water over the whole run, and what reading the floods costs.
/// </summary>
internal static class FloodRun
{
    private const int SampleHours = 6, CropNodes = 96, NodePixels = 4, Columns = 3, ChartHeight = 160, Pad = 8;
    private static readonly int[] FrameOffsets = [-36, -18, 0, 18, 36, 72];

    internal static int Run(Dictionary<string, List<string>> options, int size, ContinentDesign? design, string output)
    {
        ulong seed = ulong.Parse(options["floods"][0], CultureInfo.InvariantCulture);
        double days = options.TryGetValue("days", out List<string>? d) ? double.Parse(d[0], CultureInfo.InvariantCulture) : Seasons.YearDays;
        double startDay = options.TryGetValue("from-day", out List<string>? f) ? double.Parse(f[0], CultureInfo.InvariantCulture) : Seasons.YearDays;
        Directory.CreateDirectory(output);
        TerrainConfiguration configuration = new(seed, size);
        WorldMap map = new(configuration, MapSimulation.Run(configuration, MapRecipe.Default, design));
        MapDrainage drainage = new(map);
        WaterNetwork network = drainage.Network;
        Stopwatch clock = Stopwatch.StartNew();
        FloodPlains plains = new(network, drainage.ElevationAt);
        double plainsMs = clock.Elapsed.TotalMilliseconds;
        RiverStage stage = new(network, map, WeatherField.For(map));
        stage.Start(startDay * 24);

        int samples = (int)(days * 24 / SampleHours);
        List<(int Node, float Level)>[] flooded = new List<(int, float)>[samples];
        double[] area = new double[samples];
        double levelsMs = 0;
        FloodLevels? previous = null;
        for (int s = 0; s < samples; s++)
        {
            stage.AdvanceTo((startDay * 24) + ((s + 1) * SampleHours));
            clock.Restart();
            FloodLevels levels = previous = plains.Levels(stage.At, previous, SampleHours);
            levelsMs += clock.Elapsed.TotalMilliseconds;
            flooded[s] = [.. plains.Nodes.Where(n => !double.IsNaN(levels.LevelAt(n))).Select(n => (n, (float)levels.LevelAt(n)))];
            area[s] = levels.SquareKilometres;
        }

        MapGrid grid = network.Grid;
        double cellKm2 = grid.Spacing * grid.Spacing / 1e6, plainKm2 = plains.Nodes.Length * cellKm2;
        int peak = Array.IndexOf(area, area.Max());
        StringBuilder text = new();
        void Line(string line)
        {
            Console.WriteLine(line);
            text.AppendLine(line);
        }

        Line(Invariant($"floods: seed {seed}, {days:F0} days from day {startDay:F0}; floodplain {plainKm2:F0} km2 over {plains.Nodes.Length} nodes"));
        Line(Invariant($"cost: floodplains built in {plainsMs:F0} ms; reading the floods {levelsMs / samples:F2} ms a stage"));
        Line(Invariant($"under water: most {area.Max():F1} km2 ({area.Max() / plainKm2:P1} of the floodplain) on day {startDay + ((peak + 1) * SampleHours / 24.0):F1}; mean {area.Average():F1} km2; some flood on {area.Count(a => a > 0) * SampleHours / 24.0:F0} of {days:F0} days"));
        if (area.Max() <= 0)
        {
            File.WriteAllText(Path.Combine(output, $"floods-{seed}.txt"), text.ToString());
            return 0;
        }

        // The worst flood's place: the reach with the most nodes under water at the peak.
        int reach = flooded[peak].GroupBy(f => plains.ReachOf(f.Node)).MaxBy(g => g.Count())!.Key;
        (int Node, float Level)[] around = [.. flooded[peak].Where(f => plains.ReachOf(f.Node) == reach)];
        int ci = (int)around.Average(f => f.Node % grid.Side), cj = (int)around.Average(f => f.Node / grid.Side);
        Reach worst = network.Reaches[reach];
        Line(Invariant($"worst flood: reach {reach} at {grid.X((cj * grid.Side) + ci):F0},{grid.Z((cj * grid.Side) + ci):F0}, mean {worst.Flow:F0} m3/s (bankfull {worst.BankfullFlow:F0}), {around.Length * cellKm2:F1} km2 under water at its peak"));
        foreach (int offset in FrameOffsets)
        {
            int at = Math.Clamp(peak + (offset / SampleHours), 0, samples - 1);
            int inCrop = flooded[at].Count(f => Math.Abs((f.Node % grid.Side) - ci) < CropNodes / 2 && Math.Abs((f.Node / grid.Side) - cj) < CropNodes / 2);
            double deepest = flooded[at].Where(f => plains.ReachOf(f.Node) == reach).Select(f => f.Level - plains.Ground(f.Node)).DefaultIfEmpty(0).Max();
            Line(Invariant($"  {offset:+0;-0;0} h: {inCrop * cellKm2:F1} km2 under water in view, deepest {deepest:F1} m"));
        }

        File.WriteAllText(Path.Combine(output, $"floods-{seed}.txt"), text.ToString());
        Draw(grid, plains, flooded, area, peak, ci, cj, network).Save(Path.Combine(output, $"floods-{seed}.png"));
        return 0;
    }

    private static Image Draw(MapGrid grid, FloodPlains plains, List<(int Node, float Level)>[] flooded, double[] area, int peak, int ci, int cj, WaterNetwork network)
    {
        int frame = CropNodes * NodePixels, rows = (FrameOffsets.Length + Columns - 1) / Columns;
        Image image = new((Columns * (frame + Pad)) + Pad, (rows * (frame + Pad)) + Pad + ChartHeight + Pad);
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) image.Set(x, y, (0.08, 0.08, 0.1));
        int i0 = ci - (CropNodes / 2), j0 = cj - (CropNodes / 2);
        double low = double.MaxValue, high = double.MinValue;
        for (int j = 0; j < CropNodes; j++) for (int i = 0; i < CropNodes; i++)
        {
            int n = Node(grid, i0 + i, j0 + j);
            if (n < 0) continue;
            low = Math.Min(low, plains.Ground(n));
            high = Math.Max(high, plains.Ground(n));
        }

        bool[] channel = new bool[grid.Count];
        foreach (Reach reach in network.Reaches)
            if (reach.Flow >= WaterNetwork.CanoeFlow) foreach (int n in reach.Nodes) channel[n] = true;
        for (int k = 0; k < FrameOffsets.Length; k++)
        {
            int at = Math.Clamp(peak + (FrameOffsets[k] / SampleHours), 0, flooded.Length - 1);
            Dictionary<int, float> water = flooded[at].ToDictionary(f => f.Node, f => f.Level);
            int ox = Pad + ((k % Columns) * (frame + Pad)), oy = Pad + ((k / Columns) * (frame + Pad));
            for (int j = 0; j < CropNodes; j++) for (int i = 0; i < CropNodes; i++)
            {
                int n = Node(grid, i0 + i, j0 + j);
                (double, double, double) colour;
                if (n < 0) colour = (0, 0, 0);
                else
                {
                    double g = plains.Ground(n), shade = 0.25 + (0.6 * (g - low) / Math.Max(1, high - low));
                    int east = Node(grid, i0 + i + 1, j0 + j), south = Node(grid, i0 + i, j0 + j + 1);
                    double slope = (east >= 0 ? plains.Ground(east) - g : 0) + (south >= 0 ? plains.Ground(south) - g : 0);
                    shade = Math.Clamp(shade - (slope * 0.02), 0.1, 0.95);
                    colour = (shade * 0.8, shade, shade * 0.7);
                    if (plains.ReachOf(n) >= 0) colour = (colour.Item1 * 0.85, colour.Item2 * 0.95, colour.Item3 * 0.75 + 0.1);
                    if (channel[n]) colour = (0.15, 0.35, 0.75);
                    if (water.TryGetValue(n, out float level))
                    {
                        double depth = Math.Clamp((level - g) / 3, 0, 1);
                        colour = (0.2 - (0.15 * depth), 0.55 - (0.25 * depth), 0.95 - (0.25 * depth));
                    }
                }

                for (int py = 0; py < NodePixels; py++) for (int px = 0; px < NodePixels; px++)
                    image.Set(ox + (i * NodePixels) + px, oy + (j * NodePixels) + py, colour);
            }

            // How far through: a bar along the frame's top edge, gold at the peak.
            double share = (double)at / Math.Max(1, flooded.Length - 1);
            for (int x = 0; x < (int)(share * frame); x++) for (int y = 0; y < 3; y++)
                image.Set(ox + x, oy + y, FrameOffsets[k] == 0 ? (0.95, 0.8, 0.3) : (0.8, 0.8, 0.85));
        }

        // The area under water across the run.
        int top = Pad + (rows * (frame + Pad)), width = image.Width - (2 * Pad);
        double most = Math.Max(area.Max(), 1e-9);
        for (int x = 0; x < width; x++)
        {
            int s = (int)((double)x / width * area.Length);
            int bar = (int)(area[s] / most * (ChartHeight - 4));
            for (int y = 0; y < bar; y++) image.Set(Pad + x, top + ChartHeight - 1 - y, s == peak ? (0.95, 0.8, 0.3) : (0.3, 0.55, 0.95));
        }

        return image;
    }

    private static int Node(MapGrid grid, int i, int j) => i < 0 || j < 0 || i > grid.Segments || j > grid.Segments ? -1 : (j * grid.Side) + i;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
