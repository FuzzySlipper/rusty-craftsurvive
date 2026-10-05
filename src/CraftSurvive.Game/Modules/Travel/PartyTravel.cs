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
    private double slowHours, slowMultiplier = 1;
    private double legProgress;

    internal PartyTravel(Vector2 position) => Position = position;

    internal Vector2 Position { get; private set; }
    internal TravelState State { get; private set; }
    internal TravelRoute? Route { get; private set; }
    internal string Last { get; private set; } = "";

    /// <summary>0 fresh, 1 exhausted, up to <see cref="MaximumFatigue"/>; marching raises it and camping restores it.</summary>
    internal double Fatigue { get; private set; }
    internal bool Exhausted => Fatigue >= ExhaustedAt;

    /// <summary>Hours lost without progress (a detour, slipping away) still tire the expedition.</summary>
    internal void Tire(double hours) => Fatigue = Math.Min(MaximumFatigue, Fatigue + Math.Max(0, hours) * FatiguePerHour);

    /// <summary>Weather slows travel: the next <paramref name="hours"/> of travel cost this many times more.</summary>
    internal void Slow(double hours, double multiplier)
    {
        slowHours = Math.Max(slowHours, hours);
        slowMultiplier = multiplier;
    }

    internal double SlowHours => slowHours;

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
            double multiplier = (nightAfter(spent) ? TravelCostModel.NightMultiplier : 1) * (Exhausted ? ExhaustedMultiplier : 1)
                * (slowHours > 0 ? slowMultiplier : 1);
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
            slowHours = Math.Max(0, slowHours - (spent - before));
        }
        if (leg >= Route.LegHours.Length)
        {
            State = TravelState.Arrived;
            Last = $"Arrived at {Route.Destination}.";
        }
        return spent;
    }
}
