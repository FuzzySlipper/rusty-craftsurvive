using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// How a dungeon's voxels are surfaced: the session's mode, each block's own surface, and whether
/// it is drawn with the world's textured block materials or flat colours.
/// </summary>
internal sealed record DungeonSurface(string Name, VoxelSurfaceMode Mode, VoxelMaterialSurface[] Materials, bool Textured)
{
    /// <summary>Rock and soil read as weathered stone: rounded, a little jagged, smooth-shaded.</summary>
    private static readonly SurfaceCharacter Rock = new(VertexPlacement.Smooth, 180f, RockRoughness);

    /// <summary>Built blocks read as crisp blocks: corners kept, faces shaded flat.</summary>
    private static readonly SurfaceCharacter Built = new(VertexPlacement.Blocky, BuiltCreaseDegrees, 0f);

    private const float RockRoughness = 0.2f;
    private const float BuiltCreaseDegrees = 30f;

    /// <summary>The blocks a dungeon builds with; every other block is ground.</summary>
    private static readonly BlockId[] BuiltBlocks =
        [BlockId.Cobblestone, BlockId.Brick, BlockId.Log, BlockId.Planks, BlockId.Glass, BlockId.Lamp];

    internal static DungeonSurface Cubes { get; } = new("cubes", VoxelSurfaceMode.GreedyCubes, [], true);

    internal bool Smooth => Mode != VoxelSurfaceMode.GreedyCubes || Materials.Length > 0;

    /// <summary>The surfaces a debug command chooses by name.</summary>
    internal static DungeonSurface? Named(string name) => name switch
    {
        "cubes" => Cubes,
        "dc" => new(name, VoxelSurfaceMode.DualContouring, [], false),
        "mc" => new(name, VoxelSurfaceMode.MarchingCubes, [], false),
        "dc-textured" => new(name, VoxelSurfaceMode.DualContouring, [], true),
        "mc-textured" => new(name, VoxelSurfaceMode.MarchingCubes, [], true),
        // Ground dual contoured as rock; built blocks stay cubes.
        "dc-mixed" => new(name, VoxelSurfaceMode.DualContouring, Each(BuiltBlocks, VoxelSurfaceMode.GreedyCubes, Built)
            .Concat(Each(Ground(), VoxelSurfaceMode.DualContouring, Rock)).ToArray(), true),
        // Everything dual contoured: built blocks as crisp blocks, ground as rock.
        "dc-blocky" => new(name, VoxelSurfaceMode.DualContouring, Each(BuiltBlocks, VoxelSurfaceMode.DualContouring, Built)
            .Concat(Each(Ground(), VoxelSurfaceMode.DualContouring, Rock)).ToArray(), true),
        _ => null,
    };

    private static IEnumerable<BlockId> Ground() =>
        BlockRegistry.BoundBlocks.Select(block => block.Id).Where(id => !BuiltBlocks.Contains(id));

    private static IEnumerable<VoxelMaterialSurface> Each(IEnumerable<BlockId> blocks, VoxelSurfaceMode mode, SurfaceCharacter character) =>
        blocks.Select(block => new VoxelMaterialSurface((uint)block, mode, character));

    public override string ToString() => Name;
}
