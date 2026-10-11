using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One square tile of a heightfield as mesh streams (#9839), in units of the tile's own origin: a vertex at
/// every sampled cell corner, normals from the heights about it (reaching one cell past the tile, so a
/// shared edge has the same normal in both tiles), quads only where all four corners are sampled, split
/// along the diagonal nearer in height, and a skirt hanging below each edge.
/// </summary>
internal sealed record HeightfieldTile(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, Color[] Colors, uint[] Indices)
{
    /// <param name="cellX0">The tile's first cell, by global cell index; it spans <paramref name="cells"/> cells and one more vertex.</param>
    /// <param name="cellSize">A cell's width in drawn units; surfaces are in cells, so heights scale by it too.</param>
    /// <param name="skirtCells">How far, in cells' height, a skirt hangs below its edge.</param>
    /// <param name="cell">A cell's surface (in cells; NaN for none) and material slot, by global cell index.</param>
    /// <param name="paint">A material slot's vertex colour.</param>
    /// <returns>The tile, or null where nothing in it is sampled.</returns>
    internal static HeightfieldTile? Build(long cellX0, long cellZ0, int cells, float cellSize, float skirtCells,
        Func<long, long, (double Surface, uint Material)> cell, Func<uint, Color> paint)
    {
        int side = cells + 1, ring = side + 2;
        double[] heights = new double[ring * ring];
        uint[] slots = new uint[side * side];
        bool any = false;
        // Heights with a ring of one cell about the tile, for normals at its edge.
        for (int z = -1; z <= side; z++)
        for (int x = -1; x <= side; x++)
        {
            (double surface, uint slot) = cell(cellX0 + x, cellZ0 + z);
            heights[((z + 1) * ring) + x + 1] = surface;
            if (x < 0 || z < 0 || x >= side || z >= side) continue;
            slots[(z * side) + x] = slot;
            any |= !double.IsNaN(surface);
        }

        if (!any) return null;
        double Height(int x, int z) => heights[((z + 1) * ring) + x + 1];
        List<Vector3> positions = new(side * side);
        List<Vector3> normals = new(side * side);
        List<Vector2> uvs = new(side * side);
        List<Color> colors = new(side * side);
        int[] vertex = new int[side * side];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            double h = Height(x, z);
            if (double.IsNaN(h))
            {
                vertex[(z * side) + x] = -1;
                continue;
            }

            double slopeX = Slope(Height(x - 1, z), h, Height(x + 1, z)), slopeZ = Slope(Height(x, z - 1), h, Height(x, z + 1));
            vertex[(z * side) + x] = positions.Count;
            positions.Add(new Vector3(x * cellSize, (float)(h * cellSize), z * cellSize));
            normals.Add(Vector3.Normalize(new Vector3((float)-slopeX, 1, (float)-slopeZ)));
            uvs.Add(new Vector2(x / (float)cells, z / (float)cells));
            colors.Add(paint(slots[(z * side) + x]));
        }

        List<uint> indices = new(cells * cells * 6);
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            int a = vertex[(z * side) + x], b = vertex[(z * side) + x + 1];
            int c = vertex[((z + 1) * side) + x], d = vertex[((z + 1) * side) + x + 1];
            if (a < 0 || b < 0 || c < 0 || d < 0) continue;
            // Split along the diagonal whose ends are nearer in height, so a ridge or a gully is not cut across.
            if (Math.Abs(Height(x, z) - Height(x + 1, z + 1)) <= Math.Abs(Height(x + 1, z) - Height(x, z + 1)))
                indices.AddRange([(uint)a, (uint)c, (uint)d, (uint)a, (uint)d, (uint)b]);
            else
                indices.AddRange([(uint)a, (uint)c, (uint)b, (uint)b, (uint)c, (uint)d]);
        }

        if (indices.Count == 0) return null;
        Skirt(0, 0, 1, 0);
        Skirt(0, cells, 1, 0);
        Skirt(0, 0, 0, 1);
        Skirt(cells, 0, 0, 1);
        return new([.. positions], [.. normals], [.. uvs], [.. colors], [.. indices]);

        // A strip hanging below one edge of the tile, from (x0, z0) along (dx, dz).
        void Skirt(int x0, int z0, int dx, int dz)
        {
            Vector3 drop = new(0, skirtCells * cellSize, 0);
            for (int step = 0; step < cells; step++)
            {
                int x = x0 + (step * dx), z = z0 + (step * dz);
                int a = vertex[(z * side) + x], b = vertex[((z + dz) * side) + x + dx];
                if (a < 0 || b < 0) continue;
                int start = positions.Count;
                foreach (int top in (int[])[a, b])
                {
                    positions.Add(positions[top] - drop);
                    normals.Add(normals[top]);
                    uvs.Add(uvs[top]);
                    colors.Add(colors[top]);
                }

                indices.AddRange([(uint)a, (uint)b, (uint)start, (uint)start, (uint)b, (uint)(start + 1)]);
            }
        }
    }

    /// <summary>The height change per cell through a sample, from whichever neighbours are sampled.</summary>
    private static double Slope(double before, double here, double after) =>
        !double.IsNaN(before) && !double.IsNaN(after) ? (after - before) / 2
        : !double.IsNaN(after) ? after - here
        : !double.IsNaN(before) ? here - before
        : 0;
}
