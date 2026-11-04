using System.Globalization;
using System.Text.Json;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>The body classes wildlife moves as: each is one capsule for the Engine's character step.</summary>
internal enum WildlifeSizeClass
{
    Small,
    Human,
    Large,
    Huge,
}

/// <summary>What an animal does when the player presses it past its warning.</summary>
internal enum PressedResponse
{
    /// <summary>Runs from the player, and the herd bolts with it.</summary>
    Flee,

    /// <summary>Holds its ground facing the player, and the herd stands with it.</summary>
    Stand,

    /// <summary>Displays, then charges at the player; the others display.</summary>
    DisplayCharge,

    /// <summary>Hunkers down where it is until the player has gone.</summary>
    Hunker,
}

internal enum SocialShape
{
    Herd,
    Band,
    Family,
    Flock,
    Pair,
    Solitary,
}

/// <summary>What a herd is doing with its day.</summary>
internal enum WildlifeActivity
{
    Rest,
    Graze,
    Drift,
    Drink,
}

/// <summary>A capsule: the body a size class moves as.</summary>
internal readonly record struct WildlifeCapsule(float Radius, float Height);

internal readonly record struct WildlifeBody(double LengthMetres, double HeightMetres, double WidthMetres, WildlifeSizeClass SizeClass, uint SrgbColour);

internal readonly record struct WildlifeSpeeds(double Walk, double GrazeDrift, double Flee);

internal readonly record struct WildlifeSenses(double NoticeMetres, double ActMetres, double LookSeconds, double WarnSeconds);

internal readonly record struct WildlifePopulation(double HerdsPerSquareKilometre, double RangeMetres, double RecoveryDays);

/// <summary>One step of a day: from this hour, until the next step's, a herd does this.</summary>
internal readonly record struct ScheduleStep(double FromHour, WildlifeActivity Activity);

/// <summary>A herd's day as activities by hour, in order from midnight.</summary>
internal sealed record WildlifeSchedule(string Name, ScheduleStep[] Steps)
{
    internal const double HoursPerDay = 24;

    /// <summary>Which step of the day a day fraction falls in.</summary>
    internal int StepAt(double dayFraction)
    {
        double hour = dayFraction * HoursPerDay;
        int found = Steps.Length - 1;
        for (int i = 0; i < Steps.Length; i++)
        {
            if (Steps[i].FromHour <= hour) found = i;
        }

        return found;
    }
}

internal sealed record WildlifeKind(
    string Id,
    string Name,
    IReadOnlyDictionary<MapBiome, double> Habitat,
    SocialShape Social,
    int GroupMinimum,
    int GroupMaximum,
    double SpacingMetres,
    WildlifeBody Body,
    WildlifeSpeeds Speeds,
    WildlifeSenses Senses,
    PressedResponse Pressed,
    WildlifeSchedule Schedule,
    WildlifePopulation Population,
    int MaximumHealth,
    DefenceProfile Defence,
    int ExperienceAward,
    LootTable Loot)
{
    /// <summary>How much of this kind's density a biome carries: zero where it does not live.</summary>
    internal double HabitatWeight(MapBiome biome) => Habitat.TryGetValue(biome, out double weight) ? weight : 0;
}

/// <summary>How regions are seeded with herds, and how a herd's range is searched for water: rings out to its edge, at headings each.</summary>
internal readonly record struct WildlifePlacement(double RegionMetres, int BiomeSamplesPerSide, int HomeAttempts, int WaterRings, int WaterHeadings);

/// <summary>
/// When herds wake near the player and sleep again, and how many may be awake at once. Within
/// <see cref="BodyMetres"/> of the player an animal moves as a body against the world's collision;
/// further out it follows the ground, where no one is near enough to see it pass through a trunk.
/// </summary>
internal readonly record struct WildlifeWaking(double WakeMetres, double SleepMetres, int MostAwakeHerds, double BodyMetres);

/// <summary>How herds behave, shared by every kind; each kind's own numbers are on <see cref="WildlifeKind"/>.</summary>
internal readonly record struct WildlifeBehaviour(
    double AlarmSpreadMetresPerSecond,
    double ReleaseShare,
    double FleeUntilNoticeShare,
    double RegroupSeconds,
    double ChargeSeconds,
    double ContactMetres,
    double GoalArrivalMetres,
    double SlotArrivalMetres,
    double CatchUpSpacings,
    double SeparationShare,
    double SlotJitterShare);

