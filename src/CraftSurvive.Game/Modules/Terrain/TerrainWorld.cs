using CraftSurvive.Game.Modules.Content;
using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using EngineVoxelAddress = Rusty.Engine.VoxelAddress;

using System.Diagnostics;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Composes CraftSurvive terrain policy with the generated Engine voxel,
/// persistence, presentation, and UI mechanisms. Engine remains the collision,
/// mesh, renderer, handle, and revision authority.
/// </summary>
internal sealed class TerrainWorld : IDisposable
{
    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly TerrainRecipe recipe;
    private readonly CourtyardScene? courtyard;
    private readonly TerrainChunkGenerator chunkGenerator;
    private readonly TerrainChunkCache chunkCache;
    private readonly Queue<TerrainChunkAddress> pendingCacheWrites = new();
    private readonly TerrainResidencyPolicy residencyPolicy;
    private readonly TerrainOverlayState overlay;
    private readonly Dictionary<TerrainChunkAddress, VoxelChunkReadout> residentChunks = [];
    private TerrainAtlasCatalog? atlasCatalog;
    private SpatialSession? session;
    private PersistenceStore? persistenceStore;
    private UiStream? uiStream;
    private VoxelScenePresentation? presentation;
    private VoxelSceneMaterialMappingResult materialMapping;
    private TerrainPlayerUiFacts? playerUi;
    private DiscoveryUiFacts? discoveryUi;
    private ulong uiSequence;
    private bool started;
    private string overlayRestoreOutcome = "none";
    private static readonly TerrainChunkAddress FixedResidencyCenter = new(0, 0, 0);

