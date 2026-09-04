using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using EngineVoxelAddress = Rusty.Engine.VoxelAddress;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Composes CraftSurvive terrain policy with the generated Engine voxel,
/// persistence, presentation, and UI mechanisms. Engine remains the collision,
/// mesh, renderer, handle, and revision authority.
/// </summary>
internal sealed class TerrainWorld : IDisposable
{
    private readonly IEngineContext engine;
    private readonly TerrainRecipe recipe;
    private readonly TerrainChunkGenerator chunkGenerator;
    private readonly TerrainResidencyPolicy residencyPolicy;
    private readonly TerrainOverlayState overlay;
    private readonly Dictionary<TerrainChunkAddress, VoxelChunkLease> leases = [];
    private readonly Dictionary<TerrainChunkAddress, VoxelChunkReadout> residentChunks = [];
    private TerrainAtlasCatalog? atlasCatalog;
    private SpatialSession? session;
    private PersistenceStore? persistenceStore;
    private UiStream? uiStream;
    private VoxelScenePresentation? presentation;
    private VoxelSceneMaterialMappingLeaseReceipt materialMapping;
    private TerrainPlayerUiFacts? playerUi;
    private ulong uiSequence;
    private bool started;
    private static readonly TerrainChunkAddress FixedResidencyCenter = new(0, 0, 0);

