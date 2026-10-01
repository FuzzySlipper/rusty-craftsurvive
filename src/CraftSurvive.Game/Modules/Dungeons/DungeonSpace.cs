using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// One loaded dungeon: its own spatial session, never streamed or rebased, filled from its layout
/// a few chunks per update behind the loading screen, then drawn with the world's block materials
/// The session sits far below the open world's coordinates, so the two projections never meet in
/// view. Disposing it releases everything it made; its lights belong to the dungeon module's pool.
/// </summary>
internal sealed class DungeonSpace : IDisposable
{
    /// <summary>Where a dungeon's volume begins, in its session: far below anything in the open world.</summary>
    internal static readonly Vector3 Origin = new(0f, -2048f, 0f);

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly DungeonLayout layout;
    private readonly Queue<(int X, int Y, int Z)> pending = new();
    private readonly Material? rockMaterial;
    private MeshResource? rockMesh;
    private Appearance? rockAppearance;
    private readonly int totalChunks;
    private VoxelScenePresentation? projection;
    private long loadTicks;

    /// <summary>How often the rock's texture repeats per metre of surface.</summary>
    private const float RockUvScale = 0.25f;

    /// <summary>Faces meeting at more than this angle keep a hard edge; gentler ones shade smoothly.</summary>
    private const float RockCreaseDegrees = 50f;

    private const ulong RockAssetId = 1UL;
    private const ulong RockInstanceId = 1UL;

    /// <summary>Samples written to the Engine's sampled volume per write.</summary>
    private const int RockWriteBatch = 65_536;

    internal DungeonSpace(IEngineContext engine, TerrainWorld terrain, DungeonLayout layout, Material? rockMaterial = null)
    {
        this.rockMaterial = rockMaterial;
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Session = engine.Spatial.CreateSession(new SpatialSessionConfig(
            TerrainConstants.VoxelSize, TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.GreedyCubes));
        engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(
            Session,
            BlockRegistry.MaterialBlocks.Select(block => new VoxelMaterialCollision((uint)block.Id, block.Collidable)).ToArray()));
        for (int z = 0; z < layout.Volume.ChunksZ; z++)
        {
            for (int y = 0; y < layout.Volume.ChunksY; y++)
            {
                for (int x = 0; x < layout.Volume.ChunksX; x++)
                {
                    pending.Enqueue((x, y, z));
                }
            }
        }

