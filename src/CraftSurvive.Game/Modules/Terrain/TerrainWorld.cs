using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The voxel world: the generator and its fingerprint, the spatial session the Engine keeps the
/// scene in, and the parts that keep that scene true - residency streaming, the player's edits and
/// their save, the edit path, and the presentation. Engine remains the collision, mesh, renderer,
/// handle and revision authority.
/// </summary>
internal sealed class TerrainWorld : IDisposable
{
    /// <summary>How far above the generated surface a structure or a placed block may stand.</summary>
    private const int GroundSearchAbove = 16;

    /// <summary>How far below the generated surface an edit may have dug.</summary>
    private const int GroundSearchBelow = 16;

    private static readonly TerrainChunkAddress FixedResidencyCenter = new(0, 0, 0);

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly ProductUiPublisher ui;
    private readonly WorldFrame frame;
    private readonly TerrainRecipe recipe;
    private readonly TerrainChunkCache chunkCache;
    private readonly TerrainOverlayStore overlayStore;
    private readonly TerrainResidencyStreamer streamer;
    private readonly TerrainPresentation presentation;
    private readonly TerrainEditService edits;
    private SpatialSession? session;
    private long currentStep;
    private bool started;

    internal TerrainWorld(IEngineContext engine, ProductContent content, TerrainConfiguration configuration,
        WorldFrame frame, ProductStore store, ProductUiPublisher ui)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(content);
        this.content = content;
        ArgumentNullException.ThrowIfNull(frame);
        this.frame = frame;
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        frame.Rebased += OnRebased;
        recipe = configuration.CreateRecipe(new EngineTerrainDraws(engine.Random));

