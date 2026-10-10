namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// An analytic erosion filter (#9822): branching gullies and ridges cut into a smooth height field,
/// aligned with its slope and evaluated point by point, after Clay John's eroded terrain noise and Rune
/// Skovbo Johansen's "Fast and Gorgeous Erosion Filter" (2026).
/// <para>
/// Each octave blends cosine stripes from a jittered lattice of cells, every stripe running down the
/// slope (it varies only across it), so the land is furrowed into gullies with ridges between them. The
/// slope each octave follows includes the detail the coarser octaves added, so smaller gullies branch off
/// the larger ones rather than lying across them. A gully's depth scales with the steepness and its own
/// wavelength: flat land stays flat, mountainsides are furrowed most.
/// </para>
/// It is surface form only: the caller keeps it off rivers and anything that must not move.
/// </summary>
internal sealed class ErosionFilter(ulong seed, ErosionFilterSettings settings)
{
    private const ulong JitterXSalt = 0x3A1C_75E9_02B4_D86FUL, JitterZSalt = 0x9F5E_2C83_61A7_04BDUL;

    private const ulong CrestSalt = 0x51D7_33A8_C6E0_9B4FUL;

    internal ErosionFilterSettings Settings => settings;

    /// <summary>
    /// The crest relief at a point of a given height (metres to add): high ground broken into peaks and
    /// cols by ridged noise, none below <see cref="ErosionFilterSettings.CrestFromMetres"/>, whole by
    /// <see cref="ErosionFilterSettings.CrestFullMetres"/>. The filter's gullies follow the slope and so
    /// leave a crest line smooth; this is what makes a high range's skyline jagged.
    /// </summary>
    internal double Crest(double x, double z, double elevation)
    {
        if (settings.CrestReliefMetres <= 0 || elevation <= settings.CrestFromMetres) return 0;
        double share = WorldMap.Smooth(Math.Clamp((elevation - settings.CrestFromMetres) / (settings.CrestFullMetres - settings.CrestFromMetres), 0, 1));
        double ridges = MapNoise.Ridged(seed ^ CrestSalt, x / settings.CrestWavelengthMetres, z / settings.CrestWavelengthMetres, CrestOctaves, CrestPersistence);
        return settings.CrestReliefMetres * share * (ridges - CrestMiddle);
    }

    /// <summary>The crest noise's octaves and persistence, and the ridged value that adds nothing (higher stands, lower sinks).</summary>
    private const int CrestOctaves = 3;
    private const double CrestPersistence = 0.5, CrestMiddle = 0.55;

    /// <summary>
    /// The height to add at a point (metres) given the smooth field's slope there (rise over run along x
    /// and z). Zero where the slope is zero.
    /// </summary>
    internal double Offset(double x, double z, double slopeX, double slopeZ)
    {
        double total = 0, detailX = 0, detailZ = 0;
        double wavelength = settings.LongestMetres;
        for (int octave = 0; octave < settings.Octaves; octave++)
        {
            double gx = slopeX + detailX, gz = slopeZ + detailZ;
            double steep = Math.Sqrt((gx * gx) + (gz * gz));
            if (steep < 1e-6) break;
            // Stripes vary across the slope: their direction is the slope turned a quarter.
            double dirX = -gz / steep, dirZ = gx / steep;
            (double h, double dx, double dz) = Gullies(x / wavelength, z / wavelength, dirX, dirZ, octave);
            double amplitude = settings.Strength * Math.Min(steep, settings.SteepestSlope) * wavelength;
            total += h * amplitude;
            // The detail's own slope (its derivative in world metres) bends the next octave's gullies.
            detailX += dx * amplitude / wavelength;
            detailZ += dz * amplitude / wavelength;
            wavelength *= 0.5;
        }

        return total;
    }

    /// <summary>
    /// One octave in lattice units: the weighted blend of each nearby cell's stripe, from -1 (a gully's
    /// floor) to 1 (a ridge), and its derivative.
    /// </summary>
    private (double H, double Dx, double Dz) Gullies(double px, double pz, double dirX, double dirZ, int octave)
    {
        double ix = Math.Floor(px), iz = Math.Floor(pz), fx = px - ix, fz = pz - iz;
        double sum = 0, sumX = 0, sumZ = 0, weights = 0;
        ulong octaveSeed = seed + ((ulong)octave * 0x9E37_79B9_7F4A_7C15UL);
        for (int i = -1; i <= 2; i++)
        for (int j = -1; j <= 2; j++)
        {
            long cx = (long)ix + i, cz = (long)iz + j;
            double jitterX = 0.5 * MapNoise.Unit(octaveSeed ^ JitterXSalt, cx, cz), jitterZ = 0.5 * MapNoise.Unit(octaveSeed ^ JitterZSalt, cx, cz);
            // From the cell's jittered centre to the point.
            double ox = fx - i - jitterX, oz = fz - j - jitterZ;
            double weight = Math.Exp(-2 * ((ox * ox) + (oz * oz)));
            double phase = ((ox * dirX) + (oz * dirZ)) * Math.Tau;
            sum += Math.Cos(phase) * weight;
            double slope = -Math.Sin(phase) * Math.Tau * weight;
            sumX += slope * dirX;
            sumZ += slope * dirZ;
            weights += weight;
        }

        return (sum / weights, sumX / weights, sumZ / weights);
    }
}

