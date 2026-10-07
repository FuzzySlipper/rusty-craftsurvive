using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// What each block's material means to a voxel session, from the block registry: whether a body
/// collides with it, and whether it hides the faces of its neighbours. Water, glass and tree cores do
/// not, so the bed under a river and the room behind a window are drawn. Every session the product
/// creates - the open world and each dungeon - is configured the same way.
/// </summary>
internal static class VoxelMaterialRules
{
    internal static void Apply(IEngineContext engine, SpatialSession session)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(
            session,
            BlockRegistry.MaterialBlocks.Select(block => new VoxelMaterialCollision((uint)block.Id, block.Collidable)).ToArray()));
        engine.Voxel.ConfigureMaterialOcclusion(new VoxelMaterialOcclusionRequest(
            session,
            BlockRegistry.MaterialBlocks.Select(block => new VoxelMaterialOcclusion((uint)block.Id, block.Occludes)).ToArray()));
    }
}
