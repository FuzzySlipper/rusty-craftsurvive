using System.Numerics;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// River travel (#9879): the craft's rules, the float a boat rides in first person, and journeys planned
/// over the default continent's water network - down the port's river by canoe faster than walking, the
/// raft never upstream, a flood closing the water, the water owner's current running downstream.
/// </summary>
internal static class BoatChecks
{
    /// <summary>A settlement's boat waits at the open water within this distance (it stands off the floodplain).</summary>
    private const double SettlementWaterMetres = 3000;

    internal static void Rules()
    {
        Check.That(Boats.Speed(Boats.Canoe, 1, downstream: true) > Boats.Speed(Boats.Canoe, 1, downstream: false) && Boats.Speed(Boats.Canoe, 0, true) == Boats.Canoe.Paddle,
            "a canoe goes faster with the current than against it, and at its paddling in still water");
        Check.That(Boats.Speed(Boats.Raft, 1, downstream: false) == 0 && Boats.Speed(Boats.Raft, 1, downstream: true) == 1 + Boats.Raft.Paddle,
            "a raft drifts down at the current's pace and cannot go up");
        Check.That(Boats.Speed(Boats.Canoe, 10, downstream: false) == Boats.LiningSpeed && Boats.Lined(Boats.Canoe, 10) && !Boats.Lined(Boats.Canoe, 0.5),
            "against a torrent a canoe is lined from the bank at the lining pace");
        Check.That(Math.Abs(Boats.MapHours(3600, 1) - TravelCostModel.JourneyScale) < 1e-9 && double.IsPositiveInfinity(Boats.MapHours(100, 0)),
            "an hour on the water is compressed on the map as an hour's walk is");

        // The float: moored it holds, loosed it takes up the current, the paddle drives it, the bank stops it.
        FloatWater river = new(10, 2, 1, 0, false);
        BoatFloat moored = new(Boats.Canoe, 0, 0, 0);
        for (int step = 0; step < 120; step++) moored.Step(1.0 / 60, (_, _) => river, Vector2.Zero, laden: false);
        Check.That(moored.X == 0 && moored.Z == 0 && moored.Y == 10, "a moored boat holds its place at the water's surface");
        BoatFloat drifting = new(Boats.Raft, 0, 0, 0) { Moored = false };
        for (int step = 0; step < 60 * 30; step++) drifting.Step(1.0 / 60, (_, _) => river, Vector2.Zero, laden: true);
        Check.That(Math.Abs(drifting.Velocity.X - 1) < 0.01 && drifting.X > 25 && drifting.Y < 10, "a loosed raft takes up the current's velocity, and someone aboard sets it lower");
        BoatFloat paddled = new(Boats.Canoe, 0, 0, Math.PI / 2) { Moored = false };
        for (int step = 0; step < 60 * 10; step++) paddled.Step(1.0 / 60, (_, _) => new(10, 2, 0, 0, false), new(0, 1), laden: true);
        Check.That(paddled.Z < -10 && Math.Abs(paddled.X) < 0.5, "a canoe paddled forward goes along its bow (a heading of a quarter turn is north, -Z)");
        BoatFloat turning = new(Boats.Canoe, 0, 0, 0) { Moored = false };
        for (int step = 0; step < 60; step++) turning.Step(1.0 / 60, (_, _) => new(10, 2, 0, 0, false), new(1, 0), laden: true);
        Check.That(turning.Heading < 0, "steering right turns the bow clockwise seen from above");
        BoatFloat banked = new(Boats.Canoe, 0, 0, 0) { Moored = false };
        FloatWater Shore(double x, double z) => x < 5 ? river : new(10, 0.1, 0, 0, false);
        for (int step = 0; step < 60 * 20; step++) banked.Step(1.0 / 60, Shore, Vector2.Zero, laden: false);
        Check.That(banked.X < 5 && banked.Grounded, "a boat stops at water too shallow for its draft");
        BoatFloat iced = new(Boats.Canoe, 0, 0, 0) { Moored = false };
        iced.Step(1.0 / 60, (_, _) => new(10, 2, 1, 0, true), new(0, 1), laden: true);
        Check.That(iced.X == 0 && !iced.Floats(new(10, 2, 0, 0, true)), "ice holds a boat fast");
    }

