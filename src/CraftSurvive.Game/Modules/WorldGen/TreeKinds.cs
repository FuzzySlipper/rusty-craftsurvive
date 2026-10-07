namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>The kinds of tree the generator plants; each is drawn from its own meshes (TerrainTrees).</summary>
internal enum TreeKind : byte
{
    Oak,
    Pine,
    Birch,
    Dead,
    Palm,
}

/// <summary>One generated tree: its trunk cell, what it is, and how it is turned and sized.</summary>
/// <param name="GroundY">The first cell above its ground: the bottom of its trunk core.</param>
/// <param name="Variant">A draw in [0, 1) choosing among the kind's meshes.</param>
/// <param name="Scale">Size about the kind's authored height.</param>
/// <param name="Yaw">Turn about the vertical, in radians.</param>
internal readonly record struct TerrainTree(long X, long GroundY, long Z, TreeKind Kind, double Variant, double Scale, double Yaw);

/// <summary>Which trees grow in which country.</summary>
internal static class TreeKinds
{
    // The share of each biome's trees that are its second (and third) kind.
    private const double TemperateBirch = 0.25, TemperatePine = 0.15;
    private const double BorealBirch = 0.15;
    private const double RainforestPalm = 0.35;
    private const double GrasslandBirch = 0.2;
    private const double SteppeDead = 0.5;

    /// <param name="draw">A draw in [0, 1) for this tree.</param>
    internal static TreeKind For(MapBiome biome, double draw) => biome switch
    {
        MapBiome.TemperateForest => draw < TemperatePine ? TreeKind.Pine : draw < TemperatePine + TemperateBirch ? TreeKind.Birch : TreeKind.Oak,
        MapBiome.BorealForest => draw < BorealBirch ? TreeKind.Birch : TreeKind.Pine,
        MapBiome.Rainforest => draw < RainforestPalm ? TreeKind.Palm : TreeKind.Oak,
        MapBiome.Grassland => draw < GrasslandBirch ? TreeKind.Birch : TreeKind.Oak,
        MapBiome.ColdSteppe => draw < SteppeDead ? TreeKind.Dead : TreeKind.Pine,
        _ => TreeKind.Oak,
    };
}
