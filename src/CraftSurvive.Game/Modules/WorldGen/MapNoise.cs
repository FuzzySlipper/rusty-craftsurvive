namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Seeded 2D gradient noise for map-scale fields. Gradient noise has no lattice-aligned
/// plateaus, so domain-warped landforms do not reveal the grid the way value noise does.
/// Coordinates are metres divided by a wavelength; output is roughly -1..1.
/// </summary>
internal static class MapNoise
{
    private const int GradientCount = 16;
    private const double GradientTurn = 2 * Math.PI / GradientCount;
    private static readonly double[] GradientX = Enumerable.Range(0, GradientCount).Select(i => Math.Cos(i * GradientTurn)).ToArray();
    private static readonly double[] GradientZ = Enumerable.Range(0, GradientCount).Select(i => Math.Sin(i * GradientTurn)).ToArray();
    private const double Lacunarity = 2.03;
    private const ulong OctaveSalt = 0xD1B54A32D192ED03UL;

    internal static double Gradient(ulong seed, double x, double z)
    {
        long cx = (long)Math.Floor(x), cz = (long)Math.Floor(z);
        double fx = x - cx, fz = z - cz;
        double u = Fade(fx), v = Fade(fz);
        double a = Dot(seed, cx, cz, fx, fz), b = Dot(seed, cx + 1, cz, fx - 1, fz);
        double c = Dot(seed, cx, cz + 1, fx, fz - 1), d = Dot(seed, cx + 1, cz + 1, fx - 1, fz - 1);
        // sqrt(2) rescales the unit-gradient lattice extremes towards -1..1.
        return Math.Sqrt(2) * (a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v);
    }

    /// <summary>Fractal sum normalised by its total weight.</summary>
    internal static double Fbm(ulong seed, double x, double z, int octaves, double persistence)
    {
        double sum = 0, weight = 1, total = 0, frequency = 1;
        for (int i = 0; i < octaves; i++)
        {
            sum += weight * Gradient(seed + (ulong)i * OctaveSalt, x * frequency, z * frequency);
            total += weight;
            weight *= persistence;
            frequency *= Lacunarity;
        }
        return sum / total;
    }

    /// <summary>Sharp-crested 0..1 ridges: each octave is folded about zero and squared.</summary>
    internal static double Ridged(ulong seed, double x, double z, int octaves, double persistence)
    {
        double sum = 0, weight = 1, total = 0, frequency = 1, previous = 1;
        for (int i = 0; i < octaves; i++)
        {
            double ridge = 1 - Math.Abs(Gradient(seed + (ulong)i * OctaveSalt, x * frequency, z * frequency));
            ridge *= ridge;
            // Finer ridges ride on the crests of coarser ones rather than filling valleys.
            sum += weight * ridge * previous;
            total += weight;
            previous = ridge;
            weight *= persistence;
            frequency *= Lacunarity;
        }
        return sum / total;
    }

    internal static double Unit(ulong seed, long a, long b) =>
        TerrainRecipe.HashUnit(TerrainRecipe.CoordinateHash(seed, a, b));

    private static double Dot(ulong seed, long cx, long cz, double dx, double dz)
    {
        int g = (int)(TerrainRecipe.CoordinateHash(seed, cx, cz) % GradientCount);
        return GradientX[g] * dx + GradientZ[g] * dz;
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
}
