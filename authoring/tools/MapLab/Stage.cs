using System.Diagnostics;
using System.Globalization;
using System.Text;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// The river stage over a year (#9878): one continent's network routed hour by hour through the
/// weather and the seasons, with the flow of a few telling reaches charted against their means - the
/// port's great river, a cold mountain stream, a dry-country stream and a lake's outflow - and the share
/// of the network in flood, frozen and dry, day by day.
/// </summary>
internal static class StageRun
{
    private const int Width = 1400, PanelHeight = 170, Top = 30;
    private const double LeastRatio = 1.0 / 16, MostRatio = 16;

    internal static int Run(Dictionary<string, List<string>> options, int size, ContinentDesign? design, string output)
    {
        ulong seed = ulong.Parse(options["stage"][0], CultureInfo.InvariantCulture);
        double days = options.TryGetValue("days", out List<string>? d) ? double.Parse(d[0], CultureInfo.InvariantCulture) : Seasons.YearDays;
        double startDay = options.TryGetValue("from-day", out List<string>? f) ? double.Parse(f[0], CultureInfo.InvariantCulture) : Seasons.YearDays;
        Directory.CreateDirectory(output);
        TerrainConfiguration configuration = new(seed, size);
        WorldMap map = new(configuration, MapSimulation.Run(configuration, MapRecipe.Default, design));
        MapDrainage drainage = new(map);
        WaterNetwork network = drainage.Network;
        WeatherField weather = WeatherField.For(map);
        RiverStage stage = new(network, map, weather);
        Stopwatch clock = Stopwatch.StartNew();
        stage.Start(startDay * 24);
        double startMs = clock.Elapsed.TotalMilliseconds;

        Settlement? port = Settlements.Choose(map, design, drainage).FirstOrDefault(s => s.Kind == SettlementKind.Port);
        Reach[] reaches = [.. network.Reaches];
        double Temperature(Reach r) => map.Sample(network.Grid.X(r.Nodes[r.Nodes.Length / 2]), network.Grid.Z(r.Nodes[r.Nodes.Length / 2])).Temperature;
        double Moisture(Reach r) => map.Sample(network.Grid.X(r.Nodes[r.Nodes.Length / 2]), network.Grid.Z(r.Nodes[r.Nodes.Length / 2])).Moisture;
        List<(string Name, int Reach)> picks = [];
        if (port is not null) picks.Add(("port's great river", port.Reach));
        if (reaches.Where(r => r.Flow is >= 3 and <= 40 && r.Gradient > 0.004).MinBy(Temperature) is Reach mountain) picks.Add(("cold mountain stream", mountain.Id));
        if (reaches.Where(r => r.Flow is >= 0.2 and <= 1).MinBy(Moisture) is Reach dry) picks.Add(("dry-country stream", dry.Id));
        if (network.Lakes.FirstOrDefault(l => l.Outflow >= 0) is LakeNode lake) picks.Add(($"{lake.Lake.Kind.ToString().ToLowerInvariant()} lake's outflow", lake.Outflow));

        int hours = (int)(days * 24);
        double[,] ratio = new double[picks.Count, hours];
        bool[,] ice = new bool[picks.Count, hours], flood = new bool[picks.Count, hours], dryNow = new bool[picks.Count, hours];
        int dayCount = (int)days;
        double[] icedShare = new double[dayCount], dryShare = new double[dayCount], floodShare = new double[dayCount];
        double[] meanRatio = new double[picks.Count];
        clock.Restart();
        for (int h = 0; h < hours; h++)
        {
            stage.AdvanceTo((startDay * 24) + h + 1);
            for (int p = 0; p < picks.Count; p++)
            {
                ReachStage s = stage.At(picks[p].Reach);
                ratio[p, h] = s.Ratio;
                ice[p, h] = s.Ice;
                flood[p, h] = s.Overbank;
                dryNow[p, h] = s.Dry;
                meanRatio[p] += s.Ratio / hours;
            }

            if (h % 24 == 12)
            {
                int iced = 0, dried = 0, flooded = 0;
                for (int r = 0; r < reaches.Length; r++)
                {
                    ReachStage s = stage.At(r);
                    if (s.Ice) iced++;
                    if (s.Dry) dried++;
                    if (s.Overbank) flooded++;
                }

                icedShare[h / 24] = iced / (double)reaches.Length;
                dryShare[h / 24] = dried / (double)reaches.Length;
                floodShare[h / 24] = flooded / (double)reaches.Length;
            }
        }

        double stepMs = clock.Elapsed.TotalMilliseconds / hours;
        StringBuilder text = new();
        void Line(string line)
        {
            Console.WriteLine(line);
            text.AppendLine(line);
        }

        Line(Invariant($"river stage: seed {seed}, {reaches.Length} reaches over {stage.CellCount} precipitation cells; {days:F0} days from day {startDay:F0}"));
        Line(Invariant($"cost: start (calibration, warm-up) {startMs:F0} ms; a game hour's step {stepMs:F1} ms"));
        for (int p = 0; p < picks.Count; p++)
        {
            Reach r = reaches[picks[p].Reach];
            int iceHours = 0, floodHours = 0, dryHours = 0;
            double low = double.MaxValue, high = 0;
            for (int h = 0; h < hours; h++)
            {
                if (ice[p, h]) iceHours++;
                if (flood[p, h]) floodHours++;
                if (dryNow[p, h]) dryHours++;
                low = Math.Min(low, ratio[p, h]);
                high = Math.Max(high, ratio[p, h]);
            }

            int mid = r.Nodes[r.Nodes.Length / 2];
            Line(Invariant($"  {picks[p].Name} (reach {r.Id} at {network.Grid.X(mid):F0},{network.Grid.Z(mid):F0}): mean {r.Flow:F1} m3/s (bankfull {r.BankfullFlow:F0}); flow {low:F2}-{high:F2} x mean, averaging {meanRatio[p]:F2}; flood {floodHours / 24.0:F1} d, ice {iceHours / 24.0:F1} d, dry {dryHours / 24.0:F1} d"));
        }

        Line(Invariant($"network: most frozen {icedShare.Max():P0} of reaches (day {Array.IndexOf(icedShare, icedShare.Max())}), most dry {dryShare.Max():P0} (day {Array.IndexOf(dryShare, dryShare.Max())}), most in flood {floodShare.Max():P1} (day {Array.IndexOf(floodShare, floodShare.Max())})"));
        File.WriteAllText(Path.Combine(output, $"stage-{seed}.txt"), text.ToString());
        Draw(picks, ratio, ice, flood, dryNow, icedShare, dryShare, floodShare, startDay, hours).Save(Path.Combine(output, $"stage-{seed}.png"));
        return 0;
    }

