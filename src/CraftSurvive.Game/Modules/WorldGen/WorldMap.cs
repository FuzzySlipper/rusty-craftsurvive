using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Geography at one point. Rock is exposed bedrock (0 soil .. 1 bare stone); Detail is how much
/// local relief the place may carry; Protection is a policy core local relief must leave alone;
/// RiverSurface is the water level of a channel covering the point, or negative infinity.
/// </summary>
internal readonly record struct MapSample(double Elevation, double Temperature, double Moisture,
    double Rock, double Detail, double Protection = 0, double RiverSurface = double.NegativeInfinity)
{
    internal bool InRiver => !double.IsNegativeInfinity(RiverSurface);
}

internal readonly record struct MapSite(string Name, double X, double Z, MapSample Geography);

/// <summary>
/// A generated, immutable world map. X/Z are metres; north is -Z. Local terrain is always a
/// sample of it: smooth B-spline geography plus rivers resolved as curves. Rivers and derived
/// fields are rebuilt deterministically from the saved fields, so they never need saving.
/// </summary>
internal sealed class WorldMap
{
    internal const double MaximumElevation = 320;
    internal const double LocalReliefLimit = 12;
    private const ulong FingerprintBasis = 0xCBF29CE484222325;
    private const ulong FingerprintPrime = 0x100000001B3;

    // Exposed rock: steep slopes shed soil; resistant rock shows sooner.
    private const double BareSlopeStart = 0.45;
    private const double BareSlopeFull = 0.95;
    private const double HardOutcropSlope = 0.2;
    private const double HardOutcropWeight = 0.45;
    // Local relief allowance: rugged country carries it, flood plains do not.
    private const double MinimumDetail = 0.2;
    private const double RuggedSlope = 0.35;
    private const double PlainSlope = 0.02;
    private const double FloodPlainStart = 0.5;
    private const double FloodPlainFull = 6;
    // The arrival area stays quiet and dry enough to stand in.
    private const double ReserveRadius = 140;
    private const double ReserveFade = 120;
    // Channel cross-section and banks in metres.
    private const double BankFreeboard = 0.6;
    // Sites: one representative interior place per environment present.
    private const int SiteEdgeMargin = 3;
    private const int SiteNeighbourhood = 8;
    private const int MinimumSiteNodes = 30;
    private const double SiteWaterClearance = 2;
    private const int MaximumSites = 8;

    private readonly double[] elevation, temperature, moisture, rock, detail, protection;
    private readonly MapFields fields;

    internal WorldMap(TerrainConfiguration configuration, MapFields fields)
    {
        Configuration = configuration.Validate();
        Grid = MapGrid.For(configuration.Size);
        if (fields.Grid != Grid) throw new ArgumentException("Map lattice does not match its extent.");
        this.fields = fields;
        int count = Grid.Count;
        foreach (float[] field in (float[][])[fields.Elevation, fields.Temperature, fields.Moisture, fields.Hardness, fields.Discharge])
            if (field.Length != count) throw new ArgumentException("Map field count does not match its extent.");
        for (int i = 0; i < count; i++)
        {
            if (!float.IsFinite(fields.Elevation[i]) || fields.Elevation[i] < GenerationConstants.MinimumTerrainHeight || fields.Elevation[i] > MaximumElevation
                || !Unit(fields.Temperature[i]) || !Unit(fields.Moisture[i]) || !Unit(fields.Hardness[i])
                || !float.IsFinite(fields.Discharge[i]) || fields.Discharge[i] < 0)
                throw new ArgumentException("Map contains an invalid geographic sample.");
        }
        elevation = fields.Elevation.Select(v => (double)v).ToArray();
        temperature = fields.Temperature.Select(v => (double)v).ToArray();
        moisture = fields.Moisture.Select(v => (double)v).ToArray();
        rock = new double[count];
        detail = new double[count];
        protection = new double[count];
        for (int i = 0; i < count; i++)
        {
            double slope = Slope(i);
            double bare = Ease(BareSlopeStart, BareSlopeFull, slope);
            double outcrop = HardOutcropWeight * fields.Hardness[i] * Ease(0, HardOutcropSlope, slope);
            rock[i] = elevation[i] < GenerationConstants.WaterLevel ? 0 : Math.Clamp(Math.Max(bare, outcrop), 0, 1);
            double floodPlain = Ease(FloodPlainStart, FloodPlainFull, MapRivers.CatchmentSquareKilometres(Grid, fields.Discharge[i]));
            detail[i] = (MinimumDetail + (1 - MinimumDetail) * Ease(PlainSlope, RuggedSlope, slope)) * (1 - floodPlain);
            double centre = Math.Sqrt(Grid.X(i) * Grid.X(i) + Grid.Z(i) * Grid.Z(i));
            protection[i] = 1 - Ease(ReserveRadius, ReserveRadius + ReserveFade, centre);
        }

        bool[] outlet = MapFlow.Outlets(Grid, elevation);
        if (!outlet.Any(o => o)) throw new ArgumentException("Map has nowhere to drain.");
        double[] filled = (double[])elevation.Clone();
        double[] ones = new double[count];
        Array.Fill(ones, 1);
        MapFlow flow = MapFlow.Route(Grid, filled, outlet, ones, MapFlow.FillGradient);
        Rivers = MapRivers.Extract(Grid, flow, filled.Select(v => (float)v).ToArray(), fields.Discharge, configuration.Contract.GeographyNoiseSeed);
        Fingerprint = ComputeFingerprint();
        Sites = FindSites();
    }

