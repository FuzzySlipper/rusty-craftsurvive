using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>One front over a place and how strongly it covers it.</summary>
internal readonly record struct FrontPresence(WeatherFront Front, double Cover);

/// <summary>
/// The weather at a place and time (Den <c>design/weather-and-environment</c>): the combined channels
/// and effects, and the fronts that make them, strongest first. Consumers read this, never a kind.
/// </summary>
internal sealed record EnvironmentSample(EnvironmentChannels Channels, EnvironmentEffects Effects, IReadOnlyList<FrontPresence> Fronts)
{
    internal static EnvironmentSample Clear { get; } = new(EnvironmentChannels.Clear, EnvironmentEffects.Neutral, []);

    /// <summary>The strongest front over the place, if any.</summary>
    internal FrontPresence? Dominant => Fronts.Count == 0 ? null : Fronts[0];

    internal string Describe() => Dominant is FrontPresence top
        ? $"{top.Front.Kind.Name} ({Intensity(top.Cover)})"
        : "Clear";

    internal static string Intensity(double cover) => cover switch
    {
        < 0.25 => "edge",
        < 0.6 => "moderate",
        _ => "heavy",
    };
}

/// <summary>
/// The world's weather as a deterministic field (Den <c>design/weather-and-environment</c>). Fronts are
/// born on a space-time lattice per kind - a seeded draw per spawn cell and time slot decides whether,
/// where and when, and the kind's climate test at that place must agree - and drift along a prevailing
/// flow (the map's wind, bent gently over long distances), so a region's storms come from about the
/// same side and a front's future is its forecast. Nothing here is saved: the seed, the map and the
/// clock decide it all. Fronts summoned for diagnosis are the one exception, kept until cleared.
/// </summary>
internal sealed class WeatherField
{
    /// <summary>How far the flow bends from the prevailing wind, radians, and over what distance at the reference scale.</summary>
    private const double FlowBend = 0.6, FlowWavelengthMetres = 150_000;
    private const ulong FlowSalt = 0x5851F42D4C957F2DUL;
    private const ulong KindSalt = 0x9E3779B97F4A7C15UL;
    private const ulong SlotSalt = 0xC2B2AE3D27D4EB4FUL;
    /// <summary>Separate draws for a birth: whether, where across, where down, and when in its slot.</summary>
    private const ulong ChanceDraw = 0, AcrossDraw = 1, DownDraw = 2, WhenDraw = 3;
    /// <summary>A summoned front is placed this share of its life old, grown to full strength.</summary>
    private const double SummonedAge = 0.2;
    /// <summary>Remembered fronts (born or not) before the memory starts again.</summary>
    private const int CacheLimit = 16_384;

    private readonly ulong seed;
    private readonly double windAngle;
    private readonly Func<double, double, MapSample> geography;
    private readonly double peakMetres;
    private readonly Dictionary<FrontKey, WeatherFront?> fronts = [];
    private readonly List<WeatherFront> summoned = [];

    /// <param name="peakMetres">The world's highest elevation, which <see cref="WeatherGround.Height"/> is a share of.</param>
    internal WeatherField(ulong seed, double windAngle, double worldMetres, double peakMetres, Func<double, double, MapSample> geography, IReadOnlyList<WeatherKind>? kinds = null)
    {
        this.peakMetres = peakMetres > 0 ? peakMetres : throw new ArgumentOutOfRangeException(nameof(peakMetres));
        this.seed = seed;
        this.windAngle = windAngle;
        this.geography = geography ?? throw new ArgumentNullException(nameof(geography));
        Scale = WeatherScale.For(worldMetres);
        Kinds = kinds ?? WeatherKinds.All;
    }