    private static Image Draw(List<(string Name, int Reach)> picks, double[,] ratio, bool[,] ice, bool[,] flood, bool[,] dry,
        double[] iced, double[] dried, double[] flooded, double startDay, int hours)
    {
        int panels = picks.Count + 1;
        Image image = new(Width, Top + (panels * PanelHeight));
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < Width; x++) image.Set(x, y, (0.08, 0.08, 0.1));
        int X(int h) => (int)((double)h / hours * (Width - 1));
        // The season band: blue in winter, green in spring, gold in summer, rust in autumn.
        for (int x = 0; x < Width; x++)
        {
            double h = (startDay * 24) + ((double)x / Width * hours);
            (double, double, double) colour = Seasons.At(h) switch
            {
                Season.Winter => (0.35, 0.5, 0.85),
                Season.Spring => (0.4, 0.75, 0.4),
                Season.Summer => (0.9, 0.75, 0.3),
                _ => (0.75, 0.4, 0.25),
            };
            for (int y = 4; y < Top - 6; y++) image.Set(x, y, colour);
        }

        for (int p = 0; p < picks.Count; p++)
        {
            int top = Top + (p * PanelHeight), bottom = top + PanelHeight - 12;
            int Y(double r) => bottom - (int)((Math.Log2(Math.Clamp(r, LeastRatio, MostRatio)) - Math.Log2(LeastRatio)) / (Math.Log2(MostRatio) - Math.Log2(LeastRatio)) * (bottom - top));
            for (int h = 0; h < hours; h++)
            {
                int x = X(h);
                if (ice[p, h]) for (int y = top; y < bottom; y++) image.Set(x, y, (0.2, 0.3, 0.4));
                if (dry[p, h]) for (int y = top; y < bottom; y++) image.Set(x, y, (0.35, 0.28, 0.15));
            }

            for (int x = 0; x < Width; x++) { image.Set(x, Y(1), (0.4, 0.4, 0.45)); image.Set(x, top, (0.25, 0.25, 0.3)); }
            for (int h = 1; h < hours; h++)
            {
                int x0 = X(h - 1), x1 = X(h), y0 = Y(ratio[p, h - 1]), y1 = Y(ratio[p, h]);
                (double, double, double) colour = flood[p, h] ? (1, 0.3, 0.25) : (0.4, 0.75, 1);
                for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++) image.Set(x1, y, colour);
                image.Set(x0, y0, colour);
            }
        }

        int baseTop = Top + (picks.Count * PanelHeight), baseBottom = baseTop + PanelHeight - 12;
        for (int day = 0; day < iced.Length; day++)
        {
            int x0 = (int)((double)day / iced.Length * (Width - 1)), x1 = (int)((double)(day + 1) / iced.Length * (Width - 1));
            for (int x = x0; x < x1; x++)
            {
                int yi = baseBottom - (int)(iced[day] * (baseBottom - baseTop));
                int yd = baseBottom - (int)(dried[day] * (baseBottom - baseTop));
                int yf = baseBottom - (int)(Math.Min(1, flooded[day] * 10) * (baseBottom - baseTop));
                image.Set(x, yi, (0.6, 0.85, 1));
                image.Set(x, yd, (0.85, 0.65, 0.3));
                image.Set(x, yf, (1, 0.3, 0.25));
            }
        }

        return image;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
