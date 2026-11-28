namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Flow routing on a filled surface. Closed depressions are filled with an epsilon gradient
/// (Barnes' Priority-Flood+ε), so every land node has a strictly lower receiver and water
/// always reaches base level. Filling stands in for lake sedimentation.
/// </summary>
internal sealed class MapFlow
{
    /// <summary>Metres of rise per node step through filled flats; above float rounding at map heights.</summary>
    internal const double FillGradient = 0.002;
    internal const int Base = -1;
    private const double FillJitter = 1.5;
    private const ulong FillSalt = 0xE7037ED1A0B428DBUL;

    private MapFlow(MapGrid grid)
    {
        Grid = grid;
        Receiver = new int[grid.Count];
        Distance = new double[grid.Count];
        Order = new int[grid.Count];
        Discharge = new double[grid.Count];
    }

    internal MapGrid Grid { get; }
    internal int[] Receiver { get; }
    /// <summary>Length to the receiver in node units.</summary>
    internal double[] Distance { get; }
    /// <summary>Every node after its receiver: base level first, ridge crests last.</summary>
    internal int[] Order { get; }
    /// <summary>Rain-weighted upstream area in node units, including the node itself.</summary>
    internal double[] Discharge { get; }

    /// <summary>Metres above sea level at which a border node still drains off the map.</summary>
    internal const double BorderOutletClearance = 1;

    /// <summary>Base level for saved metre heights: the sea, and the border where it meets the sea.</summary>
    internal static bool[] Outlets(MapGrid grid, IReadOnlyList<double> height)
    {
        bool[] outlet = new bool[grid.Count];
        for (int i = 0; i < grid.Count; i++)
            outlet[i] = height[i] < GenerationConstants.WaterLevel
                || grid.IsEdge(i) && height[i] <= GenerationConstants.WaterLevel + BorderOutletClearance;
        return outlet;
    }

    /// <summary>Reusable scratch buffers for repeated fill/route passes in landscape evolution (#11184).</summary>
    internal sealed class Workspace(MapGrid grid)
    {
        internal readonly double[] Keys = new double[grid.Count];
        internal readonly bool[] Closed = new bool[grid.Count];
        internal readonly PriorityQueue<int, double> Open = new(grid.Side * 4);
        internal readonly Queue<int> Pit = new();
    }

    private const int ParallelThreshold = 65_536;

    /// <summary>Fill <paramref name="height"/> in place, then route and accumulate <paramref name="rain"/>.</summary>
    internal static MapFlow Route(MapGrid grid, double[] height, bool[] outlet, double[] rain, double fillStep, Workspace? workspace = null)
    {
        MapFlow flow = new(grid);
        RouteInto(flow, height, outlet, rain, fillStep, workspace ?? new Workspace(grid));
        return flow;
    }

    /// <summary>Fill <paramref name="height"/> in place, then route and accumulate <paramref name="rain"/> into an existing <see cref="MapFlow"/> (#11184).</summary>
    internal static void RouteInto(MapFlow flow, double[] height, bool[] outlet, double[] rain, double fillStep, Workspace workspace)
    {
        MapGrid grid = flow.Grid;
        Fill(grid, height, outlet, fillStep, workspace);
        double[] keys = workspace.Keys;
        int[] offsets = grid.Offsets();
        double[] lengths = MapGrid.StepLengths;
        int side = grid.Side, segments = grid.Segments;
        int[] order = flow.Order;
        int[] receiver = flow.Receiver;
        double[] distance = flow.Distance;
        int noLower = 0;

        void RouteRow(int z)
        {
            int rowStart = z * side;
            bool interiorZ = z > 0 && z < segments;
            for (int x = 0; x < side; x++)
            {
                int i = rowStart + x;
                order[i] = i;
                double hi = height[i];
                keys[i] = hi;
                receiver[i] = Base;
                if (outlet[i]) continue;
                double steepest = 0;
                int best = Base;
                double bestDist = 0;
                if (interiorZ && x > 0 && x < segments)
                {
                    for (int k = 0; k < 8; k++)
                    {
                        int n = i + offsets[k];
                        double slope = (hi - height[n]) / lengths[k];
                        if (slope > steepest) { steepest = slope; best = n; bestDist = lengths[k]; }
                    }
                }
                else
                {
                    for (int k = 0; k < 8; k++)
                    {
                        int n = grid.Neighbour(i, k);
                        if (n < 0) continue;
                        double slope = (hi - height[n]) / lengths[k];
                        if (slope > steepest) { steepest = slope; best = n; bestDist = lengths[k]; }
                    }
                }

                if (best == Base) Interlocked.Exchange(ref noLower, 1);
                receiver[i] = best;
                distance[i] = bestDist;
            }
        }

        if (grid.Count >= ParallelThreshold) Parallel.For(0, side, RouteRow);
        else for (int z = 0; z < side; z++) RouteRow(z);
        if (noLower != 0) throw new InvalidOperationException("A filled land node has no lower neighbour.");

        // Receivers are strictly lower, so ascending height is a valid downstream-first order.
        Array.Sort(keys, order);
        Array.Copy(rain, flow.Discharge, grid.Count);
        for (int n = grid.Count - 1; n >= 0; n--)
        {
            int i = order[n], r = receiver[i];
            if (r != Base) flow.Discharge[r] += flow.Discharge[i];
        }
    }

