using System.Globalization;
using System.Numerics;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The waves over the near water now (#9902): the wind they stand to, the clock they run on, and the
/// surface they make. One owner for the water's drawing and for what rides it: the shader is handed this
/// state's phases and wind each update (<see cref="WaterSurfaces"/>), and a boat or a swimmer asks
/// <see cref="Lift"/> for the same surface. The water owner's flat level stays the water's level for
/// everything else - depth, fords, stage, ice, wading. Engine-free.
/// </summary>
internal sealed class SeaState
{
    /// <summary>The wind (m/s) in still weather, and what the weather's full wind channel adds: a gale.</summary>
    internal const double CalmWind = 2, GaleWind = 18;

    /// <summary>Waves rise toward a stronger wind, and fall after it, over about this many game hours.</summary>
    internal const double BuildHours = 1;

    /// <summary>The wind speed (m/s) the weather's 0-1 wind channel stands for.</summary>
    internal static double WindFor(double windChannel) => CalmWind + (GaleWind * Math.Clamp(windChannel, 0, 1));

    /// <summary>The trains' direction is taken from the wind again once the player is this far from where it last was.</summary>
    internal const double RelatchMetres = 2000;

    /// <summary>How many times a point's source is refined when the surface is read at a point.</summary>
    private const int LiftIterations = 3;

    private double hours = double.NaN;
    private bool latched;
    private readonly double[] phases = new double[SeaWaves.Trains.Length];

    /// <summary>The wind speed the waves stand to now (m/s), eased toward the weather's.</summary>
    internal double WindSpeed { get; private set; } = CalmWind;

    /// <summary>The direction the trains run (a unit vector over the ground, world x and z).</summary>
    internal Vector2 Wind { get; private set; } = Vector2.UnitX;

    /// <summary>Where the trains' phases are measured from (world metres), near the player, so they stay precise.</summary>
    internal (double X, double Z) Anchor { get; private set; }

    /// <summary>The waves' clock: seconds of simulation since the session began.</summary>
    internal double Seconds { get; private set; }

    /// <summary>Assisted: a wind speed (m/s) held whatever the weather, for comparisons and captures; null follows the weather.</summary>
    internal double? HeldWind { get; set; }

    /// <summary>
    /// Moves the waves on by simulation seconds, and eases them toward the weather's wind (its 0-1 channel
    /// and flow direction at the player) by the game hours passed.
    /// </summary>
    internal void Advance(double seconds, double gameHours, double windChannel, Vector2 flow, double playerX, double playerZ)
    {
        Seconds += Math.Max(0, seconds);
        double target = HeldWind ?? WindFor(windChannel);
        double passed = double.IsNaN(hours) ? double.PositiveInfinity : Math.Max(0, gameHours - hours);
        WindSpeed += (target - WindSpeed) * (1 - Math.Exp(-passed / BuildHours));
        if (HeldWind is double held) WindSpeed = held;
        hours = gameHours;
        // The trains keep a direction while the player stays about one place: turning them would sweep
        // the whole surface. Far away (a journey, a reload), they take up the wind there.
        double away = Math.Sqrt(((playerX - Anchor.X) * (playerX - Anchor.X)) + ((playerZ - Anchor.Z) * (playerZ - Anchor.Z)));
        if (!latched || away > RelatchMetres)
        {
            Wind = flow.LengthSquared() > 0 ? Vector2.Normalize(flow) : Vector2.UnitX;
            Anchor = (Math.Round(playerX), Math.Round(playerZ));
            latched = true;
        }

        for (int i = 0; i < phases.Length; i++)
            phases[i] = Math.IEEERemainder(SeaWaves.Trains[i].Offset - (SeaWaves.Trains[i].Frequency * Seconds), 2 * Math.PI);
    }

    /// <summary>The trains' phases now, measured from <see cref="Anchor"/>.</summary>
    internal ReadOnlySpan<double> Phases => phases;

    /// <summary>
    /// Four trains' phases for the shader, which measures from the frame's origin rather than the anchor:
    /// the difference is folded in here, in double precision.
    /// </summary>
    internal Vector4 ShaderPhases(int first, double originX, double originZ)
    {
        Span<float> four = stackalloc float[4];
        for (int k = 0; k < 4; k++)
        {
            int i = first + k;
            (double ux, double uz) = SeaWaves.Direction(Wind, SeaWaves.Trains[i].Angle);
            double shift = SeaWaves.Trains[i].Number * ((ux * (originX - Anchor.X)) + (uz * (originZ - Anchor.Z)));
            four[k] = (float)Math.IEEERemainder(phases[i] + shift, 2 * Math.PI);
        }

        return new(four[0], four[1], four[2], four[3]);
    }

    /// <summary>How far the water at a point (world metres) is carried from its rest now.</summary>
    internal Vector3 Displacement(double x, double z, WaveExposure exposure, double fade) =>
        SeaWaves.Displacement(x, z, Anchor.X, Anchor.Z, Wind, WindSpeed, phases, exposure, fade);

    /// <summary>
    /// How far the drawn surface over a point stands above the water's rest level now (metres). The waves
    /// carry water sideways as well as up, so the water over the point came from nearby: its source is found
    /// by a few refinements, and its rise taken.
    /// </summary>
    internal double Lift(double x, double z, WaveExposure exposure, double fade)
    {
        if (exposure.Coverage * fade <= 0) return 0;
        double sx = x, sz = z;
        Vector3 moved = Vector3.Zero;
        for (int k = 0; k < LiftIterations; k++)
        {
            moved = Displacement(sx, sz, exposure, fade);
            (sx, sz) = (x - moved.X, z - moved.Z);
        }

        return Displacement(sx, sz, exposure, fade).Y;
    }

    /// <summary>The significant wave height (metres) the wind raises over a fetch now.</summary>
    internal double Height(double fetchMetres) => SeaWaves.SignificantHeight(WindSpeed, fetchMetres);

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"wind={WindSpeed:F1}m/s{(HeldWind is null ? "" : " held")} toward={Wind.X:F2},{Wind.Y:F2} anchor={Anchor.X:F0},{Anchor.Z:F0} clock={Seconds:F1}s");
}
