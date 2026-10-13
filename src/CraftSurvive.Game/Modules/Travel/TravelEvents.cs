using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

internal enum TravelEventKind { Encounter, Discovery, Weather, Hazard }

/// <summary>One answer the player can give to a travel event; the product decides what it does.</summary>
internal readonly record struct TravelEventChoice(string Id, string Label);

/// <summary>An event that has stopped the journey, waiting for the player's choice.</summary>
internal sealed record TravelEvent(TravelEventKind Kind, string Title, string Text, IReadOnlyList<TravelEventChoice> Choices);

/// <summary>Where the party is on the water when an event is rolled (#9904): ashore (walking, or carrying the canoe round rapids), or afloat on a river or a lake.</summary>
internal enum TravelWater { Ashore, River, Lake }

/// <summary>What the roll reads about where and how the expedition travels.</summary>
/// <param name="Risk">The expedition's own factor, <see cref="PartyTravel.EventRisk"/>: exhaustion raises it.</param>
/// <param name="HomeKilometres">Distance from home: the farther out, the more dangerous the country.</param>
/// <param name="River">The river the party is at, as its stage has it (#9878): none, running, high, in flood, frozen or dry.</param>
/// <param name="Water">Whether the party is afloat, and on what (#9904): afloat, the river is the one it rides, not a ford.</param>
internal readonly record struct TravelEventFacts(MapBiome Biome, bool Night, RiverState River, double HomeKilometres, double Risk,
    TravelWater Water = TravelWater.Ashore);

/// <summary>
/// Rolls travel events (#9470, Den design/overland-travel-mode): a chance per travel hour from
/// environment- and danger-weighted tables, raised by the expedition's risk, with a cooldown after
/// each event. Deterministic for a world seed and the order of rolls; engine-free.
/// </summary>
internal sealed class TravelEventDirector(ulong seed)
{
    /// <summary>About one event in twelve travel hours for a fresh party near home in open country.</summary>
    internal const double ChancePerHour = 1d / 12d;
    /// <summary>Each kilometre from home adds this much danger, up to <see cref="MaximumDanger"/>.</summary>
    internal const double DangerPerKilometre = 0.1;
    internal const double MaximumDanger = 2;
    /// <summary>No event follows another within this many travel hours.</summary>
    internal const double CooldownHours = 4;
    /// <summary>At night encounters are likelier and discoveries rarer.</summary>
    internal const double NightEncounterFactor = 1.5;
    internal const double NightDiscoveryFactor = 0.5;
    /// <summary>How much likelier a hazard is at a river as its stage stands: a ford as usual, running high, in flood. Ice and a dry bed are no ford at all.</summary>
    internal const double RunningHazardFactor = 1.5, HighHazardFactor = 2.5, FloodHazardFactor = 4;

    internal static double RiverHazardFactor(RiverState river) => river switch
    {
        RiverState.Running => RunningHazardFactor,
        RiverState.High => HighHazardFactor,
        RiverState.Flood => FloodHazardFactor,
        _ => 1,
    };

    /// <summary>
    /// Afloat (#9904) the water keeps most hostile things off: encounters are this much rarer, and none is
    /// fought from the boat (an ambush needs ground with a way to the player, who sits aboard).
    /// </summary>
    internal const double AfloatEncounterFactor = 0.3;
    /// <summary>
    /// Afloat, how likely trouble on the water is, whatever the country's ground: on a river snags and
    /// shallows before its stage; on a lake only shoals, its waves being the wind's (#9902, windbound).
    /// </summary>
    internal const double RiverWaterHazard = 1, LakeWaterHazard = 0.4;
    /// <summary>How much likelier trouble is on a river running high (rapids) or in flood (sweepers).</summary>
    internal const double HighWaterHazardFactor = 1.8, FloodWaterHazardFactor = 3;

    internal static double WaterHazardFactor(RiverState river) => river switch
    {
        RiverState.High => HighWaterHazardFactor,
        RiverState.Flood => FloodWaterHazardFactor,
        _ => 1,
    };

    private double sinceLast = CooldownHours;
    private ulong draws;

    internal double HoursSinceLast => sinceLast;
    internal long Rolled { get; private set; }

    /// <summary>Roll for the hours just travelled; an event resets the cooldown.</summary>
    internal TravelEventKind? Roll(double hours, TravelEventFacts facts)
    {
        if (hours <= 0) return null;
        sinceLast += hours;
        if (sinceLast < CooldownHours) return null;
        Rolled++;
        double[] weights = Weights(facts);
        double total = weights.Sum();
        double danger = Danger(facts.HomeKilometres);
        // Weights average one in ordinary country, so the total scales the base chance.
        double rate = ChancePerHour * facts.Risk * danger * total / weights.Length;
        if (Draw() >= 1 - Math.Exp(-rate * hours)) return null;
        double pick = Draw() * total;
        sinceLast = 0;
        for (int kind = 0; kind < weights.Length; kind++)
        {
            pick -= weights[kind];
            if (pick < 0) return (TravelEventKind)kind;
        }
        return TravelEventKind.Hazard;
    }

