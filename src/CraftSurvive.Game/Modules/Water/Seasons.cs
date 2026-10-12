using CraftSurvive.Game.Modules.Sky;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>The four seasons, by the year's quarter.</summary>
internal enum Season { Winter, Spring, Summer, Autumn }

/// <summary>
/// The year's turn (#9878): a minimal season for water - air colder in winter and warmer in summer, as a
/// smooth swing about each place's climate temperature (0 polar, 1 tropical). The year is the sky's own
/// (<see cref="WorldClock.StarYearDays"/>), so the stars and the seasons turn together. Nothing else in the
/// game reads seasons yet (#9554).
/// </summary>
internal static class Seasons
{
    internal const double YearDays = WorldClock.StarYearDays;
    private const double HoursPerDay = 24;

    /// <summary>How far the air swings from the climate's temperature at midwinter and midsummer.</summary>
    internal const double Swing = 0.15;

    /// <summary>The share of the year at which midsummer falls; midwinter is half a year from it. Day 0 is early spring.</summary>
    private const double Midsummer = 0.3;

    /// <summary>Air at or below this temperature (product units) freezes: snow falls and water ices.</summary>
    internal const double Freezing = 0.28;

    /// <summary>The share of the year at a time (absolute game hours).</summary>
    internal static double YearShare(double hours) => ((hours / HoursPerDay / YearDays) % 1 + 1) % 1;

    /// <summary>How much warmer (or, negative, colder) the air is than the climate's temperature at a time.</summary>
    internal static double Offset(double hours) => Swing * Math.Cos(2 * Math.PI * (YearShare(hours) - Midsummer));

    /// <summary>The season at a time: each a quarter of the year, centred on midwinter and midsummer.</summary>
    internal static Season At(double hours)
    {
        double fromMidwinter = ((YearShare(hours) - Midsummer + 0.5) % 1 + 1) % 1;
        return (Season)((int)Math.Floor((fromMidwinter + 0.125) * 4) % 4);
    }
}
