namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// How a world's size sets its simulation (#9549, Den design/continental-scale). The map
/// pipeline's lengths were tuned on regional worlds of about ten kilometres; a continent
/// (about 390 km, 150,000 km²) runs the same modelled pipeline with every length scaled by
/// <see cref="Lengths"/>, a coarser lattice, taller peaks and rivers only where a catchment is
/// regional in size. A regional world keeps exactly its tuning, so its output does not change.
/// </summary>
internal sealed record MapScale(double Lengths, double TargetSpacing, double PeakElevation, double MaximumElevation, double SourceCatchment)
{
    /// <summary>The world size the pipeline's metre lengths were tuned for.</summary>
    internal const int ReferenceSize = 10_240;
    /// <summary>Regional worlds reach this size; continents start at <see cref="ContinentalMinimumSize"/>.</summary>
    internal const int RegionalMaximumSize = 65_536;
    internal const int ContinentalMinimumSize = 320_000;
    internal const int ContinentalMaximumSize = 450_000;
    internal const int DefaultContinentalSize = 390_000;

    /// <summary>A continent's lattice: one node a kilometre; regions refine it on demand (#9550).</summary>
    private const double ContinentalSpacing = 1000;
    private const double ContinentalPeak = 1800;
    private const double ContinentalCeiling = 2400;
    /// <summary>
    /// River sources grow with the area a continent covers, less than in proportion: a continent
    /// keeps its great rivers and their main branches, and regions add the tributaries.
    /// </summary>
    private const double CatchmentExponent = 1.5;

    internal static MapScale Regional { get; } = new(1, MapGrid.TargetSpacing, MapSimulation.RegionalPeakElevation, WorldMap.MaximumElevation, MapRivers.SourceCatchment);

    internal bool Continental => Lengths > 1;

    internal static bool IsContinental(int size) => size >= ContinentalMinimumSize;

    internal static MapScale For(int size)
    {
        if (!IsContinental(size)) return Regional;
        double lengths = size / (double)ReferenceSize;
        return new(lengths, ContinentalSpacing, ContinentalPeak, ContinentalCeiling, MapRivers.SourceCatchment * Math.Pow(lengths, CatchmentExponent));
    }

    /// <summary>The scale of a lattice's world, from its extent.</summary>
    internal static MapScale For(MapGrid grid) => For((int)Math.Round(grid.Radius * 2));

    internal static bool IsValidSize(int size) =>
        size <= RegionalMaximumSize || size is >= ContinentalMinimumSize and <= ContinentalMaximumSize;
}
