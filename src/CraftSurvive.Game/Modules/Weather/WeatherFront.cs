using System.Numerics;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>Where a front was born on the weather lattice: its kind, spawn cell and time slot.</summary>
internal readonly record struct FrontKey(int Kind, long CellX, long CellZ, long Slot);

/// <summary>
/// One front (Den <c>design/weather-and-environment</c>): born at a place and hour, it drifts along the
/// prevailing flow, its track fixed hour by hour from birth, and how strongly it covers a place
/// follows its age, how well the ground under its centre suits it, and the distance from its centre.
/// Positions are world metres (x, z); times are absolute game hours.
/// </summary>
internal sealed class WeatherFront
{
    /// <summary>A front grows over this share of its life and fades over the last <see cref="FadingShare"/>.</summary>
    private const double GrowingShare = 0.2, FadingShare = 0.3;
    /// <summary>Full strength inside this share of the radius, falling smoothly to nothing at the edge.</summary>
    private const double CoreShare = 0.6;

    private readonly double[] trackX, trackZ, sustain;

    internal WeatherFront(FrontKey key, WeatherKind kind, double birthHours, double radiusMetres, double[] trackX, double[] trackZ, double[] sustain)
    {
        Key = key;
        Kind = kind;
        BirthHours = birthHours;
        RadiusMetres = radiusMetres;
        this.trackX = trackX;
        this.trackZ = trackZ;
        this.sustain = sustain;
    }

    internal FrontKey Key { get; }

    internal WeatherKind Kind { get; }

    internal double BirthHours { get; }

    internal double EndHours => BirthHours + Kind.LifeHours;

    internal double RadiusMetres { get; }

    internal bool Alive(double hours) => hours >= BirthHours && hours <= EndHours;

    /// <summary>The front's centre at a time within its life (clamped to its first and last hour).</summary>
    internal Vector2 Centre(double hours)
    {
        (int index, double t) = Along(hours);
        return new((float)Lerp(trackX[index], trackX[index + 1], t), (float)Lerp(trackZ[index], trackZ[index + 1], t));
    }

    /// <summary>Which way and how fast the centre is moving, metres per game hour.</summary>
    internal Vector2 Velocity(double hours)
    {
        (int index, _) = Along(hours);
        return new((float)(trackX[index + 1] - trackX[index]), (float)(trackZ[index + 1] - trackZ[index]));
    }

    /// <summary>The front's own strength at a time, 0..1: its age envelope times how well the ground under it suits it.</summary>
    internal double Strength(double hours)
    {
        if (!Alive(hours)) return 0;
        double age = (hours - BirthHours) / Kind.LifeHours;
        double envelope = Math.Min(Smooth(age / GrowingShare), Smooth((1 - age) / FadingShare));
        (int index, double t) = Along(hours);
        return envelope * Lerp(sustain[index], sustain[index + 1], t);
    }

    /// <summary>How strongly the front covers a place at a time, 0..1.</summary>
    internal double Cover(double x, double z, double hours)
    {
        double strength = Strength(hours);
        if (strength <= 0) return 0;
        Vector2 centre = Centre(hours);
        double distance = Math.Sqrt(((x - centre.X) * (x - centre.X)) + ((z - centre.Y) * (z - centre.Y))) / RadiusMetres;
        return strength * Smooth((1 - distance) / (1 - CoreShare));
    }

    private (int Index, double T) Along(double hours)
    {
        double at = Math.Clamp(hours - BirthHours, 0, trackX.Length - 1.000001);
        int index = (int)at;
        return (index, at - index);
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static double Smooth(double v)
    {
        v = Math.Clamp(v, 0, 1);
        return v * v * (3 - (2 * v));
    }
}