/// <param name="LongestMetres">The longest gullies' spacing; each octave halves it.</param>
/// <param name="Octaves">How many octaves, so the finest is the longest over 2^(octaves - 1).</param>
/// <param name="Strength">A gully's depth as a share of its spacing on a slope of 1 (45°).</param>
/// <param name="SteepestSlope">Slopes steeper than this cut no deeper.</param>
/// <param name="CrestReliefMetres">How far high ground is broken into peaks and cols (<see cref="ErosionFilter.Crest"/>); 0 for none.</param>
/// <param name="CrestFromMetres">Crest relief starts at this height and is whole by <paramref name="CrestFullMetres"/>.</param>
/// <param name="CrestWavelengthMetres">The spacing of the crest's peaks.</param>
internal sealed record ErosionFilterSettings(double LongestMetres, int Octaves, double Strength, double SteepestSlope,
    double CrestReliefMetres = 0, double CrestFromMetres = 0, double CrestFullMetres = 1, double CrestWavelengthMetres = 1)
{
    /// <summary>
    /// For a continent seen from tens of kilometres (#9822): gullies from 8 km down to 500 m (the finest the
    /// horizon's 250 m tier can draw), and high ground over 2.5 km broken into peaks about 5 km apart, up to
    /// 1.2 km of relief by 5 km up.
    /// </summary>
    internal static ErosionFilterSettings Continent { get; } = new(8000, 5, 0.12, 1.2, 1200, 2500, 5000, 5000);
}

/// <summary>
/// A map's height with the erosion filter cut into it (#9822): the slope from central differences of
/// the smooth map, the filter faded out near rivers (it must not move their channels) and near sea level
/// (it must not move the coast).
/// </summary>
internal sealed class ErodedHeights(WorldMap map, ErosionFilter filter)
{
    /// <summary>The slope is measured across this many metres either side.</summary>
    internal const double SlopeReachMetres = 500;
    /// <summary>The filter fades in over this height above the water, and this many river half-widths from a channel.</summary>
    private const double ShoreFadeMetres = 150, RiverFadeWidths = 4;

    internal WorldMap Map => map;

    /// <summary>The filtered height at a point, given the map's own sample there.</summary>
    internal double Height(double x, double z, MapSample sample)
    {
        if (sample.Elevation <= GenerationConstants.WaterLevel || sample.InRiver) return sample.Elevation;
        double slopeX = (map.Sample(x + SlopeReachMetres, z).Elevation - map.Sample(x - SlopeReachMetres, z).Elevation) / (2 * SlopeReachMetres);
        double slopeZ = (map.Sample(x, z + SlopeReachMetres).Elevation - map.Sample(x, z - SlopeReachMetres).Elevation) / (2 * SlopeReachMetres);
        return Height(x, z, sample, slopeX, slopeZ);
    }

    /// <summary>
    /// The filtered height at a point given the map's sample and its slope there, measured as
    /// <see cref="Height(double, double, MapSample)"/> does (central differences across <see cref="SlopeReachMetres"/>):
    /// for a caller that already holds the neighbouring samples.
    /// </summary>
    internal double Height(double x, double z, MapSample sample, double slopeX, double slopeZ) =>
        Height(x, z, sample, slopeX, slopeZ, map.Rivers.Nearest(x, z));

    /// <summary>As the overload above, given the river query's answer at the point (for a caller that needs it too).</summary>
    internal double Height(double x, double z, MapSample sample, double slopeX, double slopeZ, RiverInfluence? nearest)
    {
        double above = sample.Elevation - GenerationConstants.WaterLevel;
        if (above <= 0 || sample.InRiver) return sample.Elevation;
        double fade = WorldMap.Smooth(Math.Clamp(above / ShoreFadeMetres, 0, 1));
        if (nearest is RiverInfluence river)
            fade *= WorldMap.Smooth(Math.Clamp((river.Distance - river.HalfWidth) / (river.HalfWidth * RiverFadeWidths + SlopeReachMetres), 0, 1));
        return fade <= 0 ? sample.Elevation : sample.Elevation + (fade * (filter.Offset(x, z, slopeX, slopeZ) + filter.Crest(x, z, sample.Elevation)));
    }
}
