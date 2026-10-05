namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Metre-space relief anchored to saved geography. Never creates or reroutes drainage.</summary>
internal static class RegionalTerrain
{
    private enum Family { Dry, Upland, Frozen }
    internal readonly record struct NoiseBank(int Wavelength, int Octaves, double Persistence, double Amplitude, ulong Salt)
    {
        internal double Sample(ulong seed, double x, double z)
        {
            double value = 0, weight = 1, total = 0;
            int scale = Wavelength;
            for (int octave = 0; octave < Octaves; octave++, scale /= 2)
            {
                value += weight * (2 * TerrainRecipe.ValueNoise(seed ^ Salt ^ unchecked((ulong)octave * GenerationConstants.CoordinateXMultiplier), x, z, scale) - 1);
                total += weight;
                weight *= Persistence;
            }
            return value / total * Amplitude;
        }
    }

    internal readonly record struct Profile(NoiseBank Structure, NoiseBank Fine, double Across, double Along);
    // Finest wavelength is three metres: meaningful gradients on the one-metre density grid.
    internal static readonly Profile Dry = new(new(64, 3, 0.45, 16, 0x389CA413UL), new(12, 3, 0.6, 1.6, 0x715FA998UL), 1, 1);
    internal static readonly Profile Upland = new(new(40, 3, 0.55, 11, 0xA8790213UL), new(12, 3, 0.55, 2.2, 0x438712DEUL), 1, 1);
    internal static readonly Profile Frozen = new(new(48, 3, 0.45, 15, 0xDA51C341UL), new(12, 3, 0.65, 1.8, 0xFC213487UL), 1, 0.35);
    private static readonly NoiseBank Warp = new(96, 3, 0.5, 12, 0xC983012AUL);
    private static readonly NoiseBank Activity = new(192, 2, 0.5, 1, 0x139CAE91UL);
    private const ulong WarpZSalt = 0xBA129A23UL;
    private const ulong BearingSalt = 0x682FD1ACUL;
    private const int BearingDomain = 4096;
    private const double FullTurn = 2 * Math.PI;
    // Family blends straddle the map biome thresholds (tundra below 0.28, desert below 0.22).
    private const double ColdStart = 0.2, ColdEnd = 0.34;
    private const double DryStart = 0.16, DryEnd = 0.3;
    private const double DetailGain = 2;
    private const double QuietMinimum = 0.12;
    private const double FineQuietMinimum = 0.2;
    private const double ShelfStart = -0.22, ShelfEnd = 0.22;
    private const double OutcropStart = 0.12, OutcropEnd = 0.48;
    private const double OutcropFraction = 0.6;
    private const double FrozenRidgePower = 3;
    private const double ActivityStart = -0.4, ActivityEnd = 0.4;

    internal static (double Dry, double Upland, double Frozen) Weights(MapSample geography)
    {
        double cold = 1 - Ease(ColdStart, ColdEnd, geography.Temperature);
        double dry = (1 - cold) * (1 - Ease(DryStart, DryEnd, geography.Moisture));
        return (dry, 1 - cold - dry, cold);
    }

    internal static double Relief(ulong seed, MapSample geography, double x, double z)
    {
        // Erosion already suppresses Detail on floors and protected sites. Its zero remains
        // exactly zero; even the independent fine bank cannot obstruct a saved channel.
        double allowance = Math.Clamp(geography.Detail * DetailGain, 0, 1) * (1 - geography.Protection);
        if (allowance == 0) return 0;
        double bearing = TerrainRecipe.ValueNoise(seed ^ BearingSalt, BearingDomain, 0, BearingDomain) * FullTurn;
        double u = x * Math.Cos(bearing) - z * Math.Sin(bearing);
        double v = x * Math.Sin(bearing) + z * Math.Cos(bearing);
        double wx = u + Warp.Sample(seed, u, v), wz = v + Warp.Sample(seed ^ WarpZSalt, u, v);
        double activity = Ease(ActivityStart, ActivityEnd, Activity.Sample(seed, x, z));
        double structureMask = QuietMinimum + (1 - QuietMinimum) * activity;
        double fineMask = FineQuietMinimum + (1 - FineQuietMinimum) * Math.Max(activity, geography.Rock);
        var weights = Weights(geography);
        double dry = Shape(Dry, seed, wx, wz, Family.Dry, geography.Rock, structureMask, fineMask);
        double upland = Shape(Upland, seed, wx, wz, Family.Upland, geography.Rock, structureMask, fineMask);
        double frozen = Shape(Frozen, seed, wx, wz, Family.Frozen, geography.Rock, structureMask, fineMask);
        return allowance * Math.Clamp(weights.Dry * dry + weights.Upland * upland + weights.Frozen * frozen,
            -WorldMap.LocalReliefLimit, WorldMap.LocalReliefLimit);
    }

    private static double Shape(Profile profile, ulong seed, double x, double z, Family family, double rock, double structureMask, double fineMask)
    {
        double n = profile.Structure.Sample(seed, x * profile.Across, z * profile.Along) / profile.Structure.Amplitude;
        double shape = family switch
        {
            // Broad benches connected by scarps; no quantized height steps.
            Family.Dry => 2 * Ease(ShelfStart, ShelfEnd, n) - 1,
            // Gentle soil rolls with occasional rounded rock breaks.
            Family.Upland => n + OutcropFraction * rock * Ease(OutcropStart, OutcropEnd, n),
            // Stretched sharp ribs with generous quieter space between them.
            _ => Math.Pow(1 - Math.Abs(n), FrozenRidgePower) * 2 - 1,
        };
        return profile.Structure.Amplitude * shape * structureMask
            + profile.Fine.Sample(seed, x, z) * fineMask;
    }

    private static double Ease(double start, double end, double value) => WorldMap.Smooth(Math.Clamp((value - start) / (end - start), 0, 1));
}