        totalChunks = pending.Count;
    }

    internal SpatialSession Session { get; }

    internal DungeonLayout Layout => layout;

    /// <summary>How much of the dungeon is loaded, from 0 to 1.</summary>
    internal double Progress => totalChunks == 0 ? 1d : 1d - ((double)pending.Count / totalChunks);

    internal bool Loaded => pending.Count == 0 && projection is not null;

    /// <summary>Wall-clock time spent loading so far, in milliseconds.</summary>
    internal double LoadMilliseconds => Stopwatch.GetElapsedTime(0, loadTicks).TotalMilliseconds;

    internal int TotalChunks => totalChunks;

    /// <summary>What a sculpted dungeon's rock contributes to the appearance snapshot; nothing when its rock is voxels.</summary>
    internal IEnumerable<AppearanceFact> AppearanceFacts => rockAppearance is Appearance appearance
        ? [new AppearanceFact(ProductIds.DungeonRockObject, false, 0UL, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), appearance, true, RenderLayer.Scene)]
        : [];

    /// <summary>The rock mesh's size and cost, once built.</summary>
    internal string RockReadout { get; private set; } = "voxel rock";

    /// <summary>Where a point of the layout stands in the session.</summary>
    internal static Vector3 InSession(Vector3 layoutPoint) => layoutPoint + Origin;

    /// <summary>Admits up to some chunks; once every chunk is in, draws the space and lights it.</summary>
    internal void Advance(int chunks)
    {
        long started = Stopwatch.GetTimestamp();
        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        for (int admitted = 0; admitted < chunks && pending.TryDequeue(out (int X, int Y, int Z) chunk); admitted++)
        {
            uint offset = checked((uint)materials.Count);
            foreach (ushort material in layout.Volume.Chunk(chunk.X, chunk.Y, chunk.Z))
            {
                materials.Add(material);
            }

            operations.Add(new VoxelResidencyOperation(
                VoxelResidencyOperationKind.Admit,
                new VoxelChunkIdentity(
                    chunk.X + ((long)Origin.X / TerrainConstants.ChunkEdgeLength),
                    chunk.Y + ((long)Origin.Y / TerrainConstants.ChunkEdgeLength),
                    chunk.Z + ((long)Origin.Z / TerrainConstants.ChunkEdgeLength)),
                offset,
                checked((uint)(materials.Count - (int)offset))));
        }

        if (operations.Count > 0)
        {
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(Session, operations.ToArray(), materials.ToArray()));
        }

        if (pending.Count == 0 && projection is null)
        {
            projection = terrain.ProjectSeparateSpace(Session);
            if (layout.Rock is RockDensity rock)
            {
                BuildRock(rock);
            }
        }

        loadTicks += Stopwatch.GetTimestamp() - started;
    }

    /// <summary>
    /// Meshes the rock's density by dual contouring through the Engine's sampled volume, admits the
    /// mesh as the session's static collision, and keeps an appearance of it to draw.
    /// </summary>
    private void BuildRock(RockDensity rock)
    {
        long started = Stopwatch.GetTimestamp();
        using SampledVolume volume = engine.ImplicitSurfaces.CreateSampledVolume(new SampledVolumeCreateRequest(
            Origin + new Vector3(0.5f, 0.5f, 0.5f), RockDensity.Spacing, (uint)rock.Width, (uint)rock.Height, (uint)rock.Depth, 1f));
        DensitySample[] batch = new DensitySample[RockWriteBatch];
        for (int start = 0; start < rock.Values.Length; start += RockWriteBatch)
        {
            int count = Math.Min(RockWriteBatch, rock.Values.Length - start);
            for (int index = 0; index < count; index++)
            {
                batch[index] = new DensitySample(rock.Values[start + index]);
            }

            engine.ImplicitSurfaces.WriteSampledVolume(new SampledVolumeWriteRequest(volume, (uint)start, batch.AsMemory(0, count)));
        }

        using ImplicitField regions = engine.ImplicitSurfaces.CreateField();
        rockMesh = engine.ImplicitSurfaces.GenerateSampledVolume(new SampledVolumeGenerateRequest(
            volume, regions, 0f, RockCreaseDegrees, RockUvScale, rockMaterial!, ReadOnlyMemory<ImplicitMaterialRegion>.Empty,
            ImplicitMaterialBoundaryMode.Centroid, 0f));
        double meshMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(
            Session,
            new[] { new StaticMeshAsset(RockAssetId, new MeshResourceReference(rockMesh), 0, 0, 0, 0) },
            ReadOnlyMemory<Vector3>.Empty,
            ReadOnlyMemory<Triangle>.Empty,
            new[] { new StaticMeshInstance(RockInstanceId, RockAssetId, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One)) }));
        rockAppearance = engine.Graphics.CreateMeshAppearance(rockMesh);
        ImplicitGenerationReadout generated = engine.ImplicitSurfaces.ReadSampledVolumeGeneration(volume);
        RockReadout = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"sculpted rock: {generated.Vertices} vertices, {generated.Triangles} triangles, {generated.BoundaryEdges} boundary edges, meshed in {meshMs:F0} ms, collision and appearance in {Stopwatch.GetElapsedTime(started).TotalMilliseconds - meshMs:F0} ms");
    }

    public void Dispose()
    {
        rockAppearance?.Dispose();
        rockAppearance = null;
        rockMesh?.Dispose();
        rockMesh = null;
        projection?.Dispose();
        projection = null;
        Session.Dispose();
    }
}
