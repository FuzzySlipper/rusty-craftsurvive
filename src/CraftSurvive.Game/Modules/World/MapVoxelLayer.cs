using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One resolution of the faceted map: a height-and-material column grid voxelized into its own
/// dual-contoured spatial session. Columns and heights are in this layer's voxel units; the
/// session's voxel size places every layer in one shared presentation space.
/// </summary>
internal sealed class MapVoxelLayer : IDisposable
{
    private const float MinimumDensity = 0.001f;
    private const float CreaseDegrees = 40f;
    private const float Roughness = 0f;
    private const int EdgeLength = TerrainConstants.ChunkEdgeLength;

    private readonly IEngineContext engine;
    private readonly long originX, originZ;
    private readonly int width, depth;
    private readonly double[] surface;
    private readonly uint[] material;
    private readonly VoxelSceneMaterialBinding[] bindings;
    private readonly Queue<(long X, long Y, long Z)> pending = new();
    private VoxelScenePresentation? projection;
    private long workTicks;

    /// <param name="surface">Absolute surface height per column, in voxels; row-major, <paramref name="width"/> wide.</param>
    /// <param name="terrainLayers">Slots drawn by a terrain-layer material, each with its layer index, blended
    /// across <c>TransitionCells</c>; null draws every slot with its own material.</param>
    internal MapVoxelLayer(IEngineContext engine, double voxelSize, long originX, long originZ, int width, int depth,
        double[] surface, uint[] material, IReadOnlyDictionary<uint, Material> materials,
        (uint[] Slots, uint[] Layers, uint TransitionCells)? terrainLayers = null)
    {
        this.engine = engine;
        this.originX = originX;
        this.originZ = originZ;
        this.width = width;
        this.depth = depth;
        this.surface = surface;
        this.material = material;
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

        // Only chunks a surface passes through, plus one beneath for footing.
        for (long cz = Floor(originZ); cz <= Floor(originZ + depth - 1); cz++)
        for (long cx = Floor(originX); cx <= Floor(originX + width - 1); cx++)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (long vz = cz * EdgeLength; vz < (cz + 1) * EdgeLength; vz++)
            for (long vx = cx * EdgeLength; vx < (cx + 1) * EdgeLength; vx++)
            {
                if (!TryColumn(vx, vz, out int column)) continue;
                low = Math.Min(low, surface[column]);
                high = Math.Max(high, surface[column]);
            }
            if (low == double.MaxValue) continue;
            for (long cy = Floor((long)Math.Floor(low)) - 1; cy <= Floor((long)Math.Ceiling(high)); cy++) pending.Enqueue((cx, cy, cz));
        }
        TotalChunks = pending.Count;
    }

    internal SpatialSession Session { get; }
    internal int TotalChunks { get; }
    internal int LoadedChunks => TotalChunks - pending.Count;
    internal bool Loaded => projection is not null;
    internal double WorkMilliseconds => Stopwatch.GetElapsedTime(0, workTicks).TotalMilliseconds;

    /// <summary>Admit up to <paramref name="chunks"/> chunks; project the scene once the last has landed.</summary>
    internal void Advance(int chunks)
    {
        if (Loaded) return;
        long started = Stopwatch.GetTimestamp();
        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        List<float> densities = [];
        while (operations.Count < chunks && pending.TryDequeue(out (long X, long Y, long Z) address))
        {
            uint offset = (uint)materials.Count;
            // X-fastest, the Engine's dense payload order; density is signed distance in voxels.
            for (int z = 0; z < EdgeLength; z++)
            for (int y = 0; y < EdgeLength; y++)
            for (int x = 0; x < EdgeLength; x++)
            {
                long vx = address.X * EdgeLength + x, vy = address.Y * EdgeLength + y, vz = address.Z * EdgeLength + z;
                if (!TryColumn(vx, vz, out int column))
                {
                    materials.Add(TerrainConstants.EmptyMaterial);
                    densities.Add(1);
                    continue;
                }
                float distance = (float)(vy + 0.5 - surface[column]);
                bool solid = distance < 0;
                materials.Add(solid ? material[column] : TerrainConstants.EmptyMaterial);
                densities.Add(solid ? Math.Min(distance, -MinimumDensity) : Math.Max(distance, MinimumDensity));
            }
            operations.Add(new(VoxelResidencyOperationKind.Admit, new(address.X, address.Y, address.Z),
                offset, TerrainConstants.ChunkVolume, offset, TerrainConstants.ChunkVolume));
        }
        if (operations.Count > 0)
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, Session,
                operations.ToArray(), materials.ToArray(), densities.ToArray()));
        if (pending.Count == 0)
            projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new(Session, bindings,
                ReadOnlyMemory<VoxelSceneFaceMaterialBinding>.Empty));
        workTicks += Stopwatch.GetTimestamp() - started;
    }

    public void Dispose()
    {
        projection?.Dispose();
        projection = null;
        Session.Dispose();
    }

    private bool TryColumn(long vx, long vz, out int column)
    {
        long x = vx - originX, z = vz - originZ;
        column = (int)(z * width + x);
        return x >= 0 && z >= 0 && x < width && z < depth;
    }

    private static long Floor(long voxel) => GridMath.FloorDivide(voxel, EdgeLength);
}
