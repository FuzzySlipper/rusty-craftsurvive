using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// Moss that follows the stone (#10640). A tuft is a patch of the mats atlas cut from a piece's own triangles and lifted a
/// little off them, so it bends round a corner where a flat card would hang past it. <see cref="Fits"/> is the same question
/// for the flat cards a place keeps: whether the stone lies under the whole of one. One of these serves a kit piece, in
/// its own metres, with its triangles binned so a question reads only those nearby.
/// </summary>
internal sealed class StoneTufts
{
    /// <summary>The bins' size in metres: about a tuft across.</summary>
    private const float BinMetres = 0.5f;
    /// <summary>A tuft's width.</summary>
    private const float SmallestWidth = 0.35f, LargestWidth = 0.65f;
    /// <summary>How far a tuft lies off the stone, spread so overlapping tufts never fight for depth.</summary>
    private const float Lift = 0.012f, LiftSpread = 0.02f;
    /// <summary>A tuft takes the triangles facing at least this far along its own normal: round a corner, not onto the far side.</summary>
    private const float LeastFacing = 0f;
    /// <summary>A square's corner is this share of its width from its centre, rounded up.</summary>
    private const float CornerShare = 0.75f;
    /// <summary>A flat card fits where the stone lies within this of its plane at its ends (metres); a ray starts this far out.</summary>
    private const float FitTolerance = 0.12f, FitReach = 0.4f;
    /// <summary>The mats atlas is two cells by two.</summary>
    private const float CellShare = 0.5f;

    private readonly Vector3[] stone, stoneNormals;
    private readonly Triangle[] triangles;
    private readonly Dictionary<(int X, int Y, int Z), List<int>> bins = [];
    /// <summary>The question each triangle was last read for, so one in several bins is read once.</summary>
    private readonly int[] readFor;
    private int question;

