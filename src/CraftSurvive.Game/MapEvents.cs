using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game;

/// <summary>
/// Travel events (#9470, Den design/overland-travel-mode): rolled per travel hour while the party
/// moves, they pause the journey and wait for the player's choice on the map screen; the product
/// decides each outcome.
/// </summary>
public sealed partial class CraftSurviveProduct
{
    /// <summary>A discovery event looks this far around the party for a place not yet known.</summary>
    private const double DiscoveryRevealMetres = 1500;
    /// <summary>Mixed into the world seed so event draws are their own stream.</summary>
    private const ulong TravelEventSeedSalt = 0x7472_6176_656C_6576UL;

    private TravelEventDirector? travelEvents;

    /// <summary>A ford is judged by a river stage at most this many game hours behind the clock.</summary>
    private const double CrossingLagHours = 3;
    private TravelEvent? pendingEvent;
    /// <summary>Where a discovery event's place stands, for its Divert choice.</summary>
    private (Vector2 Position, string Name)? discoveredPlace;
    /// <summary>What going round the pending hazard costs: longer round a river in flood.</summary>
    private double hazardDetourHours = TravelEvents.DetourHours;

    private TravelEventDirector TravelEventRolls => travelEvents ??= new TravelEventDirector(worlds.Current.Map.Configuration.Seed ^ TravelEventSeedSalt);

    /// <summary>
    /// Roll for the hours the party just travelled; an event stops the journey for a choice. A front
    /// reaching the party is not rolled (#9739): each front stops the journey once, when it first covers it.
    /// </summary>
    private void RollTravelEvent(double hours, EnvironmentSample sky)
    {
        if (party is not { State: TravelState.Travelling } || pendingEvent is not null) return;
        if (frontArrivals.Arrived(sky) is WeatherFront arrived)
        {
            RaiseWeather(arrived);
            return;
        }

        if (RaiseWindbound(sky)) return;
        if (RaiseFloodwater()) return;

        EventPlace place = EventPlaceOfParty(party);
        TravelEventFacts facts = new(place.Biome, conditions.IsNight, place.River,
            Vector2.Distance(party.Position, new((float)home.Home.X, (float)home.Home.Z)) / 1000, party.EventRisk * sky.Effects.EventRisk, place.Water);
        if (TravelEventRolls.Roll(hours, facts) is TravelEventKind kind) Raise(kind, place);
    }

    /// <summary>Whether the party is already wading the flood it was last stopped at.</summary>
    private bool inFlood;

    /// <summary>
    /// Floodwater (#9880): a party on foot that walks onto flooded ground stops, offering to go round by
    /// higher ground or to wade. Once answered it is not stopped again until it is clear of the flood.
    /// </summary>
    private bool RaiseFloodwater()
    {
        if (party is null) return false;
        // On a floodplain the flood is judged by a stage no more than a few hours behind the clock, as a ford is.
        if (!party.Afloat && riverStage.Floodable(party.Position.X, party.Position.Y)) riverStage.CatchUp(CrossingLagHours);
        if (party.Afloat || !riverStage.Flooded(party.Position.X, party.Position.Y))
        {
            inFlood = false;
            return false;
        }

        if (inFlood) return false;
        inFlood = true;
        hazardDetourHours = TravelEvents.FloodDetourHours;
        party.Pause();
        pendingEvent = TravelEvents.Floodwater(null);
        worldMessage = pendingEvent.Title + ": " + pendingEvent.Text;
        SettleParty();
        return true;
    }

    /// <summary>Where an event finds the party: its country, its river as the stage has it, whether it is afloat and how its leg is made.</summary>
    private readonly record struct EventPlace(MapBiome Biome, RiverState River, TravelWater Water, LegMode Mode);

    /// <summary>
    /// Where the party is for a travel event (#9904). Afloat it is on a lake or a river; carrying the canoe
    /// round rapids it is ashore on the bank, crossing no ford.
    /// </summary>
    private EventPlace EventPlaceOfParty(PartyTravel at)
    {
        WorldMap map = worlds.Current.Map;
        MapSample here = map.Sample(at.Position.X, at.Position.Y);
        LegMode mode = at.Afloat && at.Route is TravelRoute route ? route.Mode(at.Leg) : LegMode.Walk;
        if (mode == LegMode.Portage) return new(WorldMap.Biome(here), RiverState.None, TravelWater.Ashore, mode);
        TravelWater water = mode == LegMode.Walk ? TravelWater.Ashore : map.LakeAt(at.Position.X, at.Position.Y) is null ? TravelWater.River : TravelWater.Lake;
        return new(WorldMap.Biome(here), RiverAtParty(here), water, mode);
    }

    /// <summary>The fronts already announced to the party, so each stops the journey once; and how long sheltering from the last takes.</summary>
    private readonly FrontArrivals frontArrivals = new();
    private double shelterHours;

