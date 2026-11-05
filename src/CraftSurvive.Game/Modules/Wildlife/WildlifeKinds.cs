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

    /// <summary>A hostile animal (#10615): it wakes as a creature and hunts by the creature rules, not as a herd.</summary>
    Hunt,

    /// <summary>Hunkers down where it is until the player has gone.</summary>
    Hunker,

    /// <summary>Runs for the water of its range and slips under until the player has gone; with none, flees.</summary>
    TakeToWater,
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

/// <summary>What a kind's clips are for: the behaviour each one shows (#10569).</summary>
internal enum WildlifeClipRole
{
    Idle,
    Graze,
    Walk,
    Run,
    Look,
    Warn,
    Stand,
    Hunker,
    Die,
    Slip,
    LedgeHop,
    Threat,
    Bite,
    Hurt,
}

/// <summary>A kind's animated body: its mesh and build manifest, which clip plays each role, and how it falls when killed, if it does more than play its death. A kind without one is drawn as a placeholder.</summary>
internal sealed record WildlifeMesh(string File, string Manifest, float Scale, IReadOnlyDictionary<WildlifeClipRole, string> Clips, string? Ragdoll = null);

/// <summary>
/// A kind's young (#10600): this share of a herd's animals, the adult's mesh at this scale with its
/// own paint and a larger head, sharing every clip. They dart about their nearest adult, and shelter
/// behind one when the herd is pressed.
/// </summary>
internal sealed record WildlifeYoung(
    double Share,
    float Scale,
    WildlifeSizeClass SizeClass,
    string Texture,
    string HeadJoint,
    float HeadScale,
    double DartMetres,
    double DartSpeed,
    double PauseSeconds,
    double ShelterMetres,
    double HealthShare,
    double YieldShare,
    LootTable Loot);

/// <summary>
/// How a hostile animal hunts (#10615).
/// </summary>
/// <param name="SightMetres">How far it sees the player.</param>
/// <param name="ReachMetres">How close it must stand to bite, by its body's centre: its head reaches far ahead of it.</param>
/// <param name="HaltMetres">Where it stops, inside its reach.</param>
/// <param name="CooldownTicks">How long after one bite before the next.</param>
/// <param name="BiteWindupSeconds">How far into the bite the jaws close: the blow lands then.</param>
/// <param name="DodgeMetres">How far past its reach a closing bite still finds the player.</param>
internal sealed record WildlifeHunt(double SightMetres, double ReachMetres, double HaltMetres, int Accuracy, int Power, long CooldownTicks, double BiteWindupSeconds, double DodgeMetres);

/// <summary>A capsule: the body a size class moves as.</summary>
internal readonly record struct WildlifeCapsule(float Radius, float Height);

internal readonly record struct WildlifeBody(double LengthMetres, double HeightMetres, double WidthMetres, WildlifeSizeClass SizeClass, uint SrgbColour);

internal readonly record struct WildlifeSpeeds(double Walk, double GrazeDrift, double Flee, double TurnDegrees);

/// <param name="FleeBelowHealth">Below this share of its health an animal that stands or charges runs instead; zero, it never does.</param>
internal readonly record struct WildlifeSenses(double NoticeMetres, double ActMetres, double LookSeconds, double WarnSeconds, double FleeBelowHealth);

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
    WildlifeMesh? Mesh,
    WildlifeYoung? Young,
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
    /// <summary>How it hunts, for a hostile animal; null for every other.</summary>
    internal WildlifeHunt? Hunt { get; init; }

    /// <summary>How much of this kind's density a biome carries: zero where it does not live.</summary>
    internal double HabitatWeight(MapBiome biome) => Habitat.TryGetValue(biome, out double weight) ? weight : 0;
}

/// <summary>How regions are seeded with herds, and how a herd's range is searched for water: rings out to its edge, at headings each.</summary>
internal readonly record struct WildlifePlacement(double RegionMetres, int BiomeSamplesPerSide, int HomeAttempts, int WaterRings, int WaterHeadings, double BankMetres);