/// <summary>Every wildlife kind and the rules they share, as read from content (#10567).</summary>
internal sealed record WildlifeCatalog(
    IReadOnlyDictionary<WildlifeSizeClass, WildlifeCapsule> SizeClasses,
    IReadOnlyList<WildlifeKind> Kinds,
    WildlifePlacement Placement,
    WildlifeWaking Waking,
    WildlifeBehaviour Behaviour)
{
    internal WildlifeKind Kind(string id) => Kinds.FirstOrDefault(kind => kind.Id == id)
        ?? throw new KeyNotFoundException($"no wildlife kind '{id}'");

    internal bool TryKind(string id, out WildlifeKind kind)
    {
        kind = Kinds.FirstOrDefault(candidate => candidate.Id == id)!;
        return kind is not null;
    }
}

/// <summary>
/// Reads <c>content/game/wildlife/kinds.json</c>. Pure: the product hands it the file's text, and the
/// rule lane reads the same file, so a number changed there changes both.
/// </summary>
internal static class WildlifeKinds
{
    internal const string ContentPath = "wildlife/kinds.json";

    private static readonly Dictionary<string, WildlifeSizeClass> SizeClassNames = new(StringComparer.Ordinal)
    {
        ["small"] = WildlifeSizeClass.Small,
        ["human"] = WildlifeSizeClass.Human,
        ["large"] = WildlifeSizeClass.Large,
        ["huge"] = WildlifeSizeClass.Huge,
    };

    private static readonly Dictionary<string, PressedResponse> ResponseNames = new(StringComparer.Ordinal)
    {
        ["flee"] = PressedResponse.Flee,
        ["stand"] = PressedResponse.Stand,
        ["display-charge"] = PressedResponse.DisplayCharge,
        ["hunker"] = PressedResponse.Hunker,
    };

    internal static WildlifeCatalog Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Dictionary<WildlifeSizeClass, WildlifeCapsule> classes = [];
        foreach (JsonProperty entry in root.GetProperty("sizeClasses").EnumerateObject())
        {
            WildlifeCapsule capsule = new(entry.Value.GetProperty("radius").GetSingle(), entry.Value.GetProperty("height").GetSingle());
            if (capsule.Radius <= 0 || capsule.Height <= 2 * capsule.Radius)
                throw new InvalidDataException($"size class '{entry.Name}' must be a capsule taller than its two end caps");
            classes[Named(SizeClassNames, entry.Name, "size class")] = capsule;
        }

        Dictionary<string, WildlifeSchedule> schedules = [];
        foreach (JsonProperty entry in root.GetProperty("schedules").EnumerateObject())
        {
            ScheduleStep[] steps = [.. entry.Value.EnumerateArray().Select(step => new ScheduleStep(
                step.GetProperty("fromHour").GetDouble(),
                Enum<WildlifeActivity>(step.GetProperty("activity").GetString())))];
            if (steps.Length == 0 || steps[0].FromHour != 0)
                throw new InvalidDataException($"schedule '{entry.Name}' must start at hour 0");
            for (int i = 1; i < steps.Length; i++)
            {
                if (steps[i].FromHour <= steps[i - 1].FromHour || steps[i].FromHour >= WildlifeSchedule.HoursPerDay)
                    throw new InvalidDataException($"schedule '{entry.Name}' must rise through the day");
            }

            schedules[entry.Name] = new WildlifeSchedule(entry.Name, steps);
        }

        JsonElement placement = root.GetProperty("placement");
        JsonElement waking = root.GetProperty("waking");
        JsonElement behaviour = root.GetProperty("behaviour");
        List<WildlifeKind> kinds = [];
        foreach (JsonElement kind in root.GetProperty("kinds").EnumerateArray())
        {
            kinds.Add(ReadKind(kind, classes, schedules));
        }

        if (kinds.Select(kind => kind.Id).Distinct(StringComparer.Ordinal).Count() != kinds.Count)
            throw new InvalidDataException("wildlife kind ids must be unique");

        WildlifeWaking wake = new(
            waking.GetProperty("wakeMetres").GetDouble(),
            waking.GetProperty("sleepMetres").GetDouble(),
            waking.GetProperty("mostAwakeHerds").GetInt32(),
            waking.GetProperty("bodyMetres").GetDouble());
        if (wake.SleepMetres <= wake.WakeMetres)
            throw new InvalidDataException("herds must sleep further out than they wake, or they flicker at the edge");

