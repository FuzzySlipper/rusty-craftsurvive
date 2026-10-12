using System.Numerics;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Tests;

/// <summary>
/// The moon on its own month (#9806): its age, phase and place on the sky's arc follow the clock, it
/// rises later each day, a quarter is up by day, and its light follows phase and height.
/// </summary>
internal static class MoonChecks
{
    private const double Tolerance = 1e-6;

    /// <summary>Steps through a day when looking for a moonrise: a game minute.</summary>
    private const int Steps = 24 * 60;

    internal static void Run()
    {
        Check.That(Math.Abs(WorldClock.MoonAge(new WorldTime(0, 0.5)) - WorldClock.FirstNoonAge) < Tolerance, "the first noon has the first noon's age");
        double perDay = WorldClock.MoonAge(new WorldTime(3, 0.5)) - WorldClock.MoonAge(new WorldTime(2, 0.5));
        Check.That(Math.Abs(perDay - (1d / WorldClock.LunarMonthDays)) < Tolerance, "the moon ages a month's share each day");
        WorldTime later = WorldClock.Advance(new WorldTime(1, 0.3), WorldClock.LunarMonthDays * WorldClock.DaySeconds);
        Check.That(Math.Abs(WorldClock.MoonAge(later) - WorldClock.MoonAge(new WorldTime(1, 0.3))) < 1e-4, "a month later the moon is as it was");

        WorldTime newMoon = At(0d, 0.5d), fullMoon = At(0.5d, 0d), firstQuarter = At(0.25d, 0.625d);
        Check.That(WorldClock.MoonLit(newMoon) < Tolerance && Math.Abs(WorldClock.MoonLit(fullMoon) - 1d) < Tolerance, "new is dark and full is lit");
        Check.That(Vector3.Distance(WorldClock.TowardMoon(newMoon), WorldClock.TowardSun(newMoon.DayFraction)) < 1e-4, "a new moon stands beside the sun");
        Vector3 full = WorldClock.TowardMoon(fullMoon), sun = WorldClock.TowardSun(fullMoon.DayFraction);
        Check.That(Math.Abs(full.X + sun.X) < 1e-4 && Math.Abs(full.Y + sun.Y) < 1e-4, "a full moon stands opposite the sun");
        Check.That(WorldClock.MoonElevation(fullMoon) > 0.9, "a full moon is high at midnight");
        Check.That(WorldClock.MoonElevation(firstQuarter) > 0.5 && WorldClock.SunElevation(firstQuarter.DayFraction) > 0.5, "a first-quarter moon is up by day, in the afternoon");

        // It rises later each day, by about a day's share of its month.
        double first = Moonrise(10), second = Moonrise(11);
        double lateHours = ((second - first + 1d) % 1d) * 24d;
        Check.That(lateHours is > 1.4 and < 2.1, $"the moon rises later each day ({lateHours:F2} h)");

        // A new world's first night has a bright moon up.
        WorldTime firstNight = new(0, 21d / 24d);
        Check.That(WorldClock.MoonElevation(firstNight) > 0.2 && WorldClock.MoonLit(firstNight) > 0.6, "a new world's first night has a bright moon up");

        // Light follows phase, height and whether it is up.
        Check.That(Moon.Share(fullMoon) > 0.95f, $"a full moon high gives its full light ({Moon.Share(fullMoon):F2})");
        Check.That(Moon.Share(At(0d, 0d)) == 0f, "a new moon gives no light");
        Check.That(Moon.Share(At(0.5d, 0.5d)) == 0f, "a full moon at noon is down and gives no light");
        WorldTime half = At(0.25d, 0.75d);
        Check.That(WorldClock.MoonElevation(half) > 0.9, "a first-quarter moon is high at dusk");
        float halfShare = Moon.Share(half);
        Check.That(halfShare is > 0.2f and < 0.5f, $"a near-half moon gives about a third of a full one's light ({halfShare:F2})");
        Check.That(Moon.Colour(0d).X > Moon.Colour(0d).Z && Moon.Colour(1d).Z > Moon.Colour(1d).X, "the moon rises warm and whitens as it climbs");
        Check.That(Moon.Disc(0d) > Moon.Disc(1d), "a rising moon looks large");
    }

    /// <summary>The first moment, from day 30 on, with the moon this far through its month at this day fraction (to within a day).</summary>
    private static WorldTime At(double age, double dayFraction)
    {
        double daysFromNoon = ((age - WorldClock.FirstNoonAge + 1d) % 1d) * WorldClock.LunarMonthDays;
        double moment = 30d * WorldClock.LunarMonthDays + 0.5d + daysFromNoon;
        long day = (long)Math.Round(moment - dayFraction);
        return new WorldTime(day, dayFraction);
    }

    /// <summary>The day fraction the moon rises on a day.</summary>
    private static double Moonrise(long day)
    {
        for (int step = 1; step <= Steps; step++)
        {
            double before = WorldClock.MoonElevation(new WorldTime(day, (step - 1d) / Steps));
            double now = WorldClock.MoonElevation(new WorldTime(day, (double)step / Steps));
            if (before < 0d && now >= 0d) return (double)step / Steps;
        }

        return double.NaN;
    }
}
