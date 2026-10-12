using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The water look study (#9819): one lake, filled to a level over a hollow in the generated ground,
/// drawn either way water could be made, so the two can be judged in place.
/// <list type="bullet">
/// <item>voxel: water cells in the walking world, as rivers and the sea are today. Its surface stands
/// on a whole metre (the top of the highest cell under the level).</item>
/// <item>surface: one flat mesh at the level itself, drawn with the water block's own Engine water
/// look, with the ground hiding it past the shore.</item>
/// </list>
/// Both flood the same columns: those connected to the start whose generated ground lies under the
/// level, within a radius. Study only: the surface does not swim, and neither is saved as a lake
/// (voxel water is an ordinary edit, cleared again by <see cref="Clear"/>).
/// </summary>
internal sealed class WaterStudy : IDisposable
{
    /// <summary>The mesh reaches this many cells past the flooded columns, under the rising shore, so no gap shows at the waterline.</summary>
    private const int ShoreOverlapCells = 2;

    /// <summary>The most columns one flood may cover (a 160 m disc), so a study cannot run away across a plain.</summary>
    private const int MaximumColumns = 80_000;

    private const float Roughness = 0.1f;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly List<(MeshResource Mesh, Appearance Look)> retired = [];
    private RenderResource? foam, ripples;
    private Material? material;
    private (MeshResource Mesh, Appearance Look)? shown;
    private Vector3 meshAt;
    private readonly List<VoxelAddress> placed = [];
    private string built = "none";

    internal WaterStudy(IEngineContext engine, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    /// <summary>The flooded columns: each cell (x, z) whose ground lies under the level, and whether the flood reached its radius (an open basin).</summary>
    internal sealed record Flood(HashSet<(long X, long Z)> Columns, bool Spilled);

    /// <summary>The surface's draw fact, for the snapshot; empty unless a surface lake is shown.</summary>
    internal AppearanceFact[] Facts => shown is { } lake
        ? [new(ProductIds.WaterStudyObject, false, 0, new(frame.ToLocal(meshAt.X, meshAt.Y, meshAt.Z), Quaternion.Identity, Vector3.One), lake.Look, true, RenderLayer.Scene)]
        : [];

    /// <summary>Columns under the level connected to (x, z) within the radius, by the generated ground's continuous height.</summary>
    internal static Flood FloodFrom(long x, long z, double level, long radius, Func<long, long, double> groundAt)
    {
        HashSet<(long X, long Z)> columns = [];
        Queue<(long X, long Z)> open = new();
        bool spilled = false;
        if (groundAt(x, z) >= level)
        {
            return new(columns, false);
        }

        columns.Add((x, z));
        open.Enqueue((x, z));
        ReadOnlySpan<(int X, int Z)> steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (open.Count > 0 && columns.Count < MaximumColumns)
        {
            (long cx, long cz) = open.Dequeue();
            foreach ((int dx, int dz) in steps)
            {
                (long X, long Z) next = (cx + dx, cz + dz);
                if (columns.Contains(next) || groundAt(next.X, next.Z) >= level)
                {
                    continue;
                }

                long ox = next.X - x, oz = next.Z - z;
                if ((ox * ox) + (oz * oz) > radius * radius)
                {
                    spilled = true;
                    continue;
                }

                columns.Add(next);
                open.Enqueue(next);
            }
        }

        return new(columns, spilled || open.Count > 0);
    }

    /// <summary>A closed hollow: its lowest column, the level it spills at, and the columns it holds at that level.</summary>
    internal readonly record struct Hollow(long X, long Z, double Floor, double Spill, int Columns);

    /// <summary>Sample spacing when looking for low points, and how finely a hollow's spill level is found.</summary>
    private const int FindStep = 3;
    private const double SpillPrecision = 0.1d;

    /// <summary>
    /// The deepest closed hollows within a distance of (x, z): low points of the generated ground,
    /// each filled to the highest level that stays within the radius (its spill level).
    /// </summary>
    internal static IReadOnlyList<Hollow> FindHollows(long x, long z, long distance, long radius, int count, Func<long, long, double> groundAt)
    {
        List<(long X, long Z, double Height)> lows = [];
        for (long sz = z - distance; sz <= z + distance; sz += FindStep)
        {
            for (long sx = x - distance; sx <= x + distance; sx += FindStep)
            {
                double h = groundAt(sx, sz);
                bool lowest = true;
                for (int dz = -1; dz <= 1 && lowest; dz++)
                {
                    for (int dx = -1; dx <= 1 && lowest; dx++)
                    {
                        lowest = (dx == 0 && dz == 0) || groundAt(sx + (dx * FindStep), sz + (dz * FindStep)) > h;
                    }
                }

                if (lowest)
                {
                    lows.Add((sx, sz, h));
                }
            }
        }

        List<Hollow> hollows = [];
        foreach ((long lx, long lz, double floor) in lows)
        {
            // The spill level: the highest that still floods a closed basin, found by halving.
            double low = floor + SpillPrecision, high = floor + 30d;
            if (FloodFrom(lx, lz, low, radius, groundAt).Spilled)
            {
                continue;
            }

            while (high - low > SpillPrecision)
            {
                double mid = (low + high) / 2d;
                if (FloodFrom(lx, lz, mid, radius, groundAt).Spilled) high = mid;
                else low = mid;
            }

            Flood held = FloodFrom(lx, lz, low, radius, groundAt);
            if (hollows.Any(h => held.Columns.Contains((h.X, h.Z))))
            {
                continue;
            }

            hollows.Add(new(lx, lz, floor, low, held.Columns.Count));
        }

        return [.. hollows.OrderByDescending(h => (h.Spill - h.Floor) * Math.Sqrt(h.Columns)).Take(count)];
    }

    /// <summary>Shows the flood as one flat water mesh at the level, replacing any earlier surface.</summary>
    internal string ShowSurface(long x, long z, double level, Flood flood)
    {
        HideSurface();
        if (flood.Columns.Count == 0)
        {
            return built = "surface none: the ground there is above the level";
        }

        Dictionary<(long X, long Z), float> cells = [];
        foreach ((long cx, long cz) in flood.Columns)
        {
            for (int dz = -ShoreOverlapCells; dz <= ShoreOverlapCells; dz++)
            {
                for (int dx = -ShoreOverlapCells; dx <= ShoreOverlapCells; dx++)
                {
                    cells[(cx + dx, cz + dz)] = (float)level;
                }
            }
        }

        int quads = Show(x, z, cells);
        return built = string.Create(CultureInfo.InvariantCulture,
            $"surface at {level:F2} m: {flood.Columns.Count} columns ({flood.Columns.Count / 10_000d:F2} ha), {quads} quads{(flood.Spilled ? ", spills past the radius" : "")}");
    }

    /// <summary>
    /// Shows a river's water as a skin over its channel at the surface the map gives each column (not
    /// floored to a cell), reaching under the banks; <paramref name="surfaces"/> holds the channel's columns.
    /// </summary>
    internal string ShowSkin(long x, long z, IReadOnlyDictionary<(long X, long Z), double> surfaces)
    {
        HideSurface();
        Dictionary<(long X, long Z), float> cells = surfaces.ToDictionary(pair => pair.Key, pair => (float)pair.Value);
        foreach (((long cx, long cz), double height) in surfaces)
        {
            for (int dz = -ShoreOverlapCells; dz <= ShoreOverlapCells; dz++)
            {
                for (int dx = -ShoreOverlapCells; dx <= ShoreOverlapCells; dx++)
                {
                    cells.TryAdd((cx + dx, cz + dz), (float)height);
                }
            }
        }

        int quads = Show(x, z, cells);
        return built = string.Create(CultureInfo.InvariantCulture, $"river skin over {surfaces.Count} channel columns, {quads} quads");
    }

    /// <summary>One water mesh over cells, each flat at its own height (world metres), merged along rows where heights agree.</summary>
    private int Show(long x, long z, IReadOnlyDictionary<(long X, long Z), float> cells)
    {
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Vector2> uvs = [];
        List<Color> colours = [];
        List<uint> indices = [];
        Color white = new(1f, 1f, 1f, 1f);
        foreach (IGrouping<long, (long X, long Z)> row in cells.Keys.GroupBy(cell => cell.Z))
        {
            long[] xs = [.. row.Select(cell => cell.X).Order()];
            int start = 0;
            for (int i = 1; i <= xs.Length; i++)
            {
                if (i < xs.Length && xs[i] == xs[i - 1] + 1 && cells[(xs[i], row.Key)] == cells[(xs[start], row.Key)])
                {
                    continue;
                }

                float y = cells[(xs[start], row.Key)];
                float x0 = xs[start] - x, x1 = xs[i - 1] + 1 - x, z0 = row.Key - z, z1 = row.Key + 1 - z;
                uint first = (uint)positions.Count;
                positions.AddRange([new(x0, y, z0), new(x1, y, z0), new(x1, y, z1), new(x0, y, z1)]);
                normals.AddRange([Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY]);
                uvs.AddRange([new(x0, z0), new(x1, z0), new(x1, z1), new(x0, z1)]);
                colours.AddRange([white, white, white, white]);
                indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
                start = i;
            }
        }

        foam ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFoamContentPath);
        ripples ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterRipplesContentPath);
        material ??= engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), Roughness,
            white, Vector3.Zero, 0f, false) with { AlphaMode = MaterialAlphaMode.Blend, Water = TerrainAtlasCatalog.WaterLook(foam, ripples) });
        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(),
            uvs.ToArray(), colours.ToArray(), indices.ToArray(), new MeshGroup[] { new(0, 0, (uint)indices.Count) },
            new MeshMaterialBinding[] { new(0, material) }));
        shown = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
        meshAt = new(x, 0f, z);
        return indices.Count / 6;
    }

    /// <summary>The voxel cells the flood fills: over each column's surface, up to the cell under the level.</summary>
    internal static List<VoxelAddress> VoxelCells(double level, Flood flood, Func<long, long, long> surfaceAt)
    {
        long top = (long)Math.Floor(level) - 1;
        List<VoxelAddress> cells = [];
        foreach ((long cx, long cz) in flood.Columns)
        {
            for (long y = surfaceAt(cx, cz) + 1; y <= top; y++)
            {
                cells.Add(new VoxelAddress(cx, y, cz));
            }
        }

        return cells;
    }

    /// <summary>Records voxel cells a flood placed, so they can be cleared again.</summary>
    internal void Placed(IEnumerable<VoxelAddress> cells, string summary)
    {
        placed.AddRange(cells);
        built = summary;
    }

    /// <summary>The voxel cells placed so far, which a clear removes.</summary>
    internal IReadOnlyList<VoxelAddress> PlacedCells => placed;

    internal void ForgetPlaced() => placed.Clear();

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"waterStudy surface={(shown is not null)} voxelCells={placed.Count} {built}");

    /// <summary>Hides the surface; the mesh is kept until disposal, as a published appearance cannot be destroyed at once.</summary>
    internal void HideSurface()
    {
        if (shown is { } lake)
        {
            retired.Add(lake);
        }

        shown = null;
    }

    public void Dispose()
    {
        HideSurface();
        foreach ((MeshResource mesh, Appearance look) in retired)
        {
            look.Dispose();
            mesh.Dispose();
        }

        retired.Clear();
        material?.Dispose();
        foam?.Dispose();
        ripples?.Dispose();
    }
}
