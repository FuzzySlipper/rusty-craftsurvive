using System.Numerics;
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

    /// <summary>
    /// As tall as a person and twice as wide (#10925): the low, wide cave hunters - the mouldback, the drop-sloth, the
    /// hummock - routed and stopped at their true width without asking a large body's headroom of a cave passage.
    /// </summary>
    Broad,
    Large,
    Huge,
    Towering,
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

    /// <summary>Lying flat at rest, hidden among its place (#10877: the mouldback in its fungus).</summary>
    Lie,

    /// <summary>Hanging upside down from a ceiling, its grip at the body's origin (#10877: the drop-sloth).</summary>
    Hang,

    /// <summary>Falling from a ceiling and turning over to land on its feet (#10877).</summary>
    Drop,

    /// <summary>Standing and searching for what it lost (#10878: the draft-hound flaring its ear-fans and turning them).</summary>
    Search,

    /// <summary>
    /// Lying low in shallow water, legs laid back, the body sunk to its back and head (#10879: the drowner resting in its
    /// pool, whose home is the pool's middle over a bed a few tenths of a metre down).
    /// </summary>
    Swim,

    /// <summary>Circling off between attacks, the body bowed toward its turn (#10985: the needle-mouth in the mist).</summary>
    Circle,
}

/// <summary>
/// A kind's animated body: its mesh and build manifest, which clip plays each role, how it falls when killed, if it does
/// more than play its death, and the miasma its open mouth breathes out (#10882), if any. A kind without one is drawn as a
/// placeholder.
/// </summary>
internal sealed record WildlifeMesh(string File, string Manifest, float Scale, IReadOnlyDictionary<WildlifeClipRole, string> Clips, string? Ragdoll = null,
    WildlifeBreath? Breath = null, WildlifeWorn? Worn = null, WildlifeFade? Fade = null)
{
    /// <summary>How its body follows its head as it swims (#10995), drawn over its clips; null for one its clips alone pose.</summary>
    internal WildlifeSwim? Swim { get; init; }
}

/// <summary>
/// A body faded into the mist (#10984: the fog foes, seen only when they attack). <paramref name="Paint"/> is the kind's
/// paint with its fade in the alpha (authoring/tools/creatures/fade_paint.py): the cutoff plus a little at the tail,
/// rising to 1 at the head. It is drawn masked at <paramref name="Cutoff"/>, and each animal's own alpha chooses how much
/// of it shows, from the head back: <paramref name="Rest"/> of its length at rest, <see cref="Strike"/> of it (all, unless
/// its rear always fades, as the fade-ribbon's does, #10986) while it threatens, bites, is hurt or dies, sweeping between
/// the two over <paramref name="SweepSeconds"/>. Fallen, all of it shows.
/// </summary>
internal sealed record WildlifeFade(string Paint, float Cutoff, float Rest, float SweepSeconds)
{
    internal float Strike { get; init; } = 1f;

    /// <summary>How far over the cutoff the paint's tail stays (fade_paint.py's TAIL_MARGIN), so a whole body draws whole.</summary>
    internal const float TailMargin = 0.02f;

    internal static WildlifeFade? Read(JsonElement mesh)
    {
        if (!mesh.TryGetProperty("fade", out JsonElement fade)) return null;
        WildlifeFade read = new(fade.GetProperty("paint").GetString()!, fade.GetProperty("cutoff").GetSingle(),
            fade.GetProperty("rest").GetSingle(), fade.GetProperty("sweepSeconds").GetSingle())
        {
            Strike = fade.TryGetProperty("strike", out JsonElement strike) ? strike.GetSingle() : 1f,
        };
        if (read.Strike < read.Rest || read.Strike > 1) throw new InvalidDataException("fade: a strike shows at least its rest, and at most the whole body");
        if (read.Cutoff is <= 0 or >= 1 - TailMargin) throw new InvalidDataException("fade: the cutoff must lie between 0 and 1, under the tail's margin");
        if (read.Rest is < 0 or > 1) throw new InvalidDataException("fade: rest is a share of the body, 0 to 1");
        if (read.SweepSeconds <= 0) throw new InvalidDataException("fade: the sweep needs a positive time");
        return read;
    }

    /// <summary>
    /// An animal's alpha to show this share of its body from the head back: the paint's alpha at the share's end, divided
    /// into the cutoff, so every texel nearer the head passes; 0 hides it all, and the whole body draws at 1.
    /// </summary>
    internal float AlphaFor(float share)
    {
        if (share <= 0) return 0;
        if (share >= 1) return 1;
        float paintAtEnd = Cutoff + TailMargin + ((1 - Cutoff - TailMargin) * (1 - share));
        return Cutoff / paintAtEnd;
    }

