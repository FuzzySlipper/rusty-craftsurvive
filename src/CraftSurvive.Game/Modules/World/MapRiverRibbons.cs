using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>How one tier draws its rivers: centreline points kept this far apart, and no narrower than this half width (metres).</summary>
internal readonly record struct RiverRibbonShape(double SpacingMetres, double MinimumHalfWidthMetres);

/// <summary>
/// Rivers on the map as ribbons along their centrelines (#9948), so they read as smooth bands at any
/// tier rather than the stair-steps of painted voxel cells. Each tier's ribbons are kept in square
/// tiles, one mesh a tile, so a tier rebuilds only the tiles whose ground or ownership changed. A
/// segment belongs to the tile its midpoint is in, so neighbouring tiles meet without a gap or an overlap.
/// Retired tiles are disposed once a snapshot without them has been published.
/// </summary>
internal sealed class MapRiverRibbons : IDisposable
{
    private const uint MaterialSlot = 0;
    /// <summary>Passes of a [1 2 1] average over the kept points, easing the corners a lattice's flow directions leave.</summary>
    private const int SmoothingPasses = 2;

    private readonly IEngineContext engine;
    private readonly Material material;
    private readonly Dictionary<(int Tier, long X, long Z), (int Slot, MeshResource Mesh, Appearance Look)> tiles = [];
    private readonly List<(int Slot, MeshResource Mesh, Appearance Look)> retired = [];
    private readonly Stack<int> freeSlots = new(Enumerable.Range(0, ProductIds.WorldMapRiverLimit).Reverse());

