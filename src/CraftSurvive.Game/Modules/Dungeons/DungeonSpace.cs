using System.Globalization;
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
    internal static Vector3 Origin => DungeonCollision.Origin;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly DungeonLayout layout;
    private readonly Queue<(int X, int Y, int Z)> pending;
    private readonly Material? rockMaterial;
    private readonly IReadOnlyDictionary<BlockId, Material>? voxelRock;
    private readonly bool caveLayers;
    private readonly DungeonSurface surface;
    private MeshResource? rockMesh;
    private Appearance? rockAppearance;
    /// <summary>Route C's rock pieces (#10090): merged meshes and their appearances.</summary>
    private readonly List<(MeshResource Mesh, Appearance Look)> pieces = [];
    /// <summary>The cave's living light (#10129): one mesh per kind of growth, and the materials made for them.</summary>
    // The growth meshes, each with the chunk it lies in when streamed (drawn only while that chunk's rock is resident; null:
    // always drawn).
    private readonly List<(MeshResource Mesh, Appearance Look, (int X, int Y, int Z)? Region)> growths = [];

    private readonly Dictionary<EcologyKind, (Material Plain, Material Glowing)> sharedGrowthMaterials = [];

    /// <summary>
    /// The placements grouped by the chunk each lies in (layout metres), when streamed, so growth comes and goes with the rock
    /// it grows on (#10304); the whole set as one group otherwise.
    /// </summary>
    private IEnumerable<((int X, int Y, int Z)? Region, List<T> Items)> ByRegion<T>(IEnumerable<T> items, Func<T, Vector3> at)
    {
        if (StreamRadius == 0)
        {
            return [(null, items.ToList())];
        }

        return items.GroupBy(i => ChunkOf(at(i))).Select(g => (((int X, int Y, int Z)?)g.Key, g.ToList()));
    }

    /// <summary>A cave's lakes' and pools' surfaces (#10094): their material, while the space draws them.</summary>
    private CaveWaterSurfaces? waterSurfaces;

    /// <summary>A modular dungeon's kit pieces (#10089), drawn and collided while the space is loaded.</summary>
    private DungeonKitLook? kit;
    private DungeonOvergrowth? overgrowth;

    /// <summary>The kit paint study's texture folder (#10089), or null for the kit's own textures.</summary>
    private readonly string? kitPaint;

    /// <summary>The kit stone shader's settings for this space (#10089).</summary>
    private readonly DungeonKitLook.Look kitLook;

    /// <summary>Retunes the kit's stone shader now, when the space has a kit (a study).</summary>
    internal void TuneKit(DungeonKitLook.Look look) => kit?.Tune(look);

    /// <summary>Retunes the kit's glowing pieces to the glow intensity now (a study).</summary>
    internal void TuneGlow() => kit?.TuneGlow();

    /// <summary>A cave's moss and ivy cards (#10092): their materials, while the space draws them.</summary>
    private CaveMossCards? mossCards;

    /// <summary>A chasm's waterfalls (#10094), once the space is drawn; null before, or for a space without them.</summary>
    internal CaveWaterfalls? Waterfalls { get; private set; }
    private readonly List<Material> growthMaterials = [];

    /// <summary>Route B's finer rock (#10090), meshed in slabs; the first is also <see cref="rockMesh"/>.</summary>
    private readonly List<(MeshResource Mesh, Appearance Look)> slabs = [];
    private readonly int totalChunks;
    private VoxelScenePresentation? projection;
    private long loadTicks;

    /// <param name="surface">How the space's voxels are surfaced; drawn with the world's block materials whatever it is.</param>
    /// <param name="caveLayers">Whether the rock blocks are the cave's ground materials, blended as terrain layers (#10095).</param>
    /// <param name="voxelRock">For a voxel look, the material each rock block draws in (#10094: the cave rock's age classes).</param>
    internal DungeonSpace(IEngineContext engine, TerrainWorld terrain, DungeonLayout layout, Material? rockMaterial = null,
        DungeonSurface surface = DungeonSurface.Cubes, IReadOnlyDictionary<BlockId, Material>? voxelRock = null, bool caveLayers = false,
        string? kitPaint = null, DungeonKitLook.Look? kitLook = null, int streamRadius = 0)
    {
        this.kitPaint = kitPaint;
        this.kitLook = kitLook ?? DungeonKitLook.Look.Default;
        this.voxelRock = voxelRock;
        this.caveLayers = caveLayers;
        this.surface = surface;
        this.rockMaterial = rockMaterial;
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Session = DungeonCollision.CreateSession(engine, surface, layout.VoxelSize);
        StreamRadius = streamRadius;
        if (streamRadius > 0)
        {
            // Streaming (#10304 prototype): only the chunks that hold air and the shell round them are ever admitted, and
            // at first only those within the radius of the arrival, nearest first.
            shell = Shell(layout.Volume);
            (int X, int Y, int Z) at = ChunkOf(layout.Arrival);
            pending = new Queue<(int X, int Y, int Z)>(shell.Where(c => Within(c, at, streamRadius)).OrderBy(c => Reach(c, at)));
        }
        else
        {
            pending = new Queue<(int X, int Y, int Z)>(DungeonCollision.Chunks(layout.Volume));
        }

        totalChunks = pending.Count;
    }

    // --- Streaming (#10304 prototype: a stored cave streamed about the player) ---

    /// <summary>How many chunks about the player are kept across (0: the whole space is loaded at once).</summary>
    internal int StreamRadius { get; }

    /// <summary>The streamed chunks kept up and down about the player.</summary>
    internal const int StreamRadiusUp = 3;

    /// <summary>The most chunks admitted in one update while streaming.</summary>
    internal const int StreamBudget = 6;

    /// <summary>The most chunks a deferred hand-off takes (#10332): their rebuild and install cost the Engine's worker and a later update's start, not the write the budget measures.</summary>
    internal const int DeferredStreamBudget = 6;

    private readonly HashSet<(int X, int Y, int Z)> shell = [];
    private readonly HashSet<(int X, int Y, int Z)> resident = [];
    private long streamAdmitted, streamEvicted, streamUpdates, streamNotReady;
    private double streamMs, streamSlowest;
    private int streamPending;

    private (int X, int Y, int Z) ChunkOf(Vector3 layoutPoint)
    {
        float chunk = (float)(TerrainConstants.ChunkEdgeLength * layout.VoxelSize);
        return ((int)MathF.Floor(layoutPoint.X / chunk), (int)MathF.Floor(layoutPoint.Y / chunk), (int)MathF.Floor(layoutPoint.Z / chunk));
    }

    private static bool Within((int X, int Y, int Z) c, (int X, int Y, int Z) at, int radius) =>
        Math.Max(Math.Abs(c.X - at.X), Math.Abs(c.Z - at.Z)) <= radius && Math.Abs(c.Y - at.Y) <= StreamRadiusUp;

    private static int Reach((int X, int Y, int Z) c, (int X, int Y, int Z) at) => Math.Abs(c.X - at.X) + (Math.Abs(c.Y - at.Y) * 2) + Math.Abs(c.Z - at.Z);

    /// <summary>The chunks holding any air, and every chunk touching one (their rock carries the air's surface).</summary>
    internal static HashSet<(int X, int Y, int Z)> Shell(DungeonVolume volume)
    {
        HashSet<(int X, int Y, int Z)> shell = [];
        int edge = TerrainConstants.ChunkEdgeLength;
        foreach ((int cx, int cy, int cz) in DungeonCollision.Chunks(volume))
        {
            bool air = false;
            for (int x = cx * edge; x < (cx + 1) * edge && !air; x++)
                for (int y = cy * edge; y < (cy + 1) * edge && !air; y++)
                    for (int z = cz * edge; z < (cz + 1) * edge && !air; z++)
                    {
                        air = volume.At(x, y, z) == BlockId.Air;
                    }

            if (!air)
            {
                continue;
            }

            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        (int X, int Y, int Z) n = (cx + dx, cy + dy, cz + dz);
                        if (n.X >= 0 && n.Y >= 0 && n.Z >= 0 && n.X < volume.ChunksX && n.Y < volume.ChunksY && n.Z < volume.ChunksZ)
                        {
                            shell.Add(n);
                        }
                    }
        }

        return shell;
    }

    /// <summary>The chunks the stream may ever admit (the shell), and how many the space holds in all.</summary>
    internal (int Shell, int All) StreamChunks => (shell.Count, layout.Volume.ChunksX * layout.Volume.ChunksY * layout.Volume.ChunksZ);

    /// <summary>Moves the stream one bounded step toward the player (layout metres): admits nearest-first, evicts behind.</summary>
    internal void Stream(Vector3 player)
    {
        if (StreamRadius == 0 || projection is null)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        streamUpdates++;
        (int X, int Y, int Z) at = ChunkOf(player);
        List<(int X, int Y, int Z)> wanted = [.. shell.Where(c => !resident.Contains(c) && Within(c, at, StreamRadius)).OrderBy(c => Reach(c, at))];
        streamPending = wanted.Count;
        // A deferred hand-off is live once the scene's collision has reached its revision (#10332).
        if (deferredPending.Count > 0)
        {
            ulong live = engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session)).CollisionRevision;
            foreach ((int X, int Y, int Z) chunk in deferredPending.Where(p => p.Value <= live).Select(p => p.Key).ToList())
            {
                deferredPending.Remove(chunk);
            }
        }

        static bool Round((int X, int Y, int Z) c, (int X, int Y, int Z) at) => Math.Abs(c.X - at.X) <= 1 && Math.Abs(c.Y - at.Y) <= 1 && Math.Abs(c.Z - at.Z) <= 1;
        bool installing = deferredPending.Keys.Any(c => Round(c, at));
        if (wanted.Any(c => Round(c, at)) || installing)
        {
            streamNotReady++;
            streamInstalling += installing ? 1 : 0;
        }

        // The nearest wanted chunks are prepared ahead on a worker; the update only hands prepared ones to the Engine, a
        // few at a time, until its time budget is spent. A chunk right round the player not yet prepared is prepared here.
        if (preparing is null || preparing.IsCompleted)
        {
            List<(int X, int Y, int Z)> next = [.. wanted.Where(c => !prepared.ContainsKey(c)).Take(PrepareAhead)];
            if (next.Count > 0)
            {
                DungeonVolume volume = layout.Volume;
                RockDensity? field = layout.Densities;
                bool weathered = DungeonSurfaces.Weathers(surface), pinned = !layout.Natural;
                double voxelSize = layout.VoxelSize;
                preparing = Task.Run(() =>
                {
                    foreach ((int X, int Y, int Z) chunk in next)
                    {
                        prepared.TryAdd(chunk, DungeonCollision.Prepare(volume, chunk, field, weathered, pinned, voxelSize));
                    }
                });
            }
        }

        // One hand-off a update, of as many prepared chunks (nearest first) as the time budget holds at the cost a chunk has
        // taken so far: the Engine meshes a chunk with its neighbours, so neighbours handed together are meshed once.
        streamPlanMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        bool urgent = wanted.Any(c => Round(c, at));
        bool due = urgent || Stopwatch.GetElapsedTime(lastHandOff).TotalSeconds >= StreamHandOffSeconds;
        // A batch with a chunk round the player is handed over synchronously, so the player never stands where the rock is not
        // yet live; the rest is deferred (#10332), its write all the update pays for, the rebuild on the Engine's worker.
        bool defer = StreamDeferred && !urgent;
        double perChunk = defer ? deferredApplyMs : chunkApplyMs;
        int room = due ? Math.Clamp((int)(StreamBudgetMs / Math.Max(0.05, perChunk)), 1, defer ? DeferredStreamBudget : StreamBudget) : 0;
        List<((int X, int Y, int Z) Chunk, (uint[] Materials, float[]? Densities) Payload)> batch = [];
        foreach ((int X, int Y, int Z) chunk in wanted)
        {
            if (batch.Count >= room)
            {
                break;
            }

            bool near = Math.Abs(chunk.X - at.X) <= 1 && Math.Abs(chunk.Y - at.Y) <= 1 && Math.Abs(chunk.Z - at.Z) <= 1;
            if (!prepared.TryRemove(chunk, out (uint[] Materials, float[]? Densities) payload))
            {
                if (!near)
                {
                    continue;
                }

                payload = DungeonCollision.Prepare(layout.Volume, chunk, layout.Densities, DungeonSurfaces.Weathers(surface), !layout.Natural, layout.VoxelSize);
                streamPreparedHere++;
            }

            batch.Add((chunk, payload));
        }

        // Chunks left behind go in the same transaction: the presentation then republishes only the chunks it touched.
        List<(int X, int Y, int Z)> leave = due ? [.. resident.Where(c => !Within(c, at, StreamRadius + 1))] : [];
        if (batch.Count > 0 || leave.Count > 0)
        {
            lastHandOff = Stopwatch.GetTimestamp();
            long applying = Stopwatch.GetTimestamp();
            VoxelResidencyReceipt? receipt = DungeonCollision.ApplyPrepared(engine, Session, batch, leave, layout.VoxelSize, defer);
            double applied = Stopwatch.GetElapsedTime(applying).TotalMilliseconds;
            streamApplyMs += applied;
            streamBatches++;
            streamDeferredBatches += defer ? 1 : 0;
            if (batch.Count > 0)
            {
                // The cost a chunk takes in each way, smoothed.
                if (defer)
                {
                    deferredApplyMs = (deferredApplyMs * 0.8) + (applied / batch.Count * 0.2);
                }
                else
                {
                    chunkApplyMs = (chunkApplyMs * 0.8) + (applied / batch.Count * 0.2);
                }
            }

            if (defer && receipt is VoxelResidencyReceipt handed)
            {
                foreach (((int X, int Y, int Z) chunk, _) in batch)
                {
                    deferredPending[chunk] = handed.AcceptedRevision;
                }
            }
            else
            {
                // A synchronous change installs every deferred one before it.
                deferredPending.Clear();
            }

            foreach (((int X, int Y, int Z) chunk, _) in batch)
            {
                resident.Add(chunk);
            }

            resident.ExceptWith(leave);
            streamAdmitted += batch.Count;
            streamEvicted += leave.Count;
        }

        // The presentation draws what was resident when it was made, or last refreshed: it follows the stream only when told.
        // Deferred chunks install at the start of a later update, so a deferring stream tells it every update (about 0.2 ms
        // with nothing to show, rusty-engine #10331).
        if (batch.Count > 0 || leave.Count > 0 || StreamDeferred)
        {
            long refreshing = Stopwatch.GetTimestamp();
            engine.VoxelScenePresentation.RefreshScene(projection);
            streamRefreshMs += Stopwatch.GetElapsedTime(refreshing).TotalMilliseconds;
            streamRefreshes++;
        }

        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        streamMs += ms;
        streamSlowest = Math.Max(streamSlowest, ms);
        if (streamTimes.Count < MostStreamTimes)
        {
            streamTimes.Add(ms);
        }
    }

    // Admission is prepared ahead (so many chunks a batch, on a worker) and handed to the Engine within a time budget each
    // update (at least one chunk), at most so many - a deferred hand-off's budget measuring only its write (#10332).
    private const int PrepareAhead = 24;
    private double chunkApplyMs = 1, deferredApplyMs = 0.5, streamPlanMs, streamApplyMs, streamRefreshMs;

    /// <summary>
    /// Whether streamed chunks away from the player are handed to the Engine deferred (#10332, rusty-engine #10327): their
    /// meshes and colliders rebuilt on its worker and installed at a later update's start. False hands every batch over
    /// synchronously, as before (for the A/B).
    /// </summary>
    internal static bool StreamDeferred { get; set; } = true;

    // The deferred chunks handed over whose collision is not live yet, each with the revision that makes it live; how many
    // hand-offs were deferred, and how many updates found one round the player still installing.
    private readonly Dictionary<(int X, int Y, int Z), ulong> deferredPending = [];
    private long streamDeferredBatches, streamInstalling;
    private long streamRefreshes, lastHandOff;

    /// <summary>Prototype knob: the least time between hand-offs (0 every update), sooner when a chunk round the player is missing.</summary>
    internal static double StreamHandOffSeconds { get; set; }
    private long streamBatches;
    private const double StreamBudgetMs = 3;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y, int Z), (uint[] Materials, float[]? Densities)> prepared = new();
    private Task? preparing;
    private long streamPreparedHere;
    private readonly List<double> streamTimes = [];
    private const int MostStreamTimes = 100_000;

    internal string StreamReadout() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"stream radius={StreamRadius} shell={shell.Count}/{StreamChunks.All} resident={resident.Count} pending={streamPending} admitted={streamAdmitted} evicted={streamEvicted} updates={streamUpdates} notReady={streamNotReady} installing={streamInstalling} deferred={streamDeferredBatches}/{streamBatches} preparedHere={streamPreparedHere} meanUpdate={streamMs / Math.Max(1, streamUpdates):F2}ms p99={Percentile(0.99):F1}ms slowest={streamSlowest:F1}ms growthMeshes={growths.Count} plan={streamPlanMs:F0}ms apply={streamApplyMs:F0}ms in {streamBatches} transactions ({streamApplyMs / Math.Max(1, streamAdmitted):F2}ms/chunk) refresh={streamRefreshMs / Math.Max(1, streamRefreshes):F2}ms x{streamRefreshes}");

    private double Percentile(double share)
    {
        if (streamTimes.Count == 0)
        {
            return 0;
        }

        double[] sorted = [.. streamTimes.OrderBy(t => t)];
        return sorted[Math.Min(sorted.Length - 1, (int)(share * sorted.Length))];
    }

    /// <summary>Clears the stream's measures, so a walk can be measured on its own.</summary>
    internal void ResetStreamMeasures()
    {
        streamAdmitted = streamEvicted = streamUpdates = streamNotReady = streamPreparedHere = streamInstalling = streamDeferredBatches = 0;
        streamMs = streamSlowest = streamPlanMs = streamApplyMs = streamRefreshMs = 0;
        streamBatches = streamRefreshes = 0;
        streamTimes.Clear();
    }


    internal SpatialSession Session { get; }

    internal DungeonLayout Layout => layout;

    /// <summary>How much of the dungeon is loaded, from 0 to 1.</summary>
    internal double Progress => totalChunks == 0 ? 1d : 1d - ((double)pending.Count / totalChunks);

    internal bool Loaded => pending.Count == 0 && projection is not null;

    /// <summary>Wall-clock time spent loading so far, in milliseconds.</summary>
    internal double LoadMilliseconds => Stopwatch.GetElapsedTime(0, loadTicks).TotalMilliseconds;

    internal int TotalChunks => totalChunks;

    /// <summary>What a sculpted dungeon's rock contributes to the appearance snapshot, as seen from an eye (session coordinates); nothing when its rock is voxels.</summary>
    internal IEnumerable<AppearanceFact> AppearanceFacts(Vector3 eye) =>
        [.. RockFacts, .. (kit?.Facts ?? []).Where(f => Near(f.Transform.Translation, eye)), .. (overgrowth?.Facts(eye) ?? []).Where(f => Near(f.Transform.Translation, eye)), .. growths.Take(MostGrowthMeshes).Select((growth, index) => (growth, index))
            .Where(g => g.growth.Region is not (int X, int Y, int Z) region || resident.Contains(region))
            .Select(g => new AppearanceFact(ProductIds.DungeonRockObject + GrowthObjects + (ulong)g.index, false, 0UL,
            new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), g.growth.Look, true, RenderLayer.Scene)),
            .. forest.Select((plant, index) => new AppearanceFact(ProductIds.DungeonRockObject + ForestObjects + (ulong)index, false, 0UL,
                plant.Place, plant.Look, true, RenderLayer.Scene))];

    // The growth meshes' object ids, after the forest's and before the kit's range (at most so many meshes).
    private const ulong GrowthObjects = 0x1000;
    private const int MostGrowthMeshes = 0x1000;

    /// <summary>Whether a detail (session metres) stands on rock that is resident now (always, when the whole space is loaded).</summary>
    private bool Near(Vector3 at, Vector3 eye) => StreamRadius == 0 || resident.Contains(ChunkOf(at - DungeonCollision.Origin));

    /// <summary>The forest growing into the space's openings (#10129): the world's flora looks (not this space's to dispose), each placed.</summary>
    private readonly List<(Appearance Look, Transform Place)> forest = [];

    /// <summary>Where the forest's object ids start past the rock's (at most <see cref="KarstCave"/>'s 400 plants, below the next range).</summary>
    private const ulong ForestObjects = 0xE00;

    private IEnumerable<AppearanceFact> RockFacts => rockAppearance is Appearance appearance
        ? [new AppearanceFact(ProductIds.DungeonRockObject, false, 0UL, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), appearance, true, RenderLayer.Scene),
            .. slabs.Skip(1).Select((slab, index) => new AppearanceFact(ProductIds.DungeonRockObject + 0x800 + (ulong)index, false, 0UL,
                new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), slab.Look, true, RenderLayer.Scene)),
            .. pieces.Select((piece, index) => new AppearanceFact(ProductIds.DungeonRockObject + 1 + (ulong)index, false, 0UL,
                new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), piece.Look, true, RenderLayer.Scene))]
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

        resident.UnionWith(admitted);

        DungeonCollision.Admit(engine, Session, layout.Volume, admitted, layout.Densities, DungeonSurfaces.Weathers(surface), pinned: !layout.Natural, voxelSize: layout.VoxelSize);

        if (pending.Count == 0 && projection is null)
        {
            // A voxel look's rock may be drawn in a study material (#10094); a mesh look's material is its mesh's.
            if (caveLayers)
            {
                terrain.ConfigureCaveLayers(Session);
            }

            projection = terrain.ProjectSeparateSpace(Session, DungeonSurfaces.RockAsMesh(surface) ? null : voxelRock);
            if (layout.Rock is RockDensity rock && DungeonSurfaces.RockAsMesh(surface))
            {
                BuildRock(rock);
            }

            foreach (((int X, int Y, int Z)? region, List<EcologyPlacement> part) in ByRegion(layout.Ecology, e => e.At))
            {
                foreach (MeshResourceCreateRequest request in CaveEcology.Meshes(engine, part, DungeonCollision.Origin, growthMaterials, sharedGrowthMaterials))
                {
                    MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                    growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh), region));
                }
            }

            if (layout.Waterfalls.Count > 0)
            {
                Waterfalls = new CaveWaterfalls(engine, layout.Waterfalls);
                // The sheets are published with the growths: an appearance draws only while a published fact names it.
                foreach (MeshResourceCreateRequest request in Waterfalls.SheetRequests())
                {
                    MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                    growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh), ((int X, int Y, int Z)?)null));
                }
            }

            if (layout.WaterZones.Any(z => z.Kind is WaterZoneKind.Lake or WaterZoneKind.Pool))
            {
                // Published with the growths: an appearance draws only while a published fact names it.
                waterSurfaces = new CaveWaterSurfaces(engine);
                foreach (MeshResourceCreateRequest request in waterSurfaces.Requests(layout.WaterZones, layout.Shape))
                {
                    MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                    growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh), ((int X, int Y, int Z)?)null));
                }
            }

            if (layout.Kit.Count > 0 && !DungeonSurfaces.RockAsMesh(surface))
            {
                // The kit stands where its building's voxels were taken out (DungeonKit): it draws and collides in their place.
                long kitStarted = Stopwatch.GetTimestamp();
                kit = new DungeonKitLook(engine, terrain.Content, layout.Kit, kitLook, kitPaint);
                double kitMeshMs = Stopwatch.GetElapsedTime(kitStarted).TotalMilliseconds;
                kit.Collide(Session);
                double kitCollideMs = Stopwatch.GetElapsedTime(kitStarted).TotalMilliseconds - kitMeshMs;
                RockReadout = string.Create(CultureInfo.InvariantCulture,
                    $"{RockReadout} kit={kit.Count} ({kit.Meshes} meshes; built {kitMeshMs:F0} ms, collided {kitCollideMs:F0} ms)");
            }

            if (layout.Overgrowth.Count > 0 && DungeonOvergrowth.Shown)
            {
                // The wall overgrowth (#10096): drawn only, nothing collides.
                long grownStarted = Stopwatch.GetTimestamp();
                overgrowth = new DungeonOvergrowth(engine, terrain.Content, layout.Overgrowth);
                // The vent trunks collide where nothing else holds the space's static collision (no kit, voxel rock).
                if (kit is null && !DungeonSurfaces.RockAsMesh(surface) && overgrowth.Collides)
                {
                    overgrowth.Collide(Session);
                }

                RockReadout = string.Create(CultureInfo.InvariantCulture,
                    $"{RockReadout} overgrowth={overgrowth.Count} ({overgrowth.Counts}; built {Stopwatch.GetElapsedTime(grownStarted).TotalMilliseconds:F0} ms)");
            }

            // The water marks (#10092): projected decals from the scene's shared atlas (set by SnowTracks), wet a little glossy.
            for (int i = 0; i < Math.Min(layout.Drips.Count, ProductIds.DungeonDecals); i++)
            {
                DungeonDrips.Mark mark = layout.Drips[i];
                engine.CameraView.SetDecal(new DecalRequest(ProductIds.DungeonDecalBase + (ulong)i, InSession(mark.Centre), mark.Turn, mark.Size, mark.Region,
                    new Color(1f, 1f, 1f, 1f), Roughness: DripRoughness, NormalStrength: 0f, AngleFalloff: true, OnVegetation: false, OnAnimatedMeshes: false,
                    LifeSeconds: 0f, FadeSeconds: 0f));
                drips++;
            }

            if (layout.MossCards.Count > 0)
            {
                // Published with the growths: an appearance draws only while a published fact names it.
                mossCards = new CaveMossCards(engine);
                foreach (((int X, int Y, int Z)? region, List<MossCard> part) in ByRegion(layout.MossCards, c => c.At))
                {
                    if (mossCards.Request(part) is MeshResourceCreateRequest request)
                    {
                        MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                        growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh), region));
                    }
                }
            }

            foreach (ForestPlacement plant in layout.Forest)
            {
                if (terrain.FloraLook(plant.Species, plant.Variant) is Appearance look)
                {
                    forest.Add((look, new Transform(InSession(plant.At), Quaternion.CreateFromAxisAngle(Vector3.UnitY, plant.YawDegrees * MathF.PI / 180f),
                        new Vector3(plant.Scale))));
                }
            }

            if (layout.Abyss is (Vector3 abyssAt, float abyssRadius))
            {
                MeshResource mesh = engine.Graphics.CreateMeshResource(AbyssFloor(InSession(abyssAt) - (Vector3.UnitY * AbyssDepth), abyssRadius * AbyssWiden));
                growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh), ((int X, int Y, int Z)?)null));
            }
        }

        loadTicks += Stopwatch.GetTimestamp() - started;
    }

    // A chasm's depths (#10094): what the eye meets looking down it is black, not the sky drawn behind the world - an unlit
    // black floor this far below the space, far wider than the opening so no slant past it finds the sky, out of any light's reach.
    private const float AbyssDepth = 25f, AbyssWiden = 8f;
    private const int AbyssSides = 24;

    private MeshResourceCreateRequest AbyssFloor(Vector3 centre, float radius)
    {
        Material black = engine.Graphics.CreateMaterial(new MaterialRequest(new Color(0f, 0f, 0f, 1f), default, 1f, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false));
        growthMaterials.Add(black);
        Vector3[] positions = new Vector3[AbyssSides + 1];
        positions[0] = centre;
        for (int i = 0; i < AbyssSides; i++)
        {
            float a = i * MathF.Tau / AbyssSides;
            positions[i + 1] = centre + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
        }

        uint[] indices = new uint[AbyssSides * 3];
        for (int i = 0; i < AbyssSides; i++)
        {
            // Facing up, toward an eye looking down the chasm.
            indices[(i * 3) + 0] = 0;
            indices[(i * 3) + 1] = (uint)(((i + 1) % AbyssSides) + 1);
            indices[(i * 3) + 2] = (uint)(i + 1);
        }

        return new MeshResourceCreateRequest(positions, Enumerable.Repeat(Vector3.UnitY, positions.Length).ToArray(), new Vector2[positions.Length], ReadOnlyMemory<Color>.Empty, indices,
            new[] { new MeshGroup(0, 0, (uint)indices.Length) }, new[] { new MeshMaterialBinding(0, black) });
    }

    /// <summary>
    /// Meshes the rock's density by dual contouring through the Engine's sampled volume, admits the
    /// mesh as the session's static collision, and keeps an appearance of it to draw.
    /// </summary>
    private void BuildRock(RockDensity rock)
    {
        long started = Stopwatch.GetTimestamp();
        ImplicitGenerationReadout generated;
        if (surface == DungeonSurface.Fine)
        {
            // The Engine meshes at most a few million samples at once: the finer field goes in slabs along z that share
            // their boundary plane of samples, so neighbouring slabs' surfaces meet.
            (int w, int h, int d, float[] values) = layout.Shape is { } shape ? RockRoutes.Resample(rock, shape) : RockRoutes.Refine(rock, StudySeed);
            float spacing = 1f / RockRoutes.FineSamplesPerMetre;
            int slabDepth = Math.Max(2, RockRoutes.MostSamplesPerMesh / (w * h));
            generated = default;
            for (int z0 = 0; z0 < d - 1; z0 += slabDepth - 1)
            {
                int depth = Math.Min(slabDepth, d - z0);
                float[] slab = values.AsSpan(z0 * w * h, depth * w * h).ToArray();
                MeshResource mesh = DungeonCollision.MeshRock(engine, w, h, depth, slab, spacing, rockMaterial!, out ImplicitGenerationReadout part,
                    new Vector3(0, 0, z0 * spacing));
                slabs.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
                generated = generated with
                {
                    Vertices = generated.Vertices + part.Vertices,
                    Triangles = generated.Triangles + part.Triangles,
                    BoundaryEdges = generated.BoundaryEdges + part.BoundaryEdges,
                };
            }

            rockMesh = slabs[0].Mesh;
        }
        else
        {
            rockMesh = DungeonCollision.MeshRock(engine, rock, rockMaterial!, out generated);
        }

        double meshMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        DungeonCollision.AdmitRock(engine, Session, slabs.Count > 0 ? [.. slabs.Select(slab => slab.Mesh)] : [rockMesh]);
        rockAppearance = slabs.Count > 0 ? slabs[0].Look : engine.Graphics.CreateMeshAppearance(rockMesh);
        double collisionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - meshMs;
        string piecesReadout = "";
        if (surface == DungeonSurface.Pieced)
        {
            long piecing = Stopwatch.GetTimestamp();
            int count = 0, triangles = 0;
            foreach (MeshResourceCreateRequest request in RockRoutes.Pieces(rock, StudySeed, rockMaterial!, DungeonCollision.Origin, out count))
            {
                MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                pieces.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
                triangles += request.Indices.Length / 3;
            }

            piecesReadout = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"; pieces: {count} in {pieces.Count} meshes, {triangles} triangles, built in {Stopwatch.GetElapsedTime(piecing).TotalMilliseconds:F0} ms");
        }

        RockReadout = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{surface} rock: {generated.Vertices} vertices, {generated.Triangles} triangles, {generated.BoundaryEdges} boundary edges, meshed in {meshMs:F0} ms, collision and appearance in {collisionMs:F0} ms") + piecesReadout;
    }

    /// <summary>A stable seed from the layout's name for the rock study's detail and pieces (string hashes vary per process).</summary>
    private ulong StudySeed
    {
        get
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in layout.Name) h = unchecked((h ^ c) * 1099511628211UL);
            return h;
        }
    }

    /// <summary>Re-meshes what the space draws after its voxels were edited.</summary>
    internal void Refresh()
    {
        if (projection is not null)
        {
            engine.VoxelScenePresentation.RefreshScene(projection);
        }
    }

    /// <summary>How rough a water mark leaves the surface it covers: wet stone a little glossy.</summary>
    private const float DripRoughness = 0.55f;

    private int drips;

    /// <summary>How many water marks are placed.</summary>
    internal int Drips => drips;

    public void Dispose()
    {
        for (int i = 0; i < drips; i++)
        {
            engine.CameraView.RemoveDecal(new DecalRemoval(ProductIds.DungeonDecalBase + (ulong)i));
        }

        drips = 0;
        foreach ((MeshResource mesh, Appearance look, (int X, int Y, int Z)? _) in growths)
        {
            look.Dispose();
            mesh.Dispose();
        }

        growths.Clear();
        // After the meshes drawn in its materials.
        Waterfalls?.Dispose();
        Waterfalls = null;
        waterSurfaces?.Dispose();
        waterSurfaces = null;
        mossCards?.Dispose();
        mossCards = null;
        kit?.Dispose();
        kit = null;
        overgrowth?.Dispose();
        overgrowth = null;
        foreach (Material material in growthMaterials)
        {
            material.Dispose();
        }

        growthMaterials.Clear();
        foreach ((MeshResource mesh, Appearance look) in pieces)
        {
            look.Dispose();
            mesh.Dispose();
        }

        pieces.Clear();
        foreach ((MeshResource mesh, Appearance look) in slabs.Skip(1))
        {
            look.Dispose();
            mesh.Dispose();
        }

        slabs.Clear();
        rockAppearance?.Dispose();
        rockAppearance = null;
        rockMesh?.Dispose();
        rockMesh = null;
        projection?.Dispose();
        projection = null;
        Session.Dispose();
    }
}
