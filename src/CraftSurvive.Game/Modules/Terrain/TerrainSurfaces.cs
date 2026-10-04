using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>Natural ground is reconstructed; construction and passable water keep their grid.</summary>
internal static class TerrainSurfaces
{
    private const float GroundCreaseDegrees = 60f;
    private const float GroundRoughness = 0f;

    internal static void Apply(IEngineContext engine, SpatialSession session) =>
        engine.Voxel.ConfigureMaterialSurfaces(new VoxelMaterialSurfaceRequest(
            session, VoxelSurfaceMode.DualContouring,
            BlockRegistry.BoundBlocks.Select(block => TerrainDensity.IsGround(block.Slot)
                ? new VoxelMaterialSurface(block.Slot, VoxelSurfaceMode.DualContouring,
                    new SurfaceCharacter(VertexPlacement.Sharp, GroundCreaseDegrees, GroundRoughness))
                : new VoxelMaterialSurface(block.Slot, VoxelSurfaceMode.GreedyCubes, SurfaceCharacter.Default)).ToArray()));
}
