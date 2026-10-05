using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>A planned journey: world X/Z waypoints and the daylight travel hours of each leg.</summary>
internal sealed record TravelRoute(string Destination, Vector2[] Points, double[] LegHours)
{
    internal double Hours => LegHours.Sum();
    internal double Metres
    {
        get
        {
            double metres = 0;
            for (int i = 1; i < Points.Length; i++) metres += Vector2.Distance(Points[i - 1], Points[i]);
            return metres;
        }
    }
}

/// <summary>A* over the map lattice: eight neighbours, leg cost is distance at the mean of both cells' multipliers.</summary>
internal static class TravelRouter
{
    private static readonly double[] Steps = [1, 1, 1, 1, Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2)];

    /// <summary>The cheapest route between two world points, or null when no passable route joins them.</summary>
    internal static TravelRoute? Plan(TravelCostModel cost, Vector2 from, Vector2 to, string destination)
    {
        MapGrid grid = cost.Map.Grid;
        int start = Nearest(cost, from), goal = Nearest(cost, to);
        if (start < 0 || goal < 0) return null;
        double[] best = new double[grid.Count];
        Array.Fill(best, double.PositiveInfinity);
        int[] came = new int[grid.Count];
        Array.Fill(came, -1);
        PriorityQueue<int, double> open = new();
        best[start] = 0;
        open.Enqueue(start, Heuristic(grid, start, goal));
        int[] offsets = grid.Offsets();
        while (open.TryDequeue(out int node, out _))
        {
            if (node == goal) break;
            bool interior = grid.IsInterior(node);
            for (int k = 0; k < 8; k++)
            {
                int next = interior ? node + offsets[k] : grid.Neighbour(node, k);
                if (next < 0 || !cost.Passable(next)) continue;
                double leg = Steps[k] * grid.Spacing * (cost.Multiplier(node) + cost.Multiplier(next)) / 2 / TravelCostModel.MetresPerHour;
                double total = best[node] + leg;
                if (total >= best[next]) continue;
                best[next] = total;
                came[next] = node;
                open.Enqueue(next, total + Heuristic(grid, next, goal));
            }
        }
        if (double.IsPositiveInfinity(best[goal])) return null;
        // An unreachable destination (at sea, on a cliff) resolves to the nearest ground the route reaches.
        Vector2 goalGround = new((float)grid.X(goal), (float)grid.Z(goal));
        if (Vector2.Distance(to, goalGround) > grid.Spacing) to = goalGround;
        List<int> path = [];
        for (int node = goal; node >= 0; node = came[node]) path.Add(node);
        path.Reverse();
        Vector2[] points = new Vector2[path.Count + 1];
        double[] hours = new double[path.Count];
        points[0] = from;
        for (int i = 0; i < path.Count; i++)
        {
            points[i + 1] = i == path.Count - 1 ? to : new((float)grid.X(path[i]), (float)grid.Z(path[i]));
            double multiplier = cost.Multiplier(path[i]);
            hours[i] = Vector2.Distance(points[i], points[i + 1]) * multiplier / TravelCostModel.MetresPerHour;
        }
        return new(destination, points, hours);
    }

    /// <summary>
    /// The nearest passable node to a world point. The search widens ring by ring, so a point out
    /// at sea or on a cliff resolves to the closest ground the expedition can stand on.
    /// </summary>
    internal static int Nearest(TravelCostModel cost, Vector2 point)
    {
        MapGrid grid = cost.Map.Grid;
        int cx = Math.Clamp((int)Math.Round((point.X + grid.Radius) / grid.Spacing), 0, grid.Segments);
        int cz = Math.Clamp((int)Math.Round((point.Y + grid.Radius) / grid.Spacing), 0, grid.Segments);
        for (int ring = 0; ring <= grid.Segments; ring++)
        {
            int bestNode = -1;
            double bestDistance = double.MaxValue;
            for (int dz = -ring; dz <= ring; dz++)
            for (int dx = -ring; dx <= ring; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                int x = cx + dx, z = cz + dz;
                if (x < 0 || z < 0 || x > grid.Segments || z > grid.Segments) continue;
                int node = z * grid.Side + x;
                double distance = dx * dx + dz * dz;
                if (cost.Passable(node) && distance < bestDistance) { bestDistance = distance; bestNode = node; }
            }
            if (bestNode >= 0) return bestNode;
        }
        return -1;
    }

    private static double Heuristic(MapGrid grid, int a, int b)
    {
        double dx = grid.X(a) - grid.X(b), dz = grid.Z(a) - grid.Z(b);
        return Math.Sqrt(dx * dx + dz * dz) / TravelCostModel.MetresPerHour;
    }
}
