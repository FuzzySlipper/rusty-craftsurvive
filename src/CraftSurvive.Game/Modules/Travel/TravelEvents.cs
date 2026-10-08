using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

internal enum TravelEventKind { Encounter, Discovery, Weather, Hazard }

/// <summary>One answer the player can give to a travel event; the product decides what it does.</summary>
internal readonly record struct TravelEventChoice(string Id, string Label);

/// <summary>An event that has stopped the journey, waiting for the player's choice.</summary>
internal sealed record TravelEvent(TravelEventKind Kind, string Title, string Text, IReadOnlyList<TravelEventChoice> Choices);

/// <summary>What the roll reads about where and how the expedition travels.</summary>
/// <param name="Risk">The expedition's own factor, <see cref="PartyTravel.EventRisk"/>: exhaustion raises it.</param>
/// <param name="HomeKilometres">Distance from home: the farther out, the more dangerous the country.</param>
internal readonly record struct TravelEventFacts(MapBiome Biome, bool Night, bool River, double HomeKilometres, double Risk);

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
    internal const double RiverHazardFactor = 2;

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
        if (facts.River) hazard *= RiverHazardFactor;
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
    /// <summary>Sheltering from a front camps until it has passed, but never longer than this at one time.</summary>
    internal const double LongestShelterHours = 48;
    /// <summary>Pushing through a hazard costs a ration, or this much health with none to spare.</summary>
    internal const int HazardDamage = 4;
    internal const int AmbushSize = 3;

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

    internal static TravelEvent Hazard(MapBiome biome, bool river) => new(TravelEventKind.Hazard,
        river ? "Swollen ford" : biome == MapBiome.Alpine ? "Rockslide" : "Broken ground",
        river ? "The crossing ahead runs high and fast." : "The way ahead is blocked by treacherous ground.",
        [new(Detour, FormattableString.Invariant($"Find a way round ({DetourHours:F0} h)")), new(Push, "Push through (costs a ration or health)")]);
}