    /// <summary>The share it shows, moved toward the one it should by this many seconds of its sweep.</summary>
    internal float Toward(float shown, float wanted, double seconds)
    {
        float step = (float)(seconds / SweepSeconds);
        return shown < wanted ? MathF.Min(wanted, shown + step) : MathF.Max(wanted, shown - step);
    }

    /// <summary>Whether a clip shows the whole body: it is attacking, or being hurt or killed.</summary>
    internal static bool Solid(WildlifeClipRole clip) => clip is WildlifeClipRole.Threat or WildlifeClipRole.Bite or WildlifeClipRole.Hurt or WildlifeClipRole.Die;
}

/// <summary>
/// What a kind wears and holds (#10956): rigid pieces hung from its rig's joints rather than skinned, so one body can
/// wear different shells, as equipment. <paramref name="Pieces"/> is the content path of the pieces file
/// (authoring/tools/creatures/make_shells.py writes it): each look's pieces and the held ones, with their joints. Each
/// animal wears one look, chosen by its weight in <paramref name="Looks"/>.
/// </summary>
internal sealed record WildlifeWorn(string Pieces, IReadOnlyList<(string Look, double Weight)> Looks)
{
    /// <summary>How many pieces one body may wear and hold at once: the id range gives each mesh body's slot this many.</summary>
    internal const int MostPieces = 3;

    internal static WildlifeWorn? Read(JsonElement mesh)
    {
        if (!mesh.TryGetProperty("worn", out JsonElement worn)) return null;
        (string, double)[] looks = [.. worn.GetProperty("looks").EnumerateObject().Select(look => (look.Name, look.Value.GetDouble()))];
        if (looks.Length == 0 || looks.Any(look => look.Item2 < 0) || looks.Sum(look => look.Item2) <= 0)
            throw new InvalidDataException("worn: looks need a positive weight between them");
        return new WildlifeWorn(worn.GetProperty("pieces").GetString()!, looks);
    }

    /// <summary>The look an animal wears, by its weight, from a number spread evenly over [0, 1).</summary>
    internal string Pick(double share)
    {
        double total = Looks.Sum(look => look.Weight), at = share * total;
        foreach ((string look, double weight) in Looks)
        {
            if (at < weight) return look;
            at -= weight;
        }

        return Looks[^1].Look;
    }
}

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
internal sealed record WildlifeHunt(double SightMetres, double ReachMetres, double HaltMetres, int Accuracy, int Power, long CooldownTicks, double BiteWindupSeconds, double DodgeMetres, WildlifePack? Pack = null)
{
    /// <summary>How it rests where it lives (#10876); null for one that stands about, seeing as far as its sight.</summary>
    internal WildlifeRest? Rest { get; init; }

    /// <summary>How it displays before it attacks (#10876); null shows its threat clip once a hunt, from beyond its reach.</summary>
    internal WildlifeDisplay? Display { get; init; }

    /// <summary>How it searches for a player it has lost (#10876); null gives up at once.</summary>
    internal WildlifeSearch? Search { get; init; }

    /// <summary>The brood it carries and releases instead of fighting (#10961, the brood bearer); null for one that fights.</summary>
    internal WildlifeBrood? Brood { get; init; }

    /// <summary>The doorways it seals instead of fighting (#10961, the mender); null for one that does not.</summary>
    internal WildlifeSealing? Seals { get; init; }

    /// <summary>The call it rings instead of fighting (#10962, the chime grazer); null for one that does not.</summary>
    internal WildlifeCalling? Calls { get; init; }

    /// <summary>A tender (#10961): it threatens by what it releases, shuts or calls, never by its own blows.</summary>
    internal bool Tends => Brood is not null || Seals is not null || Calls is not null;

    /// <summary>A ruin fixture's hazard (#10963); null for a creature that moves. A fixture is rooted where it grows.</summary>
    internal WildlifeFixture? Fixture { get; init; }

    /// <summary>How it lives in a mist sump (#10985, the fog foes); null for one that walks and sees.</summary>
    internal WildlifeFog? Fog { get; init; }
}

