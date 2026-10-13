using System.Numerics;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>One wave train of the spectrum: its wavelength (metres), its bearing off the wind (radians) and its phase offset.</summary>
internal readonly record struct WaveTrain(double Wavelength, double Angle, double Offset)
{
    internal double Number => 2 * Math.PI / Wavelength;

    /// <summary>Deep-water angular frequency (radians a second).</summary>
    internal double Frequency => Math.Sqrt(SeaWaves.Gravity * Number);
}

/// <summary>
/// What a point of water takes of the sea state whatever the wind: how far the wind blows over water to
/// reach it (metres), how much of the open sea's swell reaches it (0 to 1), and how much it may move at all
/// (shore coverage, 0 to 1, <see cref="WaterWaveSettings.Coverage"/>).
/// </summary>
internal readonly record struct WaveExposure(double FetchMetres, double Swell, double Coverage)
{
    internal static WaveExposure Still => new(0, 0, 0);
}

/// <summary>
/// The waves' one definition (#9902): the water shader displaces the near water by it
/// (<c>content/game/shaders/water-flow.wgsl</c> mirrors every constant here, and a check holds them
/// equal), and boats and swimmers ride the same surface through <see cref="SeaState"/>.
/// <para>
/// Eight Gerstner trains spread about the wind, under a wind-sea spectrum. How high they stand and where it peaks follow
/// fetch-limited wind-wave growth (JONSWAP): a calm pond is flat, a big lake in a gale runs long and
/// high, a river's short fetch keeps it to a chop. The sea adds a long swell. Height is capped so the
/// displacement stays within a metre of the mesh, as the Engine's culling by undisplaced bounds asks.
/// Waves shorter than the shortest train are left to the shader's ripple normals. Engine-free.
/// </para>
/// </summary>
internal static class SeaWaves
{
    internal const double Gravity = 9.81;

    /// <summary>The trains, log-spaced from 4 m (four vertices of the 1 m mesh a wave) to 43 m, about the wind.</summary>
    internal static readonly WaveTrain[] Trains =
    [
        new(4.0, 0.35, 0.0),
        new(5.6, -0.30, 1.7),
        new(7.9, 0.18, 4.1),
        new(11.0, -0.42, 2.7),
        new(15.5, 0.26, 5.3),
        new(22.0, -0.12, 0.9),
        new(31.0, 0.08, 3.6),
        new(43.0, -0.05, 2.2),
    ];

    /// <summary>The train the sea's swell rides on: the longest.</summary>
    internal const int SwellTrain = 7;

    /// <summary>Fetch-limited growth: Hs = HeightGrowth·U·√(F/g), and gTp/U = PeriodGrowth·(gF/U²)^⅓.</summary>
    internal const double HeightGrowth = 1.6e-3, PeriodGrowth = 0.286;

    /// <summary>A fully developed sea: Hs = DevelopedHeight·U²/g, and a peak period of DevelopedPeriod·U/g.</summary>
    internal const double DevelopedHeight = 0.21, DevelopedPeriod = 7.16;

    /// <summary>The highest significant wave height drawn (metres): the displacement stays near the mesh's bounds.</summary>
    internal const double MaximumHeight = 1.0;

    /// <summary>
    /// The spectrum about its peak: longer waves fall away within this width (natural-log wavelength);
    /// shorter ones keep amplitude in proportion to wavelength to this power - the wind sea's tail
    /// (Pierson-Moskowitz, energy as frequency to the minus fifth), every short wave about as steep as the
    /// next, which is what makes a gale's water rough rather than merely swelling.
    /// </summary>
    internal const double SpectrumWidth = 0.45, TailPower = 1;

    /// <summary>No train is steeper than this (wave number times amplitude), so crests never fold.</summary>
    internal const double Steepness = 0.12;

    /// <summary>How far a crest moves sideways against its height (Gerstner's Q).</summary>
    internal const double Choppiness = 0.9;

    /// <summary>The open sea's swell (metres of amplitude) on <see cref="SwellTrain"/>, whatever the local wind.</summary>
    internal const double SwellAmplitude = 0.15;

    /// <summary>Motion fades over this distance inside the near water's edge, to meet the flat distant water.</summary>
    internal const double EdgeFadeMetres = 24;

    /// <summary>Fetch below this (metres) raises no waves; it also keeps the growth's logarithms finite.</summary>
    internal const double LeastFetch = 1;

    /// <summary>A lake's effective fetch (metres): about the distance across it, from its area.</summary>
    internal static double LakeFetchMetres(double areaSquareKilometres) => Math.Sqrt(Math.Max(0, areaSquareKilometres)) * MetresPerKilometre;