    /// <summary>A front reaches the party: the journey pauses, offering to camp until it passes or to press on.</summary>
    private void RaiseWeather(WeatherFront front)
    {
        if (party is null) return;
        double now = weather.Hours, passed = now;
        while (passed < front.EndHours && front.Cover(party.Position.X, party.Position.Y, passed) >= FrontArrivals.PassedCover) passed += 1;
        shelterHours = Math.Max(1, Math.Min(passed - now, TravelEvents.LongestShelterHours));
        party.Pause();
        pendingEvent = TravelEvents.Weather(front.Kind.Name, front.Kind.Arrival, passed - now, front.Kind.Effects.Harm > 0);
        worldMessage = pendingEvent.Title + ": " + pendingEvent.Text;
        SettleParty();
    }

    /// <summary>The lake crossing the party pressed on into, which the wind no longer stops.</summary>
    private readonly WindboundCrossings windbound = new();

    /// <summary>
    /// Windbound (#9902): the party afloat on open lake water whose waves the wind raises past its boat's
    /// limit stops ashore, offering to wait until the wind drops or to press on. Only pressing on lets it
    /// across; after a wait - however it ended - the lake is judged again in the wind of the moment.
    /// </summary>
    private bool RaiseWindbound(EnvironmentSample sky)
    {
        if (party is null) return false;
        Lake? lake = party.Afloat ? worlds.Current.Map.LakeAt(party.Position.X, party.Position.Y) : null;
        if (!windbound.Holds(lake, lake is not null && riverStage.LakeIce(lake), boat.Boat?.Spec, sky.Channels.Wind)
            || lake is null || boat.Boat is not Boat held) return false;
        windbound.Raised(lake);
        (double x, double z, double now) = (party.Position.X, party.Position.Y, weather.Hours);
        double? wait = Windbound.Wait(held.Spec, lake, hours => weather.Field.Sample(x, z, now + hours).Channels.Wind, TravelEvents.LongestShelterHours);
        shelterHours = wait ?? TravelEvents.LongestShelterHours;
        party.Pause();
        pendingEvent = TravelEvents.Windbound(Windbound.Name(lake), held.Spec.Name, Windbound.Waves(lake, sky.Channels.Wind), wait);
        worldMessage = pendingEvent.Title + ": " + pendingEvent.Text;
        SettleParty();
        return true;
    }

    /// <summary>The map's river where the party stands, read as its walked reach's stage has it now (#9878).</summary>
    private RiverState RiverAtParty(MapSample here)
    {
        if (party is null || !double.IsFinite(here.RiverSurface)) return RiverState.None;
        // A crossing is judged by a stage no more than a few hours behind the clock: after a long camp or a
        // journey over land the stage is brought up first (#9878 review).
        riverStage.CatchUp(CrossingLagHours);
        return riverStage.CrossingAt(party.Position.X, party.Position.Y);
    }

    /// <summary>Present an event: the journey pauses and the map screen offers its choices.</summary>
    private void Raise(TravelEventKind kind, EventPlace place)
    {
        // Afloat (#9904) the trouble is the water's, and creatures keep to the bank.
        bool afloat = place.Water != TravelWater.Ashore;
        WaterHazard water = TravelEvents.WaterHazardAt(place.Water, place.River, place.Mode);
        hazardDetourHours = afloat ? water.RoundHours : TravelEvents.DetourFor(place.River);
        TravelEvent? raised = kind switch
        {
            TravelEventKind.Encounter => afloat ? TravelEvents.EncounterAfloat(place.Water) : TravelEvents.Encounter(place.Biome),
            TravelEventKind.Discovery => DiscoverNearParty(),
            _ => afloat ? TravelEvents.Hazard(water) : TravelEvents.Hazard(place.Biome, place.River),
        };
        // A discovery with nothing left to find nearby is no event at all.
        if (raised is null || party is null) return;
        party.Pause();
        pendingEvent = raised;
        worldMessage = raised.Title + ": " + raised.Text;
        SettleParty();
    }

    private TravelEvent? DiscoverNearParty()
    {
        if (party is null || discovery.RevealNearest(party.Position, DiscoveryRevealMetres) is not PoiSite site) return null;
        Vector2 at = new(site.X, site.Z);
        string name = DiscoveryRules.PlaceName(site.Kind);
        discoveredPlace = (at, name);
        facetedMap?.ShowPlaces(KnownPlacesNow());
        return TravelEvents.Discovery(name, Vector2.Distance(party.Position, at) / 1000);
    }

