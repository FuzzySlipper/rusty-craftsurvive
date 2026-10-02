using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>How a dungeon's voxels are surfaced: one of the looks being compared.</summary>
internal enum DungeonSurface
{
    /// <summary>Cubes; a sculpted dungeon's rock is a separate dual-contoured mesh beside them.</summary>
    Cubes,

    /// <summary>
    /// Everything voxels, dual contoured: rock shaped by its sculpted densities with smooth shading
    /// and a little roughness, building blocks kept exactly on the grid.
    /// </summary>
    Smooth,

    /// <summary>As <see cref="Smooth"/>, but rock shaded in flat facets and rougher: chiselled rather than worn.</summary>
    Faceted,

    /// <summary>Rock marched (marching cubes) in flat facets, building blocks as cubes.</summary>
    Marched,

    /// <summary>
    /// As <see cref="Faceted"/>, with the building's masonry weathered: brick reconstructed with sharp
    /// features rather than kept on the grid, a little rough, its exposed corners and edges worn back
    /// by its densities. Floors, stairs and timber stay on the grid.
    /// </summary>
    Ruined,
}

/// <summary>
/// The Engine surface each material of a dungeon gets under a <see cref="DungeonSurface"/>. Rock -
/// stone and its strata - is reconstructed from densities; building blocks keep the grid, as Blocky
/// dual contouring or as cubes, so masonry reads as masonry whatever the rock does.
/// </summary>
internal static class DungeonSurfaces
{
    /// <summary>Shading bends smoothly up to this angle on worn rock, and creases beyond it.</summary>
    private const float WornCreaseDegrees = 60f;

    /// <summary>How far worn rock's vertices are jostled, in cells.</summary>
    private const float WornRoughness = 0f;

    /// <summary>How far chiselled rock's vertices are jostled, in cells.</summary>
    private const float ChiselledRoughness = 0.05f;

    /// <summary>How far weathered building blocks' vertices are jostled, in cells.</summary>
    private const float WeatheredRoughness = 0.06f;

    /// <summary>Flat shading: every facet its own normal.</summary>
    private const float FlatCreaseDegrees = 0f;

    private static readonly BlockId[] Rock = [BlockId.Stone, BlockId.Dirt, BlockId.Gravel, BlockId.Bedrock];

    /// <summary>
    /// The building blocks that weather: masonry walls. Floors, stairs and timber (planks,
    /// cobblestone, logs) stay on the grid in every look, since a sharp-featured block has chamfered
    /// edges and a chamfered stair is a ramp the body cannot step.
    /// </summary>
    private static readonly BlockId[] Weathering = [BlockId.Brick];

    /// <summary>Whether a block wears back under a weathering look.</summary>
    internal static bool Wears(BlockId block) => Array.IndexOf(Weathering, block) >= 0;

    internal static bool IsRock(BlockId block) => Array.IndexOf(Rock, block) >= 0;

    /// <summary>Whether a look wears its building back by density, not only its rock.</summary>
    internal static bool Weathers(DungeonSurface surface) => surface == DungeonSurface.Ruined;

    /// <summary>The mode of every material not listed by <see cref="Materials"/>.</summary>
    internal static VoxelSurfaceMode SessionMode(DungeonSurface surface) => surface switch
    {
        DungeonSurface.Smooth or DungeonSurface.Faceted or DungeonSurface.Ruined => VoxelSurfaceMode.DualContouring,
        _ => VoxelSurfaceMode.GreedyCubes,
    };

    /// <summary>Each bound block's surface under a look; empty for cubes.</summary>
    internal static VoxelMaterialSurface[] Materials(DungeonSurface surface)
    {
        if (surface == DungeonSurface.Cubes)
        {
            return [];
        }

        SurfaceCharacter rock = surface switch
        {
            DungeonSurface.Smooth => new SurfaceCharacter(VertexPlacement.Sharp, WornCreaseDegrees, WornRoughness),
            _ => new SurfaceCharacter(VertexPlacement.Sharp, FlatCreaseDegrees, ChiselledRoughness),
        };
        VoxelSurfaceMode rockMode = surface == DungeonSurface.Marched ? VoxelSurfaceMode.MarchingCubes : VoxelSurfaceMode.DualContouring;
        (VoxelSurfaceMode Mode, SurfaceCharacter Character) building = surface == DungeonSurface.Marched
            ? (VoxelSurfaceMode.GreedyCubes, SurfaceCharacter.Default)
            : (VoxelSurfaceMode.DualContouring, new SurfaceCharacter(VertexPlacement.Blocky, FlatCreaseDegrees, 0f));
        SurfaceCharacter weathered = new(VertexPlacement.Sharp, FlatCreaseDegrees, WeatheredRoughness);
        return BlockRegistry.BoundBlocks
            .Select(block => IsRock(block.Id)
                ? new VoxelMaterialSurface(block.Slot, rockMode, rock)
                : Weathers(surface) && Wears(block.Id)
                    ? new VoxelMaterialSurface(block.Slot, VoxelSurfaceMode.DualContouring, weathered)
                    : new VoxelMaterialSurface(block.Slot, building.Mode, building.Character))
            .ToArray();
    }

    internal static DungeonSurface? Parse(string name) => name switch
    {
        "cubes" => DungeonSurface.Cubes,
        "dc" => DungeonSurface.Smooth,
        "faceted" => DungeonSurface.Faceted,
        "mc" => DungeonSurface.Marched,
        "ruined" => DungeonSurface.Ruined,
        _ => null,
    };
}