    private const double MetresPerKilometre = 1000;

    /// <summary>Significant wave height (metres) for a wind (m/s) over a fetch (metres).</summary>
    internal static double SignificantHeight(double wind, double fetch)
    {
        if (wind <= 0 || fetch < LeastFetch) return 0;
        double limited = HeightGrowth * wind * Math.Sqrt(fetch / Gravity);
        double developed = DevelopedHeight * wind * wind / Gravity;
        return Math.Min(MaximumHeight, Math.Min(limited, developed));
    }

    /// <summary>The spectrum's peak wavelength (metres) for a wind over a fetch.</summary>
    internal static double PeakWavelength(double wind, double fetch)
    {
        if (wind <= 0 || fetch < LeastFetch) return Trains[0].Wavelength;
        double limited = PeriodGrowth * wind / Gravity * Math.Cbrt(Gravity * fetch / (wind * wind));
        double period = Math.Min(limited, DevelopedPeriod * wind / Gravity);
        return Gravity * period * period / (2 * Math.PI);
    }

    /// <summary>Each train's amplitude (metres) at a point for a wind speed, before coverage and the edge fade.</summary>
    internal static void Amplitudes(double wind, WaveExposure exposure, Span<double> amplitudes)
    {
        double height = SignificantHeight(wind, exposure.FetchMetres);
        double peak = PeakWavelength(wind, exposure.FetchMetres);
        double sum = 0;
        for (int i = 0; i < Trains.Length; i++)
        {
            double ratio = Trains[i].Wavelength / peak, distance = Math.Log(ratio) / SpectrumWidth;
            amplitudes[i] = ratio <= 1 ? Math.Pow(ratio, TailPower) : Math.Exp(-0.5 * distance * distance);
            sum += amplitudes[i] * amplitudes[i];
        }

        // Hs is four times the surface's standard deviation; a train of amplitude a has variance a²/2.
        double scale = sum > 0 ? height / (2 * Math.Sqrt(2) * Math.Sqrt(sum)) : 0;
        for (int i = 0; i < Trains.Length; i++)
            amplitudes[i] = Math.Min(amplitudes[i] * scale, Steepness / Trains[i].Number);
        amplitudes[SwellTrain] += SwellAmplitude * exposure.Swell;
    }

    /// <summary>
    /// How far the water at a point (world metres) is carried from its rest: sideways (X, Z) and up (Y).
    /// <paramref name="phases"/> are the trains' phases now (<see cref="SeaState.Phase"/>), relative to
    /// <paramref name="anchorX"/>, <paramref name="anchorZ"/>; <paramref name="fade"/> is the edge fade.
    /// </summary>
    internal static Vector3 Displacement(double x, double z, double anchorX, double anchorZ, Vector2 wind, double windSpeed,
        ReadOnlySpan<double> phases, WaveExposure exposure, double fade)
    {
        double share = exposure.Coverage * fade;
        if (share <= 0) return Vector3.Zero;
        Span<double> amplitudes = stackalloc double[Trains.Length];
        Amplitudes(windSpeed, exposure, amplitudes);
        double dx = 0, dy = 0, dz = 0, px = x - anchorX, pz = z - anchorZ;
        for (int i = 0; i < Trains.Length; i++)
        {
            (double ux, double uz) = Direction(wind, Trains[i].Angle);
            double theta = (Trains[i].Number * ((ux * px) + (uz * pz))) + phases[i];
            double a = amplitudes[i] * share, sideways = Choppiness * a * Math.Cos(theta);
            dx += ux * sideways;
            dz += uz * sideways;
            dy += a * Math.Sin(theta);
        }

        return new((float)dx, (float)dy, (float)dz);
    }

    /// <summary>A train's direction: the wind's, turned by its angle (positive from +X toward +Z).</summary>
    internal static (double X, double Z) Direction(Vector2 wind, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return ((wind.X * c) - (wind.Y * s), (wind.X * s) + (wind.Y * c));
    }

    /// <summary>The edge fade at a point (world metres) inside a box (min x, min z, max x, max z).</summary>
    internal static double EdgeFade(double x, double z, Vector4 bounds)
    {
        double edge = Math.Min(Math.Min(x - bounds.X, z - bounds.Y), Math.Min(bounds.Z - x, bounds.W - z));
        double t = Math.Clamp(edge / EdgeFadeMetres, 0, 1);
        return t * t * (3 - (2 * t));
    }
}
