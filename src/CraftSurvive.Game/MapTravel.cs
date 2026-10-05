using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game;

/// <summary>
/// Overland travel on the map (#9467, Den design/overland-travel-mode): the party token plans
/// terrain-costed routes and travels them while the map is open, fast-forwarding the world clock.
/// Closing the map explores wherever the token stands.
/// </summary>
public sealed partial class CraftSurviveProduct
{
    private const float PartyRelocateMetres = 4;
    private const double PlayHoursPerSecond = 24d / WorldClock.DaySeconds;
    /// <summary>Travel status republishes each time this many game hours have passed, not every update.</summary>
    private const double TravelPublishHours = 0.1;
    /// <summary>A daytime camp halts the expedition this many game hours.</summary>
    private const double DayCampHours = 2;
    /// <summary>The expedition eats a ration once a whole one fits in the stomach.</summary>
    private static readonly double EatRationBelowSatiety = SurvivalRules.MaximumSatiety - ItemCatalog.Ration.Value;

    private TravelCostModel? travelCost;
    private PartyTravel? party;
    private double unpublishedTravelHours;

    private TravelCostModel TravelCost => travelCost ??= new TravelCostModel(worlds.Current.Map);

    /// <summary>When the map opens, an idle party stands where the player stands.</summary>
    private void SyncPartyToPlayer()
    {
        Vector3 feet = player.WorldFeetPosition;
        Vector2 here = new(feet.X, feet.Z);
        if (party is null) party = new PartyTravel(here);
        else if (party.State is TravelState.Idle or TravelState.Arrived && Vector2.Distance(party.Position, here) > PartyRelocateMetres)
            party.Relocate(here);
        facetedMap?.MoveParty(party.Position);
        facetedMap?.ShowRoute(party.Route?.Points);
    }

    /// <summary>Plan a route to a destination; travel happens on the faceted map.</summary>
    private void PlanTravel(Vector2 destination, string name)
    {
        if (player.InSeparateSpace) throw new FormatException("Leave this place before planning overland travel.");
        if (!facetedMapShown) ShowFacetedMap(true);
        party ??= new PartyTravel(destination);
        bool planned = party.Plan(TravelCost, destination, name);
        facetedMap?.ShowRoute(party.Route?.Points);
        worldMessage = planned && party.Route is TravelRoute route ? RoutePreview(route) : party.Last;
    }

    /// <summary>
    /// The plan as the player confirms it (#9472): distance and daylight hours, the rations the
    /// march should eat beyond the food in hand, and how dangerous the country at the far end is.
    /// Night travel lengthens the journey, so the rations are a floor.
    /// </summary>
    private string RoutePreview(TravelRoute route)
    {
        double hungerPerHour = SurvivalRules.Tuning(conditions.Difficulty).HungerPerSecond / PlayHoursPerSecond;
        double beyondInHand = route.Hours * hungerPerHour - Math.Max(0, survival.State.Satiety - EatRationBelowSatiety);
        int rations = (int)Math.Ceiling(Math.Max(0, beyondInHand) / ItemCatalog.Ration.Value);
        double homeKilometres = Vector2.Distance(route.Points[^1], new((float)home.Home.X, (float)home.Home.Z)) / 1000;
        string danger = TravelEventDirector.DangerName(TravelEventDirector.Danger(homeKilometres));
        return string.Create(CultureInfo.InvariantCulture,
            $"{party!.Last} At least {rations} ration{(rations == 1 ? "" : "s")} (carrying {inventory.Count(ItemCatalog.Ration)}); danger {danger}. Set out to confirm.");
    }

    private void TravelAction(string action)
    {
        if (party is null) throw new FormatException("Plan a route first.");
        switch (action)
        {
            case "go":
                if (pendingEvent is not null) throw new FormatException("Answer the event first.");
                if (!party.Begin()) throw new FormatException("Plan a route first.");
                break;
            case "pause":
                party.Pause();
                SettleParty();
                break;
            case "halt":
                party.Stop();
                facetedMap?.ShowRoute(null);
                SettleParty();
                break;
            case "camp":
                Camp();
                return;
        }
        worldMessage = party.Last;
    }

    /// <summary>Advance a travelling party by this update's time; the world clock follows it.</summary>
    private void AdvanceTravel(double elapsedSeconds)
    {
        if (party is not { State: TravelState.Travelling }) return;
        WorldTime start = conditions.Time;
        double spent = party.Advance(elapsedSeconds * PartyTravel.HoursPerSecond,
            hours => WorldClock.IsNight(WorldClock.Advance(start, hours / PlayHoursPerSecond).DayFraction));
        bool arrived = party.State != TravelState.Travelling;
        conditions.Pass(spent / PlayHoursPerSecond, save: arrived);
        survival.Journey(spent / PlayHoursPerSecond, Meal);
        facetedMap?.MoveParty(party.Position);
        unpublishedTravelHours += spent;
        // An event settles the party itself and keeps the route for after the choice.
        RollTravelEvent(spent);
        bool stopped = party.State != TravelState.Travelling;
        if (arrived)
        {
            worldMessage = party.Last;
            facetedMap?.ShowRoute(null);
            SettleParty();
        }
        if (stopped || unpublishedTravelHours >= TravelPublishHours)
        {
            unpublishedTravelHours = 0;
            PublishWorld();
        }
    }

