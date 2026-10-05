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

    private int leg;
    private double legProgress;

    internal PartyTravel(Vector2 position) => Position = position;

    internal Vector2 Position { get; private set; }
    internal TravelState State { get; private set; }
    internal TravelRoute? Route { get; private set; }
    internal string Last { get; private set; } = "";

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
        Last = FormattableString.Invariant($"Route to {name}: {route.Metres / 1000:F1} km, about {route.Hours:F1} h of daylight travel.");
        return true;
    }

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
    /// each leg's cost. Returns the game hours actually spent; arrival ends travel.
    /// </summary>
    internal double Advance(double hours, Func<double, bool> nightAfter)
    {
        if (State != TravelState.Travelling || Route is null || hours <= 0) return 0;
        double spent = 0;
        while (spent < hours && leg < Route.LegHours.Length)
        {
            double multiplier = nightAfter(spent) ? TravelCostModel.NightMultiplier : 1;
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
        }
        if (leg >= Route.LegHours.Length)
        {
            State = TravelState.Arrived;
            Last = $"Arrived at {Route.Destination}.";
        }
        return spent;
    }
}
