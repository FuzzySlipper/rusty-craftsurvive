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
    Leaves = 10,
    Water = 11,
    Glass = 12,
    Lamp = 13,
    Bedrock = 14,
    Snow = 15,
}
