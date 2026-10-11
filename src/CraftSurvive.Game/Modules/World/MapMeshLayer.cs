using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One resolution of the map's ground as heightfield grid meshes (#9839): the same chunk columns and
/// sampler as <see cref="MapVoxelLayer"/>, but each sampled cell is a grid vertex at its true height,
/// coloured by its material's paint, with normals from the height field. Columns are sampled and kept;
/// tiles of <see cref="TileColumns"/>² columns are meshed from them once none they read waits to be
/// sampled, so a tile's edges and normals see its neighbours' samples and the seams meet exactly. A
/// short skirt hangs from each tile's edges, hiding a crack where the next tier shows beneath.
/// </summary>
internal sealed class MapMeshLayer : IMapLayer
{
    private const int EdgeLength = MapVoxelLayer.EdgeLength;
    private const int ColumnCount = MapVoxelLayer.ColumnCount;
    /// <summary>Chunk columns along a tile's side: 64 cells, 65² vertices.</summary>
    internal const int TileColumns = 4;
    private const int TileCells = TileColumns * EdgeLength;
    /// <summary>A skirt hangs this many cells' height below its edge.</summary>
    private const float SkirtCells = 1f;
    private const uint MaterialSlot = 0;

    private sealed record Column(double[] Surface, uint[] Material);

    private sealed record Tile(MeshResource Mesh, Appearance Appearance, ulong Id, Vector3 Origin, int Vertices, int Indices);

    private readonly IEngineContext engine;
    private readonly MapVoxelLayer.ColumnSampler sampler;
    private readonly Func<uint, Color> paint;
    private readonly Material material;
    private readonly double cellSize;
    private readonly ulong firstId;
    private readonly int idLimit;
    private readonly double sampleBudgetMilliseconds;
    private readonly Dictionary<(long X, long Z), Column> columns = [];
    private readonly HashSet<(long X, long Z)> wanted = [];
    private readonly HashSet<(long X, long Z)> unsampled = [];
    private readonly HashSet<(long X, long Z)> stale = [];
    private readonly HashSet<(long X, long Z)> dirty = [];
    private readonly Dictionary<(long X, long Z), Tile> tiles = [];
    private readonly Stack<ulong> freeIds = [];
    private readonly List<Tile> retired = [];
    private ulong nextId;
    private long workTicks;

    /// <param name="cellSize">A cell's width in drawn units; sampled surfaces are in cells, so heights scale by it too.</param>
    /// <param name="paint">A sampled material slot's vertex colour.</param>
    /// <param name="material">The material every tile draws with; the vertex colour tints it.</param>
    /// <param name="firstId">The first appearance id the tiles use, and <paramref name="idLimit"/> how many there are.</param>
    /// <param name="sampleBudgetMilliseconds">As <see cref="MapVoxelLayer"/>'s: zero samples at once, more defers sampling to <see cref="Advance"/>.</param>
    internal MapMeshLayer(IEngineContext engine, double cellSize, MapVoxelLayer.ColumnSampler sampler, Func<uint, Color> paint, Material material,
        ulong firstId, int idLimit, double sampleBudgetMilliseconds = 0)
    {
        this.engine = engine;
        this.cellSize = cellSize;
        this.sampler = sampler;
        this.paint = paint;
        this.material = material;
        this.firstId = firstId;
        this.idLimit = idLimit;
        this.sampleBudgetMilliseconds = sampleBudgetMilliseconds;
    }

    public (long X, long Z) Focus { get; set; }
    public int ResidentChunks => columns.Count;
    public int PendingChunks => unsampled.Count + stale.Count + dirty.Count;
    public bool Settled => wanted.Count > 0 && unsampled.Count == 0 && stale.Count == 0 && dirty.Count == 0;
    public double WorkMilliseconds => Stopwatch.GetElapsedTime(0, workTicks).TotalMilliseconds;