    internal static void Fill(MapGrid grid, double[] height, bool[] outlet, double step, Workspace? workspace = null)
    {
        PriorityQueue<int, double> open;
        Queue<int> pit;
        bool[] closed;
        if (workspace is not null)
        {
            open = workspace.Open;
            pit = workspace.Pit;
            closed = workspace.Closed;
            open.Clear();
            pit.Clear();
            Array.Clear(closed);
        }
        else
        {
            open = new(grid.Side * 4);
            pit = new();
            closed = new bool[grid.Count];
        }

        int[] offsets = grid.Offsets();
        double[] lengths = MapGrid.StepLengths;
        int side = grid.Side, segments = grid.Segments;
        for (int i = 0; i < grid.Count; i++)
            if (outlet[i]) { closed[i] = true; open.Enqueue(i, height[i]); }
        if (open.Count == 0) throw new InvalidOperationException("A map needs at least one outlet.");
        while (open.Count > 0 || pit.Count > 0)
        {
            int c = pit.Count > 0 ? pit.Dequeue() : open.Dequeue();
            int cz = Math.DivRem(c, side, out int cx);
            bool interior = cx > 0 && cz > 0 && cx < segments && cz < segments;
            double hc = height[c];
            for (int k = 0; k < 8; k++)
            {
                int n = interior ? c + offsets[k] : grid.Neighbour(c, k);
                if (n < 0 || closed[n]) continue;
                closed[n] = true;
                // A jittered rise lets paths across filled flats wander instead of running straight.
                double floor = hc + step * lengths[k] * (1 + FillJitter * MapNoise.Unit(FillSalt, c, n));
                if (height[n] <= floor) { height[n] = floor; pit.Enqueue(n); }
                else open.Enqueue(n, height[n]);
            }
        }
    }
}

/// <summary>
/// Landscape evolution: detachment-limited stream power (Braun and Willett's implicit,
/// unconditionally stable solver) against tectonic uplift, with linear hillslope creep.
/// Rivers cut down in proportion to discharge^m x slope, so trunk valleys grade gently and
/// headwaters stay steep, which is what produces dendritic valley networks and ridges.
/// </summary>
internal static class MapErosion
{
    // The stream-power area exponent is one half, applied as a square root.
    private const double BaseErodibility = 0.08;
    private const double HardRockResistance = 0.8;
    private const double Creep = 0.04;
    private const double SimulationFill = 1e-4;
    private const int ParallelThreshold = 65_536;

