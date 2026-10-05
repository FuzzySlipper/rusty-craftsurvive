namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The square lattice the world map is simulated on. Node spacing is fixed in metres, so a
/// larger world has more geography rather than the same geography stretched thinner; only
/// a world beyond <see cref="MaximumSegments"/> nodes per side coarsens its spacing.
/// </summary>
internal readonly record struct MapGrid(int Segments, double Spacing, double Radius)
{
    internal const double TargetSpacing = 32;
    internal const int MinimumSegments = 8;
    internal const int MaximumSegments = 512;
    internal const int MaximumNodes = (MaximumSegments + 1) * (MaximumSegments + 1);
    private static readonly double DiagonalLength = Math.Sqrt(2);

    // Eight neighbours, cardinal first, with their step lengths in node units.
    internal static ReadOnlySpan<int> NeighbourX => [1, -1, 0, 0, 1, 1, -1, -1];
    internal static ReadOnlySpan<int> NeighbourZ => [0, 0, 1, -1, 1, -1, 1, -1];
    internal static double NeighbourLength(int k) => k < 4 ? 1 : DiagonalLength;
    internal static readonly double[] StepLengths = [1, 1, 1, 1, DiagonalLength, DiagonalLength, DiagonalLength, DiagonalLength];

    /// <summary>Index offsets of the eight neighbours, in <see cref="NeighbourX"/> order, valid for interior nodes.</summary>
    internal int[] Offsets() => [1, -1, Side, -Side, Side + 1, -Side + 1, Side - 1, -Side - 1];

    internal bool IsInterior(int index)
    {
        int x = index % Side, z = index / Side;
        return x > 0 && z > 0 && x < Segments && z < Segments;
    }

    /// <summary>
    /// The lattice at twice the spacing, for coarse-to-fine simulation; this lattice itself
    /// when halving would drop below the minimum resolution.
    /// </summary>
    internal MapGrid Coarsened() => Segments / 2 < MinimumSegments ? this : new(Segments / 2, Radius * 2 / (Segments / 2), Radius);

    internal static MapGrid For(int size)
    {
        int segments = Math.Clamp((int)Math.Round(size / TargetSpacing), MinimumSegments, MaximumSegments);
        return new(segments, size / (double)segments, size / 2d);
    }

    internal int Side => Segments + 1;
    internal int Count => Side * Side;
    internal double X(int index) => -Radius + index % Side * Spacing;
    internal double Z(int index) => -Radius + index / Side * Spacing;
    internal bool IsEdge(int index)
    {
        int x = index % Side, z = index / Side;
        return x == 0 || z == 0 || x == Segments || z == Segments;
    }

    /// <summary>Bilinear sample of a node field at a world position, clamped to the border.</summary>
    internal double Bilinear(double[] field, double x, double z)
    {
        double gx = Math.Clamp((x + Radius) / Spacing, 0, Segments), gz = Math.Clamp((z + Radius) / Spacing, 0, Segments);
        int ix = Math.Min((int)gx, Segments - 1), iz = Math.Min((int)gz, Segments - 1);
        double tx = gx - ix, tz = gz - iz;
        int i = iz * Side + ix;
        double near = field[i] + (field[i + 1] - field[i]) * tx;
        double far = field[i + Side] + (field[i + Side + 1] - field[i + Side]) * tx;
        return near + (far - near) * tz;
    }

    /// <summary>The neighbour index in direction k, or -1 beyond the lattice.</summary>
    internal int Neighbour(int index, int k)
    {
        int x = index % Side + NeighbourX[k], z = index / Side + NeighbourZ[k];
        return x < 0 || z < 0 || x > Segments || z > Segments ? -1 : z * Side + x;
    }
}