    internal StoneTufts(Vector3[] positions, Vector3[] normals, Triangle[] triangles)
    {
        stone = positions;
        stoneNormals = normals;
        this.triangles = triangles;
        readFor = new int[triangles.Length];
        for (int t = 0; t < triangles.Length; t++)
        {
            Vector3 a = stone[triangles[t].A], b = stone[triangles[t].B], c = stone[triangles[t].C];
            ForBins(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)), bin =>
            {
                if (!bins.TryGetValue(bin, out List<int>? found)) bins[bin] = found = [];
                found.Add(t);
            });
        }
    }

    private static void ForBins(Vector3 least, Vector3 most, Action<(int X, int Y, int Z)> visit)
    {
        (int X, int Y, int Z) from = Bin(least), to = Bin(most);
        for (int x = from.X; x <= to.X; x++)
        {
            for (int y = from.Y; y <= to.Y; y++)
            {
                for (int z = from.Z; z <= to.Z; z++) visit((x, y, z));
            }
        }
    }

    private static (int X, int Y, int Z) Bin(Vector3 at) =>
        ((int)MathF.Floor(at.X / BinMetres), (int)MathF.Floor(at.Y / BinMetres), (int)MathF.Floor(at.Z / BinMetres));

    /// <summary>The triangles in the bins a box touches, each once.</summary>
    private List<int> Near(Vector3 least, Vector3 most)
    {
        question++;
        List<int> found = [];
        ForBins(least, most, bin =>
        {
            if (!bins.TryGetValue(bin, out List<int>? inBin)) return;
            foreach (int t in inBin)
            {
                if (readFor[t] == question) continue;
                readFor[t] = question;
                found.Add(t);
            }
        });
        return found;
    }

    /// <summary>A corner of a tuft while it is cut to its square, with its place in that square (0 to 1 each way).</summary>
    private readonly record struct Point(Vector3 At, Vector3 Normal, Vector2 Patch);

    /// <summary>
    /// Adds a tuft around <paramref name="at"/>: the stone's triangles there, cut to the tuft's square. The square is unrolled
    /// from its centre, each point keeping its distance from it, so the tuft does not stretch where it bends; the mat's own
    /// painted outline ends it. <paramref name="pick"/> (0 to 1) chooses its size, turn, lift and cell. A piece standing at
    /// another scale passes it, so a tuft is the same size on it; what is added is in the piece's own metres.
    /// </summary>
    internal void Lay(Vector3 at, Vector3 normal, float pick, float scale, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<uint> indices)
    {
        Vector3 n = Vector3.Normalize(normal);
        Vector3 t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ, n));
        Vector3 u = Vector3.Transform(t, Quaternion.CreateFromAxisAngle(n, Fraction(pick * 5.31f) * MathF.Tau)), v = Vector3.Cross(n, u);
        float width = (SmallestWidth + ((LargestWidth - SmallestWidth) * Fraction(pick * 2.39f))) / scale;
        float reach = width * CornerShare;
        float lift = (Lift + (Fraction(pick * 3.3f) * LiftSpread)) / scale;
        int cell = (int)(Fraction(pick * 9.17f) * 4) & 3;
        Vector2 cellAt = new((cell % 2) * CellShare, (cell / 2) * CellShare);
        List<Point> polygon = [], cut = [];
        foreach (int index in Near(at - new Vector3(reach), at + new Vector3(reach)))
        {
            Triangle triangle = triangles[index];
            Vector3 a = stone[triangle.A], b = stone[triangle.B], c = stone[triangle.C];
            Vector3 middle = (a + b + c) / 3;
            float size = MathF.Max(Vector3.Distance(a, middle), MathF.Max(Vector3.Distance(b, middle), Vector3.Distance(c, middle)));
            if (Vector3.Distance(middle, at) > reach + size) continue;
            Vector3 cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-12f || Vector3.Dot(Vector3.Normalize(cross), n) < LeastFacing) continue;
            polygon.Clear();
            foreach (uint i in new[] { triangle.A, triangle.B, triangle.C })
            {
                Vector3 d = stone[i] - at, flat = d - (n * Vector3.Dot(d, n));
                float along = flat.Length();
                if (along > 1e-5f) flat *= d.Length() / along;
                polygon.Add(new(stone[i], stoneNormals[i], new Vector2((Vector3.Dot(flat, u) / width) + 0.5f, (Vector3.Dot(flat, v) / width) + 0.5f)));
            }

            for (int edge = 0; edge < 4 && polygon.Count > 0; edge++)
            {
                cut.Clear();
                for (int i = 0; i < polygon.Count; i++)
                {
                    Point p = polygon[i], q = polygon[(i + 1) % polygon.Count];
                    float dp = Inside(p.Patch, edge), dq = Inside(q.Patch, edge);
                    if (dp >= 0) cut.Add(p);
                    if ((dp >= 0) != (dq >= 0))
                    {
                        float s = dp / (dp - dq);
                        cut.Add(new(Vector3.Lerp(p.At, q.At, s), Vector3.Lerp(p.Normal, q.Normal, s), Vector2.Lerp(p.Patch, q.Patch, s)));
                    }
                }

                (polygon, cut) = (cut, polygon);
            }

            if (polygon.Count < 3) continue;
            uint first = (uint)positions.Count;
            foreach (Point p in polygon)
            {
                Vector3 pointing = Vector3.Normalize(p.Normal);
                positions.Add(p.At + (pointing * lift));
                normals.Add(pointing);
                uvs.Add(cellAt + (new Vector2(p.Patch.X, 1 - p.Patch.Y) * CellShare));
            }

            for (uint i = 1; i + 1 < polygon.Count; i++) indices.AddRange([first, first + i, first + i + 1]);
        }
    }

    /// <summary>Whether the stone lies under both ends of a flat card <paramref name="half"/> either side of <paramref name="at"/>, so it hangs past no corner.</summary>
    internal bool Fits(Vector3 at, Vector3 side, Vector3 facing, float half) =>
        Under(at + (side * half), facing) && Under(at - (side * half), facing);

    private bool Under(Vector3 at, Vector3 facing)
    {
        Vector3 from = at + (facing * FitReach), to = at - (facing * FitTolerance);
        foreach (int index in Near(Vector3.Min(from, to), Vector3.Max(from, to)))
        {
            Triangle t = triangles[index];
            Vector3 a = stone[t.A], e1 = stone[t.B] - a, e2 = stone[t.C] - a;
            Vector3 p = Vector3.Cross(-facing, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f) continue;
            Vector3 s = from - a;
            float u = Vector3.Dot(s, p) / det;
            if (u < 0 || u > 1) continue;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(-facing, q) / det;
            if (v < 0 || u + v > 1) continue;
            if (MathF.Abs((Vector3.Dot(e2, q) / det) - FitReach) <= FitTolerance) return true;
        }

        return false;
    }

    /// <summary>How far inside one edge of the unit square a point is (negative outside).</summary>
    private static float Inside(Vector2 p, int edge) => edge switch { 0 => p.X, 1 => 1 - p.X, 2 => p.Y, _ => 1 - p.Y };

    private static float Fraction(float x) => x - MathF.Floor(x);
}
