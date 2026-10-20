namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// Stable block identity. The numeric value is the world's material slot, so a
/// value may be appended but never renumbered: generated chunks, terrain edits
/// and the saved overlay all store the slot and must keep meaning the same block.
/// </summary>
internal enum BlockId : ushort
{
    Air = 0,
    Grass = 1,
    Dirt = 2,
    Stone = 3,
    Sand = 4,
    Gravel = 5,
    Cobblestone = 6,
    Brick = 7,
    Log = 8,
    Planks = 9,
    /// <summary>A generated tree's invisible trunk core: the tree itself is a mesh (TerrainTrees).</summary>
    TreeCore = 10,
    Water = 11,
    Glass = 12,
    Lamp = 13,
    Bedrock = 14,
    Snow = 15,
    /// <summary>Sun-dried meadow (#10041): open ground where the climate is dry, between lush grass and desert sand.</summary>
    DryGrass = 16,

    // The cave's own rock (#10095), placed by the cave generator only.

    /// <summary>Rock the active water keeps washed: clean, cool and wet.</summary>
    WashedRock = 17,

    /// <summary>Rock on levels dry for ages: darker, warmer, stained.</summary>
    PatinatedRock = 18,

    /// <summary>Calcite laid down by dripping water: pale, banded flowstone and the formations.</summary>
    Flowstone = 19,

    /// <summary>Fallen blocks and boulders: breakdown rubble, freshly broken.</summary>
    Rubble = 20,
}
