namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The world's water budget (#9874): how much of the rain reaches the rivers. The map's discharge is
/// rain-weighted area, and rain is relative (about 1 over land), so one runoff figure turns it into
/// real flow. It changes nothing the simulation does; it is what makes a river a creek or a great
/// river, its width and depth, and how far a canoe or a boat can go up it.
/// </summary>
internal static class WaterBudget
{
    /// <summary>Runoff per unit of relative rain, in metres a year: a temperate maritime climate's.</summary>
    internal const double RunoffMetresPerYear = 0.6;

    private const double SecondsPerYear = 365.25 * 24 * 3600;
    private const double SquareMetresPerSquareKilometre = 1e6;

    /// <summary>Mean flow in cubic metres a second from rain-weighted catchment in square kilometres.</summary>
    internal static double Flow(double catchmentSquareKilometres) =>
        catchmentSquareKilometres * SquareMetresPerSquareKilometre * RunoffMetresPerYear / SecondsPerYear;
}
