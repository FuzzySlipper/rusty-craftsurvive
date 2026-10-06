namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>One vertex of a river centreline; the water surface never rises downstream.</summary>
internal readonly record struct RiverPoint(double X, double Z, double Surface, double HalfWidth, double Depth);

/// <summary>The river nearest a point, resolved for local terrain.</summary>
internal readonly record struct RiverInfluence(double Distance, double Surface, double HalfWidth, double Depth, double Bank)
{
    internal bool InChannel => Distance < HalfWidth;
}

/// <summary>
/// Rivers as continuous curves rather than node-to-node segments. Reaches are traced down the
/// saved drainage between confluences, smoothed, then given a seeded meander whose amplitude
/// grows with channel width and fades on steep reaches and at both ends, so confluences and
/// mouths stay joined. The water surface is interpolated from filled node heights, so it is
/// monotone downstream by construction.
/// </summary>
internal sealed class MapRivers
{
    /// <summary>Rain-weighted catchment, in square kilometres, at which a channel appears.</summary>
    internal const double SourceCatchment = 1.0;
    private const double MinimumHalfWidth = 1.6;
    private const double MaximumHalfWidth = 16;
    private const double HalfWidthPerRootArea = 1.8;
    private const double MinimumDepth = 1.2;
    private const double MaximumDepth = 3.5;
    private const double DepthPerRootArea = 0.3;
    private const double MinimumBank = 5;
    private const double BankPerHalfWidth = 2;
    private const double QuietBanks = 2.5;
    private const int SmoothingPasses = 3;
    private const double ResampleSpacing = 6;
    private const double MeanderPerHalfWidth = 2.2;
    private const double MeanderSpacingFraction = 0.33;
    private const double MeanderWavelengthPerHalfWidth = 18;
    private const double MinimumMeanderWavelength = 60;
    private const double MeanderFlatSlope = 0.004;
    private const double MeanderSteepSlope = 0.04;
    /// <summary>Even a steep mountain stream keeps part of its meander, so no reach is ruler-straight.</summary>
    private const double SteepMeander = 0.4;
    /// <summary>A slower lateral wander across the valley floor, on top of the channel-scale meander.</summary>
    private const double WanderWavelength = 300;
    private const double WanderSpacingFraction = 0.45;
    /// <summary>Distance over which both meanders fade to zero at a reach's ends, keeping junctions joined.</summary>
    private const double JoinTaper = 90;
    private const double SteepWander = 0.6;
    private const double NoiseSwing = 2.2;
    private const double BucketSize = 32;
    private const ulong MeanderSalt = 0x8CB92BA72F3D8DD7UL;

    private readonly RiverPoint[][] reaches;
    private readonly (int Reach, int Segment)[] entries;
    private readonly Dictionary<long, (int Start, int Count)> buckets;

    private MapRivers(RiverPoint[][] reaches)
    {
        this.reaches = reaches;
        Dictionary<long, List<(int, int)>> lists = [];
        for (int r = 0; r < reaches.Length; r++)
        for (int s = 0; s + 1 < reaches[r].Length; s++)
        {
            RiverPoint a = reaches[r][s], b = reaches[r][s + 1];
            double reach = Math.Max(a.HalfWidth, b.HalfWidth) + QuietBanks * Math.Max(Bank(a.HalfWidth), Bank(b.HalfWidth));
            long x0 = Cell(Math.Min(a.X, b.X) - reach), x1 = Cell(Math.Max(a.X, b.X) + reach);
            long z0 = Cell(Math.Min(a.Z, b.Z) - reach), z1 = Cell(Math.Max(a.Z, b.Z) + reach);
            for (long cz = z0; cz <= z1; cz++)
            for (long cx = x0; cx <= x1; cx++)
            {
                long key = Key(cx, cz);
                if (!lists.TryGetValue(key, out List<(int, int)>? list)) lists[key] = list = [];
                list.Add((r, s));
            }
        }
        buckets = new(lists.Count);
        List<(int, int)> flat = [];
        foreach ((long key, List<(int, int)> list) in lists.OrderBy(pair => pair.Key))
        {
            buckets[key] = (flat.Count, list.Count);
            flat.AddRange(list);
        }
        entries = [.. flat];
    }

    internal IReadOnlyList<RiverPoint[]> Reaches => reaches;

