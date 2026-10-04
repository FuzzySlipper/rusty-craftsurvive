using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// How the voxel world is drawn: the block atlas's materials bound to the Engine's directional
/// voxel projection.
/// </summary>
internal sealed class TerrainPresentation : IDisposable
{
    private readonly IEngineContext engine;
    private TerrainAtlasCatalog? atlas;
    private VoxelScenePresentation? projection;

    /// <summary>Authored content is admitted at product create, so every later projection can bind it.</summary>
    internal TerrainPresentation(IEngineContext engine, ProductContent content)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        atlas = new TerrainAtlasCatalog(engine, content);
    }

    /// <summary>
    /// The atlas image every material in this world is built from, for anything that needs to draw
    /// from the same content - the blast's dust and debris borrow it rather than opening their own.
    /// </summary>
    internal RenderResourceReference AtlasSprite => Atlas.AtlasReference;

    internal void Project(SpatialSession session)
    {
        projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(
            session,
            MaterialBindings(),
            FaceMaterialBindings()));
    }

    /// <summary>
    /// Draws another session with the same block materials: a separate space built from the same
    /// atlas. The caller owns the projection and disposes it with the session.
    /// </summary>
    internal VoxelScenePresentation ProjectAnother(SpatialSession session) =>
        engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(
            session,
            MaterialBindings(),
            FaceMaterialBindings()));

    /// <summary>Re-meshes the projection after the scene or the world origin changed.</summary>
    internal VoxelScenePresentationReadout Refresh()
    {
        VoxelScenePresentation current = projection ?? throw new InvalidOperationException("Terrain presentation is unavailable.");
        return engine.VoxelScenePresentation.RefreshScene(current);
    }

    internal bool Projected => projection is not null;

    public void Dispose()
    {
        projection?.Dispose();
        projection = null;
        atlas?.Dispose();
        atlas = null;
    }

    private TerrainAtlasCatalog Atlas => atlas ?? throw new InvalidOperationException("Terrain atlas catalog is unavailable.");

    /// <summary>
    /// One binding per registered block. The registry is the block floor and the atlas catalog is
    /// its material closure, so a block exists in the world only once both agree.
    /// </summary>
    private ReadOnlyMemory<VoxelSceneMaterialBinding> MaterialBindings() =>
        BlockRegistry.BoundBlocks.Select(block => new VoxelSceneMaterialBinding(block.Slot, Atlas.BaseMaterial(block.Id))).ToArray();

    private ReadOnlyMemory<VoxelSceneFaceMaterialBinding> FaceMaterialBindings() =>
        BlockRegistry.BoundBlocks
            .Where(block => Atlas.TopMaterial(block.Id) is not null)
            .Select(block => new VoxelSceneFaceMaterialBinding(block.Slot, SpatialFace.PosY, Atlas.TopMaterial(block.Id)!))
            .ToArray();
}
