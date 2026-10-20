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
    private readonly ProductContent content;

    /// <summary>The cave's ground materials (#10095), one set per voxel size (a map's repeat is counted in voxels), made when first asked for.</summary>
    private readonly Dictionary<double, TerrainGroundMaterials> caveGrounds = [];

    /// <summary>Authored content is admitted at product create, so every later projection can bind it.</summary>
    internal TerrainPresentation(IEngineContext engine, ProductContent content, SurfaceMoss? moss = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        atlas = new TerrainAtlasCatalog(engine, content);
        try
        {
            ground = new TerrainGroundMaterials(engine, content);
            scatter = new TerrainScatter(engine, content, moss);
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
    /// #9498's trial); collision, picking and navigation keep full resolution. Coarse chunks grow no
    /// voxel scatter, but the voxel scatter's band (<see cref="VegetationTiles.VoxelReach"/>) ends inside
    /// this, and the vegetation tiles grow beyond it.
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
    internal string TuneScatter(float grassPerSquareMetre)
    {
        VoxelScenePresentation current = projection ?? throw new InvalidOperationException("Terrain presentation is unavailable.");
        scatter!.Tune(current, grassPerSquareMetre);
        return ScatterReadout();
    }

    /// <summary>
    /// Grows the voxel scatter again: its copies take their material's shader values when a layer is set, so a
    /// change to the surface moss (#10044) reaches the field stones only through a new layer.
    /// </summary>
    internal void RegrowScatter()
    {
        if (projection is VoxelScenePresentation current) scatter?.Grow(current);
    }

    /// <summary>Whether grass is blades or cards (#10038); setting it grows the voxel scatter again.</summary>
    internal bool GrassBlades
    {
        get => scatter!.Blades;
        set
        {
            scatter!.Blades = value;
            if (projection is VoxelScenePresentation current) scatter.Grow(current);
        }
    }

    /// <summary>The kinds of ground cover at their current densities, for the vegetation tiles to grow too.</summary>
    internal IEnumerable<TerrainScatter.Kind> ScatterKinds => scatter!.Kinds();

    /// <summary>The shade grass's clumps for the forest floor (#10034), with each one's share.</summary>
    internal IReadOnlyList<(Appearance Look, Appearance? Lite, float Share)> ShadeGrass => scatter!.ShadeGrass;

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
    /// <param name="rock">The material each of the space's rock blocks draws in; the cave rock for any not given.</param>
    internal VoxelScenePresentation ProjectAnother(SpatialSession session, IReadOnlyDictionary<BlockId, Material>? rock = null) =>
        engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(
            session,
            SeparateBindings(rock),
            FaceMaterialBindings(skipRock: ground is not null)));

    /// <summary>The cave rock (#10090): a dungeon's rock blocks are drawn in it, as its rock meshes are.</summary>
    internal const string CaveRockName = "cave-rock";

    /// <summary>The cave rock's authored, triplanar material.</summary>
    internal Material CaveRock => (ground ?? throw new InvalidOperationException("Terrain ground materials are unavailable.")).Plain(CaveRockName);

    /// <summary>The cave rock drawn through a product shader (#10094); the caller keeps it for as long as it is drawn, the set disposes it.</summary>
    internal Material CaveRockWithShader(MaterialShader shader) =>
        (ground ?? throw new InvalidOperationException("Terrain ground materials are unavailable.")).PlainWithShader(CaveRockName, shader);

    /// <summary>The cave's materials blended as terrain layers and drawn through a product shader (#10095); disposed with the set.</summary>
    internal Material CaveLayersWithShader(MaterialShader shader, double voxelSize)
    {
        if (!caveGrounds.TryGetValue(voxelSize, out TerrainGroundMaterials? set))
        {
            set = new TerrainGroundMaterials(engine, content, voxelSize, GroundTextureSet.Cave);
            caveGrounds[voxelSize] = set;
        }

        return set.LayeredWithShader(GroundTextureSet.Cave.LayerNames, shader);
    }

    /// <summary>Names a separate space's rock blocks as the cave's terrain layers, so where they meet they blend (#10095).</summary>
    internal void ConfigureCaveLayers(SpatialSession session)
    {
        (BlockId Block, int Layer)[] layers = Dungeons.CaveMaterials.Layers;
        engine.Voxel.ConfigureTerrainLayers(new VoxelTerrainLayerRequest(session,
            layers.Select(l => (uint)BlockRegistry.Get(l.Block).Slot).ToArray(), CaveTransitionCells, layers.Select(l => (uint)l.Layer).ToArray()));
    }

    /// <summary>How many voxels a cave material's blend reaches across.</summary>
    private const uint CaveTransitionCells = 2;

    /// <summary>
    /// A separate space's bindings: a block the space names a material for draws in it (a cave's materials, turf under its
    /// sky included, #10095); other rock in the cave rock; the world's other blocks unblended.
    /// </summary>
    private ReadOnlyMemory<VoxelSceneMaterialBinding> SeparateBindings(IReadOnlyDictionary<BlockId, Material>? rock) =>
        BlockRegistry.BoundBlocks.Select(block => new VoxelSceneMaterialBinding(block.Slot,
            ground is not null && rock is not null && rock.TryGetValue(block.Id, out Material? chosen) ? chosen
                : ground is not null && Dungeons.DungeonSurfaces.IsRock(block.Id) ? CaveRock
                : ground?.For(block.Id, blend: false) ?? Atlas.BaseMaterial(block.Id))).ToArray();

    /// <summary>
    /// Draws the open world's terrain (and its scatters and statics) or not. Away in a separate space it is moved to the
    /// backdrop layer, which nothing draws while the backdrop is unlinked there: it neither costs a frame nor shows
    /// through a dungeon's opening to the sky, kilometres above (#10094).
    /// </summary>
    internal void Drawn(bool drawn)
    {
        if (projection is VoxelScenePresentation current)
        {
            engine.VoxelScenePresentation.SetLayer(new VoxelSceneLayerRequest(current, drawn ? RenderLayer.Scene : RenderLayer.Backdrop));
        }
    }

    /// <summary>Re-meshes the projection after the scene or the world origin changed.</summary>
    internal VoxelScenePresentationReadout Refresh()
    {
        VoxelScenePresentation current = projection ?? throw new InvalidOperationException("Terrain presentation is unavailable.");
        return engine.VoxelScenePresentation.RefreshScene(current);
    }

    internal bool Projected => projection is not null;

    public void Dispose()
    {
        foreach (TerrainGroundMaterials set in caveGrounds.Values)
        {
            set.Dispose();
        }

        caveGrounds.Clear();
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

    /// <param name="skipRock">Leaves rock blocks to their one material (a separate space's cave rock, #10090).</param>
    private ReadOnlyMemory<VoxelSceneFaceMaterialBinding> FaceMaterialBindings(bool skipRock = false) =>
        BlockRegistry.BoundBlocks
            .Where(block => ground?.For(block.Id) is null && Atlas.TopMaterial(block.Id) is not null && !(skipRock && Dungeons.DungeonSurfaces.IsRock(block.Id)))
            .Select(block => new VoxelSceneFaceMaterialBinding(block.Slot, SpatialFace.PosY, Atlas.TopMaterial(block.Id)!))
            .ToArray();
}
