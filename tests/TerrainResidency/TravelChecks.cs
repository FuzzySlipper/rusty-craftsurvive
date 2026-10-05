using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>Overland travel rules: cell costs, routes, journey time and night travel (#9467).</summary>
internal static class TravelChecks
{
    internal static void Run()
    {
        WorldMap map = WorldMapGenerator.Generate(new TerrainConfiguration(TerrainConstants.DefaultSeed, TerrainConstants.DefaultSize));
        TravelCostModel cost = new(map);
        MapGrid grid = map.Grid;

        int sea = Enumerable.Range(0, grid.Count).First(i => map.Fields.Elevation[i] < GenerationConstants.WaterLevel);
        Check.That(!cost.Passable(sea), "the sea is not crossed overland");
        Check.That(TravelCostModel.Environment(MapBiome.TemperateForest) > TravelCostModel.Environment(MapBiome.Grassland)
            && TravelCostModel.Environment(MapBiome.Alpine) > TravelCostModel.Environment(MapBiome.Desert), "rough country costs more than open country");
        Check.That(Enumerable.Range(0, grid.Count).Where(cost.Passable).All(i => cost.Multiplier(i) >= 1), "no cell is faster than open ground");

        // A route between two representative sites: contiguous, overland, and never cheaper than the straight line.
        MapSite from = map.Sites[0], to = map.Sites[^1];
        TravelRoute? route = TravelRouter.Plan(cost, new((float)from.X, (float)from.Z), new((float)to.X, (float)to.Z), to.Name);
        Check.That(route is not null, $"a route joins {from.Name} and {to.Name}");
        if (route is not null)
        {
            Check.That(route.Points.Zip(route.Points.Skip(1)).All(p => Vector2.Distance(p.First, p.Second) <= grid.Spacing * 1.5 + 1
                || p.First == route.Points[0] || p.Second == route.Points[^1]), "route legs join neighbouring cells");
            Check.That(route.Points.Skip(1).SkipLast(1).All(p => cost.Passable(TravelRouter.Nearest(cost, p))), "the route stays on passable ground");
            double straight = Vector2.Distance(route.Points[0], route.Points[^1]) / TravelCostModel.MetresPerHour;
            Check.That(route.Hours >= straight * 0.95, "journey time is never less than the straight line at open-ground pace");
            Console.WriteLine($"route {from.Name} -> {to.Name}: {route.Metres / 1000:F1} km, {route.Hours:F1} h (straight {straight:F1} h), {route.Points.Length} points");

            // Night is allowed but very slow: the same hours of night cover a third of the ground.
            PartyTravel day = new(route.Points[0]), night = new(route.Points[0]);
            day.Plan(cost, route.Points[^1], to.Name); night.Plan(cost, route.Points[^1], to.Name);
            day.Begin(); night.Begin();
            double daySpent = Rested(day, route.Hours + 1e-6, night: false);
            Check.That(day.State == TravelState.Arrived && Math.Abs(daySpent - day.Route!.Hours) < 1e-6, "daylight travel arrives after the route's hours");
            Rested(night, route.Hours, night: true);
            Check.That(night.State == TravelState.Travelling && night.RemainingHours > route.Hours * 0.6, "night travel covers about a third of the ground");
            Rested(night, route.Hours * 3, night: true);
            Check.That(night.State == TravelState.Arrived && night.Position == route.Points[^1], "night travel still arrives, three times slower");

            PartyTravel paused = new(route.Points[0]);
            paused.Plan(cost, route.Points[^1], to.Name);
            paused.Begin();
            paused.Advance(route.Hours / 2, _ => false);
            Vector2 midway = paused.Position;
            paused.Pause();
            Check.That(paused.Advance(1, _ => false) == 0 && paused.Position == midway, "a paused party does not move");
            paused.Stop();
            Check.That(paused.State == TravelState.Idle && paused.Route is null && paused.Position == midway, "halting keeps the party where it stood");

            // Fatigue (#9469): a long march exhausts the expedition and slows it; camping restores it.
            PartyTravel tired = new(route.Points[0]), fresh = new(route.Points[0]);
            TravelRoute far = Crossing(cost, grid) ?? route;
            tired.Plan(cost, far.Points[^1], "far"); fresh.Plan(cost, far.Points[^1], "far");
            tired.Begin(); fresh.Begin();
            double march = PartyTravel.ExhaustedAt / PartyTravel.FatiguePerHour;
            tired.Advance(march, _ => false);
            Check.That(tired.Exhausted && Math.Abs(tired.Fatigue - PartyTravel.ExhaustedAt) < 1e-6, "about ten hours of marching exhausts the expedition");
            Check.That(tired.EventRisk > new PartyTravel(route.Points[0]).EventRisk, "an exhausted expedition meets more trouble than a fresh one");
            double before = tired.RemainingHours;
            tired.Advance(1, _ => false);
            double exhaustedProgress = before - tired.RemainingHours;
            Check.That(Math.Abs(exhaustedProgress - 1 / PartyTravel.ExhaustedMultiplier) < 1e-3, "an exhausted expedition moves slower");
            tired.Rest(4);
            Check.That(tired.State == TravelState.Paused && tired.Fatigue < PartyTravel.ExhaustedAt, "a daytime camp halts and recovers part of the fatigue");
            tired.Rest(0.5);
            Check.That(tired.Fatigue > 0, "half an hour before dawn is not a night's sleep");
            tired.Rest(PartyTravel.FullRestHours);
            Check.That(tired.Fatigue == 0, "a night's camp restores the expedition fully");
            fresh.Advance(PartyTravel.MaximumFatigue / PartyTravel.FatiguePerHour * 4, _ => false);
            Check.That(fresh.Fatigue <= PartyTravel.MaximumFatigue, "fatigue is bounded");
        }

        // A full-world crossing lands near the owner's two-day starting point (Den design/overland-travel-mode).
        TravelRoute? crossing = Crossing(cost, grid);
        if (crossing is not null)
        {
            Console.WriteLine($"crossing: {crossing.Metres / 1000:F1} km in {crossing.Hours:F1} h of daylight travel");
            Check.That(crossing.Hours is > 12 and < 72, "crossing a 10 km world takes roughly one to three days of travel");
        }
        int west = Enumerable.Range(0, grid.Side).Select(x => (grid.Side / 2) * grid.Side + x).First(cost.Passable);
        TravelRoute? shore = TravelRouter.Plan(cost, new((float)grid.X(west), (float)grid.Z(west)), new((float)grid.X(sea), (float)grid.Z(sea)), "sea");
        Check.That(shore is not null && map.Sample(shore.Points[^1].X, shore.Points[^1].Y).Elevation >= GenerationConstants.WaterLevel - 0.5,
            "a destination at sea routes to the nearest shore and ends on land");
    }

    /// <summary>West to east across the middle of the world.</summary>
    private static TravelRoute? Crossing(TravelCostModel cost, MapGrid grid)
    {
        int west = Enumerable.Range(0, grid.Side).Select(x => (grid.Side / 2) * grid.Side + x).First(cost.Passable);
        int east = Enumerable.Range(0, grid.Side).Select(x => (grid.Side / 2) * grid.Side + grid.Segments - x).First(cost.Passable);
        return TravelRouter.Plan(cost, new((float)grid.X(west), (float)grid.Z(west)), new((float)grid.X(east), (float)grid.Z(east)), "far side");
    }

    /// <summary>Travel in marches short enough never to tire, camping overnight between them, so pace is the terrain's and the night's alone.</summary>
    private static double Rested(PartyTravel party, double hours, bool night)
    {
        const double March = 5;
        double spent = 0;
        while (spent < hours && party.State == TravelState.Travelling)
        {
            spent += party.Advance(Math.Min(March, hours - spent), _ => night);
            if (party.State != TravelState.Travelling) break;
            party.Rest(PartyTravel.FullRestHours);
            party.Begin();
        }
        return spent;
    }
}
