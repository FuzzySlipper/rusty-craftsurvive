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
    private TerrainGroundMaterials? ground;
    private TerrainScatter? scatter;
    private VoxelScenePresentation? projection;

    /// <summary>Authored content is admitted at product create, so every later projection can bind it.</summary>
    internal TerrainPresentation(IEngineContext engine, ProductContent content)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        atlas = new TerrainAtlasCatalog(engine, content);
        try
        {
            ground = new TerrainGroundMaterials(engine, content);
            scatter = new TerrainScatter(engine);
        }
        catch
        {
            ground?.Dispose();
            atlas.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The atlas image every material in this world is built from, for anything that needs to draw
    /// from the same content - the blast's dust and debris borrow it rather than opening their own.
    /// </summary>
    internal RenderResourceReference AtlasSprite => Atlas.AtlasReference;

    internal void ConfigureGround(SpatialSession session) => ground!.Configure(engine, session);

    /// <summary>
    /// Ground farther than this from the camera is drawn from the Engine's coarse meshes (#9563):
    /// at the first-person view distance this halves the triangles with no visible change (Engine
    /// #9498's trial); collision, picking and navigation keep full resolution.
    /// </summary>
    internal const double CoarseBeyondMetres = 48;

    internal void Project(SpatialSession session)
    {
        projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(
            session,
            MaterialBindings(),
            FaceMaterialBindings()));
        engine.VoxelScenePresentation.SetLevelOfDetail(new VoxelSceneLevelOfDetailRequest(projection, CoarseBeyondMetres));
        scatter?.Grow(projection);
    }

    /// <summary>Sets how densely grass and bushes grow (0 removes them), for tuning.</summary>
    internal string TuneScatter(float grassPerSquareMetre, float bushesPerSquareMetre)
    {
        VoxelScenePresentation current = projection ?? throw new InvalidOperationException("Terrain presentation is unavailable.");
        scatter!.Tune(current, grassPerSquareMetre, bushesPerSquareMetre);
        return ScatterReadout();
    }

    /// <summary>What grows on the ground now, for the scene readout.</summary>
    internal string ScatterReadout() => projection is VoxelScenePresentation current && scatter is not null
        ? scatter.Readout(engine.VoxelScenePresentation.RefreshScene(current))
        : "scatter=none";

    /// <summary>How many chunks are drawn and how many of them coarse, for the scene readout.</summary>
    internal string LevelOfDetailReadout()
    {
        if (projection is not VoxelScenePresentation current) return "lod=none";
        VoxelScenePresentationReadout readout = engine.VoxelScenePresentation.SetLevelOfDetail(new VoxelSceneLevelOfDetailRequest(current, CoarseBeyondMetres));
        return FormattableString.Invariant($"lod coarseBeyond={CoarseBeyondMetres}m chunks={readout.ChunkCount} coarse={readout.CoarseChunkCount}");
    }

    /// <summary>
    /// Draws another session with the same block materials: a separate space built from the same
    /// atlas. The caller owns the projection and disposes it with the session.
    /// </summary>
    internal VoxelScenePresentation ProjectAnother(SpatialSession session) =>
        engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(
            session,
            MaterialBindings(blend: false),
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
        // The projection lets go of the appearances its scatters grow first.
        projection?.Dispose();
        projection = null;
        scatter?.Dispose();
        scatter = null;
        ground?.Dispose();
        ground = null;
        atlas?.Dispose();
        atlas = null;
    }

    private TerrainAtlasCatalog Atlas => atlas ?? throw new InvalidOperationException("Terrain atlas catalog is unavailable.");

    /// <summary>
    /// One binding per registered block. The registry is the block floor and the atlas catalog is
    /// its material closure, so a block exists in the world only once both agree.
    /// </summary>
    private ReadOnlyMemory<VoxelSceneMaterialBinding> MaterialBindings(bool blend = true) =>
        BlockRegistry.BoundBlocks.Select(block => new VoxelSceneMaterialBinding(block.Slot,
            ground?.For(block.Id, blend) ?? Atlas.BaseMaterial(block.Id))).ToArray();

    private ReadOnlyMemory<VoxelSceneFaceMaterialBinding> FaceMaterialBindings() =>
        BlockRegistry.BoundBlocks
            .Where(block => ground?.For(block.Id) is null && Atlas.TopMaterial(block.Id) is not null)
            .Select(block => new VoxelSceneFaceMaterialBinding(block.Slot, SpatialFace.PosY, Atlas.TopMaterial(block.Id)!))
            .ToArray();
}
