using System.Globalization;
using System.Numerics;

namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// A moment in the world's days: which day it is, counting from the first, and how far through it.
/// A day fraction of 0 is midnight, 0.25 dawn, 0.5 noon and 0.75 dusk.
/// </summary>
internal readonly record struct WorldTime(long Day, double DayFraction);

/// <summary>
/// The world's clock as rules: how fast days pass, where the sun stands, and how much daylight a
/// moment has. It is product time, advanced by the Engine's step time, so a paused or slow session
/// keeps its own days; the sky and lights realise it, and survival and encounters read it.
/// </summary>
internal static class WorldClock
{
    /// <summary>One day and night, in seconds of play.</summary>
    internal const double DaySeconds = 20d * 60d;

    /// <summary>A new world starts in the morning, so its first minutes are light.</summary>
    internal const double FirstMorning = 0.3d;

    /// <summary>Below this much daylight it is night, for the rules that care.</summary>
    internal const double NightBelowDaylight = 0.25d;

    /// <summary>The sun's elevation, as a sine, where twilight begins and full day is reached.</summary>
    internal const double TwilightStartsAt = -0.25d;
    internal const double FullDayAt = 0.35d;

    /// <summary>How far the sun's arc leans off the east-west line, so noon light is not straight down.</summary>
    private const float ArcTilt = 0.35f;

    private const int HoursPerDay = 24;
    private const int MinutesPerHour = 60;

    internal static WorldTime Start => new(0, FirstMorning);

    /// <summary>The time a number of seconds of play later.</summary>
    internal static WorldTime Advance(WorldTime time, double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        double fraction = time.DayFraction + (seconds / DaySeconds);
        long days = (long)Math.Floor(fraction);
        return new WorldTime(time.Day + days, fraction - days);
    }

    /// <summary>When a rest ends: early morning, once it is light.</summary>
    internal const double WakingFraction = 0.27d;

    /// <summary>Seconds of play from a moment until a day fraction next comes round.</summary>
    internal static double SecondsUntil(double dayFraction, double target)
    {
        double ahead = target - dayFraction;
        return (ahead <= 0d ? ahead + 1d : ahead) * DaySeconds;
    }

    /// <summary>The sun's elevation as a sine: 1 at noon, 0 at dawn and dusk, -1 at midnight.</summary>
    internal static double SunElevation(double dayFraction) => Math.Sin(2d * Math.PI * (dayFraction - 0.25d));

    /// <summary>How much of the day's light there is, from 0 at night to 1 in full day, eased through twilight.</summary>
    internal static double Daylight(double dayFraction)
    {
        double t = Math.Clamp((SunElevation(dayFraction) - TwilightStartsAt) / (FullDayAt - TwilightStartsAt), 0d, 1d);
        return t * t * (3d - (2d * t));
    }

    internal static bool IsNight(double dayFraction) => Daylight(dayFraction) < NightBelowDaylight;

    /// <summary>The unit direction toward the sun.</summary>
    internal static Vector3 TowardSun(double dayFraction) => OnArc(dayFraction);

    /// <summary>
    /// The moon's month (#9806): new to new in this many days. Half a real month, so a cycle passes in
    /// about five hours of play and a long journey sees several.
    /// </summary>
    internal const double LunarMonthDays = 15d;

    /// <summary>
    /// How far through its month the moon is on the first day's noon: a waxing gibbous, so a new
    /// world's first night has a bright moon up from dusk.
    /// </summary>
    internal const double FirstNoonAge = 0.4d;

    /// <summary>
    /// How far through its month the moon is, 0 to 1: 0 new (beside the sun), 0.25 first quarter,
    /// 0.5 full (opposite the sun), 0.75 last quarter.
    /// </summary>
    internal static double MoonAge(WorldTime time)
    {
        double age = FirstNoonAge + ((time.Day + time.DayFraction - 0.5d) / LunarMonthDays);
        return age - Math.Floor(age);
    }

    /// <summary>How much of the moon's face is lit: 0 new, 1 full.</summary>
    internal static double MoonLit(WorldTime time) => (1d - Math.Cos(2d * Math.PI * MoonAge(time))) / 2d;

    /// <summary>
    /// The unit direction toward the moon: on the sun's arc, behind the sun by its age, so it rises
    /// about an hour and a half later each day, a full moon rises at sunset, and a quarter moon is up
    /// for half the day.
    /// </summary>
    internal static Vector3 TowardMoon(WorldTime time) => OnArc(time.DayFraction - MoonAge(time));

    /// <summary>The moon's elevation as a sine.</summary>
    internal static double MoonElevation(WorldTime time) => TowardMoon(time).Y;

    /// <summary>A point of the sky's arc at a day fraction: rising toward +x at 0.25, highest at 0.5, setting toward -x at 0.75.</summary>
    private static Vector3 OnArc(double dayFraction)
    {
        double angle = 2d * Math.PI * (dayFraction - 0.25d);
        return Vector3.Normalize(new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), ArcTilt));
    }

    /// <summary>The day and the hour as a player reads them, for example "Day 3, 07:12".</summary>
    internal static string Describe(WorldTime time)
    {
        int minutes = (int)Math.Floor(time.DayFraction * HoursPerDay * MinutesPerHour);
        return string.Create(CultureInfo.InvariantCulture,
            $"Day {time.Day + 1}, {minutes / MinutesPerHour:00}:{minutes % MinutesPerHour:00}");
    }
}