/// <summary>
/// A fog foe's ways (#10985), settled with Patch on the wiki's Fog wildlife page ("Roster and rules"): it swims the mist
/// <paramref name="FloatMetres"/> over the floor, rising and falling by <paramref name="BobMetres"/> over
/// <paramref name="BobSeconds"/>, and never leaves the mist. It hunts by sound, not sight: a noise the player makes is
/// heard as far as its hunt's sight metres times the noise's loudness, and it comes at it while it heard one within
/// <paramref name="HeardSeconds"/>; within <paramref name="FeelMetres"/> in the mist it senses the player however quiet.
/// After each bite it circles off <paramref name="CircleMetres"/> from the player for <paramref name="CircleSeconds"/>
/// before it comes again. Hunting, its teeth click every <paramref name="ClickFarSeconds"/> at the edge of its hearing,
/// faster as it nears, to every <paramref name="ClickNearSeconds"/> at the player.
/// <para>
/// Its sound is its own (<see cref="Sound"/>): the needle-mouth clicks its teeth while it hunts, the fade-ribbon (#10986)
/// flutters whenever it swims, at the same paces. A darter (<see cref="DartMetres"/>, the fade-ribbon before the line,
/// #10173, exists) does not come at the player from afar: sensing them, it loops round them <paramref name="CircleMetres"/>
/// off, <see cref="LoopDegrees"/> round at a time, and darts in to bite only when they come within its dart. A waiter
/// (<see cref="Waits"/>, the gulper, #10987) holds still where it hangs while it hears them, strikes once they are within
/// its reach, and withdraws until its cooldown has passed.
/// </para>
/// </summary>
internal sealed record WildlifeFog(double FloatMetres, double BobMetres, double BobSeconds, double HeardSeconds, double FeelMetres,
    double CircleMetres, double CircleSeconds, double ClickFarSeconds, double ClickNearSeconds)
{
    internal FogSound Sound { get; init; } = FogSound.Click;

    internal double? DartMetres { get; init; }

    internal double LoopDegrees { get; init; } = DefaultLoopDegrees;

    /// <summary>A waiter (#10987, the gulper) does not come at the player: it holds still where it hangs, strikes once they are within its reach, then withdraws until its long cooldown passes.</summary>
    internal bool Waits { get; init; }

    /// <summary>How far round the player a darter loops at a time, unless its kind says.</summary>
    internal const double DefaultLoopDegrees = 50;
}

/// <summary>
/// A fog foe's own sound (#10986): the needle-mouth's teeth click while it hunts; the fade-ribbon flutters whenever it
/// swims; the gulper's throat pulses slow and low whenever it lives (#10987), so it is heard before it is reached.
/// </summary>
internal enum FogSound
{
    Click,
    Flutter,
    Pulse,
}

/// <summary>What a ruin fixture does when it acts (#10963).</summary>
internal enum FixtureAct
{
    /// <summary>The niche unfolder: it seizes the player and holds them, drawing them toward its recess.</summary>
    Seizes,

    /// <summary>The vault filter: it drops its lobes on the player beneath it, stinging and numbing them.</summary>
    Numbs,

    /// <summary>The folding bower: it folds shut on the player lingering under it and digests them until they cut out.</summary>
    Closes,

    /// <summary>The brood vessel: while the player lingers in its sight, it splits a vessel at a time, each a small skitterer.</summary>
    Hatches,
}

/// <summary>
/// A ruin fixture's hazard (#10963), settled with Patch on the wiki's Ruins wildlife page ("Fixtures as hazards"). It is
/// rooted, and acts only on a player within <paramref name="ReachMetres"/> of its middle (in sight, for one that hatches):
/// it shows its tell for <paramref name="TellSeconds"/>, acts for <paramref name="ActSeconds"/> (a seizure's longest hold,
/// a filter's lash before its lobes rise, a vessel's pause between splits), and rests <paramref name="RestSeconds"/>
/// before it can act again (a bower: after it empties). <paramref name="Damage"/> lands every
/// <paramref name="DamageEverySeconds"/> while it holds or digests, or once with a filter's sting.
/// </summary>
internal sealed record WildlifeFixture(FixtureAct Act, double ReachMetres, double TellSeconds, double ActSeconds, double RestSeconds, int Damage, double DamageEverySeconds)
{
    /// <summary>A seizure draws the player toward the recess this fast.</summary>
    internal double DrawMetresPerSecond { get; init; }

    /// <summary>Sprinting against a seizure this long breaks free, spending <see cref="StruggleStaminaPerSecond"/> meanwhile.</summary>
    internal double StruggleSeconds { get; init; }

    internal double StruggleStaminaPerSecond { get; init; }

    /// <summary>A filter's sting numbs this long, landing this far into its act.</summary>
    internal double NumbSeconds { get; init; }

    internal double StrikeAfterSeconds { get; init; }

    /// <summary>The parts it loses one by one (a filter's lobes, by its health; a vessel's vessels), and the joints that are them.</summary>
    internal int Parts { get; init; }

    internal IReadOnlyList<string> PartJoints { get; init; } = [];

    /// <summary>The blows that cut a bower's membrane through, or smash one of a vessel's vessels.</summary>
    internal int Blows { get; init; }

    /// <summary>A hanging fixture's mesh hangs from a ceiling this high over its feet; it is drawn lifted to its own ceiling. 0 stands on the floor.</summary>
    internal double CeilingMetres { get; init; }

    /// <summary>A shut bower's wall stands in a ring this far from its middle.</summary>
    internal double WallMetres { get; init; }

