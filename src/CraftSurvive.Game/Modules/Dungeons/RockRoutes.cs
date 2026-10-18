using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The rock route study (#10090): ways to draw a dungeon's sculpted rock other than its one-metre voxels. Route B meshes
/// the field itself; <see cref="Refine"/> resamples it finer first, with a little detail only a mesh can carry. Route C
/// dresses the walls and ceilings with rock pieces (<see cref="Pieces"/>), procedural here as a stand-in for an authored
/// rock kit, over route B's mesh as the collision and backing.
/// </summary>
internal static class RockRoutes
{
    /// <summary>Route B, finer: samples per metre, and the detail's size and strength (density units, a cell's surface about 0.33).</summary>
    internal const int FineSamplesPerMetre = 2;
    /// <summary>The most samples meshed at once: under the Engine's sampled-volume limit (4 million cells).</summary>
    internal const int MostSamplesPerMesh = 3_500_000;
    private const float DetailFrequency = 1.1f, DetailAmplitude = 0.16f;
    private const float OnSurfaceNudge = 0.002f;

    /// <summary>
    /// The field resampled at <see cref="FineSamplesPerMetre"/> per metre (trilinear between the cell centres), with a
    /// little smooth noise on walls (none on floors and ceilings, so treads stay as the coarse field made them). The first
    /// sample stays at the first cell's centre.
    /// </summary>
    internal static (int Width, int Height, int Depth, float[] Values) Refine(RockDensity rock, ulong seed)
    {
        int s = FineSamplesPerMetre;
        int w = ((rock.Width - 1) * s) + 1, h = ((rock.Height - 1) * s) + 1, d = ((rock.Depth - 1) * s) + 1;
        float[] values = new float[w * h * d];
        for (int z = 0; z < d; z++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float fx = x / (float)s, fy = y / (float)s, fz = z / (float)s;
                    float v = Trilinear(rock, fx, fy, fz);
                    // Walls only: the detail fades where the field's slope is mostly vertical (a floor or a ceiling).
                    float gx = Trilinear(rock, fx + 0.5f, fy, fz) - Trilinear(rock, fx - 0.5f, fy, fz);
                    float gy = Trilinear(rock, fx, fy + 0.5f, fz) - Trilinear(rock, fx, fy - 0.5f, fz);
                    float gz = Trilinear(rock, fx, fy, fz + 0.5f) - Trilinear(rock, fx, fy, fz - 0.5f);
                    float slope = MathF.Sqrt((gx * gx) + (gz * gz)) / MathF.Max(1e-4f, MathF.Sqrt((gx * gx) + (gy * gy) + (gz * gz)));
                    float detail = (Noise(seed, fx * DetailFrequency, fy * DetailFrequency, fz * DetailFrequency) - 0.5f) * 2f * DetailAmplitude;
                    // A sample exactly on the surface (every floor plane falls on the finer lattice) is ambiguous to dual
                    // contouring: nudge the field a hair toward air so the surface keeps a side.
                    values[(((z * h) + y) * w) + x] = v + (detail * slope * slope) + OnSurfaceNudge;
                }

        return (w, h, d, values);
    }

    /// <summary>A field that can be evaluated anywhere, sampled at <see cref="FineSamplesPerMetre"/> over the rock's span from the first cell's centre.</summary>
    internal static (int Width, int Height, int Depth, float[] Values) Resample(RockDensity rock, Func<Vector3, float> shape)
    {
        int s = FineSamplesPerMetre;
        int w = ((rock.Width - 1) * s) + 1, h = ((rock.Height - 1) * s) + 1, d = ((rock.Depth - 1) * s) + 1;
        float[] values = new float[w * h * d];
        Parallel.For(0, d, z =>
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    values[(((z * h) + y) * w) + x] = shape(new Vector3(0.5f + (x / (float)s), 0.5f + (y / (float)s), 0.5f + (z / (float)s))) + OnSurfaceNudge;
                }
        });

        return (w, h, d, values);
    }

    private static float Trilinear(RockDensity rock, float x, float y, float z)
    {
        x = Math.Clamp(x, 0, rock.Width - 1);
        y = Math.Clamp(y, 0, rock.Height - 1);
        z = Math.Clamp(z, 0, rock.Depth - 1);
        int x0 = Math.Min((int)x, rock.Width - 2), y0 = Math.Min((int)y, rock.Height - 2), z0 = Math.Min((int)z, rock.Depth - 2);
        float tx = x - x0, ty = y - y0, tz = z - z0;
        float c00 = Lerp(rock.At(x0, y0, z0), rock.At(x0 + 1, y0, z0), tx), c10 = Lerp(rock.At(x0, y0 + 1, z0), rock.At(x0 + 1, y0 + 1, z0), tx);
        float c01 = Lerp(rock.At(x0, y0, z0 + 1), rock.At(x0 + 1, y0, z0 + 1), tx), c11 = Lerp(rock.At(x0, y0 + 1, z0 + 1), rock.At(x0 + 1, y0 + 1, z0 + 1), tx);
        return Lerp(Lerp(c00, c10, ty), Lerp(c01, c11, ty), tz);
    }

    // --- Route C: rock pieces ---

    /// <summary>Distinct piece shapes, their sphere subdivision and how much each is lumped and squashed.</summary>
    private const int PieceShapes = 8, PieceSubdivisions = 2;
    private const float PieceLumpiness = 0.32f;
    /// <summary>One piece per lattice cell of this many metres over the walls and ceilings; its radius range (metres) and how far it stands out of the rock face.</summary>
    private const float PieceSpacing = 1.6f, PieceSmallest = 0.7f, PieceLargest = 1.2f, PieceProud = 0.25f;
    /// <summary>Pieces per merged mesh, so no one mesh grows too large.</summary>
    private const int PiecesPerMesh = 400;

    /// <summary>
    /// Rock pieces over the field's walls and ceilings (not floors, which stay walkable as the backing mesh has them):
    /// one per <see cref="PieceSpacing"/> lattice cell, at a solid cell beside open space, sunk into the rock, turned and
    /// sized by its hash, merged into a few meshes in <paramref name="material"/>. Positions are in the session.
    /// </summary>
    internal static IReadOnlyList<MeshResourceCreateRequest> Pieces(RockDensity rock, ulong seed, Material material, Vector3 origin, out int count)
    {
        (Vector3[] Positions, Vector3[] Normals, uint[] Indices)[] shapes = [.. Enumerable.Range(0, PieceShapes).Select(i => Shape(seed, i))];
        Dictionary<(int, int, int), (int X, int Y, int Z, Vector3 Open)> chosen = [];
        for (int z = 1; z < rock.Depth - 1; z++)
            for (int y = 1; y < rock.Height - 1; y++)
                for (int x = 1; x < rock.Width - 1; x++)
                {
                    if (rock.At(x, y, z) >= 0) continue;
                    // The open side: summed directions to empty neighbours. A floor (open only above) is left to the backing.
                    Vector3 open = Vector3.Zero;
                    if (rock.At(x + 1, y, z) > 0) open += Vector3.UnitX;
                    if (rock.At(x - 1, y, z) > 0) open -= Vector3.UnitX;
                    if (rock.At(x, y, z + 1) > 0) open += Vector3.UnitZ;
                    if (rock.At(x, y, z - 1) > 0) open -= Vector3.UnitZ;
                    if (rock.At(x, y - 1, z) > 0) open -= Vector3.UnitY;
                    bool sideways = open.X != 0 || open.Z != 0 || open.Y < 0;
                    if (!sideways || open == Vector3.Zero) continue;
                    (int, int, int) cell = ((int)MathF.Floor(x / PieceSpacing), (int)MathF.Floor(y / PieceSpacing), (int)MathF.Floor(z / PieceSpacing));
                    // The lattice cell keeps the candidate its hash favours, so the choice does not depend on scan order.
                    if (!chosen.TryGetValue(cell, out var held) || Hash(seed, x, y, z) > Hash(seed, held.X, held.Y, held.Z))
                        chosen[cell] = (x, y, z, Vector3.Normalize(open));
                }

        List<MeshResourceCreateRequest> meshes = [];
        List<Vector3> positions = [], normals = [];
        List<uint> indices = [];
        int inMesh = 0;
        count = 0;
        foreach ((int x, int y, int z, Vector3 open) in chosen.Values.OrderBy(v => (v.Z, v.Y, v.X)))
        {
            float pick = Hash(seed, x, y, z);
            var shape = shapes[(int)(Fraction(pick * 7.31f) * PieceShapes) % PieceShapes];
            float radius = Lerp(PieceSmallest, PieceLargest, Fraction(pick * 3.17f));
            Quaternion turn = Quaternion.CreateFromYawPitchRoll(Fraction(pick * 5.9f) * MathF.Tau, (Fraction(pick * 2.3f) - 0.5f) * 0.6f, (Fraction(pick * 9.1f) - 0.5f) * 0.6f);
            // The rock's face is about half a cell out from the solid cell's centre: the piece stands PieceProud metres out of it.
            Vector3 centre = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) + (open * (0.5f + PieceProud - radius));
            uint first = (uint)positions.Count;
            for (int i = 0; i < shape.Positions.Length; i++)
            {
                positions.Add(centre + Vector3.Transform(shape.Positions[i] * radius, turn));
                normals.Add(Vector3.Normalize(Vector3.Transform(shape.Normals[i], turn)));
            }

            foreach (uint index in shape.Indices) indices.Add(first + index);
            count++;
            if (++inMesh >= PiecesPerMesh)
            {
                meshes.Add(Merged(positions, normals, indices, material));
                positions.Clear();
                normals.Clear();
                indices.Clear();
                inMesh = 0;
            }
        }

        if (indices.Count > 0) meshes.Add(Merged(positions, normals, indices, material));
        return meshes;
    }

    private static MeshResourceCreateRequest Merged(List<Vector3> positions, List<Vector3> normals, List<uint> indices, Material material) =>
        new(positions.ToArray(), normals.ToArray(), new Vector2[positions.Count], ReadOnlyMemory<Color>.Empty, indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });

    /// <summary>A lumpy rock: a subdivided icosahedron pushed in and out by smooth noise and squashed, with smooth normals.</summary>
    private static (Vector3[] Positions, Vector3[] Normals, uint[] Indices) Shape(ulong seed, int index)
    {
        (List<Vector3> p, List<uint> t) = Icosphere(PieceSubdivisions);
        ulong shapeSeed = seed ^ ((ulong)(index + 1) * 0x9E37_79B9_7F4A_7C15UL);
        Vector3 squash = new(1f, 0.6f + (0.35f * Hash(shapeSeed, 1, 0, 0)), 0.75f + (0.4f * Hash(shapeSeed, 2, 0, 0)));
        Vector3[] positions = new Vector3[p.Count];
        for (int i = 0; i < p.Count; i++)
        {
            Vector3 n = p[i];
            float lump = 1f + ((Noise(shapeSeed, (n.X * 1.6f) + 5, (n.Y * 1.6f) + 5, (n.Z * 1.6f) + 5) - 0.5f) * 2f * PieceLumpiness);
            positions[i] = n * lump * squash;
        }

        Vector3[] normals = new Vector3[p.Count];
        uint[] indices = [.. t];
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a = positions[indices[i]], b = positions[indices[i + 1]], c = positions[indices[i + 2]];
            Vector3 face = Vector3.Cross(b - a, c - a);
            normals[indices[i]] += face;
            normals[indices[i + 1]] += face;
            normals[indices[i + 2]] += face;
        }

        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.Normalize(normals[i]);
        return (positions, normals, indices);
    }

    private static (List<Vector3> Positions, List<uint> Indices) Icosphere(int subdivisions)
    {
        float g = (1f + MathF.Sqrt(5f)) / 2f;
        List<Vector3> p = [.. new Vector3[] { new(-1, g, 0), new(1, g, 0), new(-1, -g, 0), new(1, -g, 0), new(0, -1, g), new(0, 1, g), new(0, -1, -g), new(0, 1, -g), new(g, 0, -1), new(g, 0, 1), new(-g, 0, -1), new(-g, 0, 1) }.Select(Vector3.Normalize)];
        List<uint> t = [0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8, 3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1];
        for (int s = 0; s < subdivisions; s++)
        {
            Dictionary<(uint, uint), uint> mid = [];
            uint Mid(uint a, uint b)
            {
                (uint, uint) key = a < b ? (a, b) : (b, a);
                if (mid.TryGetValue(key, out uint m)) return m;
                p.Add(Vector3.Normalize((p[(int)a] + p[(int)b]) / 2));
                return mid[key] = (uint)(p.Count - 1);
            }

            List<uint> next = [];
            for (int i = 0; i < t.Count; i += 3)
            {
                uint a = t[i], b = t[i + 1], c = t[i + 2], ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
            }

            t = next;
        }

        return (p, t);
    }

    // --- noise ---

    private static float Hash(ulong seed, long x, long y, long z)
    {
        ulong h = seed ^ ((ulong)x * 0x9E37_79B1UL) ^ ((ulong)y * 0x85EB_CA77UL << 21) ^ ((ulong)z * 0xC2B2_AE3DUL << 42);
        h ^= h >> 33;
        h *= 0xFF51_AFD7_ED55_8CCDUL;
        h ^= h >> 33;
        return (h >> 40) / (float)(1UL << 24);
    }

    /// <summary>Smooth value noise in [0, 1].</summary>
    private static float Noise(ulong seed, float x, float y, float z)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
        float fx = x - ix, fy = y - iy, fz = z - iz;
        fx = fx * fx * (3 - (2 * fx));
        fy = fy * fy * (3 - (2 * fy));
        fz = fz * fz * (3 - (2 * fz));
        float L(int dx, int dy, int dz) => Hash(seed, ix + dx, iy + dy, iz + dz);
        return Lerp(Lerp(Lerp(L(0, 0, 0), L(1, 0, 0), fx), Lerp(L(0, 1, 0), L(1, 1, 0), fx), fy),
            Lerp(Lerp(L(0, 0, 1), L(1, 0, 1), fx), Lerp(L(0, 1, 1), L(1, 1, 1), fx), fy), fz);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static float Fraction(float x) => x - MathF.Floor(x);
}