    /// <summary>Apply the player's answer to the pending event.</summary>
    private void ResolveTravelEvent(string choice)
    {
        if (pendingEvent is not TravelEvent current || party is null) throw new FormatException("No event is waiting.");
        if (!current.Choices.Any(offered => offered.Id == choice)) throw new FormatException("Choose one of the event's answers.");
        pendingEvent = null;
        windbound.Answered(pressedOn: choice == TravelEvents.Push);
        switch (choice)
        {
            case TravelEvents.Fight:
                // The fight happens in first person, at the token.
                ExploreAtParty();
                CloseMap();
                creatures.Ambush(TravelEvents.AmbushSize);
                worldMessage = "Ambushed: hostile creatures close in.";
                return;
            case TravelEvents.Evade:
                LoseHours(TravelEvents.EvadeHours);
                worldMessage = FormattableString.Invariant($"Slipped away, losing {TravelEvents.EvadeHours:F0} hours.");
                break;
            case TravelEvents.Divert when discoveredPlace is { } place:
                PlanTravel(place.Position, place.Name);
                party.Begin();
                discoveredPlace = null;
                worldMessage = party.Last;
                return;
            case TravelEvents.Note:
                discoveredPlace = null;
                worldMessage = "Noted on the map.";
                break;
            case TravelEvents.Shelter:
                Camp(shelterHours, overNight: false);
                return;
            case TravelEvents.Press:
                worldMessage = "Pressing on through the weather.";
                break;
            case TravelEvents.Detour:
                LoseHours(hazardDetourHours);
                worldMessage = FormattableString.Invariant($"Went round, losing {hazardDetourHours:F0} hours.");
                break;
            case TravelEvents.Push:
                // Afloat (#9904), what is lost goes over the side.
                bool afloat = party.Afloat;
                if (inventory.Spend(ItemCatalog.Ration)) worldMessage = afloat ? "Ran it; a ration went over the side." : "Pushed through; a ration was lost.";
                else
                {
                    player.Vitals.TakeHit(TravelEvents.HazardDamage, 0);
                    worldMessage = string.Create(CultureInfo.InvariantCulture, $"{(afloat ? "Ran it" : "Pushed through")} and took {TravelEvents.HazardDamage} damage.");
                }
                SaveJourney();
                break;
        }
        // Answered events let the journey carry on.
        party.Begin();
    }

    /// <summary>Hours spent without progress: the clock, food and fatigue all pay for them.</summary>
    private void LoseHours(double hours)
    {
        double seconds = hours / PlayHoursPerSecond;
        conditions.Pass(seconds, save: true);
        survival.Journey(seconds, Meal);
        party?.Tire(hours);
        SaveJourney();
    }

    /// <summary>The pending event for the map screen: <c>kind|title|text|id:label,...</c>, or empty.</summary>
    private string TravelEventFacts() => pendingEvent is TravelEvent shown
        ? string.Join('|', shown.Kind.ToString().ToLowerInvariant(), shown.Title, shown.Text,
            string.Join(',', shown.Choices.Select(choice => choice.Id + ":" + choice.Label)))
        : "";

    /// <summary>Raise an event of a kind now, for a live check of its presentation and outcomes.</summary>
    internal string ForceTravelEvent(string kind)
    {
        if (party is null || !mapOpen) return "refused: open the map with a party first";
        if (!Enum.TryParse(kind, ignoreCase: true, out TravelEventKind parsed)) return "refused: encounter, discovery, weather or hazard";
        pendingEvent = null;
        if (parsed == TravelEventKind.Weather)
        {
            // Weather comes only with a front: summon one first (craft.weather.summon) to see it.
            if (weather.Field.Sample(party.Position.X, party.Position.Y, weather.Hours).Dominant is not FrontPresence over) return "no event: no front over the party";
            RaiseWeather(over.Front);
            PublishWorld();
            return TravelEventFacts();
        }

        Raise(parsed, EventPlaceOfParty(party));
        PublishWorld();
        return pendingEvent is null ? "no event: nothing to find nearby" : TravelEventFacts();
    }

    internal string TravelReadout() => string.Create(CultureInfo.InvariantCulture,
        $"state={party?.State.ToString() ?? "none"} speed=x{travelSpeed} at={party?.Position.X ?? 0:F0},{party?.Position.Y ?? 0:F0} legs={party?.Route?.LegHours.Length ?? 0} refinedLegs={party?.Route?.RefinedLegs ?? 0} remainingHours={party?.RemainingHours ?? 0:F1} fatigue={party?.Fatigue ?? 0:F2} risk={party?.EventRisk ?? 1:F2} river={(party is null ? RiverState.None : RiverAtParty(worlds.Current.Map.Sample(party.Position.X, party.Position.Y)))} water={(party is null ? TravelWater.Ashore : EventPlaceOfParty(party).Water)} weather={weather.Field.Sample(party?.Position.X ?? 0, party?.Position.Y ?? 0, weather.Hours).Describe()} event={(pendingEvent is null ? "none" : TravelEventFacts())} rolls={travelEvents?.Rolled ?? 0} sinceLastEvent={travelEvents?.HoursSinceLast ?? 0:F1}h {home.Readout} places={KnownPlacesNow().Count} sledWithParty={sledWithParty} sledMarker=faceted:{facetedMap?.ShowsSled.ToString() ?? "none"},overview:{overview?.ShowsSled.ToString() ?? "none"} {sled.Readout}");
}