    internal static void Run(WorldMap continent, TerrainRecipe recipe)
    {
        WaterNetwork network = MapRegions.For(continent).Drainage.Network;
        TravelCostModel land = new(continent);
        IReadOnlyList<Settlement> settlements = Settlements.For(continent);
        Settlement port = settlements.First(s => s.Kind == SettlementKind.Port);
        Vector2 harbour = new((float)port.X, (float)port.Z);
        int canoeWins = 0, journeys = 0;
        double bestGain = 0;
        foreach (Settlement town in settlements.Where(s => s.Kind == SettlementKind.Town))
        {
            Vector2 at = new((float)town.X, (float)town.Z);
            Vector2 boatAt = RiverRouter.NearestOpenWater(land, network, Boats.Canoe, null, at, SettlementWaterMetres) ?? at;
            RiverPlan down = RiverRouter.Plan(land, network, Boats.Canoe, boatAt, null, at, harbour, port.Name);
            journeys++;
            if (down.Route is not TravelRoute route) continue;
            canoeWins++;
            bestGain = Math.Max(bestGain, down.WalkingHours / down.RiverHours);
            Check.That(route.Points[0] == at && Vector2.Distance(route.Points[^1], harbour) <= continent.Grid.Spacing && route.Modes!.Length == route.LegHours.Length
                && route.ByBoat && route.Mode(0) == LegMode.Walk && route.LegHours.All(h => h >= 0 && double.IsFinite(h)),
                $"a canoe route from {town.Name} starts on foot at the town, ends at the port, and says how each leg is made");
            Check.That(Math.Abs(down.RiverHours - (route.Hours)) < 1e-6 && down.RiverHours < down.WalkingHours,
                $"a canoe route from {town.Name} is taken only when it beats walking ({down.RiverHours:F0} h against {down.WalkingHours:F0} h)");
            RiverPlan again = RiverRouter.Plan(land, network, Boats.Canoe, boatAt, null, at, harbour, port.Name);
            Check.That(again.Route is TravelRoute same && same.Points.SequenceEqual(route.Points) && same.LegHours.SequenceEqual(route.LegHours),
                "the same journey plans the same way");

            RiverPlan up = RiverRouter.Plan(land, network, Boats.Raft, RiverRouter.NearestOpenWater(land, network, Boats.Raft, null, harbour, SettlementWaterMetres) ?? harbour,
                null, harbour, at, town.Name);
            Check.That(up.Route is null || Enumerable.Range(0, up.Route.LegHours.Length).All(leg => up.Route.Mode(leg) is LegMode.Walk or LegMode.Drift),
                "a raft only drifts: it never paddles, lines or portages");

            // A flood everywhere closes the water: the journey is walked.
            RiverPlan flooded = RiverRouter.Plan(land, network, Boats.Canoe, boatAt, _ => new ReachStage(1000, 10, 2, 3, false, false), at, harbour, port.Name);
            Check.That(flooded.Route is null && Math.Abs(flooded.WalkingHours - down.WalkingHours) < 1e-6, "rivers in flood carry no boat: the journey is walked");
        }

        Console.WriteLine($"boats: a canoe beats walking from {canoeWins} of {journeys} river towns to the port, at best {bestGain:F1}x");
        Check.That(canoeWins >= 1 && bestGain > 1.5, $"a canoe down to the port beats walking from some river town, well ({bestGain:F1}x)");

        // The water owner's current runs down the channel: along the reach, faster in mid-stream than at its edge.
        WaterModel water = new(recipe);
        Reach great = network.Reaches.Where(r => r.Flow > WaterNetwork.CanoeFlow).OrderByDescending(r => r.Flow).First();
        int node = great.Nodes[great.Nodes.Length / 2], downstream = great.Nodes[Math.Min(great.Nodes.Length - 1, (great.Nodes.Length / 2) + 4)];
        Vector2 along = Vector2.Normalize(new Vector2((float)(network.Grid.X(downstream) - network.Grid.X(node)), (float)(network.Grid.Z(downstream) - network.Grid.Z(node))));
        WaterSample? fastest = null;
        for (int dz = -60; dz <= 60; dz += 2)
        for (int dx = -60; dx <= 60; dx += 2)
        {
            WaterSample s = water.Sample(network.Grid.X(node) + dx, network.Grid.Z(node) + dz);
            if (s.Body == WaterBody.River && (fastest is null || Speed(s) > Speed(fastest.Value))) fastest = s;
        }

        Check.That(fastest is WaterSample f && Speed(f) > 0.2 && Vector2.Dot(Vector2.Normalize(new((float)f.CurrentX, (float)f.CurrentZ)), along) > 0.3,
            $"the great river's current runs downstream ({(fastest is WaterSample g ? Speed(g) : 0):F2} m/s)");
    }

    private static double Speed(WaterSample sample) => Math.Sqrt((sample.CurrentX * sample.CurrentX) + (sample.CurrentZ * sample.CurrentZ));
}
