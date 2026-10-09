using System.Numerics;

namespace CraftSurvive.Game.Modules.Travel;

internal enum TravelState { Idle, Planned, Travelling, Paused, Arrived }

/// <summary>
/// The expedition on the map: where its token stands, the route it plans or follows, and how far
/// along it is. Product-owned and engine-free; the map view draws it and the world clock is
/// advanced by the hours it reports.
/// </summary>
internal sealed class PartyTravel
{
    /// <summary>In-game hours of journey that pass per real second while the token moves.</summary>
    internal const double HoursPerSecond = 0.75;

    /// <summary>A fresh expedition marches about ten hours before it is exhausted.</summary>
    internal const double FatiguePerHour = 0.1;
    internal const double ExhaustedAt = 1;
    internal const double MaximumFatigue = 1.5;
    /// <summary>An exhausted expedition moves this many times slower until it camps.</summary>
    internal const double ExhaustedMultiplier = 1.5;
    /// <summary>A rest short of a night's camp recovers this much fatigue per hour.</summary>
    internal const double RecoveryPerHour = 0.1;
    /// <summary>An exhausted expedition meets trouble this many times as often: it is careless and slow to react.</summary>
    internal const double ExhaustedEventRisk = 1.5;
    /// <summary>A camp this long or longer is a full night's sleep and restores the expedition completely.</summary>
    internal const double FullRestHours = 6;

    private int leg;
    /// <summary>The leg at which head refinement was last tried, so an unrefinable head is not retried every update.</summary>
    private int refineTriedAt = -1;
    private double legProgress;

    internal PartyTravel(Vector2 position) => Position = position;

    internal Vector2 Position { get; private set; }
    internal TravelState State { get; private set; }
    internal TravelRoute? Route { get; private set; }
    internal string Last { get; private set; } = "";

    /// <summary>0 fresh, 1 exhausted, up to <see cref="MaximumFatigue"/>; marching raises it and camping restores it.</summary>
    internal double Fatigue { get; private set; }
    internal bool Exhausted => Fatigue >= ExhaustedAt;

    /// <summary>
    /// First-person walking tires the expedition at the map's rate per distance (#9553): the hours the
    /// map would take over these metres, at this country's cost, as if marched.
    /// </summary>
    internal void Walk(double metres, double multiplier)
    {
        if (metres <= 0 || !double.IsFinite(multiplier)) return;
        Tire(metres * multiplier / TravelCostModel.MetresPerHour);
    }

    /// <summary>Hours lost without progress (a detour, slipping away) still tire the expedition.</summary>
    internal void Tire(double hours) => Fatigue = Math.Min(MaximumFatigue, Fatigue + Math.Max(0, hours) * FatiguePerHour);

    /// <summary>A multiplier on every leg for what the party hauls (a loaded sled, #9473); 1 on foot.</summary>
    internal double LoadMultiplier { get; set; } = 1;

    /// <summary>How much more often travel events find this expedition than a fresh one, per travel hour.</summary>
    internal double EventRisk => Exhausted ? ExhaustedEventRisk : 1;

    /// <summary>A camp: a full night's sleep restores the expedition completely; a shorter rest restores part.</summary>
    internal void Rest(double hours)
    {
        bool full = hours >= FullRestHours;
        Fatigue = full ? 0 : Math.Max(0, Fatigue - hours * RecoveryPerHour);
        if (State == TravelState.Travelling) State = TravelState.Paused;
        Last = full ? "Camped through the night; the expedition is rested." : FormattableString.Invariant($"Rested {hours:F1} hours.");
    }

    /// <summary>Daylight hours still ahead on the route.</summary>
    internal double RemainingHours => Route is null ? 0 : Route.LegHours.Skip(leg).Sum() - legProgress * (leg < Route.LegHours.Length ? Route.LegHours[leg] : 0);

    /// <summary>Plan a route from the token's position; a failed plan leaves any current one.</summary>
    internal bool Plan(TravelCostModel cost, Vector2 destination, string name)
    {
        if (State == TravelState.Travelling) Pause();
        TravelRoute? route = TravelRouter.Plan(cost, Position, destination, name);
        if (route is null)
        {
            Last = $"No passable route to {name}.";
            return false;
        }
        Route = route;
        leg = 0;
        legProgress = 0;
        State = TravelState.Planned;
        refineTriedAt = -1;
        Last = $"Route to {name}: {TravelCalendar.Describe(route.Metres, route.Hours)}.";
        return true;
    }