    internal TerrainWorld(IEngineContext engine, ProductContent content, TerrainConfiguration configuration)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        recipe = configuration.CreateRecipe(new EngineTerrainDraws(engine.Random));
        chunkCache = new TerrainChunkCache(engine, recipe.Contract);
        chunkGenerator = new TerrainChunkGenerator(recipe, chunkCache);
        residencyPolicy = new TerrainResidencyPolicy(recipe, chunkGenerator);
        overlay = new TerrainOverlayState(configuration.Seed);
        // Authored content is selected during Product Create so the Engine
        // can retain the resource for every later presentation attachment.
        if (configuration.Scene == TerrainSceneMode.ExperimentalCourtyard) courtyard = new CourtyardScene(engine);
        else atlasCatalog = new TerrainAtlasCatalog(engine, content);
    }

    /// <summary>How many chunks the world currently holds resident.</summary>
    internal int ResidentChunkCount => residentChunks.Count;

    /// <summary>The generation recipe, so the live proof can hash real chunks.</summary>
    internal TerrainRecipe Recipe => recipe;

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
                VoxelSurfaceMode.GreedyCubes));
            engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(
                session,
                BlockRegistry.MaterialBlocks.Select(block =>
                    new VoxelMaterialCollision((uint)block.Id, block.Collidable)).ToArray()));
            persistenceStore = engine.Persistence.OpenStore(new PersistenceOpenRequest(TerrainConstants.PersistenceScope));
            uiStream = engine.Ui.OpenStream(new UiStreamRequest(
                TerrainConstants.UiStreamName,
                TerrainConstants.UiStreamContract));
            if (courtyard is null) RestoreOverlay();
            Synchronize(FixedResidencyCenter);
            courtyard?.Start(Session);
            if (courtyard is null) CreatePresentation();
            PublishUi();
            started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal IEnumerable<AppearanceFact> CourtyardFacts => courtyard?.Facts ?? [];
    internal bool IsCourtyard => courtyard is not null;
    internal void ReleaseRetiredCourtyard() => courtyard?.ReleaseRetired();
    internal void UpdateCourtyard() => courtyard?.Update();
    internal void TranslateCourtyard(Vector3 delta) => courtyard?.Translate(delta);
    internal string ReadWorkbenchBuild() => courtyard?.ReadWorkbenchBuild() ?? "courtyard inactive";
    internal CollisionReplaceReceipt WorkbenchCollision =>
        (courtyard ?? throw new InvalidOperationException("Courtyard inactive.")).CollisionReceipt;
    internal string ReadCourtyard() => courtyard?.Readout() ?? "courtyard inactive";
    internal string QueueCourtyardTreatment(string treatment) => courtyard?.QueueTreatment(treatment) ?? "courtyard inactive";
    internal string QueueCourtyardMasonry(string mode) => courtyard?.QueueMasonry(mode) ?? "courtyard inactive";
    internal string QueueCourtyardMaterialBoundaries(string mode) => courtyard?.QueueMaterialBoundaries(mode) ?? "courtyard inactive";
    internal string QueueCourtyardMaterialCutoff(float cutoff) => courtyard?.QueueMaterialCutoff(cutoff) ?? "courtyard inactive";
    internal bool IsGeneratedLevel => courtyard?.IsGeneratedLevel == true;
    internal CraftSurvive.Procgen.Workbench.WorkbenchCandidate? ActiveWorkbench => courtyard?.ActiveWorkbench;
    internal void ApplyWorkbench(CraftSurvive.Procgen.Workbench.WorkbenchCandidate candidate, bool switchOpen, string treatment = CraftSurvive.Procgen.Workbench.WorkbenchRealization.Intact) =>
        (courtyard ?? throw new InvalidOperationException("Workbench requires the courtyard scene.")).ApplyWorkbench(candidate, switchOpen, treatment);
    internal string ReadCourtyardLevelPlan() => courtyard?.ReadLevelPlan() ?? "courtyard inactive";
    internal string QueueCourtyardSeed(ulong seed) => courtyard?.QueueSeed(seed) ?? "courtyard inactive";
    internal string QueueCourtyardStudy(string study) => courtyard?.QueueStudy(study) ?? "courtyard inactive";
    internal string QueueCourtyardMaterialSamples(float spacing) => courtyard?.QueueMaterialSamples(spacing) ?? "courtyard inactive";
    internal string ReadCourtyardDetailParts() => courtyard?.ReadDetailParts() ?? "courtyard inactive";
    internal string QueueCourtyardDetail(string detail) => courtyard?.QueueDetail(detail) ?? "courtyard inactive";
    internal (Vector3 Eye, Vector3 Target) CourtyardInspectionView(string angle) =>
        (courtyard ?? throw new InvalidOperationException("Courtyard inactive.")).InspectionView(angle);
    internal string QueueCourtyardLayout(float width, float doorWidth, float doorOffset, ulong seed) => courtyard?.QueueLayout(width, doorWidth, doorOffset, seed) ?? "courtyard inactive";

    /// <summary>Advances the bounded voxel residency plan around a product global voxel fact.</summary>
    internal void SynchronizeAround(VoxelAddress centerVoxel)
    {
        EnsureStarted();
        if (Synchronize(centerVoxel.Chunk))
        {
            RefreshPresentation();
            PublishUi();
        }
    }

    /// <summary>Publishes concise Player facts through the terrain-owned product UI stream.</summary>
    /// <summary>
    /// Publishes what the journal knows. Discovery is a different owner from the world, so its
    /// facts arrive the way the player's do - pushed by the module that owns them - and ride
    /// the product's one terrain UI stream rather than opening a second one.
    /// </summary>
    internal void PublishDiscoveryUi(DiscoveryUiFacts facts)
    {
        discoveryUi = facts;
        PublishUi();
    }

    internal void PublishPlayerUi(TerrainPlayerUiFacts facts)
    {
        EnsureStarted();
        playerUi = facts;
        PublishUi();
    }

    /// <summary>Refreshes Engine-owned terrain facts after an accepted world-origin commit.</summary>
    internal void RefreshAfterWorldOriginCommit(TerrainPlayerUiFacts facts)
    {
        EnsureStarted();
        playerUi = facts;
        if (presentation is not null) RefreshPresentation();
        PublishUi();
    }

    /// <summary>
    /// Applies an already-decided request through the one revision-checked path: admission, the
    /// scene read the transaction is evaluated against, the Engine transaction, and the receipt
    /// handling that follows. Both the view-aimed brush and a caller-decided volume come through
    /// here, so receipt semantics live in exactly one place.
    /// </summary>
    private TerrainWorldEditResult ApplyRequest(TerrainEditRequest request, VoxelAddress target,
        VoxelAddress center, SpatialFace face, Func<VoxelAddress, bool>? playerOverlaps)
    {
        long tAdmitStart = Stopwatch.GetTimestamp();
        TerrainEditAdmissionResult admission = TerrainEditAdmission.Admit(request, playerOverlaps);
        if (admission is TerrainEditRejected rejected)
        {
            return new TerrainWorldEditRejected(center, rejected);
        }

        TerrainEditAccepted accepted = (TerrainEditAccepted)admission;
        long tSceneStart = Stopwatch.GetTimestamp();
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
        long tReadStart = Stopwatch.GetTimestamp();
        VoxelReadout targetBefore = engine.Voxel.Read(new VoxelReadRequest(Session, ToEngineVoxel(center)));
        long tProjectionStart = Stopwatch.GetTimestamp();
        SpatialProjectionReadout spatialBefore = engine.Spatial.ReadProjection(
            new SpatialProjectionReadRequest(Session));
        long tApplyStart = Stopwatch.GetTimestamp();
        VoxelEdit[] edits = accepted.Edits.Select(ToEngineEdit).ToArray();
        VoxelEditReceipt receipt = engine.Voxel.ApplyEdits(new VoxelEditTransaction(
            Session,
            edits));
        long tApplyDone = Stopwatch.GetTimestamp();
        // The front half, timed for the same reason the receipt path was: the cost of an accepted
        // multi-cell edit is a step near 240 ms that no bookkeeping item explains, so it is either
        // here - admission, the three before-reads, or the Engine transaction - or it is nowhere.
        lastFrontTiming = string.Create(CultureInfo.InvariantCulture,
            $"admit={Ms(tAdmitStart, tSceneStart):F1} scene={Ms(tSceneStart, tReadStart):F1} "
            + $"read={Ms(tReadStart, tProjectionStart):F1} projection={Ms(tProjectionStart, tApplyStart):F1} "
            + $"apply={Ms(tApplyStart, tApplyDone):F1}");
        switch (receipt.Status)
        {
            case VoxelEditStatus.NoChanges:
                return new TerrainWorldEditNoChanges(
                    request.Kind,
                    target,
                    face,
                    center,
                    targetBefore,
                    spatialBefore,
                    scene,
                    receipt);

            case VoxelEditStatus.Accepted:
            {
                // Timed in pieces because the cost of an accepted multi-cell edit turned out to be a
                // step rather than a slope - about 240 ms whether it removes seven cells or a hundred
                // and twenty-three, while a single-cell edit costs a few milliseconds. Something in
                // this list is the step, and only timings inside it can say which. These figures are
                // read by `craft.blast.readout`; they are diagnostics, not game state.
                long tOverlay = Stopwatch.GetTimestamp();
                TerrainOverlayReceipt overlayReceipt = overlay.Apply(accepted);
                long tResidency = Stopwatch.GetTimestamp();
                residencyPolicy.RefreshAfterOverlayChange(overlay, overlayReceipt);
                long tChunks = Stopwatch.GetTimestamp();
                RefreshResidentChunks(overlayReceipt.AppliedEdits.Select(edit => edit.Address.Chunk));
                long tSave = Stopwatch.GetTimestamp();
                SaveOverlay();
                long tPresentation = Stopwatch.GetTimestamp();
                VoxelScenePresentationReadout refreshedPresentation = RefreshPresentation();
                long tUi = Stopwatch.GetTimestamp();
                PublishUi();
                long tDone = Stopwatch.GetTimestamp();
                lastEditTiming = string.Create(CultureInfo.InvariantCulture,
                    $"cells={accepted.Edits.Count} {lastFrontTiming} overlay={Ms(tOverlay, tResidency):F1} residency={Ms(tResidency, tChunks):F1} "
                    + $"chunks={Ms(tChunks, tSave):F1} save={Ms(tSave, tPresentation):F1} "
                    + $"present={Ms(tPresentation, tUi):F1} ui={Ms(tUi, tDone):F1} total={Ms(tOverlay, tDone):F1}");
                return new TerrainWorldEditApplied(center, receipt, refreshedPresentation);
            }

            default:
                throw new InvalidOperationException($"Engine returned unsupported voxel edit status '{receipt.Status}'.");
        }
    }

    /// <summary>
    /// Applies an edit whose cells the caller has already decided on - the volume of a charge, the
    /// shape of a stamp, or the exact undo of something just built. A brush cannot express those:
    /// it is aimed, and re-aiming at a volume you have just filled picks a different centre, which
    /// is how an "undo" leaves a rim.
    /// </summary>
    /// <summary>The material standing at a cell now: the player's override, else the recipe's.</summary>
    internal ushort MaterialAt(VoxelAddress address) =>
        overlay.TryGetMaterial(address, out ushort material) ? material : recipe.MaterialAt(address);

    internal TerrainWorldEditResult TryEditCells(IReadOnlyList<VoxelAddress> cells, TerrainEditKind kind,
        ushort material, Func<VoxelAddress, bool>? playerOverlaps = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
        {
            return TerrainWorldEditResult.CastMiss;
        }

        TerrainEditRequest request = TerrainEditRequest.FromCells(cells, kind, material);
        return ApplyRequest(request, request.Center, request.Center, SpatialFace.PosY, playerOverlaps);
    }

    internal TerrainWorldEditResult TryEditFromView(Vector3 origin, Vector3 direction,
        TerrainEditKind kind, ushort material, int radius, Func<VoxelAddress, bool>? playerOverlaps)
    {
        EnsureStarted();
        SpatialHit cast = engine.Spatial.CastRay(new SpatialRaycastRequest(
            Session,
            origin,
            direction,
            TerrainConstants.EditReach,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty,
            ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<SpatialEntityCollider>.Empty));
        if (!cast.Present || cast.Kind != SpatialHitKind.Voxel)
        {
            return TerrainWorldEditResult.CastMiss;
        }

        SpatialHit picked = engine.Spatial.PickVoxel(new SpatialPickRequest(
            Session,
            origin,
            direction,
            TerrainConstants.EditReach,
            cast.VoxelX,
            cast.VoxelY,
            cast.VoxelZ,
            cast.Face));
        if (!picked.Present || picked.Kind != SpatialHitKind.Voxel)
        {
            return TerrainWorldEditResult.PickMiss;
        }

        VoxelAddress target = new(picked.VoxelX, picked.VoxelY, picked.VoxelZ);
        VoxelAddress center = kind == TerrainEditKind.Set ? Adjacent(target, picked.Face) : target;
        TerrainEditRequest request = kind == TerrainEditKind.Set
            ? TerrainEditRequest.Set(center, material, radius)
            : TerrainEditRequest.Clear(center, radius);
        return ApplyRequest(request, target, center, picked.Face, playerOverlaps);
    }

    /// <summary>
    /// The last accepted edit's cost, broken down by the work the receipt path does. Diagnostics
    /// for the manipulation budget, not product state: nothing reads it for game meaning.
    /// </summary>
    internal string LastEditTiming => lastEditTiming;

    private string lastEditTiming = "none";

    private string lastFrontTiming = "none";

    private static double Ms(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;

    internal void Restart()
    {
        EnsureStarted();
        if (Synchronize(FixedResidencyCenter))
        {
            RefreshPresentation();
            PublishUi();
        }
    }

    public void Dispose()
    {
        residentChunks.Clear();
        presentation?.Dispose();
        presentation = null;
        courtyard?.Dispose();
        atlasCatalog?.Dispose();
        atlasCatalog = null;
        materialMapping = default;
        uiStream?.Dispose();
        uiStream = null;
        persistenceStore?.Dispose();
        persistenceStore = null;
        session?.Dispose();
        session = null;
        started = false;
    }

    internal SpatialSession Session => session ?? throw new InvalidOperationException("Terrain spatial session is unavailable.");

    /// <summary>Reads the live Engine-owned voxel scene for product diagnostics.</summary>
    internal VoxelSceneReadout ReadScene()
    {
        EnsureStarted();
        return engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
    }

    /// <summary>Returns the copied Engine-owned directional material mapping retained by this terrain owner.</summary>
    internal VoxelSceneMaterialMappingResult ReadMaterialMapping()
    {
        EnsureStarted();
        return materialMapping;
    }

    /// <summary>Reads the selected product layout without querying or replacing Engine state.</summary>
    internal string ReadLayout() => courtyard?.Readout() ?? "mode=traversal-showcase";

    /// <summary>Formats the latest player-consumed target/edit result for the narrow live debug surface.</summary>
    internal static string FormatEditReadout(TerrainWorldEditResult? result) => result switch
    {
        null => "outcome=none",
        TerrainWorldEditCastMiss => "outcome=cast-miss",
        TerrainWorldEditPickMiss => "outcome=pick-miss",
        TerrainWorldEditRejected rejected => string.Create(CultureInfo.InvariantCulture,
            $"outcome=rejected;target={FormatVoxel(rejected.Target)};reason={rejected.Rejection.Reason};rejected={FormatVoxel(rejected.Rejection.Address)}"),
        TerrainWorldEditNoChanges noChanges => string.Create(CultureInfo.InvariantCulture,
            $"outcome=no-changes;kind={noChanges.Kind};picked={FormatVoxel(noChanges.Picked)};face={noChanges.Face};target={FormatVoxel(noChanges.Target)};beforePresent={noChanges.TargetBefore.Present};beforeMaterial={noChanges.TargetBefore.MaterialSlot};sceneRevision={noChanges.SceneBefore.SourceRevision};spatialRevision={noChanges.SpatialBefore.SourceRevision};authorityMatch={noChanges.SceneBefore.AuthorityHash == noChanges.SpatialBefore.AuthorityHash};currentRevision={noChanges.Receipt.AcceptedRevision}"),
        TerrainWorldEditApplied applied => string.Create(CultureInfo.InvariantCulture,
            $"outcome=accepted;target={FormatVoxel(applied.Target)};changed={applied.Receipt.ChangedVoxels};sceneRevision={applied.Receipt.AcceptedRevision};meshRevision={applied.Receipt.MeshRevision};presentationSourceRevision={applied.Presentation.SourceRevision};presentationMeshRevision={applied.Presentation.MeshRevision}"),
        _ => throw new InvalidOperationException($"Unsupported terrain edit result '{result.GetType().Name}'."),
    };

    private PersistenceStore PersistenceStore => persistenceStore ?? throw new InvalidOperationException("Terrain persistence store is unavailable.");

    /// <summary>
    /// Builds the admission the residency policy would apply for one chunk,
    /// without applying it. This is the staged background-preparation proof's
    /// entry point: the product composes and owns the payload, and the Engine
    /// builds the projection off the admitted update path. Returns null in
    /// courtyard mode, where residency is authored rather than generated.
    /// </summary>
    internal VoxelResidencyTransaction? PlanChunkAdmission(SpatialSession session, TerrainChunkAddress address)
    {
        if (courtyard is not null)
        {
            return null;
        }

        TerrainResidencyPlan plan = residencyPolicy.PlanFor(address, overlay);
        List<VoxelResidencyOperation> operations = [];
        List<uint> materialSlots = [];
        AddChunkAdmission(plan.Chunk(address), operations, materialSlots);
        return new VoxelResidencyTransaction(
            session,
            operations.ToArray(),
            materialSlots.ToArray());
    }
    private UiStream UiStream => uiStream ?? throw new InvalidOperationException("Terrain UI stream is unavailable.");

    private bool Synchronize(TerrainChunkAddress center)
    {
        DrainCacheWrites();

        if (courtyard is not null) return false;
        TerrainResidencyPlan plan = residencyPolicy.PlanFor(center, overlay);

        List<VoxelResidencyOperation> operations = [];
        List<uint> materialSlots = [];
        foreach (TerrainChunkAddress address in plan.Requested)
        {
            if (residentChunks.ContainsKey(address))
            {
                continue;
            }

            AddChunkAdmission(plan.Chunk(address), operations, materialSlots);
            if (operations.Count == plan.MaximumOperationsPerTick)
            {
                break;
            }
        }

        foreach (TerrainChunkAddress address in residentChunks.Keys)
        {
            if (plan.Retained.Contains(address) || operations.Count == plan.MaximumOperationsPerTick)
            {
                continue;
            }

            // A chunk leaving the resident set is exactly the chunk worth keeping: it is
            // about to cost generation again if the player turns around. Edited chunks are
            // skipped because the read path refuses them anyway.
            if (!overlay.Snapshot().TouchesChunk(address) && !pendingCacheWrites.Contains(address))
            {
                pendingCacheWrites.Enqueue(address);
            }

            operations.Add(new VoxelResidencyOperation(
                VoxelResidencyOperationKind.Evict,
                ToEngineChunk(address),
                0,
                0));
        }

        if (operations.Count > 0)
        {
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(
                Session,
                operations.ToArray(),
                materialSlots.ToArray()));
            RefreshResidentChunks(operations.Select(operation => FromEngineChunk(operation.Chunk)));
        }

        return operations.Count > 0;
    }

    /// <summary>
    /// Writes at most one evicted chunk to the cache per update. Measured at roughly
    /// 1.5 ms to regenerate plus 3 ms to store, this is deliberately off the path of a
    /// chunk becoming visible: a frame may be a little busier, but nothing a player is
    /// waiting to see is ever delayed by a cache write.
    /// </summary>
    private void DrainCacheWrites()
    {
        if (pendingCacheWrites.Count == 0)
        {
            return;
        }

        TerrainChunkAddress address = pendingCacheWrites.Dequeue();
        if (residentChunks.ContainsKey(address))
        {
            // It came back before we got to it, so there is nothing to preserve.
            return;
        }

        TerrainChunk chunk = chunkGenerator.Generate(address, overlay.Snapshot());
        chunkCache.Write(address, chunk.Materials.Span);
    }

    private void RefreshResidentChunks(IEnumerable<TerrainChunkAddress> addresses)
    {
        foreach (TerrainChunkAddress address in addresses.Distinct())
        {
            VoxelChunkReadout readout = engine.Voxel.ReadChunk(new VoxelChunkReadRequest(Session, ToEngineChunk(address)));
            if (readout.Present)
            {
                residentChunks[address] = readout;
            }
            else
            {
                residentChunks.Remove(address);
            }
        }
    }

    private static void AddChunkAdmission(TerrainChunk chunk,
        List<VoxelResidencyOperation> operations, List<uint> materialSlots)
    {
        uint offset = checked((uint)materialSlots.Count);
        foreach (ushort material in chunk.Materials.Span) materialSlots.Add(material);
        operations.Add(new VoxelResidencyOperation(
            VoxelResidencyOperationKind.Admit,
            ToEngineChunk(chunk.Address),
            offset,
            checked((uint)chunk.Materials.Length)));
    }

    private void RestoreOverlay()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(
            PersistenceStore,
            TerrainConstants.OverlayPersistenceKey));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        if (!info.Present)
        {
            return;
        }

        byte[] bytes = engine.Persistence.ReadBlobBytes(blob).ToArray();
        try
        {
            overlay.Restore(TerrainOverlayCodec.Decode(recipe.Configuration.Seed, bytes));
            overlayRestoreOutcome = "restored";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Worlds are disposable under the settled save policy: a save that does
            // not match this world's identity is discarded and the world regenerates,
            // rather than the mismatch reaching the load call and failing the
            // product. The previous generation is kept as one backup.
            PreserveOverlayBackup(bytes);
            overlayRestoreOutcome = $"discarded: {exception.Message}";
        }
    }

    /// <summary>
    /// Whether the world's overlay is saved, and how many bytes it holds. It exists
    /// so the live lane can show that a product edit reached the store, which is the
    /// half of the save path an Engine-level edit never touches.
    /// </summary>
    internal (bool Present, int Bytes) OverlaySaved()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(
            PersistenceStore,
            TerrainConstants.OverlayPersistenceKey));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        return info.Present ? (true, engine.Persistence.ReadBlobBytes(blob).Length) : (false, 0);
    }

    /// <summary>What happened to the saved overlay at startup, for evidence.</summary>
    internal string OverlayRestoreOutcome => overlayRestoreOutcome;

    private void PreserveOverlayBackup(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        engine.Persistence.Save(new PersistenceSaveRequest(
            PersistenceStore,
            TerrainConstants.OverlayBackupPersistenceKey,
            PersistenceRevisionGuard.Any,
            0,
            bytes));
    }

    private void SaveOverlay()
    {
        byte[] bytes = TerrainOverlayCodec.Encode(overlay.Snapshot());
        engine.Persistence.Save(new PersistenceSaveRequest(
            PersistenceStore,
            TerrainConstants.OverlayPersistenceKey,
            PersistenceRevisionGuard.Any,
            0,
            bytes));
    }

    private void CreatePresentation()
    {
        presentation = engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(
            Session,
            MaterialBindings(),
            FaceMaterialBindings()));
        CaptureMaterialMapping();
    }

    private VoxelScenePresentationReadout RefreshPresentation()
    {
        VoxelScenePresentation currentPresentation = presentation
            ?? throw new InvalidOperationException("Terrain presentation is unavailable.");
        VoxelScenePresentationReadout readout = engine.VoxelScenePresentation.RefreshScene(currentPresentation);
        CaptureMaterialMapping();
        return readout;
    }

    /// <summary>
    /// One binding per registered block. The registry is the block floor and the
    /// atlas catalog is its material closure, so a block exists in the world only
    /// once both agree - which the catalog validates when it admits the payload.
    /// </summary>
    private ReadOnlyMemory<VoxelSceneMaterialBinding> MaterialBindings()
    {
        List<VoxelSceneMaterialBinding> bindings = [];
        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            bindings.Add(new VoxelSceneMaterialBinding(block.Slot, AtlasCatalog.BaseMaterial(block.Id)));
        }

        return bindings.ToArray();
    }

    private ReadOnlyMemory<VoxelSceneFaceMaterialBinding> FaceMaterialBindings()
    {
        List<VoxelSceneFaceMaterialBinding> bindings = [];
        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            if (AtlasCatalog.TopMaterial(block.Id) is Material top)
            {
                bindings.Add(new VoxelSceneFaceMaterialBinding(block.Slot, SpatialFace.PosY, top));
            }
        }

        return bindings.ToArray();
    }

    private TerrainAtlasCatalog AtlasCatalog => atlasCatalog ?? throw new InvalidOperationException("Terrain atlas catalog is unavailable.");

    /// <summary>
    /// The atlas image every material in this world is built from, for anything that needs to draw
    /// from the same content - the blast's dust and debris borrow it rather than opening their own.
    /// </summary>
    internal RenderResourceReference AtlasSprite => AtlasCatalog.AtlasReference;

    private void CaptureMaterialMapping()
    {
        if (presentation is not null)
        {
            materialMapping = engine.VoxelScenePresentation.ReadMaterialMapping(presentation);
        }
    }

    private void PublishUi()
    {
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
        engine.Ui.PublishProjection(new UiProjection(UiStream, ++uiSequence,
            TerrainUiProjection.Create(scene, overlay.Count, playerUi, discoveryUi)));
    }

    private static VoxelEdit ToEngineEdit(TerrainVoxelEdit edit) => edit.Material == TerrainConstants.EmptyMaterial
        ? new VoxelEdit(VoxelEditKind.Clear, ToEngineVoxel(edit.Address), 0)
        : new VoxelEdit(VoxelEditKind.Set, ToEngineVoxel(edit.Address), edit.Material);

    private static VoxelAddress Adjacent(VoxelAddress target, SpatialFace face) => face switch
    {
        SpatialFace.PosX => target with { X = target.X + 1 },
        SpatialFace.NegX => target with { X = target.X - 1 },
        SpatialFace.PosY => target with { Y = target.Y + 1 },
        SpatialFace.NegY => target with { Y = target.Y - 1 },
        SpatialFace.PosZ => target with { Z = target.Z + 1 },
        SpatialFace.NegZ => target with { Z = target.Z - 1 },
        _ => throw new InvalidOperationException("Engine voxel pick did not include a placement face."),
    };

    private static VoxelChunkIdentity ToEngineChunk(TerrainChunkAddress address) => new(address.X, address.Y, address.Z);

    private static TerrainChunkAddress FromEngineChunk(VoxelChunkIdentity address) => new(address.X, address.Y, address.Z);

    private static EngineVoxelAddress ToEngineVoxel(VoxelAddress address) => new(address.X, address.Y, address.Z);

    private static string FormatVoxel(VoxelAddress address) => string.Create(CultureInfo.InvariantCulture,
        $"{address.X},{address.Y},{address.Z}");

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("Terrain world has not started.");
        }
    }
}

