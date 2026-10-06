using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Travel;
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
    private TravelEvent? pendingEvent;
    /// <summary>Where a discovery event's place stands, for its Divert choice.</summary>
    private (Vector2 Position, string Name)? discoveredPlace;

    private TravelEventDirector TravelEventRolls => travelEvents ??= new TravelEventDirector(worlds.Current.Map.Configuration.Seed ^ TravelEventSeedSalt);

    /// <summary>Roll for the hours the party just travelled; an event stops the journey for a choice.</summary>
    private void RollTravelEvent(double hours)
    {
        if (party is not { State: TravelState.Travelling } || pendingEvent is not null) return;
        WorldMap map = worlds.Current.Map;
        MapSample here = map.Sample(party.Position.X, party.Position.Y);
        MapBiome biome = WorldMap.Biome(here);
        bool river = double.IsFinite(here.RiverSurface);
        TravelEventFacts facts = new(biome, conditions.IsNight, river,
            Vector2.Distance(party.Position, new((float)home.Home.X, (float)home.Home.Z)) / 1000, party.EventRisk);
        if (TravelEventRolls.Roll(hours, facts) is TravelEventKind kind) Raise(kind, biome, river);
    }

    /// <summary>Present an event: the journey pauses and the map screen offers its choices.</summary>
    private void Raise(TravelEventKind kind, MapBiome biome, bool river)
    {
        TravelEvent? raised = kind switch
        {
            TravelEventKind.Encounter => TravelEvents.Encounter(biome),
            TravelEventKind.Discovery => DiscoverNearParty(),
            TravelEventKind.Weather => TravelEvents.Weather(biome),
            _ => TravelEvents.Hazard(biome, river),
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
                Camp(TravelEvents.ShelterHours);
                return;
            case TravelEvents.Press:
                party.Slow(TravelEvents.WeatherSlowHours, TravelEvents.WeatherSlowMultiplier);
                worldMessage = "Pressing on through the weather.";
                break;
            case TravelEvents.Detour:
                LoseHours(TravelEvents.DetourHours);
                worldMessage = FormattableString.Invariant($"Went round, losing {TravelEvents.DetourHours:F0} hours.");
                break;
            case TravelEvents.Push:
                if (inventory.Spend(ItemCatalog.Ration)) worldMessage = "Pushed through; a ration was lost.";
                else
                {
                    player.Vitals.TakeHit(TravelEvents.HazardDamage, 0);
                    worldMessage = string.Create(CultureInfo.InvariantCulture, $"Pushed through and took {TravelEvents.HazardDamage} damage.");
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
        MapSample here = worlds.Current.Map.Sample(party.Position.X, party.Position.Y);
        Raise(parsed, WorldMap.Biome(here), double.IsFinite(here.RiverSurface));
        PublishWorld();
        return pendingEvent is null ? "no event: nothing to find nearby" : TravelEventFacts();
    }

    internal string TravelReadout() => string.Create(CultureInfo.InvariantCulture,
        $"state={party?.State.ToString() ?? "none"} fatigue={party?.Fatigue ?? 0:F2} risk={party?.EventRisk ?? 1:F2} slowHours={party?.SlowHours ?? 0:F1} event={(pendingEvent is null ? "none" : TravelEventFacts())} rolls={travelEvents?.Rolled ?? 0} sinceLastEvent={travelEvents?.HoursSinceLast ?? 0:F1}h {home.Readout} places={KnownPlacesNow().Count} sledWithParty={sledWithParty} {sled.Readout}");
}
