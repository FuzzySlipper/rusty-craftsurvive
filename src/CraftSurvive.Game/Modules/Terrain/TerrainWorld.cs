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
    private readonly List<Material> materials = [];
    private SpatialSession? session;
    private PersistenceStore? persistenceStore;
    private UiStream? uiStream;
    private VoxelScenePresentation? presentation;
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
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        session = engine.Spatial.CreateSession(new SpatialSessionConfig(
            TerrainConstants.VoxelSize,
            TerrainConstants.VoxelChunkSize,
            VoxelSurfaceMode.GreedyCubes));
        persistenceStore = engine.Persistence.OpenStore(new PersistenceOpenRequest(TerrainConstants.PersistenceScope));
        uiStream = engine.Ui.OpenStream(new UiStreamRequest(
            TerrainConstants.UiStreamName,
            TerrainConstants.UiStreamContract));

        try
        {
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
            return TerrainWorldEditResult.Miss;
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
            return TerrainWorldEditResult.Miss;
        }

        VoxelAddress target = new(picked.VoxelX, picked.VoxelY, picked.VoxelZ);
        VoxelAddress center = kind == TerrainEditKind.Set ? Adjacent(target, picked.Face) : target;
        TerrainEditRequest request = kind == TerrainEditKind.Set
            ? TerrainEditRequest.Set(center, material, radius)
            : TerrainEditRequest.Clear(center, radius);
        TerrainEditAdmissionResult admission = TerrainEditAdmission.Admit(request, playerOverlaps);
        if (admission is TerrainEditRejected rejected)
        {
            return new TerrainWorldEditRejected(rejected);
        }

        TerrainEditAccepted accepted = (TerrainEditAccepted)admission;
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
        VoxelEdit[] edits = accepted.Edits.Select(ToEngineEdit).ToArray();
        VoxelEditReceipt receipt = engine.Voxel.ApplyEdits(new VoxelEditTransaction(
            Session,
            scene.SourceRevision,
            edits));
        overlay.Apply(accepted);
        SaveOverlay();
        RefreshPresentation();
        PublishUi();
        return new TerrainWorldEditApplied(receipt);
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

    public void Dispose()
    {
        foreach (VoxelChunkLease lease in leases.Values)
        {
            lease.Dispose();
        }

        leases.Clear();
        presentation?.Dispose();
        presentation = null;
        foreach (Material material in materials)
        {
            material.Dispose();
        }

        materials.Clear();
        uiStream?.Dispose();
        uiStream = null;
        persistenceStore?.Dispose();
        persistenceStore = null;
        session?.Dispose();
        session = null;
        started = false;
    }

    private SpatialSession Session => session ?? throw new InvalidOperationException("Terrain spatial session is unavailable.");

    private PersistenceStore PersistenceStore => persistenceStore ?? throw new InvalidOperationException("Terrain persistence store is unavailable.");

    private UiStream UiStream => uiStream ?? throw new InvalidOperationException("Terrain UI stream is unavailable.");

    private bool Synchronize(TerrainChunkAddress center)
    {
        TerrainOverlaySnapshot snapshot = overlay.Snapshot();
        TerrainResidencyPlan plan = residencyPolicy.PlanFor(center, snapshot);
        Dictionary<TerrainChunkAddress, VoxelChunkReadout> current = ReadResidentChunks();

        foreach (TerrainChunkAddress address in leases.Keys.Where(address => !plan.Requested.Contains(address)).ToArray())
        {
            leases[address].Dispose();
            leases.Remove(address);
        }

        List<VoxelResidencyOperation> operations = [];
        List<uint> materialSlots = [];
        foreach (TerrainChunkAddress address in plan.Requested)
        {
            if (current.ContainsKey(address))
            {
                continue;
            }

            AddChunkAdmission(address, snapshot, operations, materialSlots);
            if (operations.Count == plan.MaximumOperationsPerTick)
            {
                break;
            }
        }

        foreach ((TerrainChunkAddress address, VoxelChunkReadout readout) in current)
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
            current = ReadResidentChunks();
        }

        foreach (TerrainChunkAddress address in plan.Requested)
        {
            if (!current.ContainsKey(address) || leases.ContainsKey(address))
            {
                continue;
            }

            leases.Add(address, engine.Voxel.AcquireChunkLease(new VoxelChunkLeaseRequest(Session, ToEngineChunk(address))));
        }

        return operations.Count > 0;
    }

    private Dictionary<TerrainChunkAddress, VoxelChunkReadout> ReadResidentChunks()
    {
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
        Dictionary<TerrainChunkAddress, VoxelChunkReadout> chunks = [];
        for (ulong index = 0; index < scene.ResidentChunkCount; index++)
        {
            VoxelChunkReadout readout = engine.Voxel.ReadResidentChunkAt(new VoxelResidentChunkAtRequest(Session, checked((uint)index)));
            if (readout.Present)
            {
                chunks.Add(FromEngineChunk(readout.Chunk), readout);
            }
        }

        return chunks;
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
        materials.Add(CreateMaterial(TerrainPresentation.Grass));
        materials.Add(CreateMaterial(TerrainPresentation.Dirt));
        materials.Add(CreateMaterial(TerrainPresentation.Stone));
        presentation = engine.VoxelScenePresentation.ProjectScene(new ProjectVoxelSceneRequest(
            Session,
            MaterialBindings()));
    }

    private Material CreateMaterial(Color color) => engine.Appearance.CreateMaterial(new MaterialRequest(
        color,
        default,
        TerrainConstants.TerrainRoughness,
        new Color(TerrainConstants.MaterialAlpha, TerrainConstants.MaterialAlpha,
            TerrainConstants.MaterialAlpha, TerrainConstants.MaterialAlpha),
        Vector3.Zero,
        TerrainConstants.NoEmission,
        false));

    private void RefreshPresentation()
    {
        if (presentation is not null)
        {
            engine.VoxelScenePresentation.RefreshScene(presentation);
        }
    }

    private ReadOnlyMemory<VoxelSceneMaterialBinding> MaterialBindings() => new VoxelSceneMaterialBinding[]
    {
        new VoxelSceneMaterialBinding(TerrainConstants.GrassMaterial, materials[0]),
        new VoxelSceneMaterialBinding(TerrainConstants.DirtMaterial, materials[1]),
        new VoxelSceneMaterialBinding(TerrainConstants.StoneMaterial, materials[2]),
    };

    private void PublishUi()
    {
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
        engine.Ui.PublishProjection(new UiProjection(UiStream, ++uiSequence,
            TerrainUiProjection.Create(scene, overlay.Count)));
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
    internal static TerrainWorldEditResult Miss { get; } = new TerrainWorldEditMiss();
}

internal sealed record TerrainWorldEditMiss : TerrainWorldEditResult;

internal sealed record TerrainWorldEditRejected(TerrainEditRejected Rejection) : TerrainWorldEditResult;

internal sealed record TerrainWorldEditApplied(VoxelEditReceipt Receipt) : TerrainWorldEditResult;

internal static class TerrainPresentation
{
    internal static readonly Color Grass = new(0.29f, 0.52f, 0.18f, TerrainConstants.MaterialAlpha);
    internal static readonly Color Dirt = new(0.38f, 0.23f, 0.12f, TerrainConstants.MaterialAlpha);
    internal static readonly Color Stone = new(0.36f, 0.38f, 0.41f, TerrainConstants.MaterialAlpha);
}
