namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Opt-in generation experiment; ordinary worlds keep their original tree decisions.</summary>
internal enum ForestPattern { Baseline, Dense }

internal static class ForestPatterns
{
    internal const string EnvironmentVariable = "CRAFT_FOREST_STUDY";
    internal const string DenseName = "dense";
    internal const int DenseCellMetres = 4;
    private const int DenseForestOneIn = 2;
    private const int SubcellsPerOriginalCell = 4;
    private const double ClearingShare = 0.12;
    private const long GroveMetres = 48;

    internal static bool IsForest(MapBiome biome) =>
        biome is MapBiome.TemperateForest or MapBiome.BorealForest or MapBiome.Rainforest;

    internal static int CellMetres(ForestPattern pattern) => pattern == ForestPattern.Dense
        ? DenseCellMetres : GenerationConstants.FeatureCellSize;

    internal static int OneIn(ForestPattern pattern, MapBiome biome, double groveDraw) =>
        pattern == ForestPattern.Baseline ? MapBiomes.TreeOneIn(biome)
        : IsForest(biome) && groveDraw >= ClearingShare ? DenseForestOneIn
        : MapBiomes.TreeOneIn(biome) * SubcellsPerOriginalCell;

    internal static string GroveKey(long x, long z) => TerrainGeneratorContract.CoordinateKey(
        GridMath.FloorDivide(x, GroveMetres), GridMath.FloorDivide(z, GroveMetres));
}