    internal TerrainWorld(IEngineContext engine, TerrainConfiguration configuration)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        recipe = configuration.CreateRecipe();
        chunkGenerator = new TerrainChunkGenerator(recipe);
        residencyPolicy = new TerrainResidencyPolicy(recipe, chunkGenerator);
        overlay = new TerrainOverlayState(configuration.Seed);
        // Authored content is selected during Product Create so the Engine
        // can retain the resource for every later presentation attachment.
        atlasCatalog = new TerrainAtlasCatalog(engine);
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
                VoxelSurfaceMode.GreedyCubes));
            persistenceStore = engine.Persistence.OpenStore(new PersistenceOpenRequest(TerrainConstants.PersistenceScope));
            uiStream = engine.Ui.OpenStream(new UiStreamRequest(
                TerrainConstants.UiStreamName,
                TerrainConstants.UiStreamContract));
            RestoreOverlay();
            Synchronize(FixedResidencyCenter);
            CreatePresentation();
            PublishUi();
            started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Update()
    {
        EnsureStarted();
        if (Synchronize(FixedResidencyCenter))
        {
            RefreshPresentation();
            PublishUi();
        }
    }

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
        RefreshPresentation();
        PublishUi();
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
        TerrainEditAdmissionResult admission = TerrainEditAdmission.Admit(request, playerOverlaps);
        if (admission is TerrainEditRejected rejected)
        {
            return new TerrainWorldEditRejected(center, rejected);
        }

        TerrainEditAccepted accepted = (TerrainEditAccepted)admission;
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
        VoxelEdit[] edits = accepted.Edits.Select(ToEngineEdit).ToArray();
        VoxelEditReceipt receipt = engine.Voxel.ApplyEdits(new VoxelEditTransaction(
            Session,
            scene.SourceRevision,
            edits));
        switch (receipt.Status)
        {
            case VoxelEditStatus.NoChanges:
                return new TerrainWorldEditNoChanges(center, scene.SourceRevision, receipt);

            case VoxelEditStatus.StaleRevision:
            {
                // The scene can have changed since the read used for this transaction.
                // Refresh the retained Engine projection to that current authority, but
                // never replay an edit that was evaluated against the old revision.
                VoxelSceneReadout currentScene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
                VoxelScenePresentationReadout currentPresentation = RefreshPresentation();
                PublishUi();
                return new TerrainWorldEditStaleRevision(
                    center,
                    scene.SourceRevision,
                    receipt,
                    currentScene,
                    currentPresentation);
            }

            case VoxelEditStatus.Accepted:
            {
                TerrainOverlayReceipt overlayReceipt = overlay.Apply(accepted);
                residencyPolicy.RefreshAfterOverlayChange(overlay, overlayReceipt);
                RefreshResidentChunks(overlayReceipt.AppliedEdits.Select(edit => edit.Address.Chunk));
                SaveOverlay();
                VoxelScenePresentationReadout refreshedPresentation = RefreshPresentation();
                PublishUi();
                return new TerrainWorldEditApplied(center, receipt, refreshedPresentation);
            }

            default:
                throw new InvalidOperationException($"Engine returned unsupported voxel edit status '{receipt.Status}'.");
        }
    }

    internal void Restart()
    {
        EnsureStarted();
        if (Synchronize(FixedResidencyCenter))
        {
            RefreshPresentation();
            PublishUi();
        }
    }

    /// <summary>Republishes retained terrain and UI facts for a fresh Engine presentation attachment.</summary>
    internal void Attach()
    {
        EnsureStarted();
        RefreshPresentation();
        PublishUi();
    }

    public void Dispose()
    {
        foreach (VoxelChunkLease lease in leases.Values)
        {
            lease.Dispose();
        }

        leases.Clear();
        residentChunks.Clear();
        presentation?.Dispose();
        presentation = null;
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
    internal VoxelSceneMaterialMappingLeaseReceipt ReadMaterialMapping()
    {
        EnsureStarted();
        return materialMapping;
    }

    /// <summary>Formats the latest player-consumed target/edit result for the narrow live debug surface.</summary>
    internal static string FormatEditReadout(TerrainWorldEditResult? result) => result switch
    {
        null => "outcome=none",
        TerrainWorldEditCastMiss => "outcome=cast-miss",
        TerrainWorldEditPickMiss => "outcome=pick-miss",
        TerrainWorldEditRejected rejected => string.Create(CultureInfo.InvariantCulture,
            $"outcome=rejected;target={FormatVoxel(rejected.Target)};reason={rejected.Rejection.Reason};rejected={FormatVoxel(rejected.Rejection.Address)}"),
        TerrainWorldEditNoChanges noChanges => string.Create(CultureInfo.InvariantCulture,
            $"outcome=no-changes;target={FormatVoxel(noChanges.Target)};expectedRevision={noChanges.ExpectedSceneRevision};currentRevision={noChanges.Receipt.CurrentRevision}"),
        TerrainWorldEditStaleRevision stale => string.Create(CultureInfo.InvariantCulture,
            $"outcome=stale-revision;target={FormatVoxel(stale.Target)};expectedRevision={stale.ExpectedSceneRevision};currentRevision={stale.Receipt.CurrentRevision};sceneRevision={stale.CurrentScene.SourceRevision};presentationSourceRevision={stale.CurrentPresentation.SourceRevision};presentationMeshRevision={stale.CurrentPresentation.MeshRevision}"),
        TerrainWorldEditApplied applied => string.Create(CultureInfo.InvariantCulture,
            $"outcome=accepted;target={FormatVoxel(applied.Target)};changed={applied.Receipt.ChangedVoxels};sceneRevision={applied.Receipt.AcceptedRevision};meshRevision={applied.Receipt.MeshRevision};presentationSourceRevision={applied.Presentation.SourceRevision};presentationMeshRevision={applied.Presentation.MeshRevision}"),
        _ => throw new InvalidOperationException($"Unsupported terrain edit result '{result.GetType().Name}'."),
    };

    private PersistenceStore PersistenceStore => persistenceStore ?? throw new InvalidOperationException("Terrain persistence store is unavailable.");

    private UiStream UiStream => uiStream ?? throw new InvalidOperationException("Terrain UI stream is unavailable.");

    private bool Synchronize(TerrainChunkAddress center)
    {
        TerrainResidencyPlan plan = residencyPolicy.PlanFor(center, overlay);

        foreach (TerrainChunkAddress address in leases.Keys.Where(address => !plan.Requested.Contains(address)).ToArray())
        {
            leases[address].Dispose();
            leases.Remove(address);
        }

        List<VoxelResidencyOperation> operations = [];
        List<uint> materialSlots = [];
        foreach (TerrainChunkAddress address in plan.Requested)
        {
            if (residentChunks.ContainsKey(address))
            {
                continue;
            }

            AddChunkAdmission(address, plan.Overlay, operations, materialSlots);
            if (operations.Count == plan.MaximumOperationsPerTick)
            {
                break;
            }
        }

        foreach ((TerrainChunkAddress address, VoxelChunkReadout readout) in residentChunks)
        {
            if (plan.Retained.Contains(address) || leases.ContainsKey(address) || operations.Count == plan.MaximumOperationsPerTick)
            {
                continue;
            }

            operations.Add(new VoxelResidencyOperation(
                VoxelResidencyOperationKind.Evict,
                ToEngineChunk(address),
                readout.ContentHash,
                0,
                0));
        }

        if (operations.Count > 0)
        {
            VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(
                Session,
                scene.SourceRevision,
                VoxelResidencyHistoryPolicy.ResetToPublishedAuthority,
                operations.ToArray(),
                materialSlots.ToArray()));
            RefreshResidentChunks(operations.Select(operation => FromEngineChunk(operation.Chunk)));
        }

        foreach (TerrainChunkAddress address in plan.Requested)
        {
            if (!residentChunks.ContainsKey(address) || leases.ContainsKey(address))
            {
                continue;
            }

            leases.Add(address, engine.Voxel.AcquireChunkLease(new VoxelChunkLeaseRequest(Session, ToEngineChunk(address))));
        }

        return operations.Count > 0;
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

    private void AddChunkAdmission(TerrainChunkAddress address, TerrainOverlaySnapshot snapshot,
        List<VoxelResidencyOperation> operations, List<uint> materialSlots)
    {
        TerrainChunk chunk = chunkGenerator.Generate(address, snapshot);
        uint offset = checked((uint)materialSlots.Count);
        materialSlots.AddRange(chunk.Materials.ToArray().Select(static material => (uint)material));
        operations.Add(new VoxelResidencyOperation(
            VoxelResidencyOperationKind.Admit,
            ToEngineChunk(address),
            0,
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

        overlay.Restore(TerrainOverlayCodec.Decode(recipe.Configuration.Seed,
            engine.Persistence.ReadBlobBytes(blob).Span));
    }

    private void SaveOverlay()
    {
        byte[] bytes = TerrainOverlayCodec.Encode(overlay.Snapshot());
        engine.Persistence.Save(new PersistenceSaveRequest(
            PersistenceStore,
            TerrainConstants.OverlayPersistenceKey,
            TerrainConstants.PersistenceSchemaVersion,
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

    private ReadOnlyMemory<VoxelSceneMaterialBinding> MaterialBindings() => new VoxelSceneMaterialBinding[]
    {
        new VoxelSceneMaterialBinding(TerrainConstants.GrassMaterial, AtlasCatalog.GrassSide),
        new VoxelSceneMaterialBinding(TerrainConstants.DirtMaterial, AtlasCatalog.Dirt),
        new VoxelSceneMaterialBinding(TerrainConstants.StoneMaterial, AtlasCatalog.Stone),
    };

    private ReadOnlyMemory<VoxelSceneFaceMaterialBinding> FaceMaterialBindings() =>
        new VoxelSceneFaceMaterialBinding[]
        {
            new(TerrainConstants.GrassMaterial, SpatialFace.PosY, AtlasCatalog.GrassTop),
        };

    private TerrainAtlasCatalog AtlasCatalog => atlasCatalog ?? throw new InvalidOperationException("Terrain atlas catalog is unavailable.");

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
            TerrainUiProjection.Create(scene, overlay.Count, playerUi)));
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
    VoxelAddress Target,
    ulong ExpectedSceneRevision,
    VoxelEditReceipt Receipt) : TerrainWorldEditResult;

internal sealed record TerrainWorldEditStaleRevision(
    VoxelAddress Target,
    ulong ExpectedSceneRevision,
    VoxelEditReceipt Receipt,
    VoxelSceneReadout CurrentScene,
    VoxelScenePresentationReadout CurrentPresentation) : TerrainWorldEditResult;

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