    internal static double Bank(double halfWidth) => Math.Max(MinimumBank, BankPerHalfWidth * halfWidth);
    internal static double QuietDistance(double halfWidth) => halfWidth + QuietBanks * Bank(halfWidth);

    internal static double CatchmentSquareKilometres(MapGrid grid, double discharge) => discharge * grid.Spacing * grid.Spacing / 1e6;

    internal static MapRivers Empty { get; } = new([]);

    internal static MapRivers Extract(MapGrid grid, MapFlow flow, float[] elevation, float[] discharge, ulong seed)
    {
        List<int[]> paths = Trace(grid, flow, elevation, discharge, MapScale.For(grid).SourceCatchment);
        return new([.. paths.Select((path, index) => Shape(grid, path, elevation, discharge, seed, index))]);
    }

    /// <summary>Rivers from already-shaped reaches, such as a region's share of a continent's drainage (#9550).</summary>
    internal static MapRivers FromReaches(IEnumerable<RiverPoint[]> reaches) => new([.. reaches]);

    /// <summary>
    /// The node paths of every reach, between confluences, in a stable upstream-first order: a reach's
    /// index in this list seeds its meander, so a reach shaped on its own matches the same reach shaped
    /// with all the others.
    /// </summary>
    internal static List<int[]> Trace(MapGrid grid, MapFlow flow, float[] elevation, float[] discharge, double sourceCatchment)
    {
        bool[] river = new bool[grid.Count];
        int[] donors = new int[grid.Count];
        for (int i = 0; i < grid.Count; i++)
            river[i] = elevation[i] >= GenerationConstants.WaterLevel && CatchmentSquareKilometres(grid, discharge[i]) >= sourceCatchment;
        for (int i = 0; i < grid.Count; i++)
            if (river[i] && flow.Receiver[i] != MapFlow.Base) donors[flow.Receiver[i]]++;

        List<int[]> reaches = [];
        // Upstream-first order makes reach numbering stable for a given map.
        for (int n = grid.Count - 1; n >= 0; n--)
        {
            int start = flow.Order[n];
            if (!river[start] || donors[start] == 1) continue;
            List<int> path = [start];
            int j = flow.Receiver[start];
            while (j != MapFlow.Base)
            {
                path.Add(j);
                if (!river[j] || donors[j] != 1) break;
                j = flow.Receiver[j];
            }
            if (path.Count < 2) continue;
            reaches.Add([.. path]);
        }
        return reaches;
    }

    internal static RiverPoint[] Shape(MapGrid grid, IReadOnlyList<int> path, float[] elevation, float[] discharge, ulong seed, int index)
    {
        List<RiverPoint> points = path.Select(i =>
        {
            double area = Math.Sqrt(CatchmentSquareKilometres(grid, discharge[i]));
            return new RiverPoint(grid.X(i), grid.Z(i), Math.Max(GenerationConstants.WaterLevel, elevation[i]),
                Math.Clamp(HalfWidthPerRootArea * area, MinimumHalfWidth, MaximumHalfWidth),
                Math.Clamp(MinimumDepth + DepthPerRootArea * area, MinimumDepth, MaximumDepth));
        }).ToList();
        // A sea node closes the reach at the coast; its surface is the sea's.
        for (int pass = 0; pass < SmoothingPasses; pass++) points = Chaikin(points);
        List<RiverPoint> even = Resample(points);
        double length = 0;
        double[] along = new double[even.Count];
        for (int k = 1; k < even.Count; k++) along[k] = length += Math.Sqrt(Square(even[k].X - even[k - 1].X) + Square(even[k].Z - even[k - 1].Z));
        double drop = even[0].Surface - even[^1].Surface;
        double slope = length > 0 ? drop / length : 0;
        double calm = 1 - (1 - SteepMeander) * WorldMap.Smooth(Math.Clamp((slope - MeanderFlatSlope) / (MeanderSteepSlope - MeanderFlatSlope), 0, 1));
        RiverPoint[] shaped = new RiverPoint[even.Count];
        ulong reachSeed = seed ^ MeanderSalt ^ (ulong)index * GenerationConstants.CoordinateXMultiplier;
        for (int k = 0; k < even.Count; k++)
        {
            RiverPoint p = even[k];
            int a = Math.Max(0, k - 1), b = Math.Min(even.Count - 1, k + 1);
            double tx = even[b].X - even[a].X, tz = even[b].Z - even[a].Z, tl = Math.Sqrt(tx * tx + tz * tz);
            if (tl == 0) { shaped[k] = p; continue; }
            double wavelength = Math.Max(MinimumMeanderWavelength, MeanderWavelengthPerHalfWidth * p.HalfWidth);
            double amplitude = Math.Min(MeanderPerHalfWidth * p.HalfWidth, MeanderSpacingFraction * grid.Spacing) * calm;
            double taper = WorldMap.Smooth(Math.Clamp(Math.Min(along[k], length - along[k]) / Math.Min(JoinTaper, length / 4), 0, 1));
            double wander = WanderSpacingFraction * grid.Spacing * Math.Max(calm, SteepWander);
            double offset = taper * (amplitude * Swing(MapNoise.Gradient(reachSeed, along[k] / wavelength, 0.5))
                + wander * Swing(MapNoise.Gradient(reachSeed, along[k] / WanderWavelength, 3.5)));
            shaped[k] = p with { X = p.X - tz / tl * offset, Z = p.Z + tx / tl * offset };
        }
        return shaped;
    }

