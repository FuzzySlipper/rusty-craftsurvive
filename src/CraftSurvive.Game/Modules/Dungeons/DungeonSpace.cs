using System.Diagnostics;
using System.Numerics;
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
    internal static Vector3 Origin => DungeonCollision.Origin;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly DungeonLayout layout;
    private readonly Queue<(int X, int Y, int Z)> pending;
    private readonly Material? rockMaterial;
    private readonly DungeonSurface surface;
    private MeshResource? rockMesh;
    private Appearance? rockAppearance;
    private readonly int totalChunks;
    private VoxelScenePresentation? projection;
    private long loadTicks;

    /// <param name="surface">How the space's voxels are surfaced; drawn with the world's block materials whatever it is.</param>
    internal DungeonSpace(IEngineContext engine, TerrainWorld terrain, DungeonLayout layout, Material? rockMaterial = null,
        DungeonSurface surface = DungeonSurface.Cubes)
    {
        this.surface = surface;
        this.rockMaterial = rockMaterial;
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Session = DungeonCollision.CreateSession(engine, surface);
        pending = new Queue<(int X, int Y, int Z)>(DungeonCollision.Chunks(layout.Volume));
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
    internal static Vector3 InSession(Vector3 layoutPoint) => DungeonCollision.InSession(layoutPoint);

    /// <summary>Admits up to some chunks; once every chunk is in, draws the space and lights it.</summary>
    internal void Advance(int chunks)
    {
        long started = Stopwatch.GetTimestamp();
        List<(int X, int Y, int Z)> admitted = [];
        while (admitted.Count < chunks && pending.TryDequeue(out (int X, int Y, int Z) chunk))
        {
            admitted.Add(chunk);
        }

        DungeonCollision.Admit(engine, Session, layout.Volume, admitted, layout.Densities, DungeonSurfaces.Weathers(surface));

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
        rockMesh = DungeonCollision.MeshRock(engine, rock, rockMaterial!, out ImplicitGenerationReadout generated);
        double meshMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        DungeonCollision.AdmitRock(engine, Session, rockMesh);
        rockAppearance = engine.Graphics.CreateMeshAppearance(rockMesh);
        RockReadout = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"sculpted rock: {generated.Vertices} vertices, {generated.Triangles} triangles, {generated.BoundaryEdges} boundary edges, meshed in {meshMs:F0} ms, collision and appearance in {Stopwatch.GetElapsedTime(started).TotalMilliseconds - meshMs:F0} ms");
    }

    /// <summary>Re-meshes what the space draws after its voxels were edited.</summary>
    internal void Refresh()
    {
        if (projection is not null)
        {
            engine.VoxelScenePresentation.RefreshScene(projection);
        }
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
