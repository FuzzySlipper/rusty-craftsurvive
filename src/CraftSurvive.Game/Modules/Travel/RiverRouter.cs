using System.Numerics;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>
/// A journey by boat, weighed against walking (#9879): the route if the river is faster, and both times.
/// <see cref="Route"/> is null when no water route beats walking or none can be made; <see cref="Reason"/> says why.
/// </summary>
internal sealed record RiverPlan(TravelRoute? Route, double WalkingHours, double RiverHours, string Reason);

/// <summary>
/// Plans a journey that mixes the water and the land (#9879). From the boat, a search runs over the
/// walked water network (<see cref="WaterNetwork"/>, 250 m): down a reach at the craft's paddling plus
/// the current, up it at the paddling less the current or lining from the bank (a canoe only), round
/// rapids carried (a canoe only), across a lake at the paddling. The current is the reach's mean
/// velocity scaled by the stage (#9878); a reach in flood, frozen or dry is closed. Every place the
/// boat could reach is a landing, and from each the walk on to the destination comes from one search
/// back from it over the land's lattice; the best total wins, and is taken only if it beats walking
/// the whole way. Engine-free and deterministic for a network, a stage and two points.
/// </summary>
internal static class RiverRouter
{
    private static readonly double[] Steps = [1, 1, 1, 1, Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2)];

    /// <summary>A journey by boat is taken only when it saves at least this share of the walk: launching and landing for a few minutes saved is not worth it.</summary>
    private const double MinimumSaving = 0.05;

    /// <summary>The boat is put in at the network's node nearest it, within this distance.</summary>
    private const double PutInMetres = 600;

    /// <summary>A walk from a landing to the land lattice is costed at least at this multiplier (a river bank's ford).</summary>
    private const double BankMultiplier = 1;

    /// <summary>One way the boat can go from a node: where to, how far, how long and how.</summary>
    private readonly record struct Edge(int To, double Hours, LegMode Mode);

    /// <param name="walkLoad">What the party's load makes of every walked hour (a hauled sled), as the march pays it; the water carries the load.</param>
    internal static RiverPlan Plan(TravelCostModel land, WaterNetwork network, BoatSpec boat, Vector2 boatAt, Func<int, ReachStage?>? stage,
        Vector2 from, Vector2 to, string destination, double walkLoad = 1)
    {
        MapGrid grid = land.Map.Grid;
        int goal = TravelRouter.Nearest(land, to), start = TravelRouter.Nearest(land, from);
        if (goal < 0 || start < 0) return new(null, double.PositiveInfinity, double.PositiveInfinity, "No passable route.");
        (double[] toGoal, int[] next) = Backward(land, goal);
        double walking = (toGoal[start] + Walk(land, from, start)) * walkLoad;

        Dictionary<int, List<Edge>> graph = Graph(land, network, boat, stage);
        int putIn = PutIn(network, graph, boatAt);
        if (putIn < 0) return new(null, walking, double.PositiveInfinity, $"The {boat.Name} lies by no open river.");

        // The water search from the put-in.
        Dictionary<int, double> best = new() { [putIn] = 0 };
        Dictionary<int, (int From, LegMode Mode, double Hours)> came = [];
        PriorityQueue<int, double> open = new();
        open.Enqueue(putIn, 0);
        while (open.TryDequeue(out int node, out double at))
        {
            if (at > best[node]) continue;
            if (!graph.TryGetValue(node, out List<Edge>? edges)) continue;
            foreach (Edge edge in edges)
            {
                double total = at + edge.Hours;
                if (total >= best.GetValueOrDefault(edge.To, double.PositiveInfinity)) continue;
                best[edge.To] = total;
                came[edge.To] = (node, edge.Mode, edge.Hours);
                open.Enqueue(edge.To, total);
            }
        }

        // The best landing: the water so far, then the walk ashore and on to the destination.
        double boarding = Vector2.Distance(from, boatAt) * BankMultiplier / TravelCostModel.MetresPerHour;
        int landing = -1, ashore = -1;
        double river = double.PositiveInfinity;
        foreach ((int node, double afloat) in best)
        {
            if (node == putIn) continue;
            Vector2 at = Position(network, node);
            int bank = TravelRouter.Nearest(land, at);
            if (bank < 0 || double.IsPositiveInfinity(toGoal[bank])) continue;
            double total = (boarding * walkLoad) + Boats.LaunchHours + afloat + Boats.LaunchHours + ((Walk(land, at, bank) + toGoal[bank]) * walkLoad);
            if (total < river) (river, landing, ashore) = (total, node, bank);
        }

        if (landing < 0) return new(null, walking, double.PositiveInfinity, $"The {boat.Name} can go nowhere from here.");
        if (river >= walking * (1 - MinimumSaving))
            return new(null, walking, river, FormattableString.Invariant($"Walking is as quick ({walking:F0} h against {river:F0} h by {boat.Name})."));

        // The route: to the boat, the water from the put-in to the landing, ashore, then the land on.
        List<Vector2> points = [from];
        List<double> hours = [];
        List<LegMode> modes = [];
        void Leg(Vector2 point, double legHours, LegMode mode)
        {
            points.Add(point);
            hours.Add(legHours);
            modes.Add(mode);
        }

        Leg(Position(network, putIn), boarding, LegMode.Walk);
        bool launched = false;
        List<(int Node, LegMode Mode, double Hours)> water = [];
        for (int node = landing; node != putIn; node = came[node].From) water.Add((node, came[node].Mode, came[node].Hours));
        water.Reverse();
        for (int i = 0; i < water.Count; i++)
        {
            // Loading and launching, and landing and unloading, are paid on the first and last legs afloat.
            double handling = (launched ? 0 : Boats.LaunchHours) + (i == water.Count - 1 ? Boats.LaunchHours : 0);
            launched = true;
            Leg(Position(network, water[i].Node), water[i].Hours + handling, water[i].Mode);
        }
        // Ashore, each walked leg costs what the search back from the goal charged it, so the route's hours are the plan's.
        Vector2 last = Position(network, landing);
        int previous = -1;
        for (int node = ashore; node >= 0; node = next[node])
        {
            Vector2 point = node == goal ? to : new((float)grid.X(node), (float)grid.Z(node));
            double legHours = previous < 0 ? Walk(land, last, node) : toGoal[previous] - toGoal[node];
            Leg(point, legHours, LegMode.Walk);
            (last, previous) = (point, node);
        }

        TravelRoute route = new(destination, [.. points], [.. hours], 0, [.. modes]);
        return new(route, walking, river,
            FormattableString.Invariant($"By {boat.Name}: {river:F0} h against {walking:F0} h on foot."));
    }

    /// <summary>The walk from a point to a lattice node, costed at the node's ground.</summary>
    private static double Walk(TravelCostModel land, Vector2 from, int node)
    {
        MapGrid grid = land.Map.Grid;
        double metres = Vector2.Distance(from, new((float)grid.X(node), (float)grid.Z(node)));
        // Ashore at a landing the party walks up the bank, not across the river.
        return metres * Math.Max(BankMultiplier, land.BankMultiplier(node)) / TravelCostModel.MetresPerHour;
    }

    /// <summary>
    /// Hours from every lattice node to the goal, and the next node on the way, by one search back from it.
    /// Leg costs are symmetric (distance at the mean of both cells' multipliers), so this is the forward cost.
    /// </summary>
    private static (double[] Hours, int[] Next) Backward(TravelCostModel land, int goal)
    {
        MapGrid grid = land.Map.Grid;
        double[] hours = new double[grid.Count];
        int[] next = new int[grid.Count];
        Array.Fill(hours, double.PositiveInfinity);
        Array.Fill(next, -1);
        hours[goal] = 0;
        PriorityQueue<int, double> open = new();
        open.Enqueue(goal, 0);
        int[] offsets = grid.Offsets();
        while (open.TryDequeue(out int node, out double at))
        {
            if (at > hours[node]) continue;
            bool interior = grid.IsInterior(node);
            for (int k = 0; k < 8; k++)
            {
                int other = interior ? node + offsets[k] : grid.Neighbour(node, k);
                if (other < 0 || !land.Passable(other)) continue;
                double total = at + (Steps[k] * grid.Spacing * (land.Multiplier(node) + land.Multiplier(other)) / 2 / TravelCostModel.MetresPerHour);
                if (total >= hours[other]) continue;
                hours[other] = total;
                next[other] = node;
                open.Enqueue(other, total);
            }
        }

        return (hours, next);
    }

    /// <summary>
    /// The water as a graph for one craft and the stage now: each open reach's nodes linked down and (for
    /// a canoe) up, rapids carried round (a canoe only), each reach joined to the one it flows into, and a
    /// lake's inflows and outflow joined across it.
    /// </summary>
    private static Dictionary<int, List<Edge>> Graph(TravelCostModel land, WaterNetwork network, BoatSpec boat, Func<int, ReachStage?>? stage)
    {
        Dictionary<int, List<Edge>> graph = [];
        void Link(int a, int b, double legHours, LegMode mode)
        {
            if (a == b || double.IsPositiveInfinity(legHours)) return;
            if (!graph.TryGetValue(a, out List<Edge>? edges)) graph[a] = edges = [];
            edges.Add(new(b, legHours, mode));
        }

        bool[] open = new bool[network.Reaches.Count];
        foreach (Reach reach in network.Reaches)
        {
            if (reach.Flow < Boats.LeastFlow) continue;
            ReachStage? now = stage?.Invoke(reach.Id);
            // Ice, a dry bed or a flood close a reach to boats; walking still crosses it.
            if (now is { Ice: true } or { Dry: true } or { Overbank: true }) continue;
            bool navigable = reach.Class != Navigability.None;
            if (!navigable && !boat.Portages) continue;
            open[reach.Id] = true;
            double current = Boats.Current(reach, now?.Ratio ?? 1);
            for (int i = 0; i + 1 < reach.Nodes.Length; i++)
            {
                int a = reach.Nodes[i], b = reach.Nodes[i + 1];
                double metres = Vector2.Distance(Position(network, a), Position(network, b));
                if (!navigable)
                {
                    // Rapids: the party carries the canoe along the bank, either way.
                    double carry = metres * Math.Max(BankMultiplier, LandMultiplier(land, Position(network, a))) * Boats.PortageMultiplier / TravelCostModel.MetresPerHour;
                    Link(a, b, carry, LegMode.Portage);
                    Link(b, a, carry, LegMode.Portage);
                    continue;
                }

                Link(a, b, Boats.MapHours(metres, Boats.Speed(boat, current, downstream: true)), boat.Upstream ? LegMode.Paddle : LegMode.Drift);
                double up = Boats.Speed(boat, current, downstream: false);
                if (up > 0) Link(b, a, Boats.MapHours(metres, up), Boats.Lined(boat, current) ? LegMode.Line : LegMode.Paddle);
            }
        }

        // Where a reach flows into the next, the two meet (their ends may be neighbouring nodes).
        foreach (Reach reach in network.Reaches)
        {
            if (!open[reach.Id] || reach.Downstream < 0 || !open[reach.Downstream]) continue;
            int outlet = reach.Outlet, source = network.Reaches[reach.Downstream].Source;
            if (outlet == source) continue;
            double metres = Vector2.Distance(Position(network, outlet), Position(network, source));
            Link(outlet, source, Boats.MapHours(metres, boat.Paddle + Boats.LiningSpeed), boat.Upstream ? LegMode.Paddle : LegMode.Drift);
            if (boat.Upstream) Link(source, outlet, Boats.MapHours(metres, boat.Paddle), LegMode.Paddle);
        }

        // Across a lake, still water: paddled (a raft poles slowly), unless the lake is frozen.
        foreach (LakeNode lake in network.Lakes)
        {
            if (lake.Outflow >= 0 && stage?.Invoke(lake.Outflow) is { Ice: true }) continue;
            List<int> ends = [.. lake.Inflows.Where(r => open[r]).Select(r => network.Reaches[r].Outlet)];
            if (lake.Outflow >= 0 && open[lake.Outflow]) ends.Add(network.Reaches[lake.Outflow].Source);
            foreach (int a in ends)
                foreach (int b in ends)
                {
                    double metres = Vector2.Distance(Position(network, a), Position(network, b));
                    Link(a, b, Boats.MapHours(metres, boat.Paddle), boat.Upstream ? LegMode.Paddle : LegMode.Drift);
                }
        }

        return graph;
    }

    /// <summary>The open water node nearest the boat, within a distance (<see cref="PutInMetres"/> by default), or -1.</summary>
    private static int PutIn(WaterNetwork network, Dictionary<int, List<Edge>> graph, Vector2 boatAt, double within = PutInMetres)
    {
        int nearest = -1;
        double distance = within;
        foreach (int node in graph.Keys)
        {
            double d = Vector2.Distance(Position(network, node), boatAt);
            if (d < distance) (nearest, distance) = (node, d);
        }

        return nearest;
    }

    /// <summary>Where the open water nearest a point lies, within a distance (the put-in's by default); null where there is none.</summary>
    internal static Vector2? NearestOpenWater(TravelCostModel land, WaterNetwork network, BoatSpec boat, Func<int, ReachStage?>? stage, Vector2 at,
        double within = PutInMetres)
    {
        Dictionary<int, List<Edge>> graph = Graph(land, network, boat, stage);
        int node = PutIn(network, graph, at, within);
        return node < 0 ? null : Position(network, node);
    }

    private static double LandMultiplier(TravelCostModel land, Vector2 at)
    {
        int node = TravelRouter.Nearest(land, at);
        return node < 0 ? BankMultiplier : land.BankMultiplier(node);
    }

    private static Vector2 Position(WaterNetwork network, int node) => new((float)network.Grid.X(node), (float)network.Grid.Z(node));
}