    /// <summary>How dangerous the country is this far from home: 1 at home, rising to <see cref="MaximumDanger"/>.</summary>
    internal static double Danger(double homeKilometres) => Math.Min(MaximumDanger, 1 + Math.Max(0, homeKilometres) * DangerPerKilometre);

    /// <summary>How the route preview names a danger level.</summary>
    internal static string DangerName(double danger) => danger < ModerateDanger ? "low" : danger < HighDanger ? "moderate" : "high";
    private const double ModerateDanger = 1.3, HighDanger = 1.7;

    /// <summary>How likely each kind is here, indexed by <see cref="TravelEventKind"/>.</summary>
    internal static double[] Weights(TravelEventFacts facts)
    {
        (double encounter, double hazard) = facts.Biome switch
        {
            MapBiome.TemperateForest or MapBiome.BorealForest or MapBiome.Rainforest => (1.5, 0.8),
            MapBiome.Grassland or MapBiome.Shrubland or MapBiome.ColdSteppe => (1.2, 0.6),
            MapBiome.Desert => (0.7, 1.0),
            MapBiome.Tundra or MapBiome.IceField => (0.6, 1.0),
            MapBiome.Alpine => (0.6, 1.8),
            _ => (1, 1),
        };
        // Weather is never rolled (#9739): it arrives with the fronts the party can see coming.
        const double weather = 0;
        double discovery = 1;
        if (facts.Night)
        {
            encounter *= NightEncounterFactor;
            discovery *= NightDiscoveryFactor;
        }
        switch (facts.Water)
        {
            // Afloat, the trouble is the water's, not the ground's or a ford's; a discovery is as likely.
            case TravelWater.River:
                encounter *= AfloatEncounterFactor;
                hazard = RiverWaterHazard * WaterHazardFactor(facts.River);
                break;
            case TravelWater.Lake:
                encounter *= AfloatEncounterFactor;
                hazard = LakeWaterHazard;
                break;
            default:
                hazard *= RiverHazardFactor(facts.River);
                break;
        }
        return [encounter, discovery, weather, hazard];
    }

    /// <summary>A uniform draw in [0, 1): SplitMix64 over the seed and the draw's ordinal.</summary>
    private double Draw()
    {
        ulong z = seed + (++draws * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1d / (1UL << 53));
    }
}

/// <summary>
/// Trouble on the water (#9904): what it is, how going round it is put and what that costs in hours, and
/// how running it is put (it costs what pushing through a hazard ashore does).
/// </summary>
internal sealed record WaterHazard(string Title, string Text, string Round, double RoundHours, string Through);

/// <summary>What each kind of event says and offers. The product applies the chosen outcome.</summary>
internal static class TravelEvents
{
    internal const string Fight = "fight", Evade = "evade";
    internal const string Divert = "divert", Note = "note";
    internal const string Shelter = "camp", Press = "press";
    internal const string Detour = "detour", Push = "push";

    /// <summary>Hours lost slipping away from an encounter, or going round a hazard.</summary>
    internal const double EvadeHours = 2;
    internal const double DetourHours = 3;
    /// <summary>A river in flood has no ford: going round means a long way up to a crossing that holds.</summary>
    internal const double FloodDetourHours = 8;
    /// <summary>Sheltering from a front camps until it has passed, but never longer than this at one time.</summary>
    internal const double LongestShelterHours = 48;
    /// <summary>Pushing through a hazard costs a ration, or this much health with none to spare.</summary>
    internal const int HazardDamage = 4;
    internal const int AmbushSize = 3;

    /// <summary>Hours lining or carrying the boat round trouble on the water (#9904).</summary>
    internal const double SnagHours = 2, ShallowsHours = 2, RapidsHours = 3, SweepersHours = 5, ShoalsHours = 2;

    internal static readonly WaterHazard Snag = new("Snag", "A fallen tree lies across the channel ahead.", "Line the boat round it", SnagHours, "Shoot the gap");
    internal static readonly WaterHazard Shallows = new("Shallows", "The channel runs too shallow to float the loaded boat.", "Unload and walk it over", ShallowsHours, "Drag it over loaded");
    internal static readonly WaterHazard Rapids = new("Rapids ahead", "White water runs ahead, wilder than the river's usual.", "Line the boat down the bank", RapidsHours, "Run them");
    internal static readonly WaterHazard Sweepers = new("Sweepers", "The flood has torn trees from the banks; they rake the channel ahead.", "Land and line it past", SweepersHours, "Run the channel");
    internal static readonly WaterHazard Shoals = new("Shoals", "Rocks lie just under the surface across the way ahead.", "Feel a way round", ShoalsHours, "Run over them");