    internal TerrainConfiguration Configuration { get; }
    internal MapGrid Grid { get; }
    internal MapFields Fields => fields;
    internal MapRivers Rivers { get; }
    internal int Segments => Grid.Segments;
    internal int Side => Grid.Side;
    internal double Spacing => Grid.Spacing;
    internal double Radius => Grid.Radius;
    internal ulong Fingerprint { get; }
    internal IReadOnlyList<MapSite> Sites { get; }
    internal double Coordinate(int index) => -Radius + index * Spacing;
    internal bool Contains(double x, double z) => double.IsFinite(x) && double.IsFinite(z) && Math.Abs(x) <= Radius && Math.Abs(z) <= Radius;

    /// <summary>A lattice node without river resolution, for overview presentation.</summary>
    internal MapSample Node(int index) => new(elevation[index], temperature[index], moisture[index], rock[index], detail[index], protection[index]);

    /// <summary>
    /// Smooth geography at any point (clamped to the border for neighbour and normal sampling),
    /// with river channels and banks resolved in metres.
    /// </summary>
    internal MapSample Sample(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x));
        x = Math.Clamp(x, -Radius, Radius);
        z = Math.Clamp(z, -Radius, Radius);
        double gx = (x + Radius) / Spacing, gz = (z + Radius) / Spacing;
        int ix = Math.Min((int)gx, Segments - 1), iz = Math.Min((int)gz, Segments - 1);
        Span<double> wx = stackalloc double[4], wz = stackalloc double[4];
        Span<int> nx = stackalloc int[4], nz = stackalloc int[4];
        BSpline(gx - ix, wx);
        BSpline(gz - iz, wz);
        for (int k = 0; k < 4; k++)
        {
            nx[k] = Math.Clamp(ix - 1 + k, 0, Segments);
            nz[k] = Math.Clamp(iz - 1 + k, 0, Segments) * Side;
        }
        double h = 0, t = 0, m = 0, r = 0, d = 0, p = 0;
        for (int b = 0; b < 4; b++)
        for (int a = 0; a < 4; a++)
        {
            double w = wx[a] * wz[b];
            int i = nz[b] + nx[a];
            h += w * elevation[i]; t += w * temperature[i]; m += w * moisture[i];
            r += w * rock[i]; d += w * detail[i]; p += w * protection[i];
        }
        MapSample broad = new(h, t, m, r, d, p);
        return Rivers.Nearest(x, z) is RiverInfluence river ? Channel(broad, river) : broad;
    }

    /// <summary>A parabolic channel below the water surface, low soil banks, and quiet ground beyond.</summary>
    private static MapSample Channel(MapSample broad, RiverInfluence river)
    {
        double quiet = Ease(river.HalfWidth, MapRivers.QuietDistance(river.HalfWidth), river.Distance);
        if (river.InChannel)
        {
            double across = river.Distance / river.HalfWidth;
            double bed = Math.Max(GenerationConstants.MinimumTerrainHeight, river.Surface - river.Depth * (1 - across * across));
            return broad with { Elevation = Math.Min(broad.Elevation, bed), Rock = 0, Detail = 0, RiverSurface = river.Surface };
        }
        double bank = Ease(river.HalfWidth, river.HalfWidth + river.Bank, river.Distance);
        double shore = river.Surface + BankFreeboard;
        return broad with
        {
            Elevation = shore + (broad.Elevation - shore) * bank,
            Rock = broad.Rock * bank,
            Detail = broad.Detail * quiet,
        };
    }

    internal static MapBiome Biome(MapSample sample) => MapBiomes.Classify(sample);
    internal static string Region(MapSample sample) => MapBiomes.Name(Biome(sample));
    internal static bool Frozen(MapSample sample) => MapBiomes.Frozen(Biome(sample));
    internal static bool Arid(MapSample sample) => MapBiomes.Arid(Biome(sample));
    internal static double Smooth(double v) => v * v * (3 - 2 * v);
    private static double Ease(double start, double end, double value) => Smooth(Math.Clamp((value - start) / (end - start), 0, 1));
    private static bool Unit(float v) => float.IsFinite(v) && v >= 0 && v <= 1;

    private static void BSpline(double t, Span<double> w)
    {
        double t2 = t * t, t3 = t2 * t, u = 1 - t;
        w[0] = u * u * u / 6;
        w[1] = (3 * t3 - 6 * t2 + 4) / 6;
        w[2] = (-3 * t3 + 3 * t2 + 3 * t + 1) / 6;
        w[3] = t3 / 6;
    }

    /// <summary>Gradient magnitude in metres per metre from central differences.</summary>
    private double Slope(int i)
    {
        int x = i % Side, z = i / Side;
        int w = z * Side + Math.Max(0, x - 1), e = z * Side + Math.Min(Segments, x + 1);
        int n = Math.Max(0, z - 1) * Side + x, s = Math.Min(Segments, z + 1) * Side + x;
        double dx = (fields.Elevation[e] - fields.Elevation[w]) / ((Math.Min(Segments, x + 1) - Math.Max(0, x - 1)) * Spacing);
        double dz = (fields.Elevation[s] - fields.Elevation[n]) / ((Math.Min(Segments, z + 1) - Math.Max(0, z - 1)) * Spacing);
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private List<MapSite> FindSites()
    {
        Dictionary<MapBiome, (int Node, int Score, double Centre)> best = [];
        Dictionary<MapBiome, int> totals = [];
        MapBiome[] biomes = new MapBiome[Grid.Count];
        for (int i = 0; i < Grid.Count; i++)
        {
            biomes[i] = Biome(Node(i));
            totals[biomes[i]] = totals.GetValueOrDefault(biomes[i]) + 1;
        }
        for (int z = SiteEdgeMargin; z <= Segments - SiteEdgeMargin; z++)
        for (int x = SiteEdgeMargin; x <= Segments - SiteEdgeMargin; x++)
        {
            int i = z * Side + x;
            MapBiome biome = biomes[i];
            if (biome == MapBiome.Sea || elevation[i] < GenerationConstants.WaterLevel + SiteWaterClearance) continue;
            int score = 0;
            for (int dz = -SiteNeighbourhood; dz <= SiteNeighbourhood; dz++)
            for (int dx = -SiteNeighbourhood; dx <= SiteNeighbourhood; dx++)
            {
                int j = Math.Clamp(z + dz, 0, Segments) * Side + Math.Clamp(x + dx, 0, Segments);
                if (biomes[j] == biome) score++;
            }
            double centre = Math.Abs(Grid.X(i)) + Math.Abs(Grid.Z(i));
            if (best.TryGetValue(biome, out var current) && (score < current.Score || score == current.Score && centre >= current.Centre)) continue;
            best[biome] = (i, score, centre);
        }
        return best.Where(pair => totals[pair.Key] >= MinimumSiteNodes)
            .OrderBy(pair => pair.Key)
            .Take(MaximumSites)
            .Select(pair =>
            {
                double x = Grid.X(pair.Value.Node), z = Grid.Z(pair.Value.Node);
                return new MapSite(MapBiomes.Name(pair.Key), x, z, Sample(x, z));
            }).ToList();
    }

    private ulong ComputeFingerprint()
    {
        ulong hash = FingerprintBasis;
        void Mix(ulong v) => hash = unchecked((hash ^ v) * FingerprintPrime);
        Mix(Configuration.Seed); Mix(Configuration.GeneratorVersion); Mix((ulong)Configuration.Size);
        foreach (float[] field in (float[][])[fields.Elevation, fields.Temperature, fields.Moisture, fields.Hardness, fields.Discharge])
            foreach (float value in field) Mix(BitConverter.SingleToUInt32Bits(value));
        return hash;
    }
}

/// <summary>New-game map generation. Deterministic; a small cache spares repeated generation of one world.</summary>
internal static class WorldMapGenerator
{
    private const int CacheLimit = 4;
    private static readonly Lock CacheLock = new();
    private static readonly LinkedList<WorldMap> Cache = [];

    internal static WorldMap Generate(TerrainConfiguration configuration)
    {
        configuration.Validate();
        lock (CacheLock)
        {
            WorldMap? cached = Cache.FirstOrDefault(map => map.Configuration == configuration);
            if (cached is not null) return cached;
        }
        WorldMap map = new(configuration, MapSimulation.Run(configuration));
        lock (CacheLock)
        {
            Cache.AddFirst(map);
            while (Cache.Count > CacheLimit) Cache.RemoveLast();
        }
        return map;
    }
}
