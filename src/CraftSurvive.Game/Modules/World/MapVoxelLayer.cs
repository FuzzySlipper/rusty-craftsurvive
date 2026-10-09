using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One resolution of the faceted map, streamed as chunk columns in its own dual-contoured spatial
/// session. The owner says which chunk columns it wants and which have changed; the layer works out
/// the chunks to admit, replace and evict and applies a bounded number per update, refreshing the
/// projected scene as it goes. Column contents come from the owner's sampler, in this layer's voxels.
/// </summary>
internal sealed class MapVoxelLayer : IDisposable
{
    internal const int EdgeLength = TerrainConstants.ChunkEdgeLength;
    internal const int ColumnCount = EdgeLength * EdgeLength;
    private const float MinimumDensity = 0.001f;
    private const float CreaseDegrees = 40f;
    private const float Roughness = 0f;

    /// <summary>Fill a chunk column's surfaces (absolute, in voxels; NaN for none) and materials, z-major then x.</summary>
    internal delegate void ColumnSampler(long chunkX, long chunkZ, Span<double> surface, Span<uint> material);

    private sealed record Column(double[] Surface, uint[] Material, long LowChunk, long HighChunk);

    private enum Operation { Admit, Replace, Evict }

    private readonly IEngineContext engine;
    private readonly ColumnSampler sampler;
    private readonly VoxelSceneMaterialBinding[] bindings;
    private readonly Dictionary<(long X, long Z), Column> columns = [];
    private readonly HashSet<(long X, long Z)> wanted = [];
    private readonly HashSet<(long X, long Y, long Z)> resident = [];
    private readonly Dictionary<(long X, long Y, long Z), Operation> pending = [];
    private VoxelScenePresentation? projection;
    private readonly double coarseBeyond;
    private readonly RenderLayer layer;
    private long workTicks;