/// <summary>
/// When herds wake near the player and sleep again, by distance alone, and how many may be awake at
/// once. A herd can stand past the near ground's edge, seen across open country. Within
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
    double CatchUpSpeedShare,
    double SlotLeaveShare,
    double GrazeCycleSeconds,
    double PathCheckMetres,
    double SeparationShare,
    double SlotJitterShare,
    double ShoreArrivalMetres,
    double MostToWaterSeconds,
    double SlipSeconds,
    double SlipMetresPerSecond,
    double LeastUnderSeconds,
    double LedgeRiseMetres,
    double LedgeLookMetres,
    double HopSeconds,
    double HopEverySeconds);

/// <summary>How far animals of a kind differ from one another: each share either way, of size, coat brightness and coat warmth (#10601).</summary>
internal readonly record struct WildlifeVariety(double SizeShare, double ValueShare, double WarmthShare);

/// <summary>How animated bodies are shown (#10569): the distance tiers, clip changes, and the limits on how a body sits on the ground.</summary>
internal readonly record struct WildlifeAnimation(
    double FullMetres,
    double PlayMetres,
    double DrawMetres,
    double TierMarginMetres,
    double ClipHoldSeconds,
    double FadeSeconds,
    double MinimumRate,
    double MaximumRate,
    double RateStep,
    double MostPitchDegrees,
    double PitchShare,
    double MostFootMetres,
    double PitchDegreesPerSecond,
    int PitchEveryUpdates,
    int MostPlanted,
    double ReplantMetres,
    double ReplantDegrees,
    double CorpseSeconds,
    double CorpseMetres,
    WildlifeRagdollFeel Ragdoll);

/// <summary>How the dead fall (#10621): the surface of a ragdoll, and the killing blow as the speed it would give the whole body, with a share of it upward.</summary>
internal readonly record struct WildlifeRagdollFeel(
    double Friction,
    double Restitution,
    double LinearDamping,
    double AngularDamping,
    double BlowMetresPerSecond,
    double BlowLift);