    internal static MapFlow Evolve(MapRelief relief, Func<double[], double[]> rainfall, int steps, int climateInterval, MapFlow.Workspace? workspace = null)
    {
        MapGrid grid = relief.Grid;
        double[] h = relief.Height;
        double[] rain = rainfall(h);
        double[] next = new double[grid.Count];
        MapFlow.Workspace scratch = workspace ?? new MapFlow.Workspace(grid);
        MapFlow flow = MapFlow.Route(grid, h, relief.Outlet, rain, SimulationFill, scratch);
        for (int step = 0; step < steps; step++)
        {
            if (step > 0 && step % climateInterval == 0) rain = rainfall(h);
            // Downstream first: each receiver already holds its new height (implicit in space).
            int[] order = flow.Order;
            int[] receiver = flow.Receiver;
            double[] discharge = flow.Discharge;
            double[] distance = flow.Distance;
            bool[] outlet = relief.Outlet;
            double[] hardness = relief.Hardness;
            double[] uplift = relief.Uplift;
            for (int n = 0; n < order.Length; n++)
            {
                int i = order[n];
                if (outlet[i]) continue;
                int r = receiver[i];
                double k = BaseErodibility * (1 - HardRockResistance * hardness[i]);
                double f = k * Math.Sqrt(discharge[i]) / distance[i];
                // A weighted mean of the uplifted node and its receiver: never below the receiver.
                h[i] = (h[i] + uplift[i] + f * h[r]) / (1 + f);
            }
            Diffuse(grid, h, relief.Outlet, next, Creep);
            MapFlow.RouteInto(flow, h, relief.Outlet, rain, SimulationFill, scratch);
        }
        return flow;
    }

    /// <summary>Linear creep (explicit, stable for rate below 1/8 on the 8-neighbour stencil).</summary>
    internal static void Diffuse(MapGrid grid, double[] h, bool[] fixedNode, double[] scratch, double rate)
    {
        int[] offsets = grid.Offsets();
        double[] lengths = MapGrid.StepLengths;
        int side = grid.Side, segments = grid.Segments;
        double[] invLengths = new double[8];
        double interiorWeight = 0;
        for (int k = 0; k < 8; k++)
        {
            invLengths[k] = 1 / lengths[k];
            interiorWeight += invLengths[k];
        }

        void DiffuseRow(int z)
        {
            int rowStart = z * side;
            bool interiorZ = z > 0 && z < segments;
            for (int x = 0; x < side; x++)
            {
                int i = rowStart + x;
                if (fixedNode[i]) { scratch[i] = h[i]; continue; }
                double hi = h[i];
                double sum = 0;
                if (interiorZ && x > 0 && x < segments)
                {
                    for (int k = 0; k < 8; k++) sum += invLengths[k] * (h[i + offsets[k]] - hi);
                    scratch[i] = hi + rate * sum / interiorWeight * 4;
                }
                else
                {
                    double weight = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int n = grid.Neighbour(i, k);
                        if (n < 0) continue;
                        double w = invLengths[k];
                        sum += w * (h[n] - hi);
                        weight += w;
                    }
                    scratch[i] = hi + rate * sum / weight * 4;
                }
            }
        }

        if (grid.Count >= ParallelThreshold) Parallel.For(0, side, DiffuseRow);
        else for (int z = 0; z < side; z++) DiffuseRow(z);
        Array.Copy(scratch, h, h.Length);
    }

    /// <summary>
    /// Talus relaxation in metres: where a slope exceeds the angle of repose, material slides
    /// to the lower neighbour. Softens knife ridges into climbable scree without flattening relief.
    /// </summary>
    internal static void Relax(MapGrid grid, double[] h, bool[] fixedNode, double maximumSlope, int iterations)
    {
        const double Transfer = 0.25;
        int[] offsets = grid.Offsets();
        double[] lengths = MapGrid.StepLengths;
        double[] limits = new double[8];
        for (int k = 0; k < 8; k++) limits[k] = maximumSlope * grid.Spacing * lengths[k];
        int side = grid.Side, segments = grid.Segments;
        for (int pass = 0; pass < iterations; pass++)
        {
            bool moved = false;
            for (int z = 0; z < side; z++)
            {
                int rowStart = z * side;
                bool interiorZ = z > 0 && z < segments;
                for (int x = 0; x < side; x++)
                {
                    int i = rowStart + x;
                    if (fixedNode[i]) continue;
                    bool interior = interiorZ && x > 0 && x < segments;
                    for (int k = 0; k < 8; k++)
                    {
                        int n = interior ? i + offsets[k] : grid.Neighbour(i, k);
                        if (n < 0) continue;
                        double excess = h[i] - h[n] - limits[k];
                        if (excess <= 0) continue;
                        double amount = excess * Transfer;
                        h[i] -= amount;
                        if (!fixedNode[n]) h[n] += amount;
                        moved = true;
                    }
                }
            }
            if (!moved) break;
        }
    }
}
