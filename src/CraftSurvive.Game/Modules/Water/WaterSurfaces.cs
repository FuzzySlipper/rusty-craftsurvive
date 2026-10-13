using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The near water surfaces (#9877): the sea, rivers and lakes drawn as meshes at their own surfaces -
/// sloping with a river, at a lake's level - with the water block's Engine water look (depth colour,
/// foam at the shore, ripples). Tiles about the player are built from the water owner's samples, a few
/// an update, nearest first; a tile is retired when it falls behind, and disposed only after the next
/// snapshot - which no longer holds it - has been published (<see cref="AfterAppearanceSnapshot"/>). A cell is drawn where any corner is wet, and a dry corner takes its
/// wet neighbour's surface, so the plane runs on under the rising shore and the ground hides it there.
/// When the river stage moves on (#9878, <see cref="Refresh"/>), the tiles that hold a river or lake are
/// sampled again, a few an update, and rebuilt in place only where the water changed; a frozen cell is
/// drawn as ice, and the ice is also kept as triangles the walking session collides with (<see cref="Ice"/>).
/// </summary>
internal sealed class WaterSurfaces : IDisposable
{
    /// <summary>A tile's edge and the spacing of its vertices (metres).</summary>
    internal const int TileMetres = NearWaterSquare.TileMetres, Step = WaterWaveSettings.MeshSpacingMetres;
    private const int Cells = TileMetres / Step;

    /// <summary>Tiles built per update, so a walk into new water never stalls an update.</summary>
    private const int BuildsPerUpdate = 3;

    /// <summary>Tiles sampled again per update after the stage moved on; only a changed one is rebuilt.</summary>
    private const int RefreshesPerUpdate = 4;

    /// <summary>The ice's look: a pale, cold blue (linear), matte, opaque; the far field's ice is the same (#9878).</summary>
    internal static readonly Color IceColour = new(0.62f, 0.72f, 0.8f, 1);
    internal const float IceRoughness = 0.45f;

    /// <summary>Two water tops closer than this (metres) draw the same.</summary>
    private const double TopResolution = 0.02;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly WaterModel model;
    private readonly Dictionary<(long X, long Z), Tile?> tiles = [];
    /// <summary>Each tile's water as it was last sampled, and the stage revision it was sampled at.</summary>
    private readonly Dictionary<(long X, long Z), (ulong Signature, bool Staged, long Revision, Vector2[] Flow)> sampled = [];
    private readonly List<Tile> retiring = [];
    private readonly HashSet<ulong> usedIds = [];
    private WaterVisualMaterial? visual;
    private Material? iceMaterial;
    private readonly WaterFlowStudy flowStudy;
    private readonly SeaState sea = new();
    private int built, retired, refreshed;
    private long revision;

    /// <summary>A drawn tile; its ice, if any, as triangles over vertices relative to the corner.</summary>
    private sealed record Tile(ulong Id, MeshResource Mesh, Appearance Look, Vector3 Corner, int Quads, int IceQuads, Vector3[] IceVertices, Triangle[] IceTriangles);

    internal WaterSurfaces(IEngineContext engine, WorldFrame frame, WaterModel model)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        flowStudy = new(engine, frame, model);
    }

    /// <summary>The square of tiles the near water covers about a centre (<see cref="NearWaterSquare"/>).</summary>
    internal static int TileRadius(double outer) => NearWaterSquare.TileRadius(outer);

    private double outerMetres;
    private Vector4 waveBounds;

    /// <summary>The drawn tiles, for the appearance snapshot.</summary>
    internal AppearanceFact[] Facts => [.. tiles.Values.OfType<Tile>().Select(tile =>
        new AppearanceFact(tile.Id, false, 0, new Transform(frame.ToLocal(tile.Corner.X, tile.Corner.Y, tile.Corner.Z), Quaternion.Identity, Vector3.One),
            tile.Look, true, RenderLayer.Scene, ShadowCasting.None))];

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"waterTiles={tiles.Count} drawn={tiles.Values.Count(t => t is not null)} quads={tiles.Values.OfType<Tile>().Sum(t => t.Quads)} iceQuads={tiles.Values.OfType<Tile>().Sum(t => t.IceQuads)} built={built} refreshed={refreshed} retired={retired} stale={sampled.Values.Count(s => s.Staged && s.Revision != revision)} outer={outerMetres:F0} radiusTiles={TileRadius(outerMetres)} visual={visual?.Mode ?? "waves"} sea=[{sea.Readout()}] {flowStudy.Readout()}");

    internal string SetVisual(string mode) => (visual ??= new(engine, frame, waveBounds)).SetMode(mode);

    /// <summary>The waves over the near water (#9902): what the shader draws and boats and swimmers ride.</summary>
    internal SeaState Sea => sea;

    /// <summary>The near water's bounds (world min x, min z, max x, max z), inside which the waves fade to the flat distant water.</summary>
    internal Vector4 WaveBounds => waveBounds;

    /// <summary>Hands the drawn water the sea state as it stands this update.</summary>
    internal void FollowSea() => visual?.Follow(sea);

    /// <summary>
    /// How far the drawn surface stands over the water's rest level at a point now, given the point's sample:
    /// none where the drawn water does not wave (a comparison without waves, or beyond the near water).
    /// </summary>
    internal double Lift(double x, double z, WaterSample sample) =>
        visual is { Displaces: false } ? 0 : sea.Lift(x, z, model.Exposure(sample), SeaWaves.EdgeFade(x, z, waveBounds));

    internal string StartFlow(SpatialSession session, double x, double z)
    {
        MarkFlowTiles();
        string result = flowStudy.Start(session, x, z);
        MarkFlowTiles();
        return result;
    }

    internal string StopFlow()
    {
        MarkFlowTiles();
        flowStudy.Dispose();
        return flowStudy.Readout();
    }

    internal string FlowAt(double x, double z) => flowStudy.At(x, z);

    internal void UpdateFlow(long step)
    {
        if (flowStudy.Update(step)) MarkFlowTiles();
    }

    private void MarkFlowTiles()
    {
        foreach (var key in sampled.Keys.Where(key => flowStudy.Overlaps(key.X * TileMetres, key.Z * TileMetres, TileMetres)).ToArray())
            sampled[key] = sampled[key] with { Staged = true, Revision = -1 };
    }

    /// <summary>Moves on whenever the drawn ice changes, so the walking session's collision can follow.</summary>
    internal long IceRevision { get; private set; }

    /// <summary>The drawn ice as world-placed patches: each tile's corner and its triangles over vertices relative to it.</summary>
    internal IEnumerable<(Vector3 Corner, Vector3[] Vertices, Triangle[] Triangles)> Ice =>
        tiles.Values.OfType<Tile>().Where(tile => tile.IceTriangles.Length > 0).Select(tile => (tile.Corner, tile.IceVertices, tile.IceTriangles));

    /// <summary>The water's stage moved on: tiles holding a river or lake are sampled again as the player's updates allow.</summary>
    internal void Refresh() => revision++;

    /// <summary>
    /// Keeps the square of tiles about a centre (the far field's sink centre, so both agree): retires those
    /// outside it, then builds a few of the nearest missing ones.
    /// </summary>
    internal void Follow((double X, double Z) centre, double outer)
    {
        outerMetres = outer;
        int radius = TileRadius(outer);
        long cx = (long)Math.Floor(centre.X / TileMetres), cz = (long)Math.Floor(centre.Z / TileMetres);
        waveBounds = new((cx - radius) * TileMetres, (cz - radius) * TileMetres,
            (cx + radius + 1) * TileMetres, (cz + radius + 1) * TileMetres);
        visual?.SetBounds(waveBounds);
        bool Inside((long X, long Z) key) => Math.Abs(key.X - cx) <= radius && Math.Abs(key.Z - cz) <= radius;
        foreach ((long X, long Z) key in tiles.Keys.Where(key => !Inside(key)).ToArray())
        {
            if (tiles[key] is Tile tile) Retire(tile);
            tiles.Remove(key);
            sampled.Remove(key);
        }

        IEnumerable<(long X, long Z)> missing = Enumerable.Range(-radius, (2 * radius) + 1)
            .SelectMany(dz => Enumerable.Range(-radius, (2 * radius) + 1).Select(dx => (X: cx + dx, Z: cz + dz)))
            .Where(key => !tiles.ContainsKey(key))
            .OrderBy(key => Distance(key, centre.X, centre.Z)).ThenBy(key => key.X).ThenBy(key => key.Z)
            .Take(BuildsPerUpdate);
        (long X, long Z)[] building = missing.ToArray();
        foreach ((long X, long Z) key in building) Place(key, Sample(key));
        if (building.Length > 0) return;

        // Nothing new to build: sample again the nearest tiles the stage has moved on under.
        IEnumerable<(long X, long Z)> stale = sampled.Where(pair => pair.Value.Staged && pair.Value.Revision != revision).Select(pair => pair.Key)
            .OrderBy(key => Distance(key, centre.X, centre.Z)).ThenBy(key => key.X).ThenBy(key => key.Z)
            .Take(RefreshesPerUpdate);
        foreach ((long X, long Z) key in stale.ToArray())
        {
            TileSamples samples = Sample(key);
            if (samples.Signature == sampled[key].Signature && samples.Flow.AsSpan().SequenceEqual(sampled[key].Flow))
            { sampled[key] = sampled[key] with { Revision = revision }; continue; }
            if (tiles[key] is Tile old) Retire(old);
            Place(key, samples);
            refreshed++;
        }
    }

    private void Place((long X, long Z) key, TileSamples samples)
    {
        Tile? tile = Build(key, samples);
        tiles[key] = tile;
        sampled[key] = (samples.Signature, samples.Staged, revision, samples.Flow);
        if (tile is { IceTriangles.Length: > 0 }) IceRevision++;
    }

    private void Retire(Tile tile)
    {
        retiring.Add(tile);
        if (tile.IceTriangles.Length > 0) IceRevision++;
    }

    /// <summary>
    /// Called once a snapshot has been published: it held none of the retired tiles (they left <see cref="Facts"/>
    /// when retired), so they may go now. A tile is never disposed while a published snapshot still draws it.
    /// </summary>
    internal void AfterAppearanceSnapshot()
    {
        foreach (Tile tile in retiring) Dispose(tile);
        retiring.Clear();
    }

    /// <summary>Drops every tile (the ground changed under them, or the world did); they are rebuilt as the player moves.</summary>
    internal void Invalidate()
    {
        foreach (Tile? tile in tiles.Values)
            if (tile is not null) Retire(tile);
        tiles.Clear();
        sampled.Clear();
    }

    public void Dispose()
    {
        foreach (Tile? tile in tiles.Values)
            if (tile is not null) Dispose(tile);
        foreach (Tile tile in retiring) Dispose(tile);
        tiles.Clear();
        sampled.Clear();
        retiring.Clear();
        flowStudy.Dispose();
        visual?.Dispose();
        iceMaterial?.Dispose();
        visual = null;
        iceMaterial = null;
    }

    private static double Distance((long X, long Z) key, double x, double z)
    {
        double nearestX = Math.Clamp(x, key.X * TileMetres, (key.X + 1) * TileMetres), nearestZ = Math.Clamp(z, key.Z * TileMetres, (key.Z + 1) * TileMetres);
        return Math.Sqrt(((x - nearestX) * (x - nearestX)) + ((z - nearestZ) * (z - nearestZ)));
    }

    /// <summary>
    /// A tile's water samples; whether any is a river or lake (which the stage can change), and a signature
    /// of what would be drawn - each wet vertex's top to a resolution, and its ice.
    /// </summary>
    private readonly record struct TileSamples(WaterSample[] Samples, Vector2[] Flow, bool AnyWet, bool Staged, ulong Signature);

    private const int Side = Cells + 1;

    private TileSamples Sample((long X, long Z) key)
    {
        long x0 = key.X * TileMetres, z0 = key.Z * TileMetres;
        WaterSample[] samples = new WaterSample[Side * Side];
        Vector2[] flow = new Vector2[samples.Length];
        bool anyWet = false, staged = false;
        ulong signature = 14695981039346656037UL;
        for (int j = 0; j < Side; j++)
        for (int i = 0; i < Side; i++)
        {
            WaterSample sample = model.Sample(x0 + (i * Step), z0 + (j * Step));
            samples[(j * Side) + i] = sample;
            flow[(j * Side) + i] = sample.Ice ? Vector2.Zero : new((float)sample.CurrentX, (float)sample.CurrentZ);
            anyWet |= sample.Wet;
            staged |= sample.Body is WaterBody.River or WaterBody.Lake;
            long drawn = sample.Wet ? (long)Math.Round(sample.Top / TopResolution) * 2 + (sample.Ice ? 1 : 0) : long.MinValue;
            signature = (signature ^ (ulong)drawn) * 1099511628211UL;
        }

        flowStudy.Apply(x0, z0, Side, Step, flow);
        // Frozen cells must remain still even when the neighbouring study field is wet.
        for (int i = 0; i < samples.Length; i++) if (samples[i].Ice) flow[i] = Vector2.Zero;
        return new(samples, flow, anyWet, staged || flowStudy.Overlaps(x0, z0, TileMetres), signature);
    }

    /// <summary>One tile's water as a mesh, or null where it holds none.</summary>
    private Tile? Build((long X, long Z) key, TileSamples sampledTile)
    {
        long x0 = key.X * TileMetres, z0 = key.Z * TileMetres;
        const int side = Side;
        WaterSample[] samples = sampledTile.Samples;
        if (!sampledTile.AnyWet) return null;

        // A dry vertex takes the highest wet neighbour's surface, so a shore cell stays level with its water.
        double[] top = new double[side * side];
        bool[] near = new bool[side * side];
        for (int j = 0; j < side; j++)
        for (int i = 0; i < side; i++)
        {
            int v = (j * side) + i;
            if (samples[v].Wet) { top[v] = samples[v].Top; near[v] = true; continue; }
            double best = double.NegativeInfinity;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if (ni < 0 || nj < 0 || ni >= side || nj >= side || !samples[(nj * side) + ni].Wet) continue;
                best = Math.Max(best, samples[(nj * side) + ni].Top);
            }

            near[v] = !double.IsNegativeInfinity(best);
            top[v] = best;
        }

        Dictionary<int, uint> index = [];
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Vector2> uvs = [];
        List<Color> colours = [];
        List<uint> indices = [];
        List<uint> iceIndices = [];
        Color white = new(1, 1, 1, 1);
        uint Vertex(int i, int j)
        {
            int v = (j * side) + i;
            if (index.TryGetValue(v, out uint at)) return at;
            at = (uint)positions.Count;
            index[v] = at;
            positions.Add(new(i * Step, (float)top[v], j * Step));
            // The mesh normal carries what the waves (#9902) make of the point whatever the wind: its fetch
            // in kilometres and its sea swell share. The shader lights the water by the displaced triangles.
            WaveExposure exposure = model.Exposure(samples[v]);
            normals.Add(new((float)(exposure.FetchMetres / MetresPerKilometre), 1, (float)exposure.Swell));
            // Engine WATER textures use world coordinates, leaving mesh UV free for the current.
            uvs.Add(sampledTile.Flow[v]);
            // Alpha is shore coverage: it fades both translucency and visual displacement. Dry
            // corners under the bank stay pinned; adjacent tiles evaluate the same world column.
            colours.Add(white with { A = (float)exposure.Coverage });
            return at;
        }

        for (int j = 0; j < Cells; j++)
        for (int i = 0; i < Cells; i++)
        {
            int a = (j * side) + i, b = a + 1, c = a + side, d = c + 1;
            if (!(samples[a].Wet || samples[b].Wet || samples[c].Wet || samples[d].Wet)) continue;
            if (!(near[a] && near[b] && near[c] && near[d])) continue;
            uint va = Vertex(i, j), vb = Vertex(i + 1, j), vc = Vertex(i, j + 1), vd = Vertex(i + 1, j + 1);
            bool ice = samples[a].Ice || samples[b].Ice || samples[c].Ice || samples[d].Ice;
            (ice ? iceIndices : indices).AddRange([va, vd, vb, va, vc, vd]);
        }

        if (indices.Count + iceIndices.Count == 0) return null;
        visual ??= new(engine, frame, waveBounds);
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        if (indices.Count > 0) { groups.Add(new(WaterSlot, 0, (uint)indices.Count)); bindings.Add(new(WaterSlot, visual.Material)); }
        Vector3[] iceVertices = [];
        Triangle[] iceTriangles = [];
        if (iceIndices.Count > 0)
        {
            iceMaterial ??= engine.Graphics.CreateMaterial(new MaterialRequest(IceColour, default(RenderResourceReference), IceRoughness, white, Vector3.Zero, 0f, false));
            groups.Add(new(IceSlot, (uint)indices.Count, (uint)iceIndices.Count));
            bindings.Add(new(IceSlot, iceMaterial));
            (iceVertices, iceTriangles) = Collision(positions, iceIndices);
        }

        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(),
            colours.ToArray(), indices.Concat(iceIndices).ToArray(), groups.ToArray(), bindings.ToArray()));
        built++;
        return new(NextId(), mesh, engine.Graphics.CreateMeshAppearance(mesh), new(x0, 0, z0), (indices.Count + iceIndices.Count) / 6, iceIndices.Count / 6, iceVertices, iceTriangles);
    }

    private const ushort WaterSlot = 0, IceSlot = 1;
    private const double MetresPerKilometre = 1000;

    /// <summary>The ice's triangles over just the vertices they use, wound as drawn, facing up.</summary>
    private static (Vector3[] Vertices, Triangle[] Triangles) Collision(List<Vector3> positions, List<uint> ice)
    {
        Dictionary<uint, uint> remap = [];
        List<Vector3> vertices = [];
        uint At(uint v)
        {
            if (remap.TryGetValue(v, out uint at)) return at;
            remap[v] = at = (uint)vertices.Count;
            vertices.Add(positions[(int)v]);
            return at;
        }

        Triangle[] triangles = new Triangle[ice.Count / 3];
        for (int t = 0; t < triangles.Length; t++) triangles[t] = new Triangle(At(ice[3 * t]), At(ice[(3 * t) + 1]), At(ice[(3 * t) + 2]));
        return ([.. vertices], triangles);
    }

    private ulong NextId()
    {
        for (int k = 0; k < ProductIds.WaterSurfaceLimit; k++)
        {
            ulong id = ProductIds.WaterSurfaceBase + (ulong)((built + k) % ProductIds.WaterSurfaceLimit);
            if (usedIds.Add(id)) return id;
        }

        throw new InvalidOperationException("Every water surface id is in use.");
    }

    private void Dispose(Tile tile)
    {
        tile.Look.Dispose();
        tile.Mesh.Dispose();
        usedIds.Remove(tile.Id);
        retired++;
    }
}
