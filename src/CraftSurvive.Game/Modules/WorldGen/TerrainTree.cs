namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>One generated tree: its trunk cell, its species (a flora catalog id), and how it is turned and sized.</summary>
/// <param name="GroundY">The first cell above its ground: the bottom of its trunk core.</param>
/// <param name="Species">The tree's species: its index in the flora catalog (#10032).</param>
/// <param name="Variant">A draw in [0, 1) choosing among the species' meshes.</param>
/// <param name="Scale">Size about the species' authored height.</param>
/// <param name="Yaw">Turn about the vertical, in radians.</param>
internal readonly record struct TerrainTree(long X, long GroundY, long Z, ushort Species, double Variant, double Scale, double Yaw);