/// <summary>Every wildlife kind and the rules they share, as read from content (#10567).</summary>
internal sealed record WildlifeCatalog(
    IReadOnlyDictionary<WildlifeSizeClass, WildlifeCapsule> SizeClasses,
    IReadOnlyList<WildlifeKind> Kinds,
    WildlifePlacement Placement,
    WildlifeWaking Waking,
    WildlifeBehaviour Behaviour,
    WildlifeAnimation Animation,
    WildlifeVariety Variety)
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
        ["hunt"] = PressedResponse.Hunt,
        ["hunker"] = PressedResponse.Hunker,
        ["take-to-water"] = PressedResponse.TakeToWater,
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
                placement.GetProperty("waterHeadings").GetInt32(),
                placement.GetProperty("bankMetres").GetDouble()),
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
                behaviour.GetProperty("catchUpSpeedShare").GetDouble(),
                behaviour.GetProperty("slotLeaveShare").GetDouble(),
                behaviour.GetProperty("grazeCycleSeconds").GetDouble(),
                behaviour.GetProperty("pathCheckMetres").GetDouble(),
                behaviour.GetProperty("separationShare").GetDouble(),
                behaviour.GetProperty("slotJitterShare").GetDouble(),
                behaviour.GetProperty("shoreArrivalMetres").GetDouble(),
                behaviour.GetProperty("mostToWaterSeconds").GetDouble(),
                behaviour.GetProperty("slipSeconds").GetDouble(),
                behaviour.GetProperty("slipMetresPerSecond").GetDouble(),
                behaviour.GetProperty("leastUnderSeconds").GetDouble(),
                behaviour.GetProperty("ledgeRiseMetres").GetDouble(),
                behaviour.GetProperty("ledgeLookMetres").GetDouble(),
                behaviour.GetProperty("hopSeconds").GetDouble(),
                behaviour.GetProperty("hopEverySeconds").GetDouble()),
            ReadAnimation(root.GetProperty("animation")),
            ReadVariety(root.GetProperty("variety")));
    }

    private static WildlifeVariety ReadVariety(JsonElement variety)
    {
        WildlifeVariety read = new(
            variety.GetProperty("sizeShare").GetDouble(),
            variety.GetProperty("valueShare").GetDouble(),
            variety.GetProperty("warmthShare").GetDouble());
        if (read.SizeShare is < 0 or >= 1 || read.ValueShare is < 0 or >= 1 || read.WarmthShare is < 0 or >= 1)
            throw new InvalidDataException("variety shares must be at least 0 and below 1");
        return read;
    }

    private static WildlifeAnimation ReadAnimation(JsonElement animation)
    {
        WildlifeAnimation read = new(
            animation.GetProperty("fullMetres").GetDouble(),
            animation.GetProperty("playMetres").GetDouble(),
            animation.GetProperty("drawMetres").GetDouble(),
            animation.GetProperty("tierMarginMetres").GetDouble(),
            animation.GetProperty("clipHoldSeconds").GetDouble(),
            animation.GetProperty("fadeSeconds").GetDouble(),
            animation.GetProperty("minimumRate").GetDouble(),
            animation.GetProperty("maximumRate").GetDouble(),
            animation.GetProperty("rateStep").GetDouble(),
            animation.GetProperty("mostPitchDegrees").GetDouble(),
            animation.GetProperty("pitchShare").GetDouble(),
            animation.GetProperty("mostFootMetres").GetDouble(),
            animation.GetProperty("pitchDegreesPerSecond").GetDouble(),
            animation.GetProperty("pitchEveryUpdates").GetInt32(),
            animation.GetProperty("mostPlanted").GetInt32(),
            animation.GetProperty("replantMetres").GetDouble(),
            animation.GetProperty("replantDegrees").GetDouble(),
            animation.GetProperty("corpseSeconds").GetDouble(),
            animation.GetProperty("corpseMetres").GetDouble(),
            ReadRagdollFeel(animation.GetProperty("ragdoll")));
        if (read.FullMetres >= read.PlayMetres || read.PlayMetres >= read.DrawMetres)
            throw new InvalidDataException("animation tiers must widen outward: full, then play, then draw");
        if (read.PitchEveryUpdates < 1) throw new InvalidDataException("a body must pitch at least every so many updates");
        return read;
    }

    private static WildlifeRagdollFeel ReadRagdollFeel(JsonElement ragdoll) => new(
        ragdoll.GetProperty("friction").GetDouble(),
        ragdoll.GetProperty("restitution").GetDouble(),
        ragdoll.GetProperty("linearDamping").GetDouble(),
        ragdoll.GetProperty("angularDamping").GetDouble(),
        ragdoll.GetProperty("blowMetresPerSecond").GetDouble(),
        ragdoll.GetProperty("blowLift").GetDouble());

    private static WildlifeMesh? ReadMesh(JsonElement kind)
    {
        if (!kind.TryGetProperty("mesh", out JsonElement mesh)) return null;
        Dictionary<WildlifeClipRole, string> clips = [];
        foreach (JsonProperty clip in mesh.GetProperty("clips").EnumerateObject())
        {
            clips[Enum<WildlifeClipRole>(clip.Name)] = clip.Value.GetString()!;
        }

        return new WildlifeMesh(mesh.GetProperty("file").GetString()!, mesh.GetProperty("manifest").GetString()!, mesh.GetProperty("scale").GetSingle(), clips,
            mesh.TryGetProperty("ragdoll", out JsonElement ragdoll) ? ragdoll.GetString() : null);
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
            wariness.GetProperty("warnSeconds").GetDouble(),
            wariness.TryGetProperty("fleeBelowHealth", out JsonElement fleeBelow) ? fleeBelow.GetDouble() : 0);
        if (sense.ActMetres >= sense.NoticeMetres) throw new InvalidDataException($"{id}: an animal must notice before it acts");

        LootEntry[] loot = [.. kind.GetProperty("loot").EnumerateArray().Select(entry => new LootEntry(
            entry.GetProperty("item").GetString()!,
            entry.GetProperty("minimum").GetInt32(),
            entry.GetProperty("maximum").GetInt32(),
            entry.GetProperty("oneIn").GetInt32()))];
        LootTable table = new($"wildlife-{id}", 1, 1, loot);
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
            ReadMesh(kind),
            ReadYoung(kind, table, classes),
            new WildlifeSpeeds(speeds.GetProperty("walk").GetDouble(), speeds.GetProperty("grazeDrift").GetDouble(), speeds.GetProperty("flee").GetDouble(),
                speeds.GetProperty("turnDegrees").GetDouble()),
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
            table)
        {
            Hunt = ReadHunt(kind, id),
        };
    }

    private static WildlifeHunt? ReadHunt(JsonElement kind, string id)
    {
        bool hunts = kind.GetProperty("pressed").GetString() == "hunt";
        if (!kind.TryGetProperty("hunt", out JsonElement hunt))
            return hunts ? throw new InvalidDataException($"{id}: a kind that hunts needs a hunt block") : null;
        WildlifeHunt read = new(
            hunt.GetProperty("sightMetres").GetDouble(),
            hunt.GetProperty("reachMetres").GetDouble(),
            hunt.GetProperty("haltMetres").GetDouble(),
            hunt.GetProperty("accuracy").GetInt32(),
            hunt.GetProperty("power").GetInt32(),
            hunt.GetProperty("cooldownTicks").GetInt64(),
            hunt.GetProperty("biteWindupSeconds").GetDouble(),
            hunt.GetProperty("dodgeMetres").GetDouble());
        if (read.HaltMetres >= read.ReachMetres) throw new InvalidDataException($"{id}: a hunter halts inside its reach");
        return read;
    }

    private static WildlifeYoung? ReadYoung(JsonElement kind, LootTable adult, Dictionary<WildlifeSizeClass, WildlifeCapsule> classes)
    {
        if (!kind.TryGetProperty("young", out JsonElement young)) return null;
        double yield = young.GetProperty("yieldShare").GetDouble();
        WildlifeSizeClass sizeClass = Named(SizeClassNames, young.GetProperty("sizeClass").GetString()!, "size class");
        if (!classes.ContainsKey(sizeClass)) throw new InvalidDataException($"young of size class {sizeClass} have no capsule");
        // At least one of anything, or a small animal would yield nothing.
        LootEntry[] entries = [.. adult.Entries.Select(entry => entry with
        {
            Minimum = Math.Max(1, (int)Math.Round(entry.Minimum * yield)),
            Maximum = Math.Max(1, (int)Math.Round(entry.Maximum * yield)),
        })];
        return new WildlifeYoung(
            young.GetProperty("share").GetDouble(),
            young.GetProperty("scale").GetSingle(),
            sizeClass,
            young.GetProperty("texture").GetString()!,
            young.GetProperty("headJoint").GetString()!,
            young.GetProperty("headScale").GetSingle(),
            young.GetProperty("dartMetres").GetDouble(),
            young.GetProperty("dartSpeed").GetDouble(),
            young.GetProperty("pauseSeconds").GetDouble(),
            young.GetProperty("shelterMetres").GetDouble(),
            young.GetProperty("healthShare").GetDouble(),
            yield,
            adult with { Id = adult.Id + "-young", Entries = entries });
    }

    private static T Named<T>(Dictionary<string, T> names, string name, string what) =>
        names.TryGetValue(name, out T? value) ? value : throw new InvalidDataException($"unknown {what} '{name}'");

    private static T Enum<T>(string? name) where T : struct, Enum =>
        System.Enum.TryParse(name, ignoreCase: true, out T value) && System.Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException($"unknown {typeof(T).Name} '{name}'");
}
