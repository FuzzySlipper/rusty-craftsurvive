using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The near water surfaces (#9877): the sea, rivers and lakes drawn as meshes at their own surfaces -
/// sloping with a river, at a lake's level - with the water block's Engine water look (depth colour,
/// foam at the shore, ripples). Tiles about the player are built from the water owner's samples, a few
/// an update, nearest first; a tile is retired when it falls behind, and disposed only after the next
/// snapshot - which no longer holds it - has been published (<see cref="AfterAppearanceSnapshot"/>). A cell is drawn where any corner is wet, and a dry corner takes its
/// wet neighbour's surface, so the plane runs on under the rising shore and the ground hides it there.
/// </summary>
internal sealed class WaterSurfaces : IDisposable
{
    /// <summary>A tile's edge and the spacing of its vertices (metres).</summary>
    internal const int TileMetres = 64, Step = 2;
    private const int Cells = TileMetres / Step;

    /// <summary>Tiles built per update, so a walk into new water never stalls an update.</summary>
    private const int BuildsPerUpdate = 3;

    private const float Roughness = 0.1f;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly WaterModel model;
    private readonly Dictionary<(long X, long Z), Tile?> tiles = [];
    private readonly List<Tile> retiring = [];
    private readonly HashSet<ulong> usedIds = [];
    private RenderResource? foam, ripples;
    private Material? material;
    private int built, retired;

    private sealed record Tile(ulong Id, MeshResource Mesh, Appearance Look, Vector3 Corner, int Quads);

    internal WaterSurfaces(IEngineContext engine, WorldFrame frame, WaterModel model)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>
    /// The square of tiles the near water covers: those within this many tiles (Chebyshev) of the tile
    /// holding the centre, enough to reach past the far field's sunk band (<paramref name="outer"/>, metres).
    /// The far field draws no water inside the same square (<see cref="Covers"/>), so the two meet edge to
    /// edge at the same level.
    /// </summary>
    internal static int TileRadius(double outer) => Math.Max(1, (int)Math.Ceiling((outer - (TileMetres / 2.0)) / TileMetres));

    /// <summary>Whether the near water's square about a centre covers a point (the rule the far field leaves its water out by).</summary>
    internal static bool Covers((double X, double Z) centre, double outer, double x, double z)
    {
        int radius = TileRadius(outer);
        long cx = (long)Math.Floor(centre.X / TileMetres), cz = (long)Math.Floor(centre.Z / TileMetres);
        return Math.Abs((long)Math.Floor(x / TileMetres) - cx) <= radius && Math.Abs((long)Math.Floor(z / TileMetres) - cz) <= radius;
    }

    private double outerMetres;

    /// <summary>The drawn tiles, for the appearance snapshot.</summary>
    internal AppearanceFact[] Facts => [.. tiles.Values.OfType<Tile>().Select(tile =>
        new AppearanceFact(tile.Id, false, 0, new Transform(frame.ToLocal(tile.Corner.X, tile.Corner.Y, tile.Corner.Z), Quaternion.Identity, Vector3.One),
            tile.Look, true, RenderLayer.Scene, ShadowCasting.None))];

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"waterTiles={tiles.Count} drawn={tiles.Values.Count(t => t is not null)} quads={tiles.Values.OfType<Tile>().Sum(t => t.Quads)} built={built} retired={retired} outer={outerMetres:F0} radiusTiles={TileRadius(outerMetres)}");

    /// <summary>
    /// Keeps the square of tiles about a centre (the far field's sink centre, so both agree): retires those
    /// outside it, then builds a few of the nearest missing ones.
    /// </summary>
    internal void Follow((double X, double Z) centre, double outer)
    {
        outerMetres = outer;
        int radius = TileRadius(outer);
        long cx = (long)Math.Floor(centre.X / TileMetres), cz = (long)Math.Floor(centre.Z / TileMetres);
        bool Inside((long X, long Z) key) => Math.Abs(key.X - cx) <= radius && Math.Abs(key.Z - cz) <= radius;
        foreach ((long X, long Z) key in tiles.Keys.Where(key => !Inside(key)).ToArray())
        {
            if (tiles[key] is Tile tile) retiring.Add(tile);
            tiles.Remove(key);
        }

        IEnumerable<(long X, long Z)> missing = Enumerable.Range(-radius, (2 * radius) + 1)
            .SelectMany(dz => Enumerable.Range(-radius, (2 * radius) + 1).Select(dx => (X: cx + dx, Z: cz + dz)))
            .Where(key => !tiles.ContainsKey(key))
            .OrderBy(key => Distance(key, centre.X, centre.Z)).ThenBy(key => key.X).ThenBy(key => key.Z)
            .Take(BuildsPerUpdate);
        foreach ((long X, long Z) key in missing.ToArray()) tiles[key] = Build(key);
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
            if (tile is not null) retiring.Add(tile);
        tiles.Clear();
    }

    public void Dispose()
    {
        foreach (Tile? tile in tiles.Values)
            if (tile is not null) Dispose(tile);
        foreach (Tile tile in retiring) Dispose(tile);
        tiles.Clear();
        retiring.Clear();
        material?.Dispose();
        foam?.Dispose();
        ripples?.Dispose();
        material = null;
        foam = ripples = null;
    }

    private static double Distance((long X, long Z) key, double x, double z)
    {
        double nearestX = Math.Clamp(x, key.X * TileMetres, (key.X + 1) * TileMetres), nearestZ = Math.Clamp(z, key.Z * TileMetres, (key.Z + 1) * TileMetres);
        return Math.Sqrt(((x - nearestX) * (x - nearestX)) + ((z - nearestZ) * (z - nearestZ)));
    }

    /// <summary>One tile's water as a mesh, or null where it holds none.</summary>
    private Tile? Build((long X, long Z) key)
    {
        long x0 = key.X * TileMetres, z0 = key.Z * TileMetres;
        const int side = Cells + 1;
        WaterSample[] samples = new WaterSample[side * side];
        bool anyWet = false;
        for (int j = 0; j < side; j++)
        for (int i = 0; i < side; i++)
        {
            WaterSample sample = model.Sample(x0 + (i * Step), z0 + (j * Step));
            samples[(j * side) + i] = sample;
            anyWet |= sample.Wet;
        }

        if (!anyWet) return null;

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
        Color white = new(1, 1, 1, 1);
        uint Vertex(int i, int j)
        {
            int v = (j * side) + i;
            if (index.TryGetValue(v, out uint at)) return at;
            at = (uint)positions.Count;
            index[v] = at;
            positions.Add(new(i * Step, (float)top[v], j * Step));
            normals.Add(Vector3.UnitY);
            uvs.Add(new((x0 + (i * Step)) / (float)TileMetres, (z0 + (j * Step)) / (float)TileMetres));
            colours.Add(white);
            return at;
        }

        for (int j = 0; j < Cells; j++)
        for (int i = 0; i < Cells; i++)
        {
            int a = (j * side) + i, b = a + 1, c = a + side, d = c + 1;
            if (!(samples[a].Wet || samples[b].Wet || samples[c].Wet || samples[d].Wet)) continue;
            if (!(near[a] && near[b] && near[c] && near[d])) continue;
            uint va = Vertex(i, j), vb = Vertex(i + 1, j), vc = Vertex(i, j + 1), vd = Vertex(i + 1, j + 1);
            indices.AddRange([va, vd, vb, va, vc, vd]);
        }

        if (indices.Count == 0) return null;
        foam ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFoamContentPath);
        ripples ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterRipplesContentPath);
        material ??= engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), Roughness, white, Vector3.Zero, 0f, false)
            with { AlphaMode = MaterialAlphaMode.Blend, Water = TerrainAtlasCatalog.WaterLook(foam, ripples) });
        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(),
            colours.ToArray(), indices.ToArray(), new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) }));
        built++;
        return new(NextId(), mesh, engine.Graphics.CreateMeshAppearance(mesh), new(x0, 0, z0), indices.Count / 6);
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