    internal MapRiverRibbons(IEngineContext engine, Material material)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.material = material ?? throw new ArgumentNullException(nameof(material));
    }

    /// <summary>How many tiles hold a ribbon now.</summary>
    internal int Count => tiles.Count;

    internal IEnumerable<AppearanceFact> Facts => tiles.Values.Select(tile => new AppearanceFact(ProductIds.WorldMapRiverBase + (ulong)tile.Slot, false, 0,
        new(Vector3.Zero, Quaternion.Identity, Vector3.One), tile.Look, true, RenderLayer.Scene));

    /// <summary>
    /// Rebuilds a tier's ribbons in the tiles <paramref name="rebuild"/> names: the segments of
    /// <paramref name="reaches"/> whose midpoints lie in each, where <paramref name="owns"/> holds. Every
    /// named tile is replaced, and one with no segment left is cleared.
    /// </summary>
    /// <param name="place">Where a ribbon vertex is drawn, in map units, for a world point and the river's surface there.</param>
    internal void Rebuild(int tier, double tileMetres, IEnumerable<RiverPoint[]> reaches, RiverRibbonShape shape,
        Func<double, double, bool> owns, Func<double, double, double, Vector3> place, IReadOnlySet<(long X, long Z)>? rebuild)
    {
        Dictionary<(long X, long Z), (List<Vector3> Positions, List<uint> Indices)> built = [];
        foreach (RiverPoint[] reach in reaches)
        {
            RiverPoint[] points = Thinned(reach, shape.SpacingMetres);
            for (int i = 0; i + 1 < points.Length; i++)
            {
                double midX = (points[i].X + points[i + 1].X) / 2, midZ = (points[i].Z + points[i + 1].Z) / 2;
                (long X, long Z) tile = ((long)Math.Floor(midX / tileMetres), (long)Math.Floor(midZ / tileMetres));
                if ((rebuild is not null && !rebuild.Contains(tile)) || !owns(midX, midZ)) continue;
                if (!built.TryGetValue(tile, out var mesh)) built[tile] = mesh = ([], []);
                (Vector3 a, Vector3 b) = Across(points, i, shape, place);
                (Vector3 c, Vector3 d) = Across(points, i + 1, shape, place);
                uint first = (uint)mesh.Positions.Count;
                mesh.Positions.AddRange([a, b, c, d]);
                Facing(mesh.Indices, mesh.Positions, first, first + 1, first + 2);
                Facing(mesh.Indices, mesh.Positions, first + 1, first + 3, first + 2);
            }
        }

        IEnumerable<(long X, long Z)> named = rebuild ?? (IEnumerable<(long X, long Z)>)[.. tiles.Keys.Where(key => key.Tier == tier).Select(key => (key.X, key.Z))];
        foreach ((long X, long Z) tile in named.Concat(built.Keys).Distinct())
        {
            Retire((tier, tile.X, tile.Z));
            if (built.TryGetValue(tile, out var mesh) && mesh.Indices.Count > 0 && freeSlots.Count > 0)
                tiles[(tier, tile.X, tile.Z)] = Create(mesh.Positions, mesh.Indices);
        }
    }

    /// <summary>Disposes the tiles retired before the snapshot just published, which no longer names them.</summary>
    internal void AfterSnapshot()
    {
        foreach ((int slot, MeshResource mesh, Appearance look) in retired)
        {
            look.Dispose();
            mesh.Dispose();
            freeSlots.Push(slot);
        }

        retired.Clear();
    }

    public void Dispose()
    {
        foreach ((int _, MeshResource mesh, Appearance look) in tiles.Values.Concat(retired))
        {
            look.Dispose();
            mesh.Dispose();
        }

        tiles.Clear();
        retired.Clear();
    }

    private void Retire((int Tier, long X, long Z) key)
    {
        if (!tiles.Remove(key, out var tile)) return;
        retired.Add(tile);
    }

    private (int Slot, MeshResource Mesh, Appearance Look) Create(List<Vector3> positions, List<uint> indices)
    {
        int slot = freeSlots.Pop();
        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(),
            Enumerable.Repeat(Vector3.UnitY, positions.Count).ToArray(), new Vector2[positions.Count],
            Enumerable.Repeat(new Color(1, 1, 1, 1), positions.Count).ToArray(), indices.ToArray(),
            new MeshGroup[] { new(MaterialSlot, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(MaterialSlot, material) }));
        return (slot, mesh, engine.Graphics.CreateMeshAppearance(mesh));
    }

    /// <summary>The ribbon's two edges at a centreline point, square to the river's run there.</summary>
    private static (Vector3 Left, Vector3 Right) Across(RiverPoint[] points, int i, RiverRibbonShape shape, Func<double, double, double, Vector3> place)
    {
        RiverPoint before = points[Math.Max(0, i - 1)], after = points[Math.Min(points.Length - 1, i + 1)], at = points[i];
        double runX = after.X - before.X, runZ = after.Z - before.Z, run = Math.Max(Math.Sqrt((runX * runX) + (runZ * runZ)), double.Epsilon);
        double half = Math.Max(at.HalfWidth, shape.MinimumHalfWidthMetres);
        double sideX = -runZ / run * half, sideZ = runX / run * half;
        return (place(at.X + sideX, at.Z + sideZ, at.Surface), place(at.X - sideX, at.Z - sideZ, at.Surface));
    }

    /// <summary>Adds a triangle wound to face up.</summary>
    private static void Facing(List<uint> indices, List<Vector3> positions, uint a, uint b, uint c)
    {
        bool up = Vector3.Cross(positions[(int)b] - positions[(int)a], positions[(int)c] - positions[(int)a]).Y >= 0;
        indices.AddRange(up ? [a, b, c] : [a, c, b]);
    }

    /// <summary>
    /// A centreline drawn at a tier's spacing: each run of points this long becomes one, their mean, so a
    /// meander finer than the tier's cells smooths away rather than zigzagging between kept points. The
    /// ends stay where they are, so reaches still meet at confluences and mouths.
    /// </summary>
    private static RiverPoint[] Thinned(RiverPoint[] reach, double spacing)
    {
        if (reach.Length <= 2) return reach;
        List<RiverPoint> kept = [reach[0]];
        double travelled = 0, x = 0, z = 0, surface = double.MaxValue, half = 0, depth = 0;
        int count = 0;
        for (int i = 1; i < reach.Length - 1; i++)
        {
            travelled += Math.Sqrt(Math.Pow(reach[i].X - reach[i - 1].X, 2) + Math.Pow(reach[i].Z - reach[i - 1].Z, 2));
            x += reach[i].X;
            z += reach[i].Z;
            // The run's lowest surface, so the water never stands above any of it.
            surface = Math.Min(surface, reach[i].Surface);
            half = Math.Max(half, reach[i].HalfWidth);
            depth = Math.Max(depth, reach[i].Depth);
            count++;
            if (travelled < spacing) continue;
            kept.Add(new(x / count, z / count, surface, half, depth));
            (travelled, x, z, surface, half, depth, count) = (0, 0, 0, double.MaxValue, 0, 0, 0);
        }

        if (count > 0 && travelled > spacing / 2) kept.Add(new(x / count, z / count, surface, half, depth));
        kept.Add(reach[^1]);
        for (int pass = 0; pass < SmoothingPasses; pass++)
        {
            RiverPoint[] was = [.. kept];
            for (int i = 1; i < was.Length - 1; i++)
                kept[i] = was[i] with { X = (was[i - 1].X + (2 * was[i].X) + was[i + 1].X) / 4, Z = (was[i - 1].Z + (2 * was[i].Z) + was[i + 1].Z) / 4 };
        }

        return [.. kept];
    }
}
