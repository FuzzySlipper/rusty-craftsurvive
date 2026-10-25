using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// Narrow streams drawn as flow-aligned ribbons (#10191). A walked reach under <see cref="WidestMetres"/> wide is
/// too fine for the near water's 1 m lattice, whose wet and dry corners cut its waterline into steps; its ribbon
/// is a strip of sections along the centreline the region tiles carve the channel by, about a metre apart, each
/// spanning the channel and a margin of bank. Each station is sampled from the water owner as a lattice vertex is,
/// so the ribbon stands at the stream's stage, takes a flood or a lake over it and freezes as the tiles do, and its
/// bank stations run on under the ground or sink into a bank lower than the water. Each tile draws every ribbon
/// quad that reaches it, cut to its own square, so what a tile draws and the lattice it leaves never depend on
/// whether a neighbouring tile is loaded, and neighbours meet along their common edge.
/// <para>
/// A section is clear when none of its stations is wet with other water: a lake, the sea, a flood, a wider river or
/// another stream. A ribbon is drawn only between clear sections, and the lattice leaves out just the cells whose
/// every wet corner is its own reach's water under a drawn ribbon quad. So where a stream meets other water - a
/// confluence, a lake's shore, a reach running on under a lake - the ribbon stops and the lattice draws the
/// meeting, the two overlapping by no more than a lattice cell.
/// </para>
/// </summary>
internal sealed class StreamRibbons(WaterModel model)
{
    /// <summary>Reaches whose mean channel is narrower than this (metres) are drawn as ribbons.</summary>
    internal const double WidestMetres = 4;

    /// <summary>The most a section is from the next along the stream (metres).</summary>
    private const double SectionMetres = 1;

    /// <summary>How far past the channel's edge a section reaches into the bank (metres).</summary>
    private const double BankMarginMetres = 1.5;

    /// <summary>How far beyond a tile its streams' network nodes are looked for (metres): a centreline's meander and wander.</summary>
    private const double SearchPadMetres = 64;

    /// <summary>How many shaped centrelines are kept before the cache is emptied.</summary>
    private const int CacheLimit = 512;

    /// <summary>A section's stations across the stream: shares of its half width inside the channel, then bank margins.</summary>
    private static readonly (double HalfWidths, double Margins)[] Stations =
        [(-1, -1), (-1, -0.5), (-1, 0), (-0.5, 0), (0, 0), (0.5, 0), (1, 0), (1, 0.5), (1, 1)];

    /// <summary>The station at the stream's centreline.</summary>
    private const int Centre = 4;

    internal static int Width => Stations.Length;

    private readonly Dictionary<int, (RiverPoint[] Points, Vector2[] Across)> centrelines = [];

    /// <summary>
    /// One run of a stream's sections about a tile: their stations' world positions and samples, section by section
    /// (<see cref="Width"/> stations each, left to right looking along the traced order), and whether the ribbon is
    /// drawn from each section to the next: both are clear.
    /// </summary>
    internal sealed record Strip(int Reach, (double X, double Z)[] Positions, WaterSample[] Samples, bool[] Drawn)
    {
        internal int Sections => Positions.Length / Width;
    }

    /// <summary>A ribbon vertex: where it is relative to its tile's corner, and what the water shader reads there.</summary>
    internal readonly record struct RibbonVertex(Vector3 Position, Vector3 Normal, Vector2 Current, float Coverage);

    /// <summary>A tile's ribbons, and which of its lattice vertices a ribbon of the vertex's own reach covers.</summary>
    internal sealed record TileStreams(Strip[] Strips, bool[] Covered)
    {
        internal static TileStreams None { get; } = new([], []);
    }

    /// <summary>Drops the shaped centrelines (the world changed under them).</summary>
    internal void Clear() => centrelines.Clear();

    /// <summary>
    /// A tile's ribbon sections, sampled, and the lattice vertices they cover, given the tile's lattice samples
    /// (<paramref name="side"/> by <paramref name="side"/>, <paramref name="step"/> metres apart from its corner).
    /// </summary>
    internal TileStreams Sample(long x0, long z0, int tileMetres, int side, int step, WaterSample[] lattice)
    {
        int[] reaches = [.. model.StreamsWithin(x0 - SearchPadMetres, z0 - SearchPadMetres, x0 + tileMetres + SearchPadMetres,
            z0 + tileMetres + SearchPadMetres, WidestMetres)];
        if (reaches.Length == 0) return TileStreams.None;
        if (centrelines.Count > CacheLimit) centrelines.Clear();
        bool[] covered = new bool[side * side];
        List<Strip> strips = [];
        foreach (int reach in reaches)
        {
            (RiverPoint[] points, Vector2[] across) = Centreline(reach);
            if (points.Length < 2) continue;
            double now = model.HalfWidthNow(reach);
            // The segments near enough the tile that their sections draw in it or cover its lattice, in runs.
            int start = -1;
            for (int i = 0; i <= points.Length - 1; i++)
            {
                bool near = i + 1 < points.Length && Near(points[i], points[i + 1], now, x0, z0, tileMetres);
                if (near && start < 0) start = i;
                if (near || start < 0) continue;
                Strip strip = Build(reach, points, across, now, start, i);
                Cover(strip, x0, z0, side, step, lattice, covered);
                if (strip.Drawn.Any(drawn => drawn)) strips.Add(strip);
                start = -1;
            }
        }

        return new([.. strips], covered);
    }

