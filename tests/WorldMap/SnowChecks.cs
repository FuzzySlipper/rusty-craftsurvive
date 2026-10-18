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

        Console.WriteLine(FormattableString.Invariant($"snow cover: storm {before:F3}->{during:F3} m, a day later {dayLater:F3} m; warm {laid:F3}->{thawed:F3} m; near freezing {reference:F4}/{continued:F4} m"));
        Check.That(Math.Abs(continued - resumed) < 1e-12, $"saved cells carry on exactly as they would have ({continued:F4} m, restored {resumed:F4} m)");
        Check.That(Math.Abs(reference - continued) < 0.01, $"the cover follows the weather, not how often it is asked ({reference:F4} m and {continued:F4} m)");
    }
}