    /// <summary>Meshes drawn, and their vertices and indices, for the readout.</summary>
    internal (int Tiles, long Vertices, long Indices) Geometry() =>
        (tiles.Count, tiles.Values.Sum(tile => (long)tile.Vertices), tiles.Values.Sum(tile => (long)tile.Indices));

    /// <summary>The tiles drawn, in <paramref name="layer"/>, for the appearance snapshot.</summary>
    internal IEnumerable<AppearanceFact> Facts(RenderLayer layer) => tiles.Values.Select(tile => new AppearanceFact(tile.Id, false, 0,
        new Transform(tile.Origin, Quaternion.Identity, Vector3.One), tile.Appearance, true, layer, ShadowCasting.None));

    public ulong Digest()
    {
        ulong hash = 0xCBF29CE484222325UL;
        void Mix(ulong value) => hash = unchecked((hash ^ value) * 0x100000001B3UL);
        foreach (((long X, long Z) key, Column column) in columns.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Z))
        {
            Mix((ulong)key.X);
            Mix((ulong)key.Z);
            for (int i = 0; i < ColumnCount; i++)
            {
                Mix(BitConverter.SingleToUInt32Bits((float)column.Surface[i]));
                Mix(column.Material[i]);
            }
        }

        return hash;
    }

    public void Want(IEnumerable<(long X, long Z)> chunkColumns)
    {
        HashSet<(long X, long Z)> next = [.. chunkColumns];
        foreach ((long X, long Z) gone in wanted.Where(column => !next.Contains(column)).ToArray())
        {
            wanted.Remove(gone);
            unsampled.Remove(gone);
            stale.Remove(gone);
            if (columns.Remove(gone)) Touch(gone);
        }

        foreach ((long X, long Z) added in next.Where(column => !wanted.Contains(column)).ToArray())
        {
            wanted.Add(added);
            if (sampleBudgetMilliseconds > 0) unsampled.Add(added);
            else Sample(added);
        }
    }

    public void Invalidate(IEnumerable<(long X, long Z)> chunkColumns)
    {
        foreach ((long X, long Z) key in chunkColumns.Distinct())
        {
            if (!wanted.Contains(key) || unsampled.Contains(key)) continue;
            if (sampleBudgetMilliseconds > 0) stale.Add(key);
            else Sample(key);
        }
    }

    /// <summary>
    /// Sample waiting columns (changed ones first, then new ones, nearest the focus first) within the budget;
    /// then mesh up to <paramref name="budget"/> tiles whose columns are all sampled, nearest first.
    /// </summary>
    public void Advance(int budget)
    {
        long started = Stopwatch.GetTimestamp();
        // Tiles replaced last update are no longer in the published snapshot.
        foreach (Tile old in retired) Release(old);
        retired.Clear();
        while ((stale.Count > 0 || unsampled.Count > 0) && Stopwatch.GetElapsedTime(started).TotalMilliseconds < sampleBudgetMilliseconds)
        {
            HashSet<(long X, long Z)> from = stale.Count > 0 ? stale : unsampled;
            (long X, long Z) next = from.MinBy(key => Math.Max(Math.Abs(key.X - Focus.X), Math.Abs(key.Z - Focus.Z)));
            from.Remove(next);
            Sample(next);
        }

        (long X, long Z) focusTile = (GridMath.FloorDivide(Focus.X, TileColumns), GridMath.FloorDivide(Focus.Z, TileColumns));
        foreach ((long X, long Z) tile in dirty.Where(Ready)
            .OrderBy(tile => Math.Max(Math.Abs(tile.X - focusTile.X), Math.Abs(tile.Z - focusTile.Z))).Take(budget).ToArray())
        {
            dirty.Remove(tile);
            Build(tile);
        }

        workTicks += Stopwatch.GetTimestamp() - started;
    }

    public void Dispose()
    {
        foreach (Tile tile in tiles.Values.Concat(retired)) Release(tile);
        tiles.Clear();
        retired.Clear();
    }

    private void Sample((long X, long Z) key)
    {
        double[] surface = new double[ColumnCount];
        uint[] materials = new uint[ColumnCount];
        sampler(key.X, key.Z, surface, materials);
        columns[key] = new(surface, materials);
        Touch(key);
    }

    /// <summary>A column's samples are read by its own tile and, across an edge or a normal, by the tiles beside it.</summary>
    private void Touch((long X, long Z) column)
    {
        for (long dz = -1; dz <= 1; dz++)
        for (long dx = -1; dx <= 1; dx++)
            dirty.Add((GridMath.FloorDivide(column.X + dx, TileColumns), GridMath.FloorDivide(column.Z + dz, TileColumns)));
    }

    /// <summary>Whether no column a tile reads (its own and the ring about them) still waits to be sampled.</summary>
    private bool Ready((long X, long Z) tile)
    {
        for (long z = (tile.Z * TileColumns) - 1; z <= (tile.Z + 1) * TileColumns; z++)
        for (long x = (tile.X * TileColumns) - 1; x <= (tile.X + 1) * TileColumns; x++)
        {
            if (unsampled.Contains((x, z)) || stale.Contains((x, z))) return false;
        }

        return true;
    }

    /// <summary>A cell's surface (in cells) and material by global cell index; NaN where no column is sampled or the map ends.</summary>
    private (double Surface, uint Material) Cell(long cellX, long cellZ)
    {
        if (!columns.TryGetValue((GridMath.FloorDivide(cellX, EdgeLength), GridMath.FloorDivide(cellZ, EdgeLength)), out Column? column))
            return (double.NaN, 0);
        int i = ((int)(cellZ - (GridMath.FloorDivide(cellZ, EdgeLength) * EdgeLength)) * EdgeLength)
            + (int)(cellX - (GridMath.FloorDivide(cellX, EdgeLength) * EdgeLength));
        return (column.Surface[i], column.Material[i]);
    }

    private void Build((long X, long Z) key)
    {
        long cellX0 = key.X * TileCells, cellZ0 = key.Z * TileCells;
        float cell = (float)cellSize;
        HeightfieldTile? built = HeightfieldTile.Build(cellX0, cellZ0, TileCells, cell, SkirtCells, Cell, paint);
        Tile? previous = tiles.GetValueOrDefault(key);
        if (built is null)
        {
            if (previous is not null)
            {
                tiles.Remove(key);
                retired.Add(previous);
            }

            return;
        }

        MeshResource mesh = engine.Graphics.CreateMeshResource(new(built.Positions, built.Normals, built.Uvs, built.Colors, built.Indices,
            new MeshGroup[] { new(MaterialSlot, 0, (uint)built.Indices.Length) }, new MeshMaterialBinding[] { new(MaterialSlot, material) }));
        Appearance appearance;
        try { appearance = engine.Graphics.CreateMeshAppearance(mesh); }
        catch { mesh.Dispose(); throw; }
        ulong id;
        if (previous is not null)
        {
            id = previous.Id;
            retired.Add(previous with { Id = ulong.MaxValue });
        }
        else if (!freeIds.TryPop(out id))
        {
            if (nextId >= (ulong)idLimit)
            {
                appearance.Dispose();
                mesh.Dispose();
                return;
            }

            id = firstId + nextId++;
        }

        tiles[key] = new(mesh, appearance, id, new Vector3(cellX0 * cell, 0, cellZ0 * cell), built.Positions.Length, built.Indices.Length);
    }

    private void Release(Tile tile)
    {
        tile.Appearance.Dispose();
        tile.Mesh.Dispose();
        // A tile replaced in place keeps its id; one removed gives it back.
        if (tile.Id != ulong.MaxValue && !tiles.Values.Any(live => live.Id == tile.Id)) freeIds.Push(tile.Id);
    }
}
