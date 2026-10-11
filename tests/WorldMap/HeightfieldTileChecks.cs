using System.Numerics;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Tests;
using Rusty.Engine;

/// <summary>
/// The horizon's heightfield tiles (#9839): neighbouring tiles meet exactly (same edge positions and
/// normals), a normal follows the ground's slope, no quad spans an unsampled cell, and every edge hangs
/// a skirt.
/// </summary>
internal static class HeightfieldTileChecks
{
    private const int Cells = 8;
    private const float CellSize = 0.5f;
    private const float SkirtCells = 1;
    private static readonly Color Paint = new(0.2f, 0.4f, 0.6f, 1);

    /// <summary>A rolling surface, in cells, by global cell index.</summary>
    private static (double Surface, uint Material) Rolling(long x, long z) => ((3 * Math.Sin(x * 0.37)) + (2 * Math.Cos(z * 0.23)) + (0.1 * x), 1);

    internal static void Run()
    {
        HeightfieldTile left = HeightfieldTile.Build(0, 0, Cells, CellSize, SkirtCells, Rolling, _ => Paint)!;
        HeightfieldTile right = HeightfieldTile.Build(Cells, 0, Cells, CellSize, SkirtCells, Rolling, _ => Paint)!;
        int side = Cells + 1;
        Vector3 rightOrigin = new(Cells * CellSize, 0, 0);
        for (int z = 0; z < side; z++)
        {
            int edge = (z * side) + Cells, start = z * side;
            Check.That(Vector3.Distance(left.Positions[edge], right.Positions[start] + rightOrigin) < 1e-5f,
                $"a shared edge vertex stands at the same place in both tiles (row {z})");
            Check.That(Vector3.Distance(left.Normals[edge], right.Normals[start]) < 1e-5f, $"a shared edge vertex has the same normal in both tiles (row {z})");
        }

        int quads = Cells * Cells, skirtQuads = 4 * Cells;
        Check.Equal((quads + skirtQuads) * 6, left.Indices.Length, "a fully sampled tile draws every quad and a skirt along each edge");
        Check.Equal((side * side) + (skirtQuads * 2), left.Positions.Length, "a vertex at every corner and two for each skirt quad");
        Check.That(left.Colors.All(colour => colour == Paint), "every vertex takes its material's paint");

        // A plane rising one cell per cell along x: every normal leans back against that slope.
        HeightfieldTile plane = HeightfieldTile.Build(0, 0, Cells, CellSize, SkirtCells, (x, _) => (x, 0), _ => Paint)!;
        Vector3 expected = Vector3.Normalize(new Vector3(-1, 1, 0));
        Check.That(plane.Normals.Take(side * side).All(normal => Vector3.Distance(normal, expected) < 1e-5f), "a normal follows the ground's slope, at the edges too");

        // An unsampled cell (past the map's edge, or a column not yet sampled) leaves a hole, not a spike.
        HeightfieldTile holed = HeightfieldTile.Build(0, 0, Cells, CellSize, SkirtCells,
            (x, z) => x == 3 && z == 3 ? (double.NaN, 0) : Rolling(x, z), _ => Paint)!;
        Check.Equal(((quads - 4) + skirtQuads) * 6, holed.Indices.Length, "the four quads about an unsampled corner are left out");
        Check.That(holed.Positions.All(position => float.IsFinite(position.Y)), "no vertex stands at an unsampled height");

        Check.That(HeightfieldTile.Build(0, 0, Cells, CellSize, SkirtCells, (_, _) => (double.NaN, 0), _ => Paint) is null,
            "a tile with nothing sampled is not built");

        // Vertex colours are linear; the palette is sRGB, so a mid grey decodes darker and the ends stay put.
        Color decoded = MapPalette.VertexColour(new Color(0.5f, 0, 1, 1));
        Check.That(Math.Abs(decoded.R - 0.214f) < 1e-3f && decoded.G == 0 && Math.Abs(decoded.B - 1) < 1e-6f && decoded.A == 1,
            $"a palette colour is decoded from sRGB for a vertex ({decoded.R:F3},{decoded.G:F3},{decoded.B:F3})");

        float lowest = left.Positions.Take(side * side).Min(position => position.Y);
        Check.That(left.Positions.Skip(side * side).Min(position => position.Y) < lowest,
            "the skirt hangs below the tile's edge");
    }
}
