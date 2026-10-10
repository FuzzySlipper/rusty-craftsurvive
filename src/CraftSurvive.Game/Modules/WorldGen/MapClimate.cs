namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Climate from the terrain: a seeded latitude axis and altitude lapse give temperature; a
/// seeded prevailing wind carries moisture off the sea and drops it as the air rises over
/// relief, leaving rain shadows behind ranges. Values are 0..1 product units, not physics.
/// </summary>
internal sealed class MapClimate
{
    // The latitude range is centred warm of the midpoint, so the arrival area is temperate
    // and the polar side reaches ice only toward its border and on high ground.
    private const double WarmEnd = 1.0;
    private const double ColdEnd = 0.12;
    private const double AltitudeLapse = 0.22;
    private const double TemperatureNoise = 0.12;
    private const double ClimateWavelength = 3600;

    private const double EnteringHumidity = 0.75;
    private const double SeaRecharge = 0.35;
    private const double PlainRainPerKilometre = 0.07;
    private const double OrographicRain = 4.0;
    private const double Recycling = 0.25;
    private const double MarchStep = 0.5;
    /// <summary>Rain is spread over a few hundred metres, so environments form regions rather than speckle.</summary>
    private const double RainSpread = 200;
    private const int RainBlurPasses = 3;
    private const double MinimumRain = 0.15;
    private const double MaximumRain = 2.5;
    private const double HeatAridity = 0.25;
    private const double RainNoise = 0.08;

    /// <summary>
    /// An absolute climate's scales, taken from the frontier peninsula at today's heights (#9815): the
    /// climb that counts as the whole relief, the raw rain that counts as 1, the metres over which the air
    /// cools by <see cref="AltitudeLapse"/>, and the rain at each tenth of moisture (the deciles that
    /// ranking gave), so its biomes keep the variety ranking gave them.
    /// </summary>
    private const double OrographicMetres = 2260, AbsoluteRain = 0.00285, LapseMetres = 1800;
    private static readonly double[] MoistureCurve = [0.15, 0.265, 0.38, 0.49, 0.615, 0.78, 0.985, 1.205, 1.485, 1.88, 2.5];

    private const ulong AxisSalt = 0x2545F4914F6CDD1DUL;
    private const ulong WindSalt = 0x94D049BB133111EBUL;
    private const ulong TemperatureSalt = 0xBF58476D1CE4E5B9UL;
    private const ulong RainSalt = 0x7FB5D329728EA185UL;

    internal MapClimate(ulong seed) : this(seed, MapScale.Regional) { }

    /// <param name="scale">A continent's lengths stretch the climate's wavelengths and the march's per-kilometre rates (#9549).</param>
    /// <param name="design">A designed continent names its own cold side and prevailing wind (#9815); otherwise the seed draws them.</param>
    internal MapClimate(ulong seed, MapScale scale, DesignClimate? design = null)
    {
        lengths = scale.Lengths;
        LatitudeAngle = design is null ? MapNoise.Unit(seed ^ AxisSalt, 0, 0) * 2 * Math.PI : DesignClimate.Angle(design.ColdToward);
        WindAngle = design is null ? MapNoise.Unit(seed ^ WindSalt, 0, 0) * 2 * Math.PI : DesignClimate.Angle(design.WindToward);
        Absolute = design is not null;
        Seed = seed;
    }

    /// <summary>
    /// A designed continent's climate is absolute (#9815): rain from the metres the air climbs, measured
    /// in fixed units rather than against the land's average, cooling per metre rather than against the
    /// peak, and moisture a fixed curve of rain rather than its rank. One region's change then plays out
    /// in its own rain shadow and runoff without re-ranking the rest of the continent.
    /// </summary>
    internal bool Absolute { get; }

    internal double LatitudeAngle { get; }
    internal double WindAngle { get; }
    private ulong Seed { get; }
    private readonly double lengths = 1;
    private double ClimateLength => ClimateWavelength * lengths;

    /// <summary>
    /// Relative precipitation, mean near 1 over land; erosion weights discharge by it. For an absolute
    /// climate given heights in metres (<paramref name="metres"/>), the climb is in fixed metres and the
    /// rain in fixed units, so its mean is near 1 only on the continents its scale was taken from.
    /// </summary>
    internal double[] Rainfall(MapGrid grid, double[] height, bool[] sea, bool metres = false)
    {
        bool absolute = Absolute && metres;
        double relief = absolute ? OrographicMetres : Math.Max(1e-9, height.Max());
        double[] rain = new double[grid.Count];
        int[] visits = new int[grid.Count];
        double ux = Math.Cos(WindAngle), uz = Math.Sin(WindAngle);
        double reach = grid.Radius * Math.Sqrt(2);
        double ds = grid.Spacing * MarchStep;
        for (double v = -reach; v <= reach; v += grid.Spacing * MarchStep)
        {
            double q = EnteringHumidity, previous = double.NaN;
            for (double u = -reach; u <= reach; u += ds)
            {
                double x = u * ux - v * uz, z = u * uz + v * ux;
                if (Math.Abs(x) > grid.Radius || Math.Abs(z) > grid.Radius) continue;
                double gx = (x + grid.Radius) / grid.Spacing, gz = (z + grid.Radius) / grid.Spacing;
                int node = (int)Math.Round(gz) * grid.Side + (int)Math.Round(gx);
                double hn = Bilinear(grid, height, gx, gz) / relief;
                if (sea[node])
                {
                    q += (1 - q) * Math.Min(1, SeaRecharge * ds / (1000 * lengths));
                }
                else
                {
                    double climb = double.IsNaN(previous) ? 0 : Math.Max(0, hn - previous);
                    double p = Math.Min(q, q * (PlainRainPerKilometre * ds / (1000 * lengths) + OrographicRain * climb));
                    q -= p * (1 - Recycling);
                    rain[node] += p / (ds / 1000);
                }
                visits[node]++;
                previous = hn;
            }
        }
        for (int i = 0; i < grid.Count; i++) if (visits[i] > 0) rain[i] /= visits[i];
        Blur(grid, rain, Math.Max(1, (int)Math.Round(RainSpread * lengths / grid.Spacing)), RainBlurPasses);
        double landMean = 0;
        int land = 0;
        for (int i = 0; i < grid.Count; i++) if (!sea[i]) { landMean += rain[i]; land++; }
        landMean = absolute ? AbsoluteRain : land > 0 ? landMean / land : 1;
        for (int i = 0; i < grid.Count; i++)
        {
            double variation = 1 + RainNoise * MapNoise.Fbm(Seed ^ RainSalt, grid.X(i) / ClimateLength, grid.Z(i) / ClimateLength, 3, 0.5);
            rain[i] = Math.Clamp(rain[i] / Math.Max(landMean, 1e-9) * variation, MinimumRain, MaximumRain);
        }
        return rain;
    }

