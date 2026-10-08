using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// What the air is doing at a place, 0..1 per channel (Den <c>design/weather-and-environment</c>).
/// Presentation reads these; a new environmental system adds to them rather than to its consumers.
/// </summary>
internal readonly record struct EnvironmentChannels(double Precipitation, double Cloud, double Wind, double Cold, double Murk, double Arcane)
{
    internal static EnvironmentChannels Clear => default;

    /// <summary>Two contributions together: each channel as 1 - (1 - a)(1 - b), so overlapping fronts deepen without passing 1.</summary>
    internal EnvironmentChannels With(EnvironmentChannels other, double strength) => new(
        Join(Precipitation, other.Precipitation * strength),
        Join(Cloud, other.Cloud * strength),
        Join(Wind, other.Wind * strength),
        Join(Cold, other.Cold * strength),
        Join(Murk, other.Murk * strength),
        Join(Arcane, other.Arcane * strength));

    private static double Join(double a, double b) => 1 - ((1 - a) * (1 - Math.Clamp(b, 0, 1)));
}

/// <summary>
/// What the weather does to play (Den <c>design/weather-and-environment</c>). Multipliers are 1 when
/// neutral, rates 0: <see cref="TravelCost"/>, <see cref="SupplyUse"/> and <see cref="EventRisk"/>
/// scale map travel; <see cref="Wetting"/> and <see cref="Chill"/> are per game hour out in the open;
/// <see cref="Harm"/> is health per game hour out in the open; <see cref="Sight"/> scales how far
/// the player sees and discovers. A new consequence is a new field here.
/// </summary>
internal readonly record struct EnvironmentEffects(double TravelCost, double SupplyUse, double EventRisk, double Wetting, double Chill, double Harm, double Sight)
{
    internal static EnvironmentEffects Neutral => new(1, 1, 1, 0, 0, 0, 1);

    /// <summary>Adds another's departures from neutral, scaled by its strength.</summary>
    internal EnvironmentEffects With(EnvironmentEffects other, double strength)
    {
        EnvironmentEffects n = Neutral;
        return new(
            TravelCost + ((other.TravelCost - n.TravelCost) * strength),
            SupplyUse + ((other.SupplyUse - n.SupplyUse) * strength),
            EventRisk + ((other.EventRisk - n.EventRisk) * strength),
            Wetting + (other.Wetting * strength),
            Chill + (other.Chill * strength),
            Harm + (other.Harm * strength),
            Math.Max(MinimumSight, Sight + ((other.Sight - n.Sight) * strength)));
    }

    private const double MinimumSight = 0.1;
}

/// <summary>
/// The ground under a front, as a weather kind judges it: the map's geography and its height as a
/// share of the world's peak (0 at sea level, 1 at the highest peaks), so "uplands" mean the same on
/// a regional world and a continent.
/// </summary>
internal readonly record struct WeatherGround(MapSample Sample, double Height)
{
    internal MapBiome Biome => MapBiomes.Classify(Sample);
}

/// <summary>
/// One kind of weather (Den <c>design/weather-and-environment</c>): where it may form and what keeps
/// it alive (each 0..1 from the geography under it), its size, speed and life, how often it is
/// born, and what it does. Lengths are metres and times game hours at the reference scale; a small
/// world shrinks them (<see cref="WeatherScale"/>).
/// </summary>
internal sealed record WeatherKind(
    string Id,
    string Name,
    Func<WeatherGround, double> Birth,
    Func<WeatherGround, double> Sustain,
    double RadiusMetres,
    double MetresPerHour,
    double LifeHours,
    double SpawnCellMetres,
    double SlotHours,
    double BirthChance,
    bool Arcane,
    EnvironmentChannels Channels,
    EnvironmentEffects Effects)
{
    /// <summary>How far a front of this kind can reach from where it was born: its whole drift plus its radius.</summary>
    internal double ReachMetres(WeatherScale scale) => (MetresPerHour * scale.Speeds * LifeHours) + (RadiusMetres * scale.Lengths);
}

/// <summary>
/// How a world's size scales weather (Den <c>design/weather-and-environment</c>): fronts are sized for
/// continents, so a small regional world shrinks their lengths (radius, spawn spacing) and, more
/// gently, their speed, keeping the hours a front takes to pass about the same.
/// </summary>
internal readonly record struct WeatherScale(double Lengths, double Speeds)
{
    private const double ReferenceWorldMetres = 100_000;
    private const double SmallestLengths = 0.2;

    internal static WeatherScale For(double worldMetres)
    {
        double lengths = Math.Clamp(worldMetres / ReferenceWorldMetres, SmallestLengths, 1);
        return new(lengths, Math.Sqrt(lengths));
    }
}
