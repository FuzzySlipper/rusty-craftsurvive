using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
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
    private readonly int totalChunks;
    private VoxelScenePresentation? projection;
    private long loadTicks;

    internal DungeonSpace(IEngineContext engine, TerrainWorld terrain, DungeonLayout layout)
    {
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
        }

        loadTicks += Stopwatch.GetTimestamp() - started;
    }

    public void Dispose()
    {
        projection?.Dispose();
        projection = null;
        Session.Dispose();
    }
}