        // The fingerprint comes from a fresh recipe, so nothing the live one memoises can hide a
        // change in the generator or in the Engine's keyed draws underneath it.
        GenerationFingerprint = TerrainGenerationFingerprint.Compute(
            configuration.CreateRecipe(new EngineTerrainDraws(engine.Random)),
            TerrainGenerationFingerprint.Startup);
        CacheIdentity = TerrainGenerationFingerprint.CacheIdentity(GenerationFingerprint, TerrainGeneratorSource.Stamp);
        chunkCache = new TerrainChunkCache(engine, recipe.Contract, CacheIdentity);
        TerrainChunkGenerator generator = new(recipe, chunkCache);
        TerrainResidencyPolicy policy = new(recipe, generator);
        SaveIdentity = new SaveIdentity(recipe.Contract.Version, recipe.Contract.Seed);
        overlayStore = new TerrainOverlayStore(engine, store, SaveIdentity);
        streamer = new TerrainResidencyStreamer(engine, policy, generator, chunkCache, overlayStore.Overlay, configuration.Seed);
        presentation = new TerrainPresentation(engine, content);
        edits = new TerrainEditService(engine, overlayStore, policy, streamer, presentation, ui.Publish);
    }

    /// <summary>The generation recipe, for the modules that reason about the generated world.</summary>
    internal TerrainRecipe Recipe => recipe;

    internal LandscapeSampleSpace CreateLandscapeStudy(LandscapeStudy study) =>
        new(engine, content, study);

    /// <summary>The world every save belongs to; a save written for another is discarded.</summary>
    internal SaveIdentity SaveIdentity { get; }

    /// <summary>What this run's generator produces over the startup probe, with the Engine's draws.</summary>
    internal ulong GenerationFingerprint { get; }

    /// <summary>What cached chunks are keyed on: the output fingerprint and the generator's source stamp.</summary>
    internal ulong CacheIdentity { get; }

    internal SpatialSession Session => session ?? throw new InvalidOperationException("Terrain spatial session is unavailable.");

    /// <summary>Draws a separate space's session with this world's block materials; the caller disposes the projection.</summary>
    internal VoxelScenePresentation ProjectSeparateSpace(SpatialSession separate) => presentation.ProjectAnother(separate);

    /// <summary>The atlas image every block material is built from.</summary>
    internal RenderResourceReference AtlasSprite => presentation.AtlasSprite;

    /// <summary>The last accepted edit's cost by part; diagnostics, not game state.</summary>
    internal string LastEditTiming => edits.LastTiming;

    /// <summary>Switches recording of where each edit's time goes; diagnostics only, off by default.</summary>
    internal bool EditTimingEnabled
    {
        get => edits.TimingEnabled;
        set => edits.TimingEnabled = value;
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        try
        {
            session = engine.Spatial.CreateSession(new SpatialSessionConfig(
                TerrainConstants.VoxelSize,
                TerrainConstants.VoxelChunkSize,
                VoxelSurfaceMode.DualContouring));
            TerrainSurfaces.Apply(engine, session);
            VoxelMaterialRules.Apply(engine, session);
            ui.Open(ProductUiPublisher.StreamName, ProductUiPublisher.StreamContract, WorldFacts);
            overlayStore.Restore();
            streamer.Synchronize(session, FixedResidencyCenter);
            presentation.Project(session);
            ui.Publish();
            started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Saves the player's edits once they pause. Called once per product update.</summary>
    internal void Update(ProductStep step)
    {
        currentStep = step.Step;
        overlayStore.SaveIfDue(step.Step);
    }

    internal void Restart()
    {
        EnsureStarted();
        if (streamer.Synchronize(Session, FixedResidencyCenter))
        {
            presentation.Refresh();
            ui.Publish();
        }
    }

    public void Dispose()
    {
        if (started)
        {
            overlayStore.Flush();
        }

        streamer.Clear();
        presentation.Dispose();
        chunkCache.Dispose();
        session?.Dispose();
        session = null;
        started = false;
    }

    /// <summary>Advances the bounded voxel residency plan around a product global voxel fact.</summary>
    internal void SynchronizeAround(VoxelAddress centerVoxel)
    {
        EnsureStarted();
        if (streamer.Synchronize(Session, centerVoxel.Chunk))
        {
            presentation.Refresh();
            ui.Publish();
        }
    }

    /// <summary>
    /// The Engine's standing surface in resident terrain, including its reconstructed shape.
    /// Outside residency, the recipe and edit overlay give a coarse placement estimate; collision
    /// must be available before a body actually moves there.
    /// </summary>
    internal float GroundAt(double x, double z)
    {
        long cellX = (long)Math.Floor(x), cellZ = (long)Math.Floor(z);
        long surface = recipe.SurfaceAt(cellX, cellZ);
        if (session is not null && IsResident(new VoxelAddress(cellX, surface, cellZ)))
        {
            double top = surface + GroundSearchAbove + 1;
            SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
                session, frame.ToLocal(x, top, z), -Vector3.UnitY, GroundSearchAbove + GroundSearchBelow + 1,
                new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
                ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty,
                ReadOnlyMemory<SpatialEntityCollider>.Empty));
            if (hit.Present)
            {
                return (float)(top - hit.Distance);
            }
        }

        for (long y = surface + GroundSearchAbove; y >= surface - GroundSearchBelow; y--)
        {
            if (BlockRegistry.TryGetBySlot(MaterialAt(new VoxelAddress(cellX, y, cellZ)), out BlockDefinition block) && block.Collidable)
            {
                return y + 1;
            }
        }

        return surface + 1;
    }

    /// <summary>Whether the chunk holding a cell is resident: drawn, and solid to collision and sight.</summary>
    internal bool IsResident(VoxelAddress cell) => streamer.IsResident(cell.Chunk);

    /// <summary>Moves whenever the player's edits change, so anything derived from collision can tell it is stale.</summary>
    internal ulong EditRevision => overlayStore.Overlay.Revision;

    /// <summary>The material standing at a cell now: the player's override, else the recipe's.</summary>
    internal ushort MaterialAt(VoxelAddress address) =>
        overlayStore.Overlay.TryGetMaterial(address, out ushort material) ? material : recipe.MaterialAt(address);

    /// <summary>
    /// Applies an edit whose cells the caller has already decided on - the volume of a charge, the
    /// shape of a stamp, or the exact undo of something just built.
    /// </summary>
    internal TerrainWorldEditResult TryEditCells(IReadOnlyList<VoxelAddress> cells, TerrainEditKind kind,
        ushort material, Func<VoxelAddress, bool>? playerOverlaps = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
        {
            return TerrainWorldEditResult.CastMiss;
        }

        TerrainEditRequest request = TerrainEditRequest.FromCells(cells, kind, material);
        return edits.Apply(Session, request, request.Center, request.Center, SpatialFace.PosY, playerOverlaps, currentStep);
    }

    /// <summary>Applies an aimed brush edit along the player's view.</summary>
    internal TerrainWorldEditResult TryEditFromView(Vector3 origin, Vector3 direction,
        TerrainEditKind kind, ushort material, int radius, Func<VoxelAddress, bool>? playerOverlaps)
    {
        EnsureStarted();
        return edits.ApplyFromView(Session, origin, direction, kind, material, radius, playerOverlaps, currentStep);
    }

    /// <summary>What the view from this origin is aimed at, within edit reach.</summary>
    internal TerrainPick PickFromView(Vector3 origin, Vector3 direction)
    {
        EnsureStarted();
        return edits.Pick(Session, origin, direction);
    }

    /// <summary>
    /// The generator's identity as a readout: its version, its live fingerprint and whether that
    /// fingerprint is the one recorded for the version, plus residency and the chunk cache.
    /// </summary>
    internal string GenerationReadout()
    {
        uint version = recipe.Contract.Version;
        string golden = TerrainGenerationGoldens.Live.TryGetValue(version, out ulong expected)
            ? expected == GenerationFingerprint ? "match" : string.Create(CultureInfo.InvariantCulture, $"mismatch expected={expected:x16}")
            : "unrecorded";
        return string.Create(CultureInfo.InvariantCulture,
            $"version={version} fingerprint={GenerationFingerprint:x16} golden={golden} source={TerrainGeneratorSource.Stamp:x16} cacheIdentity={CacheIdentity:x16} {streamer.Readout()}");
    }

    /// <summary>Reads the live Engine-owned voxel scene for product diagnostics.</summary>
    internal VoxelSceneReadout ReadScene()
    {
        EnsureStarted();
        return engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
    }

    /// <summary>Moves what the world holds in local space after the player commits a rebase.</summary>
    private void OnRebased(Vector3 translation)
    {
        if (presentation.Projected)
        {
            presentation.Refresh();
        }
    }

    private WorldUiFacts WorldFacts() => new(
        engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session)),
        overlayStore.Overlay.Count);

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("Terrain world has not started.");
        }
    }
}