    /// <summary>What a vessel hatches, one animal a split, and how long its spent vessels take to regrow.</summary>
    internal WildlifeBrood? Brood { get; init; }

    /// <summary>
    /// The clips its acts play beyond the roles, by name: `hold` (held or shut), `recover` (folding away, rising, opening),
    /// and a vessel's `whole`, `swell`, `split` and `spent`, where {0} is the count of vessels spent.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Clips { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A chime grazer's call (#10962), settled with Patch on the wiki's Ruins wildlife page: disturbed, it rings its crest for
/// its display's while, and a blow cuts the ring short. A ring that finishes calls every other ruin hunter of its space
/// within <paramref name="ReachMetres"/>, tenders excepted, to search where it rang; resting ones wake to it. Then it walks
/// away from the player, and can ring again <paramref name="AgainSeconds"/> after the last.
/// </summary>
internal sealed record WildlifeCalling(double ReachMetres, double AgainSeconds);

/// <summary>
/// A brood bearer's brood (#10961), settled with Patch on the wiki's Ruins wildlife page: the first time its threat shows, it
/// splits its pods (its worn look turns to <paramref name="SplitLook"/>) and <paramref name="Count"/> animals of
/// <paramref name="Kind"/> spill out about it, already hunting. They defend it within <paramref name="GuardMetres"/> of
/// it, going back to it when the player is further off; it shuffles away from the player meanwhile. Its pods regrow
/// after <paramref name="RegrowSeconds"/> (a game day).
/// </summary>
internal sealed record WildlifeBrood(string Kind, int Count, string SplitLook, double GuardMetres, double RegrowSeconds);

/// <summary>
/// A mender's sealing (#10961), settled with Patch on the wiki's Ruins wildlife page: seeing the player, it goes to the
/// ruin doorway nearest the player and grows a membrane across it over <paramref name="Seconds"/>; a blow stops it. A seal
/// blocks passage and sight. A fresh one falls to <paramref name="FreshBlows"/> blows; after <paramref name="HardenSeconds"/>
/// it has hardened and takes <paramref name="HardBlows"/>. It seals at most <paramref name="MostSeals"/> doorways near
/// the player, then needs <paramref name="RefillSeconds"/> before the next.
/// </summary>
internal sealed record WildlifeSealing(double Seconds, int FreshBlows, int HardBlows, double HardenSeconds, int MostSeals, double RefillSeconds, double ReachMetres);

/// <summary>A hunter that rests (#10876) lies still, showing this clip, until it sees the player within its wake distance.</summary>
/// <param name="Hangs">It rests hanging from the ceiling over its home and drops when it wakes (#10877: the drop-sloth).</param>
internal sealed record WildlifeRest(double WakeMetres, WildlifeClipRole Clip, bool Hangs = false)
{
    /// <summary>
    /// A hunter that rests facing into a cave's draft (#10878: the draft-hound, ears held into it) wakes to a player coming
    /// from downwind at only this share of <see cref="WakeMetres"/>, the share growing to the whole of it upwind; null for
    /// one that ignores the draft.
    /// </summary>
    internal double? DownwindShare { get; init; }
}

/// <summary>A hunter's display before it attacks (#10876): it stands this long showing this clip, however near the player is.</summary>
internal sealed record WildlifeDisplay(double Seconds, WildlifeClipRole Clip);

/// <summary>A hunter's search (#10876): it goes to where it last saw the player and looks about, showing this clip, for this long.</summary>
internal sealed record WildlifeSearch(double Seconds, WildlifeClipRole Clip);

/// <summary>Where in a cave a cave animal lives (#10876): one of the everyday zones, or one of the eight communities.</summary>
internal enum CaveHabitat
{
    /// <summary>The twilight of a cave mouth's tendril, on the overworld's side.</summary>
    Mouth,

    /// <summary>The damp dead ends where fungi crowd.</summary>
    DampDeadEnds,

    /// <summary>The cave's lakes and pools.</summary>
    Pools,

    /// <summary>The cave's deep floors, along its main route.</summary>
    DeepFloors,

