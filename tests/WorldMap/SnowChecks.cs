using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The snow cover (#10068), on synthetic worlds of one climate: snow builds under a snowstorm and lies on in
/// freezing air, melts where it is warm, is the same for the same weather, and carries on from its saved cells
/// exactly as it would have run on.
/// </summary>
internal static class SnowChecks
{
    private const ulong Seed = 0x5_0A0_10068UL;
    private const double WindAngle = 0.4, WorldMetres = 100_000, Peak = 400;
    /// <summary>A polar climate, and a temperate one, in product temperature units.</summary>
    private const double Polar = 0.05, Temperate = 0.6;
    /// <summary>The checks start a year and a half in, at midwinter; the storm is summoned there.</summary>
    private const double StartHours = (1.3 * Seasons.YearDays * 24) + (0.5 * Seasons.YearDays * 24);
    private const double StormHours = 6;

    private static Func<double, double, MapSample> Climate(double temperature) =>
        (_, _) => new MapSample(Elevation: 50, Temperature: temperature, Moisture: 0.5, Rock: 0, Detail: 0);

    internal static void Run()
    {
        WeatherField polar = new(Seed, WindAngle, WorldMetres, Peak, Climate(Polar));
        SnowCover cover = new(polar);
        cover.Lay(0, 0, StartHours, -SnowCover.MostDepth);
        double before = cover.DepthAt(0, 0, StartHours);
        polar.Summon(WeatherKinds.Snowstorm, 0, 0, StartHours);
        double during = cover.DepthAt(0, 0, StartHours + StormHours);
        Check.That(during > before + (SnowCover.DepthPerHour * StormHours * 0.3), $"snow builds under a snowstorm ({before:F3} m to {during:F3} m in {StormHours} h)");
        Check.That(during <= SnowCover.MostDepth, "snow lies no deeper than its most");

        polar.ClearSummoned();
        double dayLater = cover.DepthAt(0, 0, StartHours + StormHours + 24);
        Check.That(dayLater > during * 0.8, $"in polar air the snow is still there a day later ({dayLater:F3} m of {during:F3} m)");

        WeatherField temperate = new(Seed, WindAngle, WorldMetres, Peak, Climate(Temperate));
        SnowCover warm = new(temperate);
        warm.Lay(0, 0, StartHours, 0.4);
        double laid = warm.DepthAt(0, 0, StartHours);
        double thawed = warm.DepthAt(0, 0, StartHours + 24);
        Check.That(laid > 0.3 && thawed < laid * 0.2, $"in warm air laid snow melts within a day ({laid:F3} m to {thawed:F3} m)");

        WeatherField near = new(Seed, WindAngle, WorldMetres, Peak, Climate(Seasons.Freezing + Seasons.Swing));
        SnowCover asked = new(near), carried = new(near);
        asked.DepthAt(1200, -800, StartHours + 10);
        for (double hours = StartHours + 10; hours <= StartHours + 30; hours += 0.25) asked.DepthAt(1200, -800, hours);
        double reference = asked.DepthAt(1200, -800, StartHours + 30);
        carried.DepthAt(1200, -800, StartHours + 10);
        SnowCover restored = new(near);
        restored.Restore(carried.Capture(), StartHours + 10);
        double resumed = restored.DepthAt(1200, -800, StartHours + 30);
        double continued = carried.DepthAt(1200, -800, StartHours + 30);
        SnowCover budgeted = new(near), direct = new(near);
        int calls = 0;
        do { budgeted.FollowAround(1200, -800, StartHours + 40); calls++; } while (budgeted.Settled < 9 && calls < 1000);
        double spread = budgeted.DepthAt(1200, -800, StartHours + 40), atOnce = direct.DepthAt(1200, -800, StartHours + 40);
        Check.That(calls > 1 && budgeted.Settled == 9, $"cells coming into reach settle over several updates, not at once ({calls} calls)");
        Check.That(Math.Abs(spread - atOnce) < 0.02, $"settling a little each update lays the same snow as at once ({spread:F4} m and {atOnce:F4} m)");

        // A full store (R10068-2): with every kept cell visited recently, walking on into new ground keeps the party's cells
        // and forgets old ones, never the cell just needed.
        SnowCover full = new(near);
        List<SnowCell> kept = [];
        for (int i = 0; i < SnowCell.MostCells; i++) kept.Add(new SnowCell(i % 32, 1000 + (i / 32), 0.2, StartHours + 39));
        full.Restore([.. kept.OrderBy(cell => cell.CellZ).ThenBy(cell => cell.CellX)], StartHours + 40);
        bool walked = true;
        for (int step = 0; step < 12; step++)
        {
            try { full.FollowAround(-50_000 + (step * SnowCover.CellMetres), 0, StartHours + 40 + step); full.DepthAt(-50_000 + (step * SnowCover.CellMetres), 0, StartHours + 40 + step); }
            catch (KeyNotFoundException) { walked = false; }
        }

        Check.That(walked && full.Count <= SnowCell.MostCells, $"a full store keeps walking into new ground within its bound ({full.Count} cells)");

        // Tracks fill in at the cap (R10068-4): with the snow as deep as it lies, a storm still falls into a fresh print and
        // covers it within hours.
        SnowCover capped = new(polar);
        capped.Lay(0, 0, StartHours + 100, SnowCover.MostDepth);
        double atStamp = capped.FallenAt(0, 0, StartHours + 100);
        polar.Summon(WeatherKinds.Snowstorm, 0, 0, StartHours + 100);
        int stage = 0;
        double filledAfter = double.NaN;
        for (double later = 0.5; later <= 6 && stage < TrackFading.Gone; later += 0.5)
        {
            double fallen = capped.FallenAt(0, 0, StartHours + 100 + later) - atStamp;
            stage = TrackFading.StageOf(fallen, later, capped.DepthAt(0, 0, StartHours + 100 + later));
            if (stage >= TrackFading.Gone) filledAfter = later;
        }

        polar.ClearSummoned();
        Check.That(stage >= TrackFading.Gone && capped.DepthAt(0, 0, StartHours + 106) >= SnowCover.MostDepth - 0.01,
            $"at the depth's cap a storm still fills a print, gone after {filledAfter} h, the depth staying at its cap");
        Check.That(TrackFading.StageOf(0, 1, 0.005) == TrackFading.Gone && TrackFading.StageOf(0, 1, 0.3) == 0,
            "a print goes once the snow under it melts out, and stands while it lies");

        // A reused slot is told again (R10068-5): fill and publish the pool, then a new fresh print in the oldest slot is
        // placed at its own position on the very next publish, though its stage (fresh) is the stage the slot showed.
        SnowCover deep = new(polar);
        deep.Lay(0, 0, StartHours + 200, 0.4);
        FootprintPool pool = new(deep, 8);
        for (int i = 0; i < pool.Size; i++) pool.Stamp(new System.Numerics.Vector3(i, 0, 0), 0, false, FootprintMaker.Boot, StartHours + 200);
        pool.Fade(StartHours + 200);
        IReadOnlyList<FootprintChange> first = pool.Changes(shown: true, all: false);
        foreach (FootprintChange change in first) pool.Told(change, taken: true);
        bool quiet = pool.Changes(shown: true, all: false).Count == 0;
        int reused = pool.Stamp(new System.Numerics.Vector3(-3, 0, 5), 1, true, FootprintMaker.Paw, StartHours + 200);
        pool.Fade(StartHours + 200);
        IReadOnlyList<FootprintChange> next = pool.Changes(shown: true, all: false);
        Check.That(first.Count == pool.Size && quiet && next.Count == 1 && next[0].Slot == reused && next[0].Print is { } placed
            && placed.At == new System.Numerics.Vector3(-3, 0, 5) && placed.Maker == FootprintMaker.Paw && placed.Stage == 0,
            "a pool slot reused for a fresh print is placed again at its new position, though its stage is unchanged");
        foreach (FootprintChange change in next) pool.Told(change, taken: true);
        IReadOnlyList<FootprintChange> hidden = pool.Changes(shown: false, all: false);
        Check.That(hidden.Count == pool.Size && hidden.All(change => change.Print is null), "leaving the open world removes every placed print");

        Console.WriteLine(FormattableString.Invariant($"snow cover: storm {before:F3}->{during:F3} m, a day later {dayLater:F3} m; warm {laid:F3}->{thawed:F3} m; near freezing {reference:F4}/{continued:F4} m"));
        Check.That(Math.Abs(continued - resumed) < 1e-12, $"saved cells carry on exactly as they would have ({continued:F4} m, restored {resumed:F4} m)");
        Check.That(Math.Abs(reference - continued) < 0.01, $"the cover follows the weather, not how often it is asked ({reference:F4} m and {continued:F4} m)");
    }
}