    /// <summary>The field for a generated world: its seed, its prevailing wind and its broad geography.</summary>
    internal static WeatherField For(WorldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        ulong seed = map.Configuration.Contract.GeographyNoiseSeed;
        // A designed continent's wind is its design's (#9815), as the map's own climate has it.
        MapClimate climate = new(seed, map.Scale, ContinentDesign.For(map.Configuration.Size)?.Climate);
        return new(seed, climate.WindAngle, map.Radius * 2, map.Scale.PeakElevation, map.Broad);
    }

    internal WeatherScale Scale { get; }

    internal IReadOnlyList<WeatherKind> Kinds { get; }

    /// <summary>Changes whenever fronts are summoned or cleared, so presentation drawn from the field redraws.</summary>
    internal int Revision { get; private set; }

    /// <summary>How many fronts the field has worked out (born or not), for the readout.</summary>
    internal int Remembered => fronts.Count;

    /// <summary>The prevailing flow at a place: a unit direction in world x, z.</summary>
    internal Vector2 Flow(double x, double z)
    {
        double wavelength = FlowWavelengthMetres * Scale.Lengths;
        double angle = windAngle + (FlowBend * MapNoise.Fbm(seed ^ FlowSalt, x / wavelength, z / wavelength, 2, 0.5));
        return new((float)Math.Cos(angle), (float)Math.Sin(angle));
    }

    /// <summary>The weather at a place and time.</summary>
    internal EnvironmentSample Sample(double x, double z, double hours)
    {
        List<FrontPresence> over = [];
        foreach (WeatherFront front in Alive(x, z, hours, 0))
        {
            double cover = front.Cover(x, z, hours);
            if (cover > 0) over.Add(new(front, cover));
        }

        if (over.Count == 0) return EnvironmentSample.Clear;
        over.Sort((a, b) => b.Cover.CompareTo(a.Cover));
        EnvironmentChannels channels = EnvironmentChannels.Clear;
        EnvironmentEffects effects = EnvironmentEffects.Neutral;
        foreach (FrontPresence presence in over)
        {
            channels = channels.With(presence.Front.Kind.Channels, presence.Cover);
            effects = effects.With(presence.Front.Kind.Effects, presence.Cover);
        }

        return new(channels, effects, over);
    }

    /// <summary>
    /// Every front alive at a time whose centre could lie within <paramref name="aroundMetres"/> of a place
    /// plus its own radius - what the map shows near the party, or what may cover the place.
    /// </summary>
    internal IEnumerable<WeatherFront> Alive(double x, double z, double hours, double aroundMetres)
    {
        for (int kindIndex = 0; kindIndex < Kinds.Count; kindIndex++)
        {
            WeatherKind kind = Kinds[kindIndex];
            double cell = kind.SpawnCellMetres * Scale.Lengths;
            double reach = kind.ReachMetres(Scale) + aroundMetres;
            long lowX = (long)Math.Floor((x - reach) / cell), highX = (long)Math.Floor((x + reach) / cell);
            long lowZ = (long)Math.Floor((z - reach) / cell), highZ = (long)Math.Floor((z + reach) / cell);
            long firstSlot = (long)Math.Floor((hours - kind.LifeHours) / kind.SlotHours), lastSlot = (long)Math.Floor(hours / kind.SlotHours);
            for (long slot = firstSlot; slot <= lastSlot; slot++)
            {
                for (long cellX = lowX; cellX <= highX; cellX++)
                {
                    for (long cellZ = lowZ; cellZ <= highZ; cellZ++)
                    {
                        if (Front(new FrontKey(kindIndex, cellX, cellZ, slot)) is not WeatherFront front || !front.Alive(hours)) continue;
                        Vector2 centre = front.Centre(hours);
                        double reachOf = front.RadiusMetres + aroundMetres;
                        if (((centre.X - x) * (centre.X - x)) + ((centre.Y - z) * (centre.Y - z)) <= reachOf * reachOf) yield return front;
                    }
                }
            }
        }

        foreach (WeatherFront front in summoned)
        {
            if (!front.Alive(hours)) continue;
            Vector2 centre = front.Centre(hours);
            double reachOf = front.RadiusMetres + aroundMetres;
            if (((centre.X - x) * (centre.X - x)) + ((centre.Y - z) * (centre.Y - z)) <= reachOf * reachOf) yield return front;
        }
    }

