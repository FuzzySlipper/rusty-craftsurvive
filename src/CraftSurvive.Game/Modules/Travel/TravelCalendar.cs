using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Sky;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>
/// Journey lengths in the terms an expedition plans by (#9552): a continent is crossed in days and
/// weeks, not hours. A day of travel is the daylight of one world day; the nights are camped.
/// </summary>
internal static class TravelCalendar
{
    private const int DaySamples = 1440;
    private const double HoursPerDay = 24;

    /// <summary>Ground covered on foot in first person per game hour: the walking speed over one game hour of real time.</summary>
    internal const double WalkingMetresPerHour = PlayerConstants.GroundSpeed * WorldClock.DaySeconds / HoursPerDay;

    /// <summary>Daylight hours in one world day, from the world clock's own night rule.</summary>
    internal static double DaylightHoursPerDay { get; } =
        Enumerable.Range(0, DaySamples).Count(i => !WorldClock.IsNight((i + 0.5) / DaySamples)) * HoursPerDay / DaySamples;

    /// <summary>Days of travel for this many daylight hours, camping the nights.</summary>
    internal static double Days(double daylightHours) => daylightHours / DaylightHoursPerDay;

    /// <summary>What remains of a journey: daylight hours while within a day, days beyond.</summary>
    internal static string Remaining(double daylightHours) => daylightHours < DaylightHoursPerDay
        ? FormattableString.Invariant($"{daylightHours:F1} h of daylight travel")
        : FormattableString.Invariant($"about {Days(daylightHours):F0} days of travel");

    /// <summary>
    /// A route's length and both paces (#9553): by map, in daylight hours while within a day and days
    /// beyond, and on foot in first person, which covers open ground at walking speed whatever the country.
    /// </summary>
    internal static string Describe(double metres, double daylightHours)
    {
        double footHours = metres / WalkingMetresPerHour;
        return daylightHours < DaylightHoursPerDay
            ? FormattableString.Invariant($"{metres / 1000:F1} km, ≈ {daylightHours:F1} h by map · ≈ {footHours:F1} h on foot")
            : FormattableString.Invariant($"{metres / 1000:F0} km, ≈ {Days(daylightHours):F0} days by map ({daylightHours:F0} h by daylight) · ≈ {Days(footHours):F0} days on foot");
    }
}
