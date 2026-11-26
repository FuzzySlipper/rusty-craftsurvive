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
    internal static readonly string[] Names = ["limestone", "washed", "patinated", "mud", "silt", "rubble", "flowstone", "turf", "moss", "slime", "moonmilk"];

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
        (BlockId.MossyRock, 8),
        (BlockId.SlimyRock, 9),
        (BlockId.Moonmilk, 10),
    ];

    /// <summary>
    /// A cave in the overworld's session (#10889): its rock, mud and silt become the cave's own blocks (the overworld draws
    /// stone, dirt and sand as its own ground), and its ruin's leftover masonry the cave's rubble; everything else keeps its block.
    /// </summary>
    internal static BlockId Underground(BlockId block) => block switch
    {
        BlockId.Stone or BlockId.Bedrock => BlockId.CaveLimestone,
        BlockId.Dirt => BlockId.CaveMud,
        BlockId.Sand => BlockId.CaveSilt,
        // What of a ruin's masonry its kit leaves as voxels - floors, treads, the stubs of burst walls - is the cave's
        // broken stone (#10964): the overworld's session draws brick, cobblestone and planks as the player's building
        // blocks, square and in their atlas tiles.
        BlockId.Brick or BlockId.Cobblestone or BlockId.Planks => BlockId.Rubble,
        _ => block,
    };

    /// <summary>
    /// The cave's blocks that blend as the overworld session's terrain layers, each with its map in <see cref="Names"/>:
    /// the session takes sixteen slots at most and the overworld's ground has eight, so the cave's two rarest coats, slime
    /// and moonmilk, are drawn unblended (#10889; an Engine limit), and its turf is the overworld's grass.
    /// </summary>
    internal static readonly (BlockId Block, string Map)[] Blended =
    [
        (BlockId.CaveLimestone, "limestone"),
        (BlockId.WashedRock, "washed"),
        (BlockId.PatinatedRock, "patinated"),
        (BlockId.CaveMud, "mud"),
        (BlockId.CaveSilt, "silt"),
        (BlockId.Rubble, "rubble"),
        (BlockId.Flowstone, "flowstone"),
        (BlockId.MossyRock, "moss"),
    ];

    /// <summary>The cave's blocks drawn unblended in the overworld's session, each in its own map.</summary>
    internal static readonly (BlockId Block, string Map)[] Unblended =
    [
        (BlockId.SlimyRock, "slime"),
        (BlockId.Moonmilk, "moonmilk"),
    ];
}
