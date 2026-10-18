namespace CraftSurvive.Game.Modules.Weather;

/// <summary>One cell's snow as saved (#10068): where (cell indices), how deep (metres) and the hour it stands at.</summary>
internal readonly record struct SnowCell(int CellX, int CellZ, double Depth, double Hours)
{
    /// <summary>Snow lies no deeper than this (metres); above it, more falling is blown off.</summary>
    internal const double MostDepth = 0.8;
    /// <summary>The snow cover keeps (and saves) at most this many cells.</summary>
    internal const int MostCells = 1024;
}
