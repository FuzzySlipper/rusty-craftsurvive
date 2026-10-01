namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// The blocks a player builds with, in the order the UI offers them. Building is the product's
/// cubic, grid-precise construction; it is kept apart from the terrain's own materials so a player
/// builds homes from building blocks rather than reshaping the ground. A block is named by its
/// registry name, so the UI carries names and never block ids.
/// </summary>
internal static class BuildPalette
{
    private static readonly BlockId[] Ordered =
    [
        BlockId.Planks,
        BlockId.Cobblestone,
        BlockId.Brick,
        BlockId.Stone,
        BlockId.Log,
        BlockId.Glass,
    ];

    /// <summary>What a floor or wall is built from when the request names nothing.</summary>
    internal const BlockId Default = BlockId.Planks;

    internal static ReadOnlySpan<BlockId> Blocks => Ordered;

    /// <summary>The palette as the UI receives it: registry names, comma-separated, in order.</summary>
    internal static string Names { get; } = string.Join(',', Ordered.Select(id => BlockRegistry.Get(id).Name));

    /// <summary>The palette block with a registry name, or false for anything not in the palette.</summary>
    internal static bool TryFind(string name, out BlockId block)
    {
        foreach (BlockId id in Ordered)
        {
            if (string.Equals(BlockRegistry.Get(id).Name, name, StringComparison.Ordinal))
            {
                block = id;
                return true;
            }
        }

        block = BlockId.Air;
        return false;
    }
}