        return new WildlifeCatalog(
            classes,
            kinds,
            new WildlifePlacement(
                placement.GetProperty("regionMetres").GetDouble(),
                placement.GetProperty("biomeSamplesPerSide").GetInt32(),
                placement.GetProperty("homeAttempts").GetInt32(),
                placement.GetProperty("waterRings").GetInt32(),
                placement.GetProperty("waterHeadings").GetInt32()),
            wake,
            new WildlifeBehaviour(
                behaviour.GetProperty("alarmSpreadMetresPerSecond").GetDouble(),
                behaviour.GetProperty("releaseShare").GetDouble(),
                behaviour.GetProperty("fleeUntilNoticeShare").GetDouble(),
                behaviour.GetProperty("regroupSeconds").GetDouble(),
                behaviour.GetProperty("chargeSeconds").GetDouble(),
                behaviour.GetProperty("contactMetres").GetDouble(),
                behaviour.GetProperty("goalArrivalMetres").GetDouble(),
                behaviour.GetProperty("slotArrivalMetres").GetDouble(),
                behaviour.GetProperty("catchUpSpacings").GetDouble(),
                behaviour.GetProperty("separationShare").GetDouble(),
                behaviour.GetProperty("slotJitterShare").GetDouble()));
    }

    private static WildlifeKind ReadKind(JsonElement kind, Dictionary<WildlifeSizeClass, WildlifeCapsule> classes,
        Dictionary<string, WildlifeSchedule> schedules)
    {
        string id = kind.GetProperty("id").GetString()!;
        Dictionary<MapBiome, double> habitat = [];
        foreach (JsonProperty biome in kind.GetProperty("habitat").EnumerateObject())
        {
            habitat[Enum<MapBiome>(biome.Name)] = biome.Value.GetDouble();
        }

        JsonElement social = kind.GetProperty("social");
        JsonElement body = kind.GetProperty("body");
        JsonElement speeds = kind.GetProperty("speeds");
        JsonElement wariness = kind.GetProperty("wariness");
        JsonElement population = kind.GetProperty("population");
        JsonElement combat = kind.GetProperty("combat");
        WildlifeSizeClass sizeClass = Named(SizeClassNames, body.GetProperty("sizeClass").GetString()!, "size class");
        if (!classes.ContainsKey(sizeClass)) throw new InvalidDataException($"{id}: size class {sizeClass} has no capsule");
        string scheduleName = kind.GetProperty("schedule").GetString()!;
        if (!schedules.TryGetValue(scheduleName, out WildlifeSchedule? schedule))
            throw new InvalidDataException($"{id}: no schedule '{scheduleName}'");

        int minimum = social.GetProperty("groupMinimum").GetInt32();
        int maximum = social.GetProperty("groupMaximum").GetInt32();
        if (minimum < 1 || maximum < minimum) throw new InvalidDataException($"{id}: group size must be 1 or more, minimum first");
        WildlifeSenses sense = new(
            wariness.GetProperty("noticeMetres").GetDouble(),
            wariness.GetProperty("actMetres").GetDouble(),
            wariness.GetProperty("lookSeconds").GetDouble(),
            wariness.GetProperty("warnSeconds").GetDouble());
        if (sense.ActMetres >= sense.NoticeMetres) throw new InvalidDataException($"{id}: an animal must notice before it acts");

        LootEntry[] loot = [.. kind.GetProperty("loot").EnumerateArray().Select(entry => new LootEntry(
            entry.GetProperty("item").GetString()!,
            entry.GetProperty("minimum").GetInt32(),
            entry.GetProperty("maximum").GetInt32(),
            entry.GetProperty("oneIn").GetInt32()))];
        return new WildlifeKind(
            id,
            kind.GetProperty("name").GetString()!,
            habitat,
            Enum<SocialShape>(social.GetProperty("shape").GetString()),
            minimum,
            maximum,
            social.GetProperty("spacingMetres").GetDouble(),
            new WildlifeBody(
                body.GetProperty("lengthMetres").GetDouble(),
                body.GetProperty("heightMetres").GetDouble(),
                body.GetProperty("widthMetres").GetDouble(),
                sizeClass,
                uint.Parse(body.GetProperty("colour").GetString()!.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            new WildlifeSpeeds(speeds.GetProperty("walk").GetDouble(), speeds.GetProperty("grazeDrift").GetDouble(), speeds.GetProperty("flee").GetDouble()),
            sense,
            Named(ResponseNames, kind.GetProperty("pressed").GetString()!, "pressed response"),
            schedule,
            new WildlifePopulation(
                population.GetProperty("herdsPerSquareKilometre").GetDouble(),
                population.GetProperty("rangeMetres").GetDouble(),
                population.GetProperty("recoveryDays").GetDouble()),
            combat.GetProperty("health").GetInt32(),
            new DefenceProfile(combat.GetProperty("evasion").GetInt32(), new ArmourProfile(combat.GetProperty("armour").GetInt32())),
            combat.GetProperty("experience").GetInt32(),
            new LootTable($"wildlife-{id}", 1, 1, loot));
    }

    private static T Named<T>(Dictionary<string, T> names, string name, string what) =>
        names.TryGetValue(name, out T? value) ? value : throw new InvalidDataException($"unknown {what} '{name}'");

    private static T Enum<T>(string? name) where T : struct, Enum =>
        System.Enum.TryParse(name, ignoreCase: true, out T value) && System.Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException($"unknown {typeof(T).Name} '{name}'");
}
