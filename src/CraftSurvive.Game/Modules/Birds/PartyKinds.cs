using System.Text.Json;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Birds;

/// <summary>How a bird goes about the ground: a hop this far every so often, and how much of its time it pecks.</summary>
internal sealed record PartyGait(BirdSpan HopEverySeconds, double HopSeconds, double HopMetres, double HopHeightMetres, double PeckShare);

/// <summary>How a party leaves: when the player comes this near, its birds go up within a moment of each other, fanned about the way away from the player.</summary>
internal sealed record PartyBurst(double FlushMetres, double StaggerSeconds, double MetresPerSecond, double ClimbMetresPerSecond, double FanDegrees);

/// <summary>A party's two bodies: folded on the ground with its three clips, and spread in the air with one.</summary>
internal sealed record PartyMesh(string Ground, float GroundScale, string Idle, string Peck, string Hop, string Air, float AirScale, string Flap);

/// <summary>A kind of bird that feeds on the ground in a party (#10639): where and when, how many, and how open the ground has to be.</summary>
internal sealed record PartyKind(
    string Id,
    IReadOnlyDictionary<MapBiome, double> Habitat,
    BirdSpan Hours,
    IReadOnlySet<Season> Seasons,
    double MostCanopy,
    int Least,
    int Most,
    double SpreadMetres,
    PartyGait Gait,
    PartyBurst Burst,
    PartyMesh Mesh)
{
    /// <summary>Whether it feeds here and now.</summary>
    internal bool FeedsAt(MapBiome biome, double hour, Season season) =>
        Habitat.GetValueOrDefault(biome) > 0 && hour >= Hours.Least && hour < Hours.Most && Seasons.Contains(season);
}

/// <summary>The rules every party shares, as read from content.</summary>
internal sealed record PartyCatalog(int MostParties, BirdSpan EverySeconds, BirdSpan AppearMetres, BirdSpan StaySeconds, double GoneMetres, double FadeMetres, double ViewMarginDegrees, int PlaceTries, IReadOnlyList<PartyKind> Kinds)
{
    /// <summary>The most birds that can be on the ground at once.</summary>
    internal int MostBirds => MostParties * Kinds.Max(kind => kind.Most);

    internal static PartyCatalog Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement.GetProperty("parties");
        PartyCatalog read = new(
            root.GetProperty("mostParties").GetInt32(),
            BirdCatalog.Span(root.GetProperty("everySeconds")),
            BirdCatalog.Span(root.GetProperty("appearMetres")),
            BirdCatalog.Span(root.GetProperty("staySeconds")),
            root.GetProperty("goneMetres").GetDouble(),
            root.GetProperty("fadeMetres").GetDouble(),
            root.GetProperty("viewMarginDegrees").GetDouble(),
            root.GetProperty("placeTries").GetInt32(),
            [.. root.GetProperty("kinds").EnumerateArray().Select(ReadKind)]);
        if (read.Kinds.Count == 0) throw new InvalidDataException("parties need a kind");
        if (read.FadeMetres <= 0 || read.FadeMetres >= read.GoneMetres) throw new InvalidDataException("a bird fades over part of the way out");
        if (read.AppearMetres.Most >= read.GoneMetres) throw new InvalidDataException("a party appears nearer than birds go");
        return read;
    }

    private static PartyKind ReadKind(JsonElement kind)
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

        JsonElement gait = kind.GetProperty("gait"), burst = kind.GetProperty("burst"), mesh = kind.GetProperty("mesh");
        PartyKind read = new(id, habitat, BirdCatalog.Span(kind.GetProperty("hours")), seasons,
            kind.GetProperty("mostCanopy").GetDouble(),
            kind.GetProperty("least").GetInt32(),
            kind.GetProperty("most").GetInt32(),
            kind.GetProperty("spreadMetres").GetDouble(),
            new PartyGait(BirdCatalog.Span(gait.GetProperty("hopEverySeconds")), gait.GetProperty("hopSeconds").GetDouble(), gait.GetProperty("hopMetres").GetDouble(),
                gait.GetProperty("hopHeightMetres").GetDouble(), gait.GetProperty("peckShare").GetDouble()),
            new PartyBurst(burst.GetProperty("flushMetres").GetDouble(), burst.GetProperty("staggerSeconds").GetDouble(), burst.GetProperty("metresPerSecond").GetDouble(),
                burst.GetProperty("climbMetresPerSecond").GetDouble(), burst.GetProperty("fanDegrees").GetDouble()),
            new PartyMesh(mesh.GetProperty("ground").GetString()!, mesh.GetProperty("groundScale").GetSingle(), mesh.GetProperty("idle").GetString()!, mesh.GetProperty("peck").GetString()!,
                mesh.GetProperty("hop").GetString()!, mesh.GetProperty("air").GetString()!, mesh.GetProperty("airScale").GetSingle(), mesh.GetProperty("flap").GetString()!));
        if (read.Least < 1 || read.Most < read.Least) throw new InvalidDataException($"{id}: a party is one or more, least first");
        if (read.Gait.HopSeconds <= 0 || read.Gait.HopEverySeconds.Least <= 0) throw new InvalidDataException($"{id}: a hop takes time, and comes after a rest");
        if (read.Burst.MetresPerSecond <= 0) throw new InvalidDataException($"{id}: a burst has to get somewhere");
        return read;
    }
}