    /// <summary>
    /// The trouble afloat (#9904): on a lake, shoals; on a river, rapids when it runs high and sweepers in
    /// flood, otherwise a snag across the channel, or shallows for a drifting raft's deeper draught.
    /// </summary>
    internal static WaterHazard WaterHazardAt(TravelWater water, RiverState river, LegMode mode) => water == TravelWater.Lake ? Shoals : river switch
    {
        RiverState.High => Rapids,
        RiverState.Flood => Sweepers,
        _ => mode == LegMode.Drift ? Shallows : Snag,
    };

    /// <summary>A hazard on the water: go round it, lining or carrying the boat, or run it and risk the load.</summary>
    internal static TravelEvent Hazard(WaterHazard hazard) => new(TravelEventKind.Hazard, hazard.Title, hazard.Text,
        [new(Detour, FormattableString.Invariant($"{hazard.Round} ({hazard.RoundHours:F0} h)")), new(Push, hazard.Through + " (risk the load: costs a ration or health)")]);

    /// <summary>
    /// Hostile creatures afloat (#9904): they keep pace along the bank or shore, and the party, aboard, does
    /// not fight them; it stands off from the bank and loses hours, or runs past and risks a hit or the load.
    /// </summary>
    internal static TravelEvent EncounterAfloat(TravelWater water)
    {
        string edge = water == TravelWater.Lake ? "shore" : "bank";
        return new(TravelEventKind.Encounter, "Hostile creatures", $"Something hostile is keeping pace with the boat along the {edge}.",
            [new(Evade, FormattableString.Invariant($"Stand off from the {edge} ({EvadeHours:F0} h)")), new(Push, "Run past them (costs a ration or health)")]);
    }

    internal static TravelEvent Encounter(MapBiome biome) => new(TravelEventKind.Encounter,
        "Hostile creatures",
        biome is MapBiome.TemperateForest or MapBiome.BorealForest or MapBiome.Rainforest
            ? "Shapes move between the trees, closing on the party."
            : "Something hostile has picked up the party's trail and is closing in.",
        [new(Fight, "Stand and fight"), new(Evade, FormattableString.Invariant($"Slip away ({EvadeHours:F0} h)"))]);

    internal static TravelEvent Discovery(string place, double kilometres) => new(TravelEventKind.Discovery,
        "Something on the horizon",
        FormattableString.Invariant($"A scout spots a {place.ToLowerInvariant()} {kilometres:F1} km away. It is now on the map."),
        [new(Divert, "Divert to it"), new(Note, "Note it and carry on")]);

    /// <summary>
    /// A front reaches the party (#9739): named for what it is, with how long sheltering would take
    /// (until it passes, up to <see cref="LongestShelterHours"/>) and what pressing on costs.
    /// </summary>
    internal static TravelEvent Weather(string name, string arrival, double shelterHours, bool wounds) => new(TravelEventKind.Weather,
        name,
        arrival,
        [new(Shelter, FormattableString.Invariant($"Make camp until it passes ({Math.Min(shelterHours, LongestShelterHours):F0} h)")),
            new(Press, wounds ? "Press on (it wounds)" : "Press on (slow going)")]);

    /// <summary>
    /// Windbound (#9902): the wind raises waves on the lake ahead past what the boat crosses in. Waiting ashore
    /// takes until the wind drops (up to <see cref="LongestShelterHours"/>); pressing on risks swamping.
    /// </summary>
    internal static TravelEvent Windbound(string lake, string boat, double waves, double? waitHours) => new(TravelEventKind.Weather,
        "Windbound",
        FormattableString.Invariant($"The wind whips {lake} into waves of {waves:F1} m, too much for the {boat}."),
        [new(Shelter, waitHours is double wait
                ? FormattableString.Invariant($"Wait ashore for the wind to drop ({wait:F0} h)")
                : FormattableString.Invariant($"Wait ashore ({LongestShelterHours:F0} h; the wind holds)")),
            new(Push, "Press on (risk swamping: costs a ration or health)")]);

    /// <summary>Hours a detour round a hazard costs: a flooded river's is long.</summary>
    internal static double DetourFor(RiverState river) => river == RiverState.Flood ? FloodDetourHours : DetourHours;

    /// <summary>A hazard: at a river, its ford as the stage stands (#9878); elsewhere, or where the river is frozen or dry, the ground.</summary>
    internal static TravelEvent Hazard(MapBiome biome, RiverState river)
    {
        (string title, string text) = river switch
        {
            RiverState.Running => ("Cold ford", "The crossing ahead runs chest-deep and cold."),
            RiverState.High => ("Swollen ford", "The crossing ahead runs high and fast."),
            RiverState.Flood => ("River in flood", "The river is over its banks; no ford holds."),
            _ => (biome == MapBiome.Alpine ? "Rockslide" : "Broken ground", "The way ahead is blocked by treacherous ground."),
        };
        return new(TravelEventKind.Hazard, title, text,
            [new(Detour, FormattableString.Invariant($"Find a way round ({DetourFor(river):F0} h)")), new(Push, "Push through (costs a ration or health)")]);
    }
}
