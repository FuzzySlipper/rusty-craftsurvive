using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// What a player standing at a viewpoint sees of the land: for each bearing, the highest elevation
/// angle the terrain reaches out to a range, and how far off that ridge stands. It is the measure of
/// whether ranges read on the horizon (#9782). The earth is flat here, as the backdrop draws it.
/// </summary>
internal sealed class Skyline
{
    /// <summary>Bearings are sampled this many to a degree.</summary>
    internal const int PerDegree = 4;
    /// <summary>The eye stands this far above the ground.</summary>
    private const double EyeMetres = 1.7;
    /// <summary>The march starts this far out (the near ground is the player's own) and steps a share of the distance.</summary>
    private const double FirstMetres = 200, LeastStepMetres = 25, StepShare = 0.004;

    internal Skyline(Func<double, double, MapSample> sample, double radius, double x, double z, double reachMetres)
    {
        X = x;
        Z = z;
        Eye = Math.Max(sample(x, z).Elevation, GenerationConstants.WaterLevel) + EyeMetres;
        Degrees = new double[360 * PerDegree];
        Distance = new double[360 * PerDegree];
        for (int b = 0; b < Degrees.Length; b++)
        {
            double bearing = b * Math.PI / 180 / PerDegree;
            // Bearings run clockwise from north, which is -Z.
            double dx = Math.Sin(bearing), dz = -Math.Cos(bearing);
            double best = double.NegativeInfinity, at = 0;
            for (double r = FirstMetres; r <= reachMetres; r += Math.Max(LeastStepMetres, r * StepShare))
            {
                double px = x + (dx * r), pz = z + (dz * r);
                if (Math.Abs(px) > radius || Math.Abs(pz) > radius) break;
                double ground = Math.Max(sample(px, pz).Elevation, GenerationConstants.WaterLevel);
                double angle = Math.Atan2(ground - Eye, r) * 180 / Math.PI;
                if (angle > best)
                {
                    best = angle;
                    at = r;
                }
            }

            Degrees[b] = best;
            Distance[b] = at;
        }
    }

    internal double X { get; }

    internal double Z { get; }

    internal double Eye { get; }

    /// <summary>The skyline's elevation angle in degrees by bearing step.</summary>
    internal double[] Degrees { get; }

    /// <summary>How far off, in metres, the ridge making the skyline stands, by bearing step.</summary>
    internal double[] Distance { get; }

    /// <summary>
    /// The skyline as a strip: a pixel a quarter degree across, <paramref name="pixelsPerDegree"/>
    /// up, from a degree below the horizon. The land is shaded by its ridge's distance, near dark and
    /// far pale; faint lines mark each whole degree and the horizon.
    /// </summary>
    internal Image Draw(int pixelsPerDegree, double topDegrees, double reachMetres)
    {
        const double BelowDegrees = 1;
        int height = (int)Math.Ceiling((topDegrees + BelowDegrees) * pixelsPerDegree);
        Image image = new(Degrees.Length, height);
        for (int b = 0; b < Degrees.Length; b++)
        {
            double far = Math.Clamp(Distance[b] / reachMetres, 0, 1);
            (double R, double G, double B) land = Mix((0.18, 0.24, 0.2), (0.7, 0.76, 0.82), Math.Sqrt(far));
            for (int y = 0; y < height; y++)
            {
                double degrees = topDegrees - ((y + 0.5) / pixelsPerDegree);
                bool ground = degrees <= Degrees[b];
                bool line = Math.Abs(degrees - Math.Round(degrees)) < 0.5 / pixelsPerDegree;
                (double R, double G, double B) sky = Math.Abs(degrees) < 0.5 / pixelsPerDegree ? (0.55, 0.3, 0.3)
                    : line ? (0.78, 0.84, 0.92) : (0.88, 0.92, 0.97);
                image.Set(b, y, ground ? land : sky);
            }

            // Each compass point gets a tick at the top.
            if (b % (45 * PerDegree) == 0)
                for (int y = 0; y < pixelsPerDegree / 2; y++) image.Set(b, y, (0.8, 0.1, 0.1));
        }

        return image;
    }

    private static (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + ((b.R - a.R) * t), a.G + ((b.G - a.G) * t), a.B + ((b.B - a.B) * t));
}
