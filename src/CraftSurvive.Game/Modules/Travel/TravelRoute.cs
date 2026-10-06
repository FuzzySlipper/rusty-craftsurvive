using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>
/// A planned journey: world X/Z waypoints and the daylight travel hours of each leg. On a continent
/// the first <see cref="RefinedLegs"/> legs follow a region tile's 32 m ground and the rest the
/// kilometre lattice (#9552).
/// </summary>
internal sealed record TravelRoute(string Destination, Vector2[] Points, double[] LegHours, int RefinedLegs = 0)
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

/// <summary>
/// A* over a map lattice: eight neighbours, leg cost is distance at the mean of both cells' multipliers.
/// A continent plans over its kilometre lattice, then refines the route's head over the region tile
/// the party stands in (#9552), and the party refines again as it reaches the end of that head.
/// </summary>
internal static class TravelRouter
{
    private static readonly double[] Steps = [1, 1, 1, 1, Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2)];
    /// <summary>How far ahead a route's head is refined over region ground.</summary>
    internal const double RefineAheadMetres = 6000;
    /// <summary>The refined search may stray this far beside the straight line between its ends.</summary>
    private const double RefineMarginMetres = 1500;
    /// <summary>A refined head stays this far inside its tile's simulated extent.</summary>
    private const double RefineTileInset = 256;

    /// <summary>The cheapest route between two world points, or null when no passable route joins them.</summary>
    internal static TravelRoute? Plan(TravelCostModel cost, Vector2 from, Vector2 to, string destination)
    {
        MapGrid grid = cost.Map.Grid;
        int start = Nearest(cost, from), goal = Nearest(cost, to);
        if (start < 0 || goal < 0) return null;
        if (Search(cost, start, goal, null) is not List<int> path) return null;
        // An unreachable destination (at sea, on a cliff) resolves to the nearest ground the route reaches.
        Vector2 goalGround = new((float)grid.X(goal), (float)grid.Z(goal));
        if (Vector2.Distance(to, goalGround) > grid.Spacing) to = goalGround;
        TravelRoute coarse = Assemble(cost, path, Vector2.Zero, from, to, destination);
        return RefineHead(cost, coarse) ?? coarse;
    }

    /// <summary>
    /// The route with its head, up to <see cref="RefineAheadMetres"/> and within the region tile at its
    /// start, re-planned over that tile's 32 m lattice; null when the tile is not built yet (it is queued)
    /// or there is nothing to refine.
    /// </summary>
    internal static TravelRoute? RefineHead(TravelCostModel cost, TravelRoute route)
    {
        if (cost.Regions is not MapRegions regions || route.Points.Length < 2) return null;
        Vector2 from = route.Points[0];
        (int X, int Z) key = MapRegions.TileAt(from.X, from.Y);
        if (!regions.IsReady(key))
        {
            regions.Prefetch(from.X, from.Y, RefineAheadMetres);
            return null;
        }
        (double cx, double cz) = MapRegions.Centre(key);
        double usable = MapRegions.TileExtent / 2d - RefineTileInset, along = 0;
        int target = 0;
        for (int k = 1; k < route.Points.Length; k++)
        {
            along += Vector2.Distance(route.Points[k - 1], route.Points[k]);
            if (along > RefineAheadMetres || Math.Abs(route.Points[k].X - cx) > usable || Math.Abs(route.Points[k].Y - cz) > usable) break;
            target = k;
        }
        if (target == 0) return null;
        TravelCostModel local = cost.RegionCost(regions.Tile(key).Map);
        MapGrid grid = local.Map.Grid;
        Vector2 offset = new((float)cx, (float)cz), a = from - offset, b = route.Points[target] - offset;
        int start = Nearest(local, a), goal = Nearest(local, b);
        if (start < 0 || goal < 0) return null;
        int Index(double metres) => Math.Clamp((int)Math.Round((metres + grid.Radius) / grid.Spacing), 0, grid.Segments);
        (int, int, int, int) bounds = (Index(Math.Min(a.X, b.X) - RefineMarginMetres), Index(Math.Min(a.Y, b.Y) - RefineMarginMetres),
            Index(Math.Max(a.X, b.X) + RefineMarginMetres), Index(Math.Max(a.Y, b.Y) + RefineMarginMetres));
        if (Search(local, start, goal, bounds) is not List<int> path) return null;
        TravelRoute head = Assemble(local, path, offset, from, route.Points[target], route.Destination);
        return new(route.Destination, [.. head.Points, .. route.Points.Skip(target + 1)], [.. head.LegHours, .. route.LegHours.Skip(target)], head.LegHours.Length);
    }

    /// <summary>The node path from start to goal, optionally within a box of node columns and rows; null when none joins them.</summary>
    private static List<int>? Search(TravelCostModel cost, int start, int goal, (int MinX, int MinZ, int MaxX, int MaxZ)? bounds)
    {
        MapGrid grid = cost.Map.Grid;
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
                if (bounds is { } box && (next % grid.Side < box.MinX || next % grid.Side > box.MaxX || next / grid.Side < box.MinZ || next / grid.Side > box.MaxZ)) continue;
                double leg = Steps[k] * grid.Spacing * (cost.Multiplier(node) + cost.Multiplier(next)) / 2 / TravelCostModel.MetresPerHour;
                double total = best[node] + leg;
                if (total >= best[next]) continue;
                best[next] = total;
                came[next] = node;
                open.Enqueue(next, total + Heuristic(grid, next, goal));
            }
        }
        if (double.IsPositiveInfinity(best[goal])) return null;
        List<int> path = [];
        for (int node = goal; node >= 0; node = came[node]) path.Add(node);
        path.Reverse();
        return path;
    }

    /// <summary>A route from a node path: world points (lattice metres plus <paramref name="offset"/>) and each leg's daylight hours.</summary>
    private static TravelRoute Assemble(TravelCostModel cost, List<int> path, Vector2 offset, Vector2 from, Vector2 to, string destination)
    {
        MapGrid grid = cost.Map.Grid;
        Vector2[] points = new Vector2[path.Count + 1];
        double[] hours = new double[path.Count];
        points[0] = from;
        for (int i = 0; i < path.Count; i++)
        {
            points[i + 1] = i == path.Count - 1 ? to : new Vector2((float)grid.X(path[i]), (float)grid.Z(path[i])) + offset;
            hours[i] = Vector2.Distance(points[i], points[i + 1]) * cost.Multiplier(path[i]) / TravelCostModel.MetresPerHour;
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
