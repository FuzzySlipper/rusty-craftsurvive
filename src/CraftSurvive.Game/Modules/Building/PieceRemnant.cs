using System.Numerics;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>A blast's bite out of a piece: a sphere in world metres.</summary>
internal readonly record struct Crater(Vector3 Centre, float Radius);

/// <summary>
/// What is left of a piece a charge has reached (#9731, Den <c>decision-building-hybrid-pieces</c>):
/// the piece's own shape with every crater taken out of it. It is described, not stored as voxels,
/// so it regenerates exactly from its save; the construction session draws it sampled on the fine
/// grid, and its collision is the boxes its solid cells merge into.
/// </summary>
internal sealed record PieceRemnant(PlacedPiece Piece, IReadOnlyList<Crater> Craters)
{
    /// <summary>How many craters a remnant keeps; one more and it falls to pieces.</summary>
    internal const int MaximumCraters = 8;

    /// <summary>The fine grid a remnant is drawn and collides on: a quarter metre, as the study's crispest look.</summary>
    internal const float VoxelMetres = 0.25f;

    /// <summary>A remnant with less than this share of its piece's cells left falls to pieces.</summary>
    internal const float CollapseShare = 0.15f;

    /// <summary>
    /// Boards thinner than the grid (a 0.2 m wall, a 0.15 m roof) are drawn at least this half
    /// thickness, so a row of cell centres always lies inside them (as the building study thickened
    /// its pieces): a broken wall reads about 0.3 m thick.
    /// </summary>
    internal const float MinimumHalf = 0.15f;

    /// <summary>Signed distance from a world point to the piece as the fine grid draws it (before craters).</summary>
    internal float PieceDistance(Vector3 point) => PieceGeometry.Distance(Piece, point, MinimumHalf);

    /// <summary>Signed distance (metres, negative inside) from a world point to what is left.</summary>
    internal float Distance(Vector3 point)
    {
        float distance = PieceDistance(point);
        foreach (Crater crater in Craters)
        {
            distance = MathF.Max(distance, crater.Radius - Vector3.Distance(point, crater.Centre));
        }

        return distance;
    }

    internal PieceRemnant WithCrater(Crater crater) => this with { Craters = [.. Craters, crater] };

    /// <summary>The world bounds of the piece (what is left lies inside them).</summary>
    internal (Vector3 Low, Vector3 High) Bounds()
    {
        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
        foreach ((Vector3 centre, Vector3 half) in PieceGeometry.Bounds(Piece))
        {
            low = Vector3.Min(low, centre - half - new Vector3(MinimumHalf));
            high = Vector3.Max(high, centre + half + new Vector3(MinimumHalf));
        }

        return (low, high);
    }

    /// <summary>The fine-grid cells (inclusive lows, exclusive highs) the piece's bounds cover.</summary>
    internal (long X0, long Y0, long Z0, long X1, long Y1, long Z1) Cells()
    {
        (Vector3 low, Vector3 high) = Bounds();
        return ((long)MathF.Floor(low.X / VoxelMetres), (long)MathF.Floor(low.Y / VoxelMetres), (long)MathF.Floor(low.Z / VoxelMetres),
            (long)MathF.Ceiling(high.X / VoxelMetres), (long)MathF.Ceiling(high.Y / VoxelMetres), (long)MathF.Ceiling(high.Z / VoxelMetres));
    }

    /// <summary>Whether the fine-grid cell's centre lies in what is left.</summary>
    internal bool Solid(long x, long y, long z) => Distance(Centre(x, y, z)) < 0;

    internal static Vector3 Centre(long x, long y, long z) =>
        new((x + 0.5f) * VoxelMetres, (y + 0.5f) * VoxelMetres, (z + 0.5f) * VoxelMetres);

    /// <summary>What share of the piece's own cells is left (1 before any crater).</summary>
    internal float ShareLeft()
    {
        (long x0, long y0, long z0, long x1, long y1, long z1) = Cells();
        int whole = 0, left = 0;
        for (long x = x0; x < x1; x++)
        {
            for (long y = y0; y < y1; y++)
            {
                for (long z = z0; z < z1; z++)
                {
                    Vector3 centre = Centre(x, y, z);
                    if (PieceDistance(centre) >= 0) continue;
                    whole++;
                    if (Distance(centre) < 0) left++;
                }
            }
        }

        return whole == 0 ? 0f : left / (float)whole;
    }

    /// <summary>
    /// What is left as world-axis boxes, for the character step and the aim: its solid fine-grid
    /// cells merged greedily, first along X, then Z, then Y, so a cratered wall is a few dozen boxes.
    /// </summary>
    internal List<(Vector3 Centre, Vector3 Half)> Boxes()
    {
        (long x0, long y0, long z0, long x1, long y1, long z1) = Cells();
        int sx = (int)(x1 - x0), sy = (int)(y1 - y0), sz = (int)(z1 - z0);
        bool[,,] solid = new bool[sx, sy, sz];
        for (int x = 0; x < sx; x++)
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    solid[x, y, z] = Solid(x0 + x, y0 + y, z0 + z);

        List<(Vector3, Vector3)> boxes = [];
        for (int y = 0; y < sy; y++)
        {
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    if (!solid[x, y, z]) continue;
                    int w = 1;
                    while (x + w < sx && solid[x + w, y, z]) w++;
                    int d = 1;
                    while (z + d < sz && Row(solid, x, w, y, z + d)) d++;
                    int h = 1;
                    while (y + h < sy && Slab(solid, x, w, y + h, z, d)) h++;
                    for (int cy = y; cy < y + h; cy++)
                        for (int cz = z; cz < z + d; cz++)
                            for (int cx = x; cx < x + w; cx++)
                                solid[cx, cy, cz] = false;

                    Vector3 low = new((x0 + x) * VoxelMetres, (y0 + y) * VoxelMetres, (z0 + z) * VoxelMetres);
                    Vector3 size = new(w * VoxelMetres, h * VoxelMetres, d * VoxelMetres);
                    boxes.Add((low + (size / 2), size / 2));
                }
            }
        }

        return boxes;
    }

    private static bool Row(bool[,,] solid, int x, int w, int y, int z)
    {
        for (int cx = x; cx < x + w; cx++)
        {
            if (!solid[cx, y, z]) return false;
        }

        return true;
    }

    private static bool Slab(bool[,,] solid, int x, int w, int y, int z, int d)
    {
        for (int cz = z; cz < z + d; cz++)
        {
            if (!Row(solid, x, w, y, cz)) return false;
        }

        return true;
    }
}