internal abstract record TerrainWorldEditResult
{
    internal static TerrainWorldEditResult CastMiss { get; } = new TerrainWorldEditCastMiss();

    internal static TerrainWorldEditResult PickMiss { get; } = new TerrainWorldEditPickMiss();
}

internal sealed record TerrainWorldEditCastMiss : TerrainWorldEditResult;

internal sealed record TerrainWorldEditPickMiss : TerrainWorldEditResult;

internal sealed record TerrainWorldEditRejected(VoxelAddress Target, TerrainEditRejected Rejection) : TerrainWorldEditResult;

internal sealed record TerrainWorldEditNoChanges(
    TerrainEditKind Kind,
    VoxelAddress Picked,
    SpatialFace Face,
    VoxelAddress Target,
    VoxelReadout TargetBefore,
    SpatialProjectionReadout SpatialBefore,
    VoxelSceneReadout SceneBefore,
    VoxelEditReceipt Receipt) : TerrainWorldEditResult;

internal sealed record TerrainWorldEditApplied(
    VoxelAddress Target,
    VoxelEditReceipt Receipt,
    VoxelScenePresentationReadout Presentation) : TerrainWorldEditResult;

/// <summary>Small Player-to-UI fact projection carried by Terrain's existing product stream.</summary>
internal readonly record struct TerrainPlayerUiFacts(
    double EyeX,
    double EyeY,
    double EyeZ,
    double YawDegrees,
    double PitchDegrees,
    bool Grounded,
    bool Crouched,
    double PlatformX,
    double PlatformY,
    double PlatformZ);

/// <summary>
/// What the journal knows, as numbers, for the product's UI projection. It is a flat
/// snapshot with no identity strings: the projection's encoder is numeric by design, and a
/// place's name belongs to the journal's own readout.
/// </summary>
internal readonly record struct DiscoveryUiFacts(
    double Places,
    double Visited,
    double Seen,
    double Refused,
    double NearestMetres,
    double LastX,
    double LastZ,
    double LastKind,
    double LastStage,
    double LastTick);
