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

    /// <summary>Fill <paramref name="height"/> in place, then route and accumulate <paramref name="rain"/>.</summary>
    internal static MapFlow Route(MapGrid grid, double[] height, bool[] outlet, double[] rain, double fillStep)
    {
        Fill(grid, height, outlet, fillStep);
        MapFlow flow = new(grid);
        double[] keys = new double[grid.Count];
        int[] offsets = grid.Offsets();
        double[] lengths = MapGrid.StepLengths;
        for (int i = 0; i < grid.Count; i++)
        {
            flow.Order[i] = i;
            keys[i] = height[i];
            flow.Receiver[i] = Base;
            if (outlet[i]) continue;
            double steepest = 0;
            bool interior = grid.IsInterior(i);
            for (int k = 0; k < 8; k++)
            {
                int n = interior ? i + offsets[k] : grid.Neighbour(i, k);
                if (n < 0) continue;
                double slope = (height[i] - height[n]) / lengths[k];
                if (slope > steepest) { steepest = slope; flow.Receiver[i] = n; flow.Distance[i] = lengths[k]; }
            }
            if (flow.Receiver[i] == Base) throw new InvalidOperationException("A filled land node has no lower neighbour.");
        }
        // Receivers are strictly lower, so ascending height is a valid downstream-first order.
        Array.Sort(keys, flow.Order);
        for (int i = 0; i < grid.Count; i++) flow.Discharge[i] = rain[i];
        for (int n = grid.Count - 1; n >= 0; n--)
        {
            int i = flow.Order[n], r = flow.Receiver[i];
            if (r != Base) flow.Discharge[r] += flow.Discharge[i];
        }
        return flow;
    }

    internal static void Fill(MapGrid grid, double[] height, bool[] outlet, double step)
    {
        PriorityQueue<int, double> open = new(grid.Side * 4);
        Queue<int> pit = new();
        bool[] closed = new bool[grid.Count];
        int[] offsets = grid.Offsets();
        double[] lengths = MapGrid.StepLengths;
        for (int i = 0; i < grid.Count; i++)
            if (outlet[i]) { closed[i] = true; open.Enqueue(i, height[i]); }
        if (open.Count == 0) throw new InvalidOperationException("A map needs at least one outlet.");
        while (open.Count > 0 || pit.Count > 0)
        {
            int c = pit.Count > 0 ? pit.Dequeue() : open.Dequeue();
            bool interior = grid.IsInterior(c);
            for (int k = 0; k < 8; k++)
            {
                int n = interior ? c + offsets[k] : grid.Neighbour(c, k);
                if (n < 0 || closed[n]) continue;
                closed[n] = true;
                // A jittered rise lets paths across filled flats wander instead of running straight.
                double floor = height[c] + step * lengths[k] * (1 + FillJitter * MapNoise.Unit(FillSalt, c, n));
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

    internal static MapFlow Evolve(MapRelief relief, Func<double[], double[]> rainfall, int steps, int climateInterval)
    {
        MapGrid grid = relief.Grid;
        double[] h = relief.Height;
        double[] rain = rainfall(h);
        double[] next = new double[grid.Count];
        MapFlow flow = MapFlow.Route(grid, h, relief.Outlet, rain, SimulationFill);
        for (int step = 0; step < steps; step++)
        {
            if (step > 0 && step % climateInterval == 0) rain = rainfall(h);
            // Downstream first: each receiver already holds its new height (implicit in space).
            foreach (int i in flow.Order)
            {
                if (relief.Outlet[i]) continue;
                int r = flow.Receiver[i];
                double k = BaseErodibility * (1 - HardRockResistance * relief.Hardness[i]);
                double f = k * Math.Sqrt(flow.Discharge[i]) / flow.Distance[i];
                // A weighted mean of the uplifted node and its receiver: never below the receiver.
                h[i] = (h[i] + relief.Uplift[i] + f * h[r]) / (1 + f);
            }
            Diffuse(grid, h, relief.Outlet, next, Creep);
            flow = MapFlow.Route(grid, h, relief.Outlet, rain, SimulationFill);
        }
        return flow;
    }

    /// <summary>Linear creep (explicit, stable for rate below 1/8 on the 8-neighbour stencil).</summary>
    internal static void Diffuse(MapGrid grid, double[] h, bool[] fixedNode, double[] scratch, double rate)
    {
        int[] offsets = grid.Offsets();
        double[] lengths = MapGrid.StepLengths;
        for (int i = 0; i < grid.Count; i++)
        {
            if (fixedNode[i]) { scratch[i] = h[i]; continue; }
            double sum = 0, weight = 0;
            bool interior = grid.IsInterior(i);
            for (int k = 0; k < 8; k++)
            {
                int n = interior ? i + offsets[k] : grid.Neighbour(i, k);
                if (n < 0) continue;
                double w = 1 / lengths[k];
                sum += w * (h[n] - h[i]);
                weight += w;
            }
            scratch[i] = h[i] + rate * sum / weight * 4;
        }
        Array.Copy(scratch, h, h.Length);
    }

    /// <summary>
    /// Talus relaxation in metres: where a slope exceeds the angle of repose, material slides
    /// to the lower neighbour. Softens knife ridges into climbable scree without flattening relief.
    /// </summary>
    internal static void Relax(MapGrid grid, double[] h, bool[] fixedNode, double maximumSlope, int iterations)
    {
        const double Transfer = 0.25;
        for (int pass = 0; pass < iterations; pass++)
        {
            bool moved = false;
            for (int i = 0; i < grid.Count; i++)
            {
                if (fixedNode[i]) continue;
                for (int k = 0; k < 8; k++)
                {
                    int n = grid.Neighbour(i, k);
                    if (n < 0) continue;
                    double limit = maximumSlope * grid.Spacing * MapGrid.NeighbourLength(k);
                    double excess = h[i] - h[n] - limit;
                    if (excess <= 0) continue;
                    double amount = excess * Transfer;
                    h[i] -= amount;
                    if (!fixedNode[n]) h[n] += amount;
                    moved = true;
                }
            }
            if (!moved) break;
        }
    }
}
