using CraftSurvive.Game.Modules.Travel;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// The fronts a journey would meet, and when (#9739): the party's march is rehearsed with the costs
/// it will really pay - night, fatigue, load and the weather on the way - and the field is read where
/// the party would be at the hour it would be there. A daylight-pace estimate would miss a front
/// that arrives after the nominal duration but before a night march gets in.
/// </summary>
internal static class RouteForecast
{
    /// <summary>A front is met where it covers the party at least this much.</summary>
    internal const double CoverMet = 0.3;
    /// <summary>The march is read about this many times along the route, and at least this often in game hours.</summary>
    private const int Readings = 48;
    private const double LongestStepHours = 0.25;
    /// <summary>No rehearsal runs past this many game hours (two months), whatever the route.</summary>
    private const double LongestJourneyHours = 60 * 24;

    /// <param name="party">The party as it would set out; it is copied, never moved.</param>
    /// <param name="startHours">The weather's clock (absolute game hours) when the party sets out.</param>
    /// <param name="nightAfter">Whether it is night so many game hours after setting out.</param>
    internal static IReadOnlyList<(WeatherFront Front, double After)> Encounters(
        PartyTravel party, WeatherField field, double startHours, Func<double, bool> nightAfter)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(nightAfter);
        PartyTravel walk = party.Rehearsal();
        List<(WeatherFront Front, double After)> met = [];
        if (walk.Route is null) return met;
        double step = Math.Min(LongestStepHours, Math.Max(walk.RemainingHours, LongestStepHours) / Readings);
        double elapsed = 0;
        Read(field, walk, startHours, elapsed, met);
        while (walk.State == TravelState.Travelling && elapsed < LongestJourneyHours)
        {
            double from = elapsed;
            double spent = walk.Advance(step, hours => nightAfter(from + hours),
                hours => field.Sample(walk.Position.X, walk.Position.Y, startHours + from + hours).Effects.TravelCost);
            if (spent <= 0) break;
            elapsed += spent;
            Read(field, walk, startHours, elapsed, met);
        }

        return met;
    }

    private static void Read(WeatherField field, PartyTravel walk, double startHours, double elapsed, List<(WeatherFront Front, double After)> met)
    {
        foreach (FrontPresence over in field.Sample(walk.Position.X, walk.Position.Y, startHours + elapsed).Fronts)
        {
            if (over.Cover >= CoverMet && met.All(seen => seen.Front.Key != over.Front.Key)) met.Add((over.Front, elapsed));
        }
    }
}