    /// <summary>
    /// On a continent, re-plan the route's head over region ground once the party reaches the end of
    /// the part already refined (#9552). Progress is kept: the new route starts where the party stands.
    /// Returns true when the route changed.
    /// </summary>
    internal bool Refine(TravelCostModel cost)
    {
        if (cost.Regions is null || Route is null || State != TravelState.Travelling || leg < Route.RefinedLegs - 1 || leg == refineTriedAt) return false;
        if (TravelRouter.RefineHead(cost, Remaining(Route)) is not TravelRoute refined)
        {
            // A tile still building is retried on the next leg; the route is followed meanwhile.
            refineTriedAt = leg;
            return false;
        }
        Route = refined;
        leg = 0;
        legProgress = 0;
        refineTriedAt = -1;
        return true;
    }

    /// <summary>The rest of a route from where the party stands.</summary>
    private TravelRoute Remaining(TravelRoute route)
    {
        Vector2[] points = [Position, .. route.Points.Skip(leg + 1)];
        double[] hours = [.. route.LegHours.Skip(leg)];
        if (hours.Length > 0) hours[0] *= 1 - legProgress;
        return new(route.Destination, points, hours);
    }

    /// <summary>
    /// A copy of the party already on its way, to walk ahead of time (#9739): the forecast along a route
    /// rehearses the journey with the same night, fatigue, load and weather costs the march will pay.
    /// </summary>
    internal PartyTravel Rehearsal()
    {
        PartyTravel copy = Route is null ? new PartyTravel(Position) : Rehearsing(Route, Fatigue, LoadMultiplier);
        copy.Position = Position;
        copy.leg = leg;
        copy.legProgress = legProgress;
        return copy;
    }

    /// <summary>A party setting out along a route from its start, as tired and as loaded as given.</summary>
    internal static PartyTravel Rehearsing(TravelRoute route, double fatigue, double loadMultiplier) =>
        new(route.Points[0])
        {
            Route = route,
            State = TravelState.Travelling,
            Fatigue = fatigue,
            LoadMultiplier = loadMultiplier,
        };

    internal bool Begin()
    {
        if (Route is null || State is not (TravelState.Planned or TravelState.Paused)) return false;
        State = TravelState.Travelling;
        Last = $"Travelling to {Route.Destination}.";
        return true;
    }

    internal void Pause()
    {
        if (State != TravelState.Travelling) return;
        State = TravelState.Paused;
        Last = "Paused.";
    }

    /// <summary>Stop where the token stands, abandoning the route.</summary>
    internal void Stop()
    {
        Route = null;
        State = TravelState.Idle;
        Last = "Stopped.";
    }

    /// <summary>The token takes a new position (the player walked); any route is abandoned.</summary>
    internal void Relocate(Vector2 position)
    {
        Position = position;
        Route = null;
        State = TravelState.Idle;
    }

    /// <summary>
    /// Spend up to <paramref name="hours"/> of game time moving along the route. Night multiplies
    /// each leg's cost, and so does the weather where the party stands (#9739): <paramref name="environment"/>
    /// gives the multiplier after so many hours, read where the party then is. Returns the game hours
    /// actually spent; arrival ends travel.
    /// </summary>
    internal double Advance(double hours, Func<double, bool> nightAfter, Func<double, double>? environment = null)
    {
        if (State != TravelState.Travelling || Route is null || hours <= 0) return 0;
        double spent = 0;
        while (spent < hours && leg < Route.LegHours.Length)
        {
            double multiplier = (nightAfter(spent) ? TravelCostModel.NightMultiplier : 1) * (Exhausted ? ExhaustedMultiplier : 1)
                * Math.Max(1, environment?.Invoke(spent) ?? 1) * LoadMultiplier;
            double before = spent;
            double legHours = Math.Max(Route.LegHours[leg] * multiplier, 1e-9);
            double available = hours - spent;
            double needed = (1 - legProgress) * legHours;
            if (available >= needed)
            {
                spent += needed;
                leg++;
                legProgress = 0;
            }
            else
            {
                legProgress += available / legHours;
                spent = hours;
            }
            Position = leg < Route.LegHours.Length
                ? Vector2.Lerp(Route.Points[leg], Route.Points[leg + 1], (float)legProgress)
                : Route.Points[^1];
            Fatigue = Math.Min(MaximumFatigue, Fatigue + (spent - before) * FatiguePerHour);
        }
        if (leg >= Route.LegHours.Length)
        {
            State = TravelState.Arrived;
            Last = $"Arrived at {Route.Destination}.";
        }
        return spent;
    }
}
