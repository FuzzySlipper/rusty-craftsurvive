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
    private readonly List<(MeshResource Mesh, Appearance Look)> growths = [];

    /// <summary>A cave's lakes' and pools' surfaces (#10094): their material, while the space draws them.</summary>
    private CaveWaterSurfaces? waterSurfaces;

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
        DungeonSurface surface = DungeonSurface.Cubes, IReadOnlyDictionary<BlockId, Material>? voxelRock = null, bool caveLayers = false)
    {
        this.voxelRock = voxelRock;
        this.caveLayers = caveLayers;
        this.surface = surface;
        this.rockMaterial = rockMaterial;
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Session = DungeonCollision.CreateSession(engine, surface, layout.VoxelSize);
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
    internal IEnumerable<AppearanceFact> AppearanceFacts =>
        [.. RockFacts, .. growths.Select((growth, index) => new AppearanceFact(ProductIds.DungeonRockObject + 0xC00 + (ulong)index, false, 0UL,
            new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), growth.Look, true, RenderLayer.Scene))];

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

            foreach (MeshResourceCreateRequest request in CaveEcology.Meshes(engine, layout.Ecology, DungeonCollision.Origin, growthMaterials))
            {
                MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
            }

            if (layout.Waterfalls.Count > 0)
            {
                Waterfalls = new CaveWaterfalls(engine, layout.Waterfalls);
                // The sheets are published with the growths: an appearance draws only while a published fact names it.
                foreach (MeshResourceCreateRequest request in Waterfalls.SheetRequests())
                {
                    MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                    growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
                }
            }

            if (layout.WaterZones.Any(z => z.Kind is WaterZoneKind.Lake or WaterZoneKind.Pool))
            {
                // Published with the growths: an appearance draws only while a published fact names it.
                waterSurfaces = new CaveWaterSurfaces(engine);
                foreach (MeshResourceCreateRequest request in waterSurfaces.Requests(layout.WaterZones))
                {
                    MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                    growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
                }
            }

            if (layout.Abyss is (Vector3 abyssAt, float abyssRadius))
            {
                MeshResource mesh = engine.Graphics.CreateMeshResource(AbyssFloor(InSession(abyssAt) - (Vector3.UnitY * AbyssDepth), abyssRadius * AbyssWiden));
                growths.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
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

    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in growths)
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
