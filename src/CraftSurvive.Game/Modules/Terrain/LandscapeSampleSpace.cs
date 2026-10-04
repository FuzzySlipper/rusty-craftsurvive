using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>A finite terrain study using the ordinary Engine voxel and player services.</summary>
internal sealed class LandscapeSampleSpace : IDisposable
{
    private const double VoxelSize = TerrainConstants.VoxelSize;
    private const int RadiusMetres = 48;
    private const int BottomMetres = -16;
    private const int TopMetres = 32;
    // A multiple of both texture tile widths, so vertical texture phase is preserved.
    private const int OriginY = -3072;
    private const int ChunksPerUpdate = 4;
    private const double ReferenceSampleCentre = 0.5;
    private const double SurfaceOffset = 1;
    private const float MinimumDensity = 0.001f;
    private const float ArrivalClearance = 0.1f;
    private readonly IEngineContext engine;
    private readonly LandscapeStudy study;
    private readonly TerrainGroundMaterials ground;
    private readonly Queue<TerrainChunkAddress> pending = new();
    private readonly int totalChunks;
    private long generationTicks;
    private VoxelScenePresentation? projection;

    internal LandscapeSampleSpace(IEngineContext engine, ProductContent content, LandscapeStudy study)
    {
        this.engine = engine;
        this.study = study;
        Session = engine.Spatial.CreateSession(new SpatialSessionConfig(VoxelSize,
            TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.DualContouring));
        try
        {
            TerrainSurfaces.Apply(engine, Session);
            VoxelMaterialRules.Apply(engine, Session);
            ground = new TerrainGroundMaterials(engine, content, VoxelSize);
            ground.Configure(engine, Session);
        }
        catch { Session.Dispose(); throw; }
        double chunkMetres = TerrainConstants.ChunkEdgeLength * VoxelSize;
        for (long z = (long)((study.CentreZ - RadiusMetres) / chunkMetres); z < (study.CentreZ + RadiusMetres) / chunkMetres; z++)
        for (long x = (long)((study.CentreX - RadiusMetres) / chunkMetres); x < (study.CentreX + RadiusMetres) / chunkMetres; x++)
        for (long y = (long)Math.Floor((OriginY + BottomMetres) / chunkMetres); y < (OriginY + TopMetres) / chunkMetres; y++)
            pending.Enqueue(new(x, y, z));
        totalChunks = pending.Count;
    }

    internal SpatialSession Session { get; }
    internal bool Loaded => projection is not null;
    internal string Readout => FormattableString.Invariant($"{study.Id} cell={VoxelSize}m chunks={totalChunks - pending.Count}/{totalChunks} samples={(long)totalChunks * TerrainConstants.ChunkVolume} admissionMs={Stopwatch.GetElapsedTime(0, generationTicks).TotalMilliseconds:F0}");

    internal Vector3 Arrival
    {
        get
        {
            double x = study.CentreX, z = study.CentreZ + LandscapeStudies.ArrivalOffsetZ;
            double height = LandscapeStudies.Height(x - ReferenceSampleCentre, z - ReferenceSampleCentre, 0) + SurfaceOffset;
            return new((float)x, (float)(OriginY + height + ArrivalClearance), (float)z);
        }
    }

    internal void Advance()
    {
        if (Loaded) return;
        long started = Stopwatch.GetTimestamp();
        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        List<float> densities = [];
        while (operations.Count < ChunksPerUpdate && pending.TryDequeue(out TerrainChunkAddress address))
        {
            uint offset = (uint)materials.Count;
            VoxelAddress origin = address.Origin;
            // X-fastest, exactly the Engine's dense payload order. Sample the same metre-space
            // function at cell centres; density magnitude is converted back into voxel units.
            for (int z = 0; z < TerrainConstants.ChunkEdgeLength; z++)
            {
                double worldZ = (origin.Z + z + ReferenceSampleCentre) * VoxelSize - ReferenceSampleCentre;
                double[] heights = new double[TerrainConstants.ChunkEdgeLength];
                uint[] columns = new uint[TerrainConstants.ChunkEdgeLength];
                for (int x = 0; x < TerrainConstants.ChunkEdgeLength; x++)
                {
                    double worldX = (origin.X + x + ReferenceSampleCentre) * VoxelSize - ReferenceSampleCentre;
                    heights[x] = LandscapeStudies.Height(worldX, worldZ, 0) + SurfaceOffset;
                    columns[x] = (uint)LandscapeStudies.Material(study, worldX, worldZ);
                }
                for (int y = 0; y < TerrainConstants.ChunkEdgeLength; y++)
                for (int x = 0; x < TerrainConstants.ChunkEdgeLength; x++)
                {
                    float distance = (float)(((origin.Y + y + ReferenceSampleCentre) * VoxelSize - OriginY - heights[x]) / VoxelSize);
                    bool solid = distance < 0;
                    materials.Add(solid ? columns[x] : TerrainConstants.EmptyMaterial);
                    densities.Add(solid ? Math.Min(distance, -MinimumDensity) : Math.Max(distance, MinimumDensity));
                }
            }
            operations.Add(new(VoxelResidencyOperationKind.Admit, new(address.X, address.Y, address.Z),
                offset, TerrainConstants.ChunkVolume, offset, TerrainConstants.ChunkVolume));
        }
        if (operations.Count > 0)
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, Session,
                operations.ToArray(), materials.ToArray(), densities.ToArray()));
        if (pending.Count == 0)
        {
            VoxelSceneMaterialBinding[] bindings = BlockRegistry.BoundBlocks
                .Where(block => ground.For(block.Id) is not null)
                .Select(block => new VoxelSceneMaterialBinding(block.Slot, ground.For(block.Id)!)).ToArray();
            projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new(Session, bindings,
                ReadOnlyMemory<VoxelSceneFaceMaterialBinding>.Empty));
        }
        generationTicks += Stopwatch.GetTimestamp() - started;
    }

    public void Dispose()
    {
        projection?.Dispose();
        projection = null;
        ground.Dispose();
        Session.Dispose();
    }
}