    /// <summary>0 polar .. 1 tropical, cooled with altitude in metres.</summary>
    internal double Temperature(double x, double z, double radius, double elevation, double peak)
    {
        double t = Math.Clamp((x * Math.Cos(LatitudeAngle) + z * Math.Sin(LatitudeAngle)) / radius, -1, 1);
        double latitude = WarmEnd + (ColdEnd - WarmEnd) * (t + 1) / 2;
        double noise = TemperatureNoise * MapNoise.Fbm(Seed ^ TemperatureSalt, x / ClimateLength, z / ClimateLength, 3, 0.5);
        return Math.Clamp(latitude + noise - AltitudeLapse * Math.Max(0, elevation) / (Absolute ? LapseMetres : peak), 0, 1);
    }

    /// <summary>
    /// Land moisture, drier where it is hot: the rank of its rainfall, or for an absolute climate a fixed
    /// curve of it (<see cref="MoistureCurve"/>), so how wet a place is depends on its own rain alone.
    /// </summary>
    internal double[] Moisture(double[] rain, bool[] sea, double[] temperature)
    {
        if (Absolute)
        {
            double[] wet = new double[rain.Length];
            for (int i = 0; i < rain.Length; i++)
                wet[i] = sea[i] ? 1 : Math.Clamp(Curve(rain[i]) - HeatAridity * (temperature[i] - 0.5), 0, 1);
            return wet;
        }

        int[] land = Enumerable.Range(0, rain.Length).Where(i => !sea[i]).OrderBy(i => rain[i]).ThenBy(i => i).ToArray();
        double[] moisture = new double[rain.Length];
        Array.Fill(moisture, 1);
        for (int r = 0; r < land.Length; r++)
        {
            int i = land[r];
            double rank = land.Length > 1 ? r / (double)(land.Length - 1) : 0.5;
            moisture[i] = Math.Clamp(rank - HeatAridity * (temperature[i] - 0.5), 0, 1);
        }
        return moisture;
    }

    /// <summary>Where a rain value falls on <see cref="MoistureCurve"/>, from 0 to 1.</summary>
    private static double Curve(double rain)
    {
        if (rain <= MoistureCurve[0]) return 0;
        for (int k = 1; k < MoistureCurve.Length; k++)
        {
            if (rain > MoistureCurve[k]) continue;
            double t = (rain - MoistureCurve[k - 1]) / (MoistureCurve[k] - MoistureCurve[k - 1]);
            return (k - 1 + t) / (MoistureCurve.Length - 1);
        }

        return 1;
    }

    /// <summary>Separable box blur repeated toward a Gaussian; the border clamps.</summary>
    internal static void Blur(MapGrid grid, double[] field, int radius, int passes)
    {
        double[] scratch = new double[field.Length];
        int side = grid.Side;
        for (int pass = 0; pass < passes; pass++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                for (int row = 0; row < side; row++)
                {
                    double sum = 0;
                    int Index(int along) => axis == 0 ? row * side + Math.Clamp(along, 0, side - 1) : Math.Clamp(along, 0, side - 1) * side + row;
                    for (int k = -radius; k <= radius; k++) sum += field[Index(k)];
                    for (int along = 0; along < side; along++)
                    {
                        scratch[Index(along)] = sum / (2 * radius + 1);
                        sum += field[Index(along + radius + 1)] - field[Index(along - radius)];
                    }
                }
                Array.Copy(scratch, field, field.Length);
            }
        }
    }

    internal static double Bilinear(MapGrid grid, double[] field, double gx, double gz)
    {
        gx = Math.Clamp(gx, 0, grid.Segments);
        gz = Math.Clamp(gz, 0, grid.Segments);
        int ix = Math.Min((int)gx, grid.Segments - 1), iz = Math.Min((int)gz, grid.Segments - 1);
        double tx = gx - ix, tz = gz - iz;
        int i = iz * grid.Side + ix;
        double near = field[i] + (field[i + 1] - field[i]) * tx;
        double far = field[i + grid.Side] + (field[i + grid.Side + 1] - field[i + grid.Side]) * tx;
        return near + (far - near) * tz;
    }
}
