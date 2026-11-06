using System.Text.Json;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Birds;

/// <summary>A span to draw from.</summary>
internal readonly record struct BirdSpan(double Least, double Most)
{
    /// <summary>The value this share of the way along.</summary>
    internal double At(double share) => Least + ((Most - Least) * share);
}

/// <summary>
/// How a kind flies (#10630). Its flights drift across the sky at this speed while circling at this radius,
/// one turn in this many seconds; a radius of zero flies straight. Its birds flap for a few seconds now and then.
/// Past the end of its line it climbs at this rate over rising ground.
/// </summary>
internal sealed record BirdFlight(
    BirdSpan HeightMetres,
    double DriftMetresPerSecond,
    double ClimbMetresPerSecond,
    BirdSpan RadiusMetres,
    BirdSpan TurnSeconds,
    double BankDegrees,
    double FlapEverySeconds,
    double FlapSeconds)
{
    /// <summary>Whether it flies a straight line rather than circling along it.</summary>
    internal bool Straight => RadiusMetres.Most <= 0;
}

/// <summary>A V: how far apart its birds fly along each arm, how wide the arms sweep from straight back, how much of the flock the longer arm holds, and how far a bird strays from its place, and how slowly.</summary>
internal sealed record BirdFormation(double SpacingMetres, double SweepDegrees, double LongerArmShare, double StrayMetres, double StraySeconds);

/// <summary>A flock: how many birds, how far they keep apart in height and along their circle, and the V they fly in, if any.</summary>
internal sealed record BirdFlock(int Least, int Most, double HeightMetres, double RadiusShare, BirdFormation? Formation);

/// <summary>A kind's body and its two clips.</summary>
internal sealed record BirdMesh(string File, float Scale, string Glide, string Flap);

/// <summary>A kind of bird: where and when it flies, and how.</summary>
internal sealed record BirdKind(
    string Id,
    IReadOnlyDictionary<MapBiome, double> Habitat,
    BirdSpan Hours,
    IReadOnlySet<Season> Seasons,
    BirdFlight Flight,
    BirdFlock Flock,
    BirdMesh Mesh)
{
    /// <summary>Whether it flies here and now.</summary>
    internal bool FliesAt(MapBiome biome, double hour, Season season) =>
        Habitat.GetValueOrDefault(biome) > 0 && hour >= Hours.Least && hour < Hours.Most && Seasons.Contains(season);
}

/// <summary>Every bird and the rules they share, as read from content.</summary>
internal sealed record BirdCatalog(int MostFlights, int MostBirds, BirdSpan EverySeconds, double ReachMetres, double FadeMetres, BirdSpan MissMetres, double GroundEveryMetres, IReadOnlyList<BirdKind> Kinds)
{
    internal const string ContentPath = "birds/birds.json";

    internal static BirdCatalog Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        BirdCatalog read = new(
            root.GetProperty("mostFlights").GetInt32(),
            root.GetProperty("mostBirds").GetInt32(),
            Span(root.GetProperty("everySeconds")),
            root.GetProperty("reachMetres").GetDouble(),
            root.GetProperty("fadeMetres").GetDouble(),
            Span(root.GetProperty("missMetres")),
            root.GetProperty("groundEveryMetres").GetDouble(),
            [.. root.GetProperty("kinds").EnumerateArray().Select(ReadKind)]);
        if (read.Kinds.Count == 0) throw new InvalidDataException("birds need a kind");
        if (read.Kinds.Any(kind => kind.Flock.Most > read.MostBirds)) throw new InvalidDataException("a flock has to fit in the sky");
        if (read.FadeMetres <= 0) throw new InvalidDataException("a bird has to fade in over some distance");
        if (read.MissMetres.Most >= read.ReachMetres - read.FadeMetres) throw new InvalidDataException("a flight misses the player inside its fade");
        if (read.GroundEveryMetres <= 0) throw new InvalidDataException("a flight has to sample the ground");
        return read;
    }

    private static BirdKind ReadKind(JsonElement kind)
    {
        string id = kind.GetProperty("id").GetString()!;
        Dictionary<MapBiome, double> habitat = [];
        foreach (JsonProperty biome in kind.GetProperty("habitat").EnumerateObject())
        {
            if (!Enum.TryParse(biome.Name, out MapBiome named)) throw new InvalidDataException($"{id}: unknown biome '{biome.Name}'");
            habitat[named] = biome.Value.GetDouble();
        }

        HashSet<Season> seasons = [];
        foreach (JsonElement season in kind.GetProperty("seasons").EnumerateArray())
        {
            if (!Enum.TryParse(season.GetString(), out Season named)) throw new InvalidDataException($"{id}: unknown season '{season.GetString()}'");
            seasons.Add(named);
        }

        JsonElement flight = kind.GetProperty("flight"), flock = kind.GetProperty("flock"), mesh = kind.GetProperty("mesh");
        BirdKind read = new(id, habitat, Span(kind.GetProperty("hours")), seasons,
            new BirdFlight(
                Span(flight.GetProperty("heightMetres")),
                flight.GetProperty("driftMetresPerSecond").GetDouble(),
                flight.GetProperty("climbMetresPerSecond").GetDouble(),
                Span(flight.GetProperty("radiusMetres")),
                Span(flight.GetProperty("turnSeconds")),
                flight.GetProperty("bankDegrees").GetDouble(),
                flight.GetProperty("flapEverySeconds").GetDouble(),
                flight.GetProperty("flapSeconds").GetDouble()),
            new BirdFlock(flock.GetProperty("least").GetInt32(), flock.GetProperty("most").GetInt32(),
                flock.GetProperty("heightMetres").GetDouble(), flock.GetProperty("radiusShare").GetDouble(),
                flock.TryGetProperty("formation", out JsonElement formation) ? ReadFormation(formation) : null),
            new BirdMesh(mesh.GetProperty("file").GetString()!, mesh.GetProperty("scale").GetSingle(),
                mesh.GetProperty("glide").GetString()!, mesh.GetProperty("flap").GetString()!));
        if (read.Flight.DriftMetresPerSecond <= 0) throw new InvalidDataException($"{id}: a flight has to get somewhere");
        if (read.Flight.ClimbMetresPerSecond <= 0) throw new InvalidDataException($"{id}: a flight has to be able to climb");
        if (read.Flock.Least < 1 || read.Flock.Most < read.Flock.Least) throw new InvalidDataException($"{id}: a flock is one or more, least first");
        if (read.Flight.FlapSeconds >= read.Flight.FlapEverySeconds) throw new InvalidDataException($"{id}: a bird has to rest from flapping");
        return read;
    }

    private static BirdFormation ReadFormation(JsonElement formation)
    {
        BirdFormation read = new(
            formation.GetProperty("spacingMetres").GetDouble(),
            formation.GetProperty("sweepDegrees").GetDouble(),
            formation.GetProperty("longerArmShare").GetDouble(),
            formation.GetProperty("strayMetres").GetDouble(),
            formation.GetProperty("straySeconds").GetDouble());
        if (read.SpacingMetres <= 0 || read.StraySeconds <= 0) throw new InvalidDataException("a formation needs a spacing and a stray time");
        if (read.LongerArmShare is < 0.5 or > 1) throw new InvalidDataException("the longer arm holds at least half the flock");
        return read;
    }

    internal static BirdSpan Span(JsonElement span)
    {
        BirdSpan read = new(span.GetProperty("least").GetDouble(), span.GetProperty("most").GetDouble());
        return read.Most < read.Least ? throw new InvalidDataException("a span goes from least to most") : read;
    }
}
