using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>
/// Windbound (#9902): a canoe or raft does not cross open lake water in waves past its limit. The waves
/// are the sea state's own - the weather's wind over the lake's fetch - so a small lake is crossed in a
/// gale that keeps the party ashore at a great one. Rivers, whose fetch is a few channel widths, never
/// hold a boat. Engine-free.
/// </summary>
internal static class Windbound
{
    /// <summary>The waves (significant height, metres) on a lake in a wind (the weather's 0-1 channel).</summary>
    internal static double Waves(Lake lake, double windChannel) =>
        SeaWaves.SignificantHeight(SeaState.WindFor(windChannel), SeaWaves.LakeFetchMetres(lake.AreaSquareKilometres));

    /// <summary>Whether a boat is held ashore at a lake in a wind.</summary>
    internal static bool Holds(BoatSpec boat, Lake lake, double windChannel) => Waves(lake, windChannel) > boat.WaveLimit;

    /// <summary>
    /// How many hours until the wind over a lake drops enough to cross, reading the wind <paramref name="windIn"/>
    /// hours ahead, an hour at a time; null when it does not within <paramref name="longestHours"/>.
    /// </summary>
    internal static double? Wait(BoatSpec boat, Lake lake, Func<double, double> windIn, double longestHours)
    {
        for (double hours = 1; hours <= longestHours; hours++)
            if (!Holds(boat, lake, windIn(hours))) return hours;
        return null;
    }

    /// <summary>How a lake is named to the party.</summary>
    internal static string Name(Lake lake) => string.IsNullOrEmpty(lake.Name) ? "the lake" : lake.Name;
}

/// <summary>
/// Which lake crossing the party has been let onto (#9902 review): only pressing on lets it cross a lake
/// the wind holds it at. Waiting - whether the rest is taken, cut short at its longest, or refused - lets it
/// cross nothing, so the lake is judged again in the wind as it is when the journey resumes. Leaving the
/// lake, or finding it frozen, ends the crossing. Engine-free.
/// </summary>
internal sealed class WindboundCrossings
{
    private int pending = -1, crossing = -1;

    /// <summary>Whether to hold the party at the lake it is afloat on (null for none) in a wind (the weather's 0-1 channel).</summary>
    internal bool Holds(Lake? lake, bool frozen, BoatSpec? boat, double windChannel)
    {
        if (lake is null || frozen)
        {
            crossing = -1;
            return false;
        }

        return lake.Id != crossing && boat is not null && Windbound.Holds(boat, lake, windChannel);
    }

    /// <summary>The party was held at a lake: its answer decides the crossing.</summary>
    internal void Raised(Lake lake) => pending = lake.Id;

    /// <summary>An event was answered; pressing on through a windbound one lets the party cross that lake.</summary>
    internal void Answered(bool pressedOn)
    {
        if (pending >= 0 && pressedOn) crossing = pending;
        pending = -1;
    }
}
