using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.World;
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

    /// <summary>The travel speeds offered (#9552): game time passes this many times faster while the party moves.</summary>
    private static readonly int[] TravelSpeeds = [1, 2, 4];
    private int travelSpeed = 1;
    private TravelCostModel? travelCost;
    private PartyTravel? party;
    private double unpublishedTravelHours;

    private TravelCostModel TravelCost => travelCost ??= new TravelCostModel(worlds.Current.Map);


    /// <summary>
    /// When the map opens, the party stands where the player stands. A journey left paused or planned
    /// keeps its destination and is planned again from there, for however the party now travels.
    /// </summary>
    private void SyncPartyToPlayer()
    {
        Vector3 feet = player.WorldFeetPosition;
        Vector2 here = new(feet.X, feet.Z);
        TravelRoute? resumed = null;
        if (party is null) party = new PartyTravel(here);
        else if (party.State != TravelState.Travelling && Vector2.Distance(party.Position, here) > PartyRelocateMetres)
        {
            resumed = party.State is TravelState.Planned or TravelState.Paused ? party.Route : null;
            party.Relocate(here);
        }
        HitchSledIfNear();
        if (resumed is not null && party.Plan(PartyCost, resumed.Points[^1], resumed.Destination) && party.Route is TravelRoute route)
            worldMessage = RoutePreview(route);
        facetedMap?.MoveParty(party.Position);
        facetedMap?.ShowRoute(party.Route?.Points);
    }

    /// <summary>Plan a route to a destination; travel happens on the faceted map.</summary>
    private void PlanTravel(Vector2 destination, string name)
    {
        if (player.InSeparateSpace) throw new FormatException("Leave this place before planning overland travel.");
        if (!facetedMapShown) ShowFacetedMap(true);
        party ??= new PartyTravel(destination);
        ApplySledLoad();
        bool planned = party.Plan(PartyCost, destination, name);
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
            $"{party!.Last} At least {rations} ration{(rations == 1 ? "" : "s")} (carrying {inventory.Count(ItemCatalog.Ration) + (sledWithParty ? sled.Sled.Count(ItemCatalog.Ration) : 0)}); danger {danger}; {(sledWithParty ? "hauling the sled" : "the sled stays behind")}. {WeatherOnRoute(route)} Set out to confirm.");
    }

    /// <summary>The route is checked against the forecast at about this many places along it.</summary>
    private const int RouteForecastSamples = 48;
    private const double RouteForecastCover = 0.3;

    /// <summary>
    /// The forecast along a route (#9739): each front the party would meet, and about when, reading
    /// the weather where the party would be at the hour it would get there (daylight pace, no weather
    /// delays), so the player can wait, detour or prepare.
    /// </summary>
    private string WeatherOnRoute(TravelRoute route)
    {
        double now = weather.Hours, total = route.Hours;
        List<(WeatherFront Front, double After)> met = [];
        double along = 0;
        int leg = 0;
        for (int sample = 0; sample <= RouteForecastSamples; sample++)
        {
            double target = total * sample / RouteForecastSamples;
            while (leg < route.LegHours.Length - 1 && along + route.LegHours[leg] < target) along += route.LegHours[leg++];
            double t = route.LegHours.Length == 0 || route.LegHours[leg] <= 0 ? 0 : Math.Clamp((target - along) / route.LegHours[leg], 0, 1);
            Vector2 at = route.LegHours.Length == 0 ? route.Points[0] : Vector2.Lerp(route.Points[leg], route.Points[leg + 1], (float)t);
            foreach (FrontPresence over in weather.Field.Sample(at.X, at.Y, now + target).Fronts)
            {
                if (over.Cover >= RouteForecastCover && met.All(seen => seen.Front.Key != over.Front.Key)) met.Add((over.Front, target));
            }
        }

        if (met.Count == 0) return "Clear skies forecast.";
        return "Weather on the way: " + string.Join(", ", met.Select(seen => seen.After < 1
            ? $"{seen.Front.Kind.Name.ToLowerInvariant()} now"
            : string.Create(CultureInfo.InvariantCulture, $"{seen.Front.Kind.Name.ToLowerInvariant()} in about {seen.After:F0} h"))) + ".";
    }

    /// <summary>The map shows fronts within this many map radii of the party, and where each will be over this many hours.</summary>
    private const double WeatherShownRadii = 1.5;
    private const double WeatherTrackHours = 24, WeatherTrackStepHours = 6;
    /// <summary>The map's fronts are redrawn once the clock has moved this many game hours.</summary>
    private const double WeatherRedrawHours = 0.1;
    private double weatherDrawnHours = double.NaN;
    private object? weatherDrawnOn;
    private int weatherDrawnRevision;

    /// <summary>Draws the fronts near the party on the faceted map (#9739), again whenever the clock has moved on.</summary>
    private void ShowWeatherOnMap()
    {
        if (facetedMap is null) return;
        double hours = weather.Hours;
        if (ReferenceEquals(weatherDrawnOn, facetedMap) && weatherDrawnRevision == weather.Field.Revision && Math.Abs(hours - weatherDrawnHours) < WeatherRedrawHours) return;
        Vector2 at = party?.Position ?? new(player.WorldFeetPosition.X, player.WorldFeetPosition.Z);
        List<WeatherMarker> fronts = [];
        double edge = worlds.Current.Map.Radius;
        // Fronts that touch the world, nearest the party first; those still beyond its edge are not drawn.
        foreach (WeatherFront front in weather.Field.Alive(at.X, at.Y, hours, edge * WeatherShownRadii)
            .Where(front => Math.Abs(front.Centre(hours).X) <= edge + front.RadiusMetres && Math.Abs(front.Centre(hours).Y) <= edge + front.RadiusMetres)
            .OrderBy(front => Vector2.Distance(front.Centre(hours), at)))
        {
            List<Vector2> track = [];
            for (double ahead = WeatherTrackStepHours; ahead <= WeatherTrackHours && hours + ahead <= front.EndHours; ahead += WeatherTrackStepHours)
                track.Add(front.Centre(hours + ahead));
            fronts.Add(new WeatherMarker(front.Kind.Id, front.Centre(hours), (float)front.RadiusMetres, (float)front.Strength(hours), [.. track]));
        }

        facetedMap.ShowWeather(fronts);
        weatherDrawnHours = hours;
        weatherDrawnOn = facetedMap;
        weatherDrawnRevision = weather.Field.Revision;
    }

    /// <summary>Choose how fast the journey runs; events still stop it whatever the speed.</summary>
    private void SetTravelSpeed(int speed)
    {
        if (!TravelSpeeds.Contains(speed)) throw new FormatException("Choose one of the offered travel speeds.");
        travelSpeed = speed;
        worldMessage = FormattableString.Invariant($"Travelling at ×{speed}.");
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

    /// <summary>A step longer than this between updates is a teleport, a rebase or a dungeon's door, not walking.</summary>
    private const float WalkStepLimitMetres = 25;
    /// <summary>Beyond this from the sled, first person prompts the player to bring the expedition by map (#9553).</summary>
    private const float SledLeashMetres = 1000;
    private Vector2? lastWalked;

    /// <summary>
    /// First person carries the journey's fatigue (#9553): ground walked tires the expedition at the
    /// map's rate for the country under the player, so walking is no way around the expedition's costs.
    /// </summary>
    private void AccrueWalkingFatigue()
    {
        if (mapOpen || player.InSeparateSpace) { lastWalked = null; return; }
        Vector2 here = new(player.WorldFeetPosition.X, player.WorldFeetPosition.Z);
        if (lastWalked is Vector2 before && Vector2.Distance(before, here) is float metres and > 0 and < WalkStepLimitMetres)
        {
            party ??= new PartyTravel(here);
            party.Walk(metres, TravelCost.MultiplierAt(here));
        }
        lastWalked = here;
    }

    /// <summary>The first-person reminders of the expedition: a sled left far behind, and exhaustion.</summary>
    private string ExpeditionPrompt()
    {
        List<string> parts = [];
        float fromSled = Vector2.Distance(new(player.WorldFeetPosition.X, player.WorldFeetPosition.Z), sled.Sled.Position);
        if (!player.InSeparateSpace && fromSled > SledLeashMetres)
            parts.Add(FormattableString.Invariant($"The sled is {fromSled / 1000:F1} km back: open the map to bring the expedition"));
        if (party is { Exhausted: true }) parts.Add("Exhausted: camp or rest to recover");
        // Weather that wounds anyone in the open (#9741) says so while the player stands in it.
        if (weather.Exposure is { Harm: > 0, Sheltered: false })
            parts.Add($"Take cover: the {weather.Here.Dominant?.Front.Kind.Name.ToLowerInvariant() ?? "weather"} wounds anyone in the open");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Advance a travelling party by this update's time; the world clock follows it. The weather where
    /// the party walks (#9739) slows each leg, makes the march hungrier, and may wound.
    /// </summary>
    private void AdvanceTravel(double elapsedSeconds)
    {
        if (party is not { State: TravelState.Travelling }) return;
        WorldTime start = conditions.Time;
        double startHours = WeatherModule.Of(start);
        double spent = party.Advance(elapsedSeconds * PartyTravel.HoursPerSecond * travelSpeed,
            hours => WorldClock.IsNight(WorldClock.Advance(start, hours / PlayHoursPerSecond).DayFraction),
            hours => weather.Field.Sample(party.Position.X, party.Position.Y, startHours + hours).Effects.TravelCost);
        bool arrived = party.State != TravelState.Travelling;
        conditions.Pass(spent / PlayHoursPerSecond, save: arrived);
        EnvironmentSample sky = weather.Field.Sample(party.Position.X, party.Position.Y, weather.Hours);
        survival.Journey(spent / PlayHoursPerSecond, Meal, sky.Effects.SupplyUse);
        WoundInTheOpen(sky, spent);
        facetedMap?.MoveParty(party.Position);
        // On a continent, the country ahead is refined before the party could stop in it (#9550), and
        // the route's head follows region ground as the party reaches it (#9552).
        if (worlds.Current.Map.Scale.Continental)
        {
            MapRegions.For(worlds.Current.Map).PrefetchAhead(party.Position, party.Route?.Points);
            if (party.Refine(PartyCost)) facetedMap?.ShowRoute(party.Route?.Points);
        }
        unpublishedTravelHours += spent;
        // An event settles the party itself and keeps the route for after the choice.
        RollTravelEvent(spent, sky);
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

    /// <summary>Wounds a party travelling in the open owes the weather, carried between updates until whole.</summary>
    private double woundsOwed;

    /// <summary>Weather that harms (the glass storm, #9739) wounds a party travelling through it; a camp is shelter.</summary>
    private void WoundInTheOpen(EnvironmentSample sky, double hours)
    {
        if (sky.Effects.Harm <= 0 || hours <= 0) return;
        woundsOwed += sky.Effects.Harm * hours;
        int wounds = (int)woundsOwed;
        if (wounds <= 0) return;
        woundsOwed -= wounds;
        player.Vitals.TakeHit(wounds, 0);
        worldMessage = string.Create(CultureInfo.InvariantCulture, $"The {sky.Dominant?.Front.Kind.Name.ToLowerInvariant() ?? "weather"} cuts at the party: {wounds} damage.");
    }

    /// <summary>The expedition eats a ration from its packs whenever one would not be wasted, marching or camped.</summary>
    private SurvivalState Meal(SurvivalState state) =>
        state.Satiety <= EatRationBelowSatiety && (inventory.Spend(ItemCatalog.Ration) || TakeSledRation()) ? SurvivalRules.Eat(state, ItemCatalog.Ration.Value) : state;

    /// <summary>
    /// Make camp where the token stands: at night the expedition sleeps until morning, fully rested
    /// after a long enough night; by day it halts a couple of hours and recovers part of its fatigue. Camping is never
    /// required; it is how fatigue and the slow night march are avoided.
    /// </summary>
    /// <param name="overNight">At night, sleep until morning; otherwise (sheltering from weather) rest exactly <paramref name="dayHours"/>.</param>
    private void Camp(double dayHours = DayCampHours, bool overNight = true)
    {
        if (party is null) throw new FormatException("Open the map to make camp.");
        party.Pause();
        if (!SettleParty()) return;
        bool night = conditions.IsNight && overNight;
        long refused = survival.RestsRefused;
        // The rest itself restores the expedition, through the survival owner's Slept (#9553).
        string outcome = night ? survival.Rest(Meal) : survival.RestFor(dayHours / PlayHoursPerSecond, Meal);
        if (survival.RestsRefused != refused)
        {
            worldMessage = "Cannot camp: " + outcome;
            return;
        }
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
        SettleSled();
        SaveJourney();
        return true;
    }

    /// <summary>Map travel changes food and packs outside gameplay updates, so their saves follow the journey's.</summary>
    private void SaveJourney()
    {
        survival.SaveNow();
        inventory.SaveNow();
        if (sledWithParty) sled.Save();
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
            $"{clock} · {phase} {route.Destination} · {TravelCalendar.Remaining(party.RemainingHours)} left{(night ? " · night: very slow, consider camping" : "")}");
    }

    /// <summary>What the expedition carries and how worn it is, for the journey panel.</summary>
    private string TravelSupplies()
    {
        int rations = inventory.Count(ItemCatalog.Ration);
        double fatigue = party?.Fatigue ?? 0;
        string worn = party?.Exhausted == true ? "exhausted: slow until camp" : fatigue >= PartyTravel.ExhaustedAt / 2 ? "tiring" : "fresh";
        return string.Create(CultureInfo.InvariantCulture,
            $"rations {rations} · food {survival.State.Satiety:F0}% · {worn} ({fatigue * 100:F0}% fatigue) · {SledSupplies()}");
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