    /// <summary>Gradient noise rarely nears its extremes; this spreads typical values across the full swing.</summary>
    private static double Swing(double noise) => Math.Clamp(noise * NoiseSwing, -1, 1);

    private static List<RiverPoint> Chaikin(List<RiverPoint> points)
    {
        if (points.Count < 3) return points;
        List<RiverPoint> result = [points[0]];
        for (int k = 0; k + 1 < points.Count; k++)
        {
            result.Add(Mix(points[k], points[k + 1], 0.25));
            result.Add(Mix(points[k], points[k + 1], 0.75));
        }
        result.Add(points[^1]);
        return result;
    }

    private static List<RiverPoint> Resample(List<RiverPoint> points)
    {
        List<RiverPoint> result = [points[0]];
        double carried = 0;
        for (int k = 0; k + 1 < points.Count; k++)
        {
            RiverPoint a = points[k], b = points[k + 1];
            double length = Math.Sqrt(Square(b.X - a.X) + Square(b.Z - a.Z));
            double position = ResampleSpacing - carried;
            while (position < length)
            {
                result.Add(Mix(a, b, position / length));
                position += ResampleSpacing;
            }
            carried = length - (position - ResampleSpacing);
        }
        if (result[^1] != points[^1]) result.Add(points[^1]);
        return result;
    }

    private static RiverPoint Mix(RiverPoint a, RiverPoint b, double t) => new(
        a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t, a.Surface + (b.Surface - a.Surface) * t,
        a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t, a.Depth + (b.Depth - a.Depth) * t);

    /// <summary>The river whose banks reach this point most closely, if any reaches it at all.</summary>
    internal RiverInfluence? Nearest(double x, double z)
    {
        if (!buckets.TryGetValue(Key(Cell(x), Cell(z)), out (int Start, int Count) range)) return null;
        RiverInfluence? best = null;
        double bestScore = double.MaxValue;
        for (int e = range.Start; e < range.Start + range.Count; e++)
        {
            (int r, int s) = entries[e];
            RiverPoint a = reaches[r][s], b = reaches[r][s + 1];
            double dx = b.X - a.X, dz = b.Z - a.Z, lengthSquared = dx * dx + dz * dz;
            double t = lengthSquared > 0 ? Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared, 0, 1) : 0;
            double distance = Math.Sqrt(Square(x - a.X - dx * t) + Square(z - a.Z - dz * t));
            double halfWidth = a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t;
            double bank = Bank(halfWidth);
            // Rank by how deep inside a river's footprint the point is, so a wide trunk wins a confluence.
            double score = (distance - halfWidth) / bank;
            if (distance >= QuietDistance(halfWidth) || score >= bestScore) continue;
            bestScore = score;
            best = new(distance, a.Surface + (b.Surface - a.Surface) * t, halfWidth, a.Depth + (b.Depth - a.Depth) * t, bank);
        }
        return best;
    }

    private static long Cell(double v) => (long)Math.Floor(v / BucketSize);
    private static long Key(long x, long z) => unchecked((x << 32) ^ (z & 0xFFFF_FFFFL));
    private static double Square(double v) => v * v;
}