    /// <param name="terrainLayers">Slots drawn by a terrain-layer material, each with its layer index, blended
    /// across <c>TransitionCells</c>; null draws every slot with its own material.</param>
    /// <param name="coarseBeyond">Chunks farther than this from the camera, in map units, are drawn from coarse meshes (#9563); zero draws all fine.</param>
    /// <param name="layer">The render layer it is drawn in: the scene for the map, the backdrop for the horizon (#9779).</param>
    internal MapVoxelLayer(IEngineContext engine, double voxelSize, ColumnSampler sampler, IReadOnlyDictionary<uint, Material> materials,
        (uint[] Slots, uint[] Layers, uint TransitionCells)? terrainLayers = null, double coarseBeyond = 0, RenderLayer layer = RenderLayer.Scene)
    {
        this.engine = engine;
        this.sampler = sampler;
        this.coarseBeyond = coarseBeyond;
        this.layer = layer;
        bindings = [.. materials.Select(pair => new VoxelSceneMaterialBinding(pair.Key, pair.Value))];
        Session = engine.Spatial.CreateSession(new SpatialSessionConfig(voxelSize, TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.DualContouring));
        try
        {
            uint[] slots = [.. materials.Keys];
            engine.Voxel.ConfigureMaterialSurfaces(new VoxelMaterialSurfaceRequest(Session, VoxelSurfaceMode.DualContouring,
                slots.Select(slot => new VoxelMaterialSurface(slot, VoxelSurfaceMode.DualContouring,
                    new SurfaceCharacter(VertexPlacement.Sharp, CreaseDegrees, Roughness))).ToArray()));
            engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(Session,
                slots.Select(slot => new VoxelMaterialCollision(slot, false)).ToArray()));
            engine.Voxel.ConfigureMaterialOcclusion(new VoxelMaterialOcclusionRequest(Session,
                slots.Select(slot => new VoxelMaterialOcclusion(slot, true)).ToArray()));
            if (terrainLayers is { } layered)
                engine.Voxel.ConfigureTerrainLayers(new VoxelTerrainLayerRequest(Session, layered.Slots, layered.TransitionCells, layered.Layers));
        }
        catch { Session.Dispose(); throw; }
    }

    internal SpatialSession Session { get; }

    /// <summary>Drawn chunks and how many are coarse now, for the map readout.</summary>
    internal (ulong Chunks, ulong Coarse) LevelOfDetail()
    {
        if (projection is not VoxelScenePresentation current) return (0, 0);
        VoxelScenePresentationReadout readout = engine.VoxelScenePresentation.SetLevelOfDetail(new VoxelSceneLevelOfDetailRequest(current, coarseBeyond));
        return (readout.ChunkCount, readout.CoarseChunkCount);
    }
    internal int ResidentChunks => resident.Count;
    internal int PendingChunks => pending.Count;
    /// <summary>Whether the scene is projected and nothing waits to be applied.</summary>
    internal bool Settled => projection is not null && pending.Count == 0;
    internal double WorkMilliseconds => Stopwatch.GetElapsedTime(0, workTicks).TotalMilliseconds;

    /// <summary>Make exactly these chunk columns resident: new ones are admitted, dropped ones evicted.</summary>
    internal void Want(IEnumerable<(long X, long Z)> chunkColumns)
    {
        HashSet<(long X, long Z)> next = [.. chunkColumns];
        foreach ((long X, long Z) gone in wanted.Where(column => !next.Contains(column)).ToArray())
        {
            if (columns.Remove(gone, out Column? old))
                for (long y = old.LowChunk; y <= old.HighChunk; y++) Queue((gone.X, y, gone.Z), Operation.Evict);
            wanted.Remove(gone);
        }
        foreach ((long X, long Z) added in next.Where(column => !wanted.Contains(column)).ToArray())
        {
            wanted.Add(added);
            Column column = Sample(added);
            columns[added] = column;
            for (long y = column.LowChunk; y <= column.HighChunk; y++) Queue((added.X, y, added.Z), Operation.Admit);
        }
    }

    /// <summary>Resample these chunk columns; their resident chunks are replaced, and a changed band admits or evicts.</summary>
    internal void Invalidate(IEnumerable<(long X, long Z)> chunkColumns)
    {
        foreach ((long X, long Z) key in chunkColumns.Distinct())
        {
            if (!wanted.Contains(key)) continue;
            Column old = columns[key];
            Column fresh = Sample(key);
            columns[key] = fresh;
            for (long y = Math.Min(old.LowChunk, fresh.LowChunk); y <= Math.Max(old.HighChunk, fresh.HighChunk); y++)
            {
                (long, long, long) address = (key.X, y, key.Z);
                if (y >= fresh.LowChunk && y <= fresh.HighChunk) Queue(address, resident.Contains(address) ? Operation.Replace : Operation.Admit);
                else Queue(address, Operation.Evict);
            }
        }
    }

    /// <summary>Apply up to <paramref name="budget"/> pending chunk operations, then project or refresh the scene.</summary>
    internal void Advance(int budget)
    {
        long started = Stopwatch.GetTimestamp();
        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        List<float> densities = [];
        foreach (((long X, long Y, long Z) address, Operation queued) in pending.Take(budget).ToArray())
        {
            pending.Remove(address);
            VoxelChunkIdentity chunk = new(address.X, address.Y, address.Z);
            if (queued == Operation.Evict)
            {
                if (!resident.Remove(address)) continue;
                operations.Add(new(VoxelResidencyOperationKind.Evict, chunk, 0, 0));
                continue;
            }
            if (!columns.TryGetValue((address.X, address.Z), out Column? column)) continue;
            bool replacing = resident.Contains(address);
            uint offset = (uint)materials.Count;
            Fill(column, address.Y, materials, densities);
            operations.Add(new(replacing ? VoxelResidencyOperationKind.Replace : VoxelResidencyOperationKind.Admit,
                chunk, offset, TerrainConstants.ChunkVolume, offset, TerrainConstants.ChunkVolume));
            resident.Add(address);
        }
        if (operations.Count > 0)
        {
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, Session,
                operations.ToArray(), materials.ToArray(), densities.ToArray()));
            if (projection is not null) engine.VoxelScenePresentation.RefreshScene(projection);
        }
        // The first projection waits for the first complete load, so it appears whole.
        if (projection is null && pending.Count == 0 && wanted.Count > 0)
        {
            projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new(Session, bindings,
                ReadOnlyMemory<VoxelSceneFaceMaterialBinding>.Empty));
            if (coarseBeyond > 0) engine.VoxelScenePresentation.SetLevelOfDetail(new VoxelSceneLevelOfDetailRequest(projection, coarseBeyond));
            if (layer != RenderLayer.Scene) engine.VoxelScenePresentation.SetLayer(new(projection, layer));
        }
        workTicks += Stopwatch.GetTimestamp() - started;
    }

    public void Dispose()
    {
        projection?.Dispose();
        projection = null;
        Session.Dispose();
    }

    private void Queue((long X, long Y, long Z) address, Operation operation)
    {
        // Evicting a chunk that was only ever queued, never applied, simply forgets it.
        if (operation == Operation.Evict && !resident.Contains(address))
        {
            pending.Remove(address);
            return;
        }
        pending[address] = operation;
    }

    private Column Sample((long X, long Z) key)
    {
        double[] surface = new double[ColumnCount];
        uint[] material = new uint[ColumnCount];
        sampler(key.X, key.Z, surface, material);
        double low = double.MaxValue, high = double.MinValue;
        foreach (double s in surface)
        {
            if (double.IsNaN(s)) continue;
            low = Math.Min(low, s);
            high = Math.Max(high, s);
        }
        // Only chunks a surface passes through, plus one beneath for footing.
        return low == double.MaxValue
            ? new(surface, material, 1, 0)
            : new(surface, material, GridMath.FloorDivide((long)Math.Floor(low), EdgeLength) - 1,
                GridMath.FloorDivide((long)Math.Ceiling(high), EdgeLength));
    }

    /// <summary>X-fastest dense payload; density is signed distance in voxels from the column's surface.</summary>
    private static void Fill(Column column, long chunkY, List<uint> materials, List<float> densities)
    {
        for (int z = 0; z < EdgeLength; z++)
        for (int y = 0; y < EdgeLength; y++)
        for (int x = 0; x < EdgeLength; x++)
        {
            double surface = column.Surface[z * EdgeLength + x];
            if (double.IsNaN(surface))
            {
                materials.Add(TerrainConstants.EmptyMaterial);
                densities.Add(1);
                continue;
            }
            float distance = (float)(chunkY * EdgeLength + y + 0.5 - surface);
            bool solid = distance < 0;
            materials.Add(solid ? column.Material[z * EdgeLength + x] : TerrainConstants.EmptyMaterial);
            densities.Add(solid ? Math.Min(distance, -MinimumDensity) : Math.Max(distance, MinimumDensity));
        }
    }
}
