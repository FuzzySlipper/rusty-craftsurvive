using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Sky;
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
        party.Plan(TravelCost, destination, name);
        facetedMap?.ShowRoute(party.Route?.Points);
        worldMessage = party.Last;
    }

    private void TravelAction(string action)
    {
        if (party is null) throw new FormatException("Plan a route first.");
        switch (action)
        {
            case "go":
                if (!party.Begin()) throw new FormatException("Plan a route first.");
                break;
            case "pause":
                party.Pause();
                conditions.Pass(0, save: true);
                break;
            case "halt":
                party.Stop();
                facetedMap?.ShowRoute(null);
                conditions.Pass(0, save: true);
                break;
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
        bool stopped = party.State != TravelState.Travelling;
        conditions.Pass(spent / PlayHoursPerSecond, save: stopped);
        facetedMap?.MoveParty(party.Position);
        unpublishedTravelHours += spent;
        if (stopped)
        {
            worldMessage = party.Last;
            facetedMap?.ShowRoute(null);
        }
        if (stopped || unpublishedTravelHours >= TravelPublishHours)
        {
            unpublishedTravelHours = 0;
            PublishWorld();
        }
    }

    /// <summary>Leaving the map explores where the token stands.</summary>
    private void ExploreAtParty()
    {
        if (party is null || player.InSeparateSpace) return;
        party.Pause();
        Vector3 feet = player.WorldFeetPosition;
        if (Vector2.Distance(party.Position, new(feet.X, feet.Z)) <= PartyRelocateMetres) return;
        if (player.Teleport(party.Position.X, terrain.GroundAt(party.Position.X, party.Position.Y) + ArrivalClearance, party.Position.Y) is null)
            worldMessage = "No clear ground at the party's position; exploring from your last place instead.";
        else if (party.State == TravelState.Arrived && party.Route is not null) worldMessage = $"Exploring {party.Route.Destination}";
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

    private string TravelPhase => party?.State switch
    {
        TravelState.Planned => "planned",
        TravelState.Travelling => "travelling",
        TravelState.Paused => "paused",
        TravelState.Arrived => "arrived",
        _ => "idle",
    };
}