    CushionBarrens,
    RibbonThicket,
    FunnelGarden,
    InvertedGrove,
    VeilForest,
    IvoryHeath,
    GlassSteppe,
    VentOrchard,
}

/// <summary>A cave animal's place (#10876): where it lives in a cave, and how many homes one cave holds at most.</summary>
internal sealed record WildlifeCave(CaveHabitat Lives, int HomesPerCave);

/// <summary>
/// A small cave creature kept as ambience (#10876; Patch: furtive, no colliders and no combat): drawn as its kind's
/// placeholder body, it wanders near its home and slips away from the player, hiding while they stay near.
/// </summary>
/// <param name="GroupMinimum">The fewest in a group; a group is one home.</param>
/// <param name="WanderMetres">How far from its home it wanders.</param>
/// <param name="FleeMetres">The player this near sends it away.</param>
/// <param name="HideSeconds">How long it runs before it slips out of sight, until the player has gone.</param>
/// <param name="Swims">A swimmer keeps under its pool's surface rather than on a floor.</param>
/// <param name="Mesh">Its animated body (#10884), with idle, walk and run clips: no collider and no ragdoll. Without one it is drawn as a placeholder.</param>
internal sealed record CaveAmbientKind(
    string Id,
    string Name,
    WildlifeCave Cave,
    int GroupMinimum,
    int GroupMaximum,
    WildlifeBody Body,
    double WalkSpeed,
    double FleeSpeed,
    double WanderMetres,
    double FleeMetres,
    double HideSeconds,
    bool Swims,
    WildlifeMesh? Mesh = null);

/// <summary>
/// How a pack hunts (#10626): what one sees calls the others from this far, for this long after it last
/// saw, and each animal but the first comes in turned this far off the straight line, the next further,
/// until it is this near the player, then straight in.
/// </summary>
internal sealed record WildlifePack(double AlarmMetres, double AlarmSeconds, double FlankDegrees, double CloseMetres);

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

    /// <summary>Where it lives in a cave (#10876), for a cave animal; null for one of the overworld's regions.</summary>
    internal WildlifeCave? Cave { get; init; }

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

    /// <summary>The small creatures kept as a cave's ambience (#10876).</summary>
    internal IReadOnlyList<CaveAmbientKind> CaveAmbient { get; init; } = [];

    /// <summary>A cave animal's home within this distance of the player (across the ground) is woken (#10876): about a cave's reach.</summary>
    internal double CaveWakeMetres { get; init; }
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
        ["broad"] = WildlifeSizeClass.Broad,
        ["large"] = WildlifeSizeClass.Large,
        ["huge"] = WildlifeSizeClass.Huge,
        ["towering"] = WildlifeSizeClass.Towering,
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