    /// <summary>Whether a segment's ribbon could reach a tile.</summary>
    private static bool Near(RiverPoint a, RiverPoint b, double now, long x0, long z0, int tileMetres)
    {
        double reachOut = Outer(Math.Max(a.HalfWidth, b.HalfWidth), now);
        return Math.Max(a.X, b.X) + reachOut >= x0 && Math.Min(a.X, b.X) - reachOut <= x0 + tileMetres
            && Math.Max(a.Z, b.Z) + reachOut >= z0 && Math.Min(a.Z, b.Z) - reachOut <= z0 + tileMetres;
    }

    /// <summary>How far from the centreline a ribbon reaches (metres), for its channel's half width and its half width now.</summary>
    private static double Outer(double halfWidth, double now) => Math.Max(halfWidth, now) + BankMarginMetres;

    /// <summary>How many pieces a segment's ribbon is cut into, about a metre each.</summary>
    private static int Pieces(RiverPoint a, RiverPoint b) =>
        Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Square(b.X - a.X) + Square(b.Z - a.Z)) / SectionMetres));

    /// <summary>
    /// Adds a tile's ribbons to its mesh, cut to its square: <paramref name="look"/> gives what the shader reads at a
    /// station from its sample (its position is set here), and <paramref name="add"/> adds a vertex and returns its
    /// index. A quad is drawn where one of its wet corners is its own stream's water, and is ice where any corner is
    /// frozen. Returns how many quads draw in the tile.
    /// </summary>
    internal static int Append(TileStreams streams, long x0, long z0, int tileMetres, Func<WaterSample, RibbonVertex> look, Func<RibbonVertex, uint> add,
        List<uint> water, List<uint> ice)
    {
        int quads = 0;
        foreach (Strip strip in streams.Strips)
        {
            double[] tops = Tops(strip);
            Corner[] corners = new Corner[strip.Positions.Length];
            uint?[] index = new uint?[corners.Length];
            for (int v = 0; v < corners.Length; v++)
            {
                (double x, double z) = strip.Positions[v];
                RibbonVertex seen = look(strip.Samples[v]);
                corners[v] = new(x - x0, tops[v], z - z0, seen.Normal, seen.Current, seen.Coverage);
            }

            uint At(int v) => index[v] ??= add(corners[v].Vertex);
            for (int s = 0; s + 1 < strip.Sections; s++)
            for (int k = 0; k + 1 < Width && strip.Drawn[s]; k++)
            {
                if (!Quad(strip, s, k)) continue;
                // The lattice's corner order: a and b across the stream, c and d the next section's; round the quad a, c, d, b.
                int a = (s * Width) + k, b = a + 1, c = a + Width, d = c + 1;
                bool frozen = strip.Samples[a].Ice || strip.Samples[b].Ice || strip.Samples[c].Ice || strip.Samples[d].Ice;
                List<uint> into = frozen ? ice : water;
                if (new[] { a, b, c, d }.All(v => corners[v].Within(tileMetres)))
                {
                    uint va = At(a), vb = At(b), vc = At(c), vd = At(d);
                    into.AddRange([va, vd, vb, va, vc, vd]);
                    quads++;
                    continue;
                }

                List<Corner> cut = Cut([corners[a], corners[c], corners[d], corners[b]], tileMetres);
                if (cut.Count < 3) continue;
                uint[] at = [.. cut.Select(corner => add(corner.Vertex))];
                for (int t = 1; t + 1 < at.Length; t++) into.AddRange([at[0], at[t], at[t + 1]]);
                quads++;
            }
        }

        return quads;
    }

    /// <summary>A ribbon corner relative to its tile's corner, in double precision so neighbouring tiles cut it alike.</summary>
    private readonly record struct Corner(double X, double Y, double Z, Vector3 Normal, Vector2 Current, float Coverage)
    {
        internal RibbonVertex Vertex => new(new((float)X, (float)Y, (float)Z), Normal, Current, Coverage);

        internal bool Within(int tileMetres) => X >= 0 && X <= tileMetres && Z >= 0 && Z <= tileMetres;

        internal static Corner Lerp(Corner a, Corner b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t),
            Vector3.Lerp(a.Normal, b.Normal, (float)t), Vector2.Lerp(a.Current, b.Current, (float)t), a.Coverage + ((b.Coverage - a.Coverage) * (float)t));
    }

    /// <summary>A polygon cut to a tile's square (Sutherland-Hodgman), keeping its winding.</summary>
    private static List<Corner> Cut(List<Corner> polygon, int tileMetres)
    {
        (Func<Corner, double> Along, double Edge, bool Below)[] sides =
            [(c => c.X, 0, false), (c => c.X, tileMetres, true), (c => c.Z, 0, false), (c => c.Z, tileMetres, true)];
        foreach ((Func<Corner, double> along, double edge, bool below) in sides)
        {
            if (polygon.Count == 0) break;
            bool Keeps(Corner c) => below ? along(c) <= edge : along(c) >= edge;
            List<Corner> kept = [];
            for (int i = 0; i < polygon.Count; i++)
            {
                Corner from = polygon[i], to = polygon[(i + 1) % polygon.Count];
                if (Keeps(from)) kept.Add(from);
                if (Keeps(from) != Keeps(to)) kept.Add(Corner.Lerp(from, to, (edge - along(from)) / (along(to) - along(from))));
            }

            polygon = kept;
        }

        return polygon;
    }

    /// <summary>Whether a ribbon quad (from section s, across from station k) is drawn where its stretch is: one of its corners is its own stream's water.</summary>
    private static bool Quad(Strip strip, int s, int k)
    {
        int a = (s * Width) + k, c = a + Width;
        return Own(strip, a) || Own(strip, a + 1) || Own(strip, c) || Own(strip, c + 1);
    }

    /// <summary>Whether a station is wet with its own stream's water.</summary>
    private static bool Own(Strip strip, int v) => strip.Samples[v] is { Wet: true, Body: WaterBody.River } sample && sample.BodyId == strip.Reach;

    /// <summary>
    /// Each station's drawn top: its water's where it is wet; on a dry bank its section's water (the wet station
    /// nearest the centre), carried on under the ground - or its own ground, a little under it, where that lies lower.
    /// </summary>
    private static double[] Tops(Strip strip)
    {
        double[] tops = new double[strip.Positions.Length];
        for (int s = 0; s < strip.Sections; s++)
        {
            int row = s * Width;
            double level = double.NaN;
            for (int offset = 0; offset <= Centre && double.IsNaN(level); offset++)
            {
                if (strip.Samples[row + Centre - offset].Wet) level = strip.Samples[row + Centre - offset].Top;
                else if (strip.Samples[row + Centre + offset].Wet) level = strip.Samples[row + Centre + offset].Top;
            }

            for (int k = 0; k < Width; k++)
            {
                WaterSample sample = strip.Samples[row + k];
                double sunk = sample.Bed - WaterWaveSettings.ShoreSink;
                tops[row + k] = sample.Wet ? sample.Top : double.IsNaN(level) ? sunk : Math.Min(level, sunk);
            }
        }

        return tops;
    }

    /// <summary>
    /// A strip's sections over segments first to last (exclusive), sampled: a section at every joint and about a
    /// metre between; it is drawn between clear sections.
    /// </summary>
    private Strip Build(int reach, RiverPoint[] points, Vector2[] across, double now, int first, int last)
    {
        List<(double X, double Z)> positions = [];
        void Section(int i, double t)
        {
            RiverPoint a = points[i], b = points[Math.Min(i + 1, points.Length - 1)];
            double x = a.X + ((b.X - a.X) * t), z = a.Z + ((b.Z - a.Z) * t);
            Vector2 side = Vector2.Normalize(Vector2.Lerp(across[i], across[Math.Min(i + 1, points.Length - 1)], (float)t));
            double half = Math.Max(a.HalfWidth + ((b.HalfWidth - a.HalfWidth) * t), now);
            foreach ((double halfWidths, double margins) in Stations)
            {
                double offset = (halfWidths * half) + (margins * BankMarginMetres);
                positions.Add((x + (side.X * offset), z + (side.Y * offset)));
            }
        }

        for (int i = first; i < last; i++)
        {
            int pieces = Pieces(points[i], points[i + 1]);
            for (int p = 0; p < pieces; p++) Section(i, p / (double)pieces);
        }

        Section(last, 0);
        WaterSample[] samples = new WaterSample[positions.Count];
        for (int v = 0; v < samples.Length; v++) samples[v] = model.Sample(positions[v].X, positions[v].Z);
        int sections = samples.Length / Width;
        bool[] clear = new bool[sections];
        for (int s = 0; s < sections; s++)
        {
            clear[s] = true;
            for (int k = 0; k < Width && clear[s]; k++)
                clear[s] = samples[(s * Width) + k] is not { Wet: true } sample || (sample.Body == WaterBody.River && sample.BodyId == reach);
        }

        bool[] drawn = new bool[sections];
        for (int s = 0; s + 1 < sections; s++) drawn[s] = clear[s] && clear[s + 1];
        return new(reach, [.. positions], samples, drawn);
    }

    /// <summary>
    /// Marks the lattice vertices a strip's ribbon covers: wet with its reach's water and under a quad the tile
    /// draws (cut to its square, which holds them all), short of the outer bank quads - so the lattice keeps a vertex
    /// near the ribbon's edge, whose water might run on past it.
    /// </summary>
    private static void Cover(Strip strip, long x0, long z0, int side, int step, WaterSample[] lattice, bool[] covered)
    {
        double extent = (side - 1) * step;
        for (int s = 0; s + 1 < strip.Sections; s++)
        for (int k = 1; k + 2 < Width && strip.Drawn[s]; k++)
        {
            if (!Quad(strip, s, k)) continue;
            int a = (s * Width) + k;
            (double X, double Z)[] corners = [strip.Positions[a], strip.Positions[a + 1], strip.Positions[a + Width + 1], strip.Positions[a + Width]];
            double minX = corners.Min(c => c.X) - x0, maxX = corners.Max(c => c.X) - x0, minZ = corners.Min(c => c.Z) - z0, maxZ = corners.Max(c => c.Z) - z0;
            if (maxX < 0 || maxZ < 0 || minX > extent || minZ > extent) continue;
            int i0 = Math.Max(0, (int)Math.Ceiling(minX / step)), i1 = Math.Min(side - 1, (int)Math.Floor(maxX / step));
            int j0 = Math.Max(0, (int)Math.Ceiling(minZ / step)), j1 = Math.Min(side - 1, (int)Math.Floor(maxZ / step));
            for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                int v = (j * side) + i;
                if (covered[v] || lattice[v] is not { Wet: true, Body: WaterBody.River } sample || sample.BodyId != strip.Reach) continue;
                covered[v] = Inside((x0 + (i * step), z0 + (j * step)), corners);
            }
        }
    }

    /// <summary>Whether a point lies in a quad seen from above (its corners in order round it), its edges included.</summary>
    private static bool Inside((double X, double Z) p, (double X, double Z)[] corners)
    {
        double sign = 0;
        for (int e = 0; e < corners.Length; e++)
        {
            (double X, double Z) a = corners[e], b = corners[(e + 1) % corners.Length];
            double cross = ((b.X - a.X) * (p.Z - a.Z)) - ((b.Z - a.Z) * (p.X - a.X));
            if (Math.Abs(cross) < InsideTolerance) continue;
            if (sign == 0) sign = Math.Sign(cross);
            else if (Math.Sign(cross) != sign) return false;
        }

        return true;
    }

    /// <summary>A point this near a quad's edge (square metres of cross product) counts as on it.</summary>
    private const double InsideTolerance = 1e-9;

    /// <summary>
    /// A reach's shaped centreline and, at each point, the unit vector across it: the lattice's +x to a stream
    /// running +z, so a strip's quads wind as the lattice's do; at a joint, across both segments' mean.
    /// </summary>
    private (RiverPoint[] Points, Vector2[] Across) Centreline(int reach)
    {
        if (centrelines.TryGetValue(reach, out var found)) return found;
        RiverPoint[] points = model.Centreline(reach);
        Vector2[] across = new Vector2[points.Length];
        Vector2 Direction(int i)
        {
            Vector2 d = new((float)(points[i + 1].X - points[i].X), (float)(points[i + 1].Z - points[i].Z));
            return d.LengthSquared() > 0 ? Vector2.Normalize(d) : Vector2.UnitY;
        }

        for (int i = 0; i < points.Length && points.Length > 1; i++)
        {
            Vector2 forward = i == 0 ? Direction(0) : i == points.Length - 1 ? Direction(i - 1) : Direction(i - 1) + Direction(i);
            if (forward.LengthSquared() < 1e-6f) forward = Direction(Math.Min(i, points.Length - 2));
            forward = Vector2.Normalize(forward);
            across[i] = new(forward.Y, -forward.X);
        }

        return centrelines[reach] = (points, across);
    }

    private static double Square(double v) => v * v;
}