    /// <summary>The expedition eats a ration from its packs whenever one would not be wasted, marching or camped.</summary>
    private SurvivalState Meal(SurvivalState state) =>
        state.Satiety <= EatRationBelowSatiety && inventory.Spend(ItemCatalog.Ration) ? SurvivalRules.Eat(state, ItemCatalog.Ration.Value) : state;

    /// <summary>
    /// Make camp where the token stands: at night the expedition sleeps until morning, fully rested
    /// after a long enough night; by day it halts a couple of hours and recovers part of its fatigue. Camping is never
    /// required; it is how fatigue and the slow night march are avoided.
    /// </summary>
    private void Camp(double dayHours = DayCampHours)
    {
        if (party is null) throw new FormatException("Open the map to make camp.");
        party.Pause();
        if (!SettleParty()) return;
        bool night = conditions.IsNight;
        long refused = survival.RestsRefused;
        double began = conditions.Time.DayFraction;
        string outcome = night ? survival.Rest(Meal) : survival.RestFor(dayHours / PlayHoursPerSecond, Meal);
        if (survival.RestsRefused != refused)
        {
            worldMessage = "Cannot camp: " + outcome;
            return;
        }
        double slept = WorldClock.SecondsUntil(began, conditions.Time.DayFraction) * PlayHoursPerSecond;
        party.Rest(slept);
        SaveJourney();
        worldMessage = $"{party.Last} ({outcome})";
    }

    /// <summary>Leaving the map explores where the token stands.</summary>
    private void ExploreAtParty()
    {
        if (party is null || player.InSeparateSpace) return;
        bool arrived = party.State == TravelState.Arrived;
        party.Pause();
        if (SettleParty() && arrived && party.Route is not null) worldMessage = $"Exploring {party.Route.Destination}";
    }

    /// <summary>
    /// Whenever the journey stops, the player stands where the token stands and the continuation is
    /// saved at once: the existing player continuation is the one record of where the party is, so
    /// a restart restores it and reopening the map puts the token back there. The clock is saved too.
    /// </summary>
    private bool SettleParty()
    {
        conditions.Pass(0, save: true);
        if (party is null || player.InSeparateSpace) return false;
        Vector3 feet = player.WorldFeetPosition;
        if (Vector2.Distance(party.Position, new(feet.X, feet.Z)) > PartyRelocateMetres
            && player.Teleport(party.Position.X, terrain.GroundAt(party.Position.X, party.Position.Y) + ArrivalClearance, party.Position.Y) is null)
        {
            worldMessage = "No clear ground at the party's position; the expedition waits at its last camp.";
            return false;
        }
        player.SaveContinuationNow();
        SaveJourney();
        return true;
    }

    /// <summary>Map travel changes food and packs outside gameplay updates, so their saves follow the journey's.</summary>
    private void SaveJourney()
    {
        survival.SaveNow();
        inventory.SaveNow();
    }

    private string TravelStatus()
    {
        WorldTime time = conditions.Time;
        string clock = WorldClock.Describe(time);
        if (party?.Route is not TravelRoute route) return clock;
        string phase = party.State switch
        {
            TravelState.Planned => "route planned to",
            TravelState.Travelling => "travelling to",
            TravelState.Paused => "paused on the way to",
            TravelState.Arrived => "arrived at",
            _ => "idle near",
        };
        bool night = WorldClock.IsNight(time.DayFraction);
        return string.Create(CultureInfo.InvariantCulture,
            $"{clock} · {phase} {route.Destination} · {party.RemainingHours:F1} h of daylight travel left{(night ? " · night: very slow, consider camping" : "")}");
    }

    /// <summary>What the expedition carries and how worn it is, for the journey panel.</summary>
    private string TravelSupplies()
    {
        int rations = inventory.Count(ItemCatalog.Ration);
        double fatigue = party?.Fatigue ?? 0;
        string worn = party?.Exhausted == true ? "exhausted: slow until camp" : fatigue >= PartyTravel.ExhaustedAt / 2 ? "tiring" : "fresh";
        return string.Create(CultureInfo.InvariantCulture,
            $"rations {rations} · food {survival.State.Satiety:F0}% · {worn} ({fatigue * 100:F0}% fatigue)");
    }

    private string TravelPhase => party?.State switch
    {
        TravelState.Planned => "planned",
        TravelState.Travelling => "travelling",
        TravelState.Paused => "paused",
        TravelState.Arrived => "arrived",
        _ => "idle",
    };
}
