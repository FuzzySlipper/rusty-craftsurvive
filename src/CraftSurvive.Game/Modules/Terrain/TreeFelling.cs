using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// A generated tree stands while its whole trunk core stands on solid ground (#9665). An edit that
/// breaks any cell of the core, or takes away the cell it stands on, fells the tree: the rest of
/// the core is cleared in the same transaction, so no invisible obstacle and no floating tree is
/// left, and the overlay records the felling with the edit, across streaming and reloads.
/// </summary>
internal static class TreeFelling
{
    /// <summary>The cells the edits take with them: the remaining core of every tree they fell.</summary>
    internal static IReadOnlyList<TerrainVoxelEdit> Dependents(IReadOnlyList<TerrainVoxelEdit> edits, TerrainRecipe recipe,
        Func<VoxelAddress, ushort> materialAt)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(materialAt);
        List<TerrainVoxelEdit> felled = [];
        foreach (IGrouping<(long X, long Z), TerrainVoxelEdit> column in edits.GroupBy(edit => (edit.Address.X, edit.Address.Z)))
        {
            if (recipe.TreeAtColumn(column.Key.X, column.Key.Z) is not TerrainTree tree
                || !column.Any(edit => Fells(edit, tree)))
            {
                continue;
            }

            foreach (VoxelAddress core in Core(tree))
            {
                if (materialAt(core) == (ushort)BlockId.TreeCore)
                {
                    felled.Add(new TerrainVoxelEdit(core, TerrainConstants.EmptyMaterial));
                }
            }
        }

        return felled;
    }

    /// <summary>Whether a tree still stands: every core cell in place, on collidable ground.</summary>
    internal static bool Stands(TerrainTree tree, Func<VoxelAddress, ushort> materialAt) =>
        Core(tree).All(cell => materialAt(cell) == (ushort)BlockId.TreeCore)
        && BlockRegistry.TryGetBySlot(materialAt(Support(tree)), out BlockDefinition ground) && ground.Collidable;

    internal static IEnumerable<VoxelAddress> Core(TerrainTree tree)
    {
        for (long y = 0; y < GenerationConstants.TreeCoreHeight; y++)
        {
            yield return new VoxelAddress(tree.X, tree.GroundY + y, tree.Z);
        }
    }

    internal static VoxelAddress Support(TerrainTree tree) => new(tree.X, tree.GroundY - 1, tree.Z);

    /// <summary>An edit fells a tree when it changes a core cell to anything else, or leaves its support not collidable.</summary>
    private static bool Fells(TerrainVoxelEdit edit, TerrainTree tree)
    {
        long y = edit.Address.Y;
        if (y >= tree.GroundY && y < tree.GroundY + GenerationConstants.TreeCoreHeight)
        {
            return edit.Material != (ushort)BlockId.TreeCore;
        }

        return y == tree.GroundY - 1
            && !(BlockRegistry.TryGetBySlot(edit.Material, out BlockDefinition ground) && ground.Collidable);
    }
}