        double caveWake = waking.GetProperty("caveMetres").GetDouble();
        if (caveWake <= 0 || caveWake >= wake.WakeMetres) throw new InvalidDataException("cave animals wake nearer than the overworld's herds, and at some distance");
        List<CaveAmbientKind> ambient = root.TryGetProperty("caveAmbient", out JsonElement ambience)
            ? [.. ambience.EnumerateArray().Select(ReadAmbient)]
            : [];
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
            ReadVariety(root.GetProperty("variety")))
        {
            CaveAmbient = ambient,
            CaveWakeMetres = caveWake,
        };
    }

    /// <summary>A fog foe's sound by its name in kinds.json (#10986).</summary>
    private static readonly Dictionary<string, FogSound> FogSounds = new(StringComparer.Ordinal)
    {
        ["click"] = FogSound.Click,
        ["flutter"] = FogSound.Flutter,
        ["pulse"] = FogSound.Pulse,
    };

    private static readonly Dictionary<string, CaveHabitat> CaveHabitatNames = new(StringComparer.Ordinal)
    {
        ["mouth"] = CaveHabitat.Mouth,
        ["damp-dead-ends"] = CaveHabitat.DampDeadEnds,
        ["pools"] = CaveHabitat.Pools,
        ["deep-floors"] = CaveHabitat.DeepFloors,
        ["cushion-barrens"] = CaveHabitat.CushionBarrens,
        ["ribbon-thicket"] = CaveHabitat.RibbonThicket,
        ["funnel-garden"] = CaveHabitat.FunnelGarden,
        ["inverted-grove"] = CaveHabitat.InvertedGrove,
        ["veil-forest"] = CaveHabitat.VeilForest,
        ["ivory-heath"] = CaveHabitat.IvoryHeath,
        ["glass-steppe"] = CaveHabitat.GlassSteppe,
        ["vent-orchard"] = CaveHabitat.VentOrchard,
    };

    private static WildlifeCave? ReadCave(JsonElement kind, string id)
    {
        if (!kind.TryGetProperty("cave", out JsonElement cave)) return null;
        WildlifeCave read = new(Named(CaveHabitatNames, cave.GetProperty("lives").GetString()!, "cave habitat"), cave.GetProperty("homesPerCave").GetInt32());
        if (read.HomesPerCave < 1) throw new InvalidDataException($"{id}: a cave animal has at least one home in a cave");
        return read;
    }

    /// <summary>The clips an ambient creature's mesh plays (#10884): standing, wandering and running off.</summary>
    internal static readonly WildlifeClipRole[] AmbientClips = [WildlifeClipRole.Idle, WildlifeClipRole.Walk, WildlifeClipRole.Run];

    private static CaveAmbientKind ReadAmbient(JsonElement kind)
    {
        string id = kind.GetProperty("id").GetString()!;
        JsonElement body = kind.GetProperty("body");
        CaveAmbientKind read = new(
            id,
            kind.GetProperty("name").GetString()!,
            ReadCave(kind, id) ?? throw new InvalidDataException($"{id}: a cave creature needs a cave block"),
            kind.GetProperty("groupMinimum").GetInt32(),
            kind.GetProperty("groupMaximum").GetInt32(),
            new WildlifeBody(
                body.GetProperty("lengthMetres").GetDouble(),
                body.GetProperty("heightMetres").GetDouble(),
                body.GetProperty("widthMetres").GetDouble(),
                WildlifeSizeClass.Small,
                uint.Parse(body.GetProperty("colour").GetString()!.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            kind.GetProperty("walkSpeed").GetDouble(),
            kind.GetProperty("fleeSpeed").GetDouble(),
            kind.GetProperty("wanderMetres").GetDouble(),
            kind.GetProperty("fleeMetres").GetDouble(),
            kind.GetProperty("hideSeconds").GetDouble(),
            kind.TryGetProperty("swims", out JsonElement swims) && swims.GetBoolean(),
            ReadMesh(kind));
        foreach (WildlifeClipRole role in AmbientClips)
        {
            if (read.Mesh is { } mesh && !mesh.Clips.ContainsKey(role)) throw new InvalidDataException($"{id}: a cave creature's mesh plays {role}");
        }

        if (read.GroupMinimum < 1 || read.GroupMaximum < read.GroupMinimum) throw new InvalidDataException($"{id}: group size must be 1 or more, minimum first");
        if (read.FleeSpeed <= read.WalkSpeed) throw new InvalidDataException($"{id}: a cave creature runs faster than it wanders");
        return read;
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

        WildlifeBreath? breath = WildlifeBreath.Read(mesh, Enum<WildlifeClipRole>);
        foreach (WildlifeClipRole role in breath?.At.Keys ?? [])
        {
            if (!clips.ContainsKey(role)) throw new InvalidDataException($"breath: the mesh plays no clip for {role}");
        }

        return new WildlifeMesh(mesh.GetProperty("file").GetString()!, mesh.GetProperty("manifest").GetString()!, mesh.GetProperty("scale").GetSingle(), clips,
            mesh.TryGetProperty("ragdoll", out JsonElement ragdoll) ? ragdoll.GetString() : null, breath, WildlifeWorn.Read(mesh), WildlifeFade.Read(mesh))
        {
            Swim = WildlifeSwim.Read(mesh),
        };
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
            Cave = ReadCave(kind, id),
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
            hunt.GetProperty("dodgeMetres").GetDouble(),
            hunt.TryGetProperty("pack", out JsonElement pack)
                ? new WildlifePack(pack.GetProperty("alarmMetres").GetDouble(), pack.GetProperty("alarmSeconds").GetDouble(), pack.GetProperty("flankDegrees").GetDouble(), pack.GetProperty("closeMetres").GetDouble())
                : null);
        if (read.HaltMetres >= read.ReachMetres) throw new InvalidDataException($"{id}: a hunter halts inside its reach");
        WildlifeClipRole Clip(JsonElement block, WildlifeClipRole otherwise) =>
            block.TryGetProperty("clip", out JsonElement clip) ? Enum<WildlifeClipRole>(clip.GetString()) : otherwise;
        read = read with
        {
            Rest = hunt.TryGetProperty("rest", out JsonElement rest)
                ? new WildlifeRest(rest.GetProperty("wakeMetres").GetDouble(), Clip(rest, WildlifeClipRole.Idle), rest.TryGetProperty("hang", out JsonElement hang) && hang.GetBoolean())
                {
                    DownwindShare = rest.TryGetProperty("draft", out JsonElement draft) ? draft.GetProperty("downwindShare").GetDouble() : null,
                }
                : null,
            Display = hunt.TryGetProperty("display", out JsonElement display) ? new WildlifeDisplay(display.GetProperty("seconds").GetDouble(), Clip(display, WildlifeClipRole.Threat)) : null,
            Search = hunt.TryGetProperty("search", out JsonElement search) ? new WildlifeSearch(search.GetProperty("seconds").GetDouble(), Clip(search, WildlifeClipRole.Look)) : null,
            Brood = hunt.TryGetProperty("brood", out JsonElement brood)
                ? new WildlifeBrood(brood.GetProperty("kind").GetString()!, brood.GetProperty("count").GetInt32(), brood.GetProperty("splitLook").GetString()!,
                    brood.GetProperty("guardMetres").GetDouble(), brood.GetProperty("regrowSeconds").GetDouble())
                : null,
            Seals = hunt.TryGetProperty("seals", out JsonElement seals)
                ? new WildlifeSealing(seals.GetProperty("seconds").GetDouble(), seals.GetProperty("freshBlows").GetInt32(), seals.GetProperty("hardBlows").GetInt32(),
                    seals.GetProperty("hardenSeconds").GetDouble(), seals.GetProperty("mostSeals").GetInt32(), seals.GetProperty("refillSeconds").GetDouble(),
                    seals.GetProperty("reachMetres").GetDouble())
                : null,
            Calls = hunt.TryGetProperty("calls", out JsonElement calls)
                ? new WildlifeCalling(calls.GetProperty("reachMetres").GetDouble(), calls.GetProperty("againSeconds").GetDouble())
                : null,
            Fixture = hunt.TryGetProperty("fixture", out JsonElement fixture) ? ReadFixture(fixture, id) : null,
            Fog = hunt.TryGetProperty("fog", out JsonElement fog)
                ? new WildlifeFog(fog.GetProperty("floatMetres").GetDouble(), fog.GetProperty("bobMetres").GetDouble(), fog.GetProperty("bobSeconds").GetDouble(),
                    fog.GetProperty("heardSeconds").GetDouble(), fog.GetProperty("feelMetres").GetDouble(), fog.GetProperty("circleMetres").GetDouble(),
                    fog.GetProperty("circleSeconds").GetDouble(), fog.GetProperty("clickFarSeconds").GetDouble(), fog.GetProperty("clickNearSeconds").GetDouble())
                {
                    Sound = fog.TryGetProperty("sound", out JsonElement sound)
                        ? FogSounds.TryGetValue(sound.GetString() ?? "", out FogSound named) ? named : throw new InvalidDataException($"{id}: a fog foe's sound is {string.Join(" or ", FogSounds.Keys)}")
                        : FogSound.Click,
                    DartMetres = fog.TryGetProperty("dartMetres", out JsonElement dart) ? dart.GetDouble() : null,
                    LoopDegrees = fog.TryGetProperty("loopDegrees", out JsonElement loop) ? loop.GetDouble() : WildlifeFog.DefaultLoopDegrees,
                    Waits = fog.TryGetProperty("waits", out JsonElement waits) && waits.GetBoolean(),
                }
                : null,
        };
        if (read.Fog is { } foe && (foe.FloatMetres <= 0 || foe.BobMetres < 0 || foe.BobSeconds <= 0 || foe.HeardSeconds <= 0 || foe.FeelMetres < read.ReachMetres
            || foe.CircleMetres <= read.ReachMetres || foe.CircleSeconds <= 0 || foe.ClickNearSeconds <= 0 || foe.ClickFarSeconds < foe.ClickNearSeconds))
            throw new InvalidDataException($"{id}: a fog foe floats, hears for a while, feels at least as far as it reaches, circles off beyond its reach, and clicks faster near");
        if (read.Fog is { DartMetres: double darts } darter && (darts < read.ReachMetres || darts >= darter.CircleMetres || darter.LoopDegrees is <= 0 or > 180))
            throw new InvalidDataException($"{id}: a darter darts from at least its reach, inside the loop it swims, a part of the way round at a time");
        if (read.Fog is { Waits: true } waiter && (waiter.DartMetres is not null || waiter.FeelMetres < read.ReachMetres))
            throw new InvalidDataException($"{id}: a waiter does not dart, and feels at least as far as it strikes");
        if (read.Calls is { ReachMetres: <= 0 } or { AgainSeconds: <= 0 } || (read.Calls is not null && read.Display is null))
            throw new InvalidDataException($"{id}: a call reaches somewhere, takes a rest between rings, and rings for its display's while");
        if (read.Brood is { Count: <= 0 } or { GuardMetres: <= 0 } or { RegrowSeconds: <= 0 }) throw new InvalidDataException($"{id}: a brood has animals, a guard and a regrowth");
        if (read.Seals is { Seconds: <= 0 } or { FreshBlows: <= 0 } or { MostSeals: <= 0 } or { ReachMetres: <= 0 } || read.Seals is { } sealing && sealing.HardBlows < sealing.FreshBlows)
            throw new InvalidDataException($"{id}: a seal takes time and blows, and hardens");
        if (read.Rest is { } resting && (resting.WakeMetres <= read.ReachMetres || resting.WakeMetres > read.SightMetres))
            throw new InvalidDataException($"{id}: a resting hunter wakes beyond its reach and within its sight");
        if (read.Display is { Seconds: <= 0 } || read.Search is { Seconds: <= 0 }) throw new InvalidDataException($"{id}: a display or search lasts some time");
        return read;
    }

    private static readonly Dictionary<string, FixtureAct> FixtureActs = new(StringComparer.Ordinal)
    {
        ["seizes"] = FixtureAct.Seizes,
        ["numbs"] = FixtureAct.Numbs,
        ["closes"] = FixtureAct.Closes,
        ["hatches"] = FixtureAct.Hatches,
    };

    private static WildlifeFixture ReadFixture(JsonElement fixture, string id)
    {
        double Number(string name) => fixture.TryGetProperty(name, out JsonElement value) ? value.GetDouble() : 0;
        string act = fixture.GetProperty("act").GetString() ?? "";
        WildlifeFixture read = new(
            FixtureActs.TryGetValue(act, out FixtureAct named) ? named : throw new InvalidDataException($"{id}: a fixture's act is {string.Join(" or ", FixtureActs.Keys)}"),
            Number("reachMetres"), Number("tellSeconds"), Number("actSeconds"), Number("restSeconds"),
            fixture.TryGetProperty("damage", out JsonElement damage) ? damage.GetInt32() : 0, Number("damageEverySeconds"))
        {
            DrawMetresPerSecond = Number("drawMetresPerSecond"),
            StruggleSeconds = Number("struggleSeconds"),
            StruggleStaminaPerSecond = Number("struggleStaminaPerSecond"),
            NumbSeconds = Number("numbSeconds"),
            StrikeAfterSeconds = Number("strikeAfterSeconds"),
            Parts = fixture.TryGetProperty("parts", out JsonElement parts) ? parts.GetInt32() : 0,
            PartJoints = fixture.TryGetProperty("partJoints", out JsonElement joints) ? [.. joints.EnumerateArray().Select(joint => joint.GetString()!)] : [],
            Blows = fixture.TryGetProperty("blows", out JsonElement blows) ? blows.GetInt32() : 0,
            CeilingMetres = Number("ceilingMetres"),
            WallMetres = Number("wallMetres"),
            Brood = fixture.TryGetProperty("brood", out JsonElement brood)
                ? new WildlifeBrood(brood.GetProperty("kind").GetString()!, brood.GetProperty("count").GetInt32(), "", brood.GetProperty("guardMetres").GetDouble(), brood.GetProperty("regrowSeconds").GetDouble())
                : null,
            Clips = fixture.TryGetProperty("clips", out JsonElement clips)
                ? clips.EnumerateObject().Where(clip => !clip.Name.StartsWith('$')).ToDictionary(clip => clip.Name, clip => clip.Value.GetString()!, StringComparer.Ordinal)
                : new Dictionary<string, string>(),
        };
        if (read.ReachMetres <= 0 || read.TellSeconds <= 0 || read.ActSeconds <= 0 || read.RestSeconds <= 0)
            throw new InvalidDataException($"{id}: a fixture reaches somewhere, tells, acts and rests");
        bool fits = read.Act switch
        {
            FixtureAct.Seizes => read.Damage > 0 && read.DamageEverySeconds > 0 && read.StruggleSeconds > 0 && read.StruggleStaminaPerSecond > 0 && read.DrawMetresPerSecond >= 0
                && read.Clips.ContainsKey("hold") && read.Clips.ContainsKey("recover"),
            FixtureAct.Numbs => read.Damage > 0 && read.NumbSeconds > 0 && read.StrikeAfterSeconds < read.ActSeconds && read.Parts > 0 && read.PartJoints.Count == read.Parts
                && read.Clips.ContainsKey("recover"),
            FixtureAct.Closes => read.Damage > 0 && read.DamageEverySeconds > 0 && read.Blows > 0 && read.WallMetres > read.ReachMetres
                && read.Clips.ContainsKey("hold") && read.Clips.ContainsKey("recover"),
            FixtureAct.Hatches => read.Parts > 0 && read.Blows > 0 && read.Brood is { Count: > 0, GuardMetres: > 0, RegrowSeconds: > 0 }
                && new[] { "whole", "swell", "split", "spent" }.All(read.Clips.ContainsKey),
            _ => false,
        };
        if (!fits) throw new InvalidDataException($"{id}: a fixture that {act} lacks what its act needs (see WildlifeFixture)");
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

/// <summary>
/// A light riding with a held piece (#10957, the implement-carrier's core): a point light parented to the piece's object
/// at <paramref name="At"/> in its joint's frame, so the Engine carries it with the hand. It burns
/// <paramref name="ThreatShare"/> times brighter while its bearer threatens or strikes.
/// </summary>
internal sealed record WildlifeHeldLight(Vector3 At, Vector3 Colour, float Intensity, float Range, float ThreatShare)
{
    internal static WildlifeHeldLight Read(JsonElement light)
    {
        static Vector3 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
        WildlifeHeldLight read = new(Vector(light.GetProperty("at")), Vector(light.GetProperty("colour")), light.GetProperty("intensity").GetSingle(),
            light.GetProperty("range").GetSingle(), light.TryGetProperty("threatShare", out JsonElement share) ? share.GetSingle() : 1f);
        if (read.Intensity <= 0 || read.Range <= 0 || read.ThreatShare <= 0) throw new InvalidDataException("a held light needs a positive intensity, range and threat share");
        return read;
    }
}
