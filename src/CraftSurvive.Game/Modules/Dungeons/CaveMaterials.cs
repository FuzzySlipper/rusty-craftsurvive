using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The cave's ground materials (#10095; owner, 2026-10-18: underground is not one material called rock): which rock
/// block is which material, and the terrain layer each draws in, so where two meet on the smooth rock the Engine blends
/// them. Each material's colour is its own calm map (content/game/textures/cave-materials/, whose alpha says how dry it
/// is); the cave shader adds the strata, the 3D pixels and the grain over all of them alike.
/// </summary>
internal static class CaveMaterials
{
    /// <summary>The cave's material maps, base first: the order of the terrain layers.</summary>
    internal static readonly string[] Names = ["limestone", "washed", "patinated", "mud", "silt", "rubble", "flowstone", "turf"];

    /// <summary>Each rock block and the layer (an index into <see cref="Names"/>) it draws in.</summary>
    internal static readonly (BlockId Block, int Layer)[] Layers =
    [
        (BlockId.Stone, 0),
        (BlockId.WashedRock, 1),
        (BlockId.PatinatedRock, 2),
        (BlockId.Dirt, 3),
        (BlockId.Sand, 4),
        (BlockId.Rubble, 5),
        (BlockId.Flowstone, 6),
        (BlockId.Grass, 7),
    ];
}