    /// <summary>The front born at a lattice place, or null if none was: a seeded draw, then the kind's climate there.</summary>
    internal WeatherFront? Front(FrontKey key)
    {
        if (fronts.TryGetValue(key, out WeatherFront? known)) return known;
        if (fronts.Count >= CacheLimit) fronts.Clear();
        WeatherKind kind = Kinds[key.Kind];
        ulong draws = seed ^ (KindSalt * (ulong)(key.Kind + 1)) ^ unchecked((ulong)key.Slot * SlotSalt);
        double cell = kind.SpawnCellMetres * Scale.Lengths;
        double bornX = (key.CellX + MapNoise.Unit(draws + AcrossDraw, key.CellX, key.CellZ)) * cell;
        double bornZ = (key.CellZ + MapNoise.Unit(draws + DownDraw, key.CellX, key.CellZ)) * cell;
        WeatherFront? front = null;
        if (MapNoise.Unit(draws + ChanceDraw, key.CellX, key.CellZ) < kind.BirthChance * kind.Birth(Ground(bornX, bornZ)))
        {
            double bornAt = (key.Slot + MapNoise.Unit(draws + WhenDraw, key.CellX, key.CellZ)) * kind.SlotHours;
            front = Drift(key, kind, bornX, bornZ, bornAt);
        }

        fronts[key] = front;
        return front;
    }

    /// <summary>
    /// A front of a kind placed by hand for diagnosis, born now at a place: it drifts and fades like any
    /// other, and is kept until <see cref="ClearSummoned"/>.
    /// </summary>
    internal WeatherFront Summon(WeatherKind kind, double x, double z, double hours)
    {
        // Born a fifth of its life ago, upwind, so it stands over the place at full strength now.
        int index = Math.Max(0, Kinds.ToList().IndexOf(kind));
        double grown = kind.LifeHours * SummonedAge;
        Vector2 flow = Flow(x, z);
        double back = kind.MetresPerHour * Scale.Speeds * grown;
        WeatherFront front = Drift(new FrontKey(index, long.MinValue, summoned.Count, long.MinValue), kind,
            x - (flow.X * back), z - (flow.Y * back), hours - grown, summonedFront: true);
        summoned.Add(front);
        Revision++;
        return front;
    }

    internal int ClearSummoned()
    {
        int count = summoned.Count;
        summoned.Clear();
        Revision++;
        return count;
    }

    /// <summary>The ground at a place as weather judges it.</summary>
    internal WeatherGround Ground(double x, double z)
    {
        MapSample sample = geography(x, z);
        return new(sample, Math.Clamp(sample.Elevation / peakMetres, 0, 1));
    }

    /// <summary>A front's whole track, hour by hour from its birth, along the flow; and how well the ground under each hour suits it.</summary>
    private WeatherFront Drift(FrontKey key, WeatherKind kind, double x, double z, double bornAt, bool summonedFront = false)
    {
        int hours = (int)Math.Ceiling(kind.LifeHours);
        double[] trackX = new double[hours + 1], trackZ = new double[hours + 1], sustain = new double[hours + 1];
        double speed = kind.MetresPerHour * Scale.Speeds;
        for (int hour = 0; hour <= hours; hour++)
        {
            trackX[hour] = x;
            trackZ[hour] = z;
            // A summoned front holds full strength wherever it is put; a born one answers to the ground.
            sustain[hour] = summonedFront ? 1 : Math.Clamp(kind.Sustain(Ground(x, z)), 0, 1);
            Vector2 flow = Flow(x, z);
            x += flow.X * speed;
            z += flow.Y * speed;
        }

        return new WeatherFront(key, kind, bornAt, kind.RadiusMetres * Scale.Lengths, trackX, trackZ, sustain);
    }
}
