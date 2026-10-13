using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>An area of the world (metres) where water froze, thawed, ran dry, came back, flooded or receded.</summary>
internal readonly record struct WaterBounds(double MinX, double MinZ, double MaxX, double MaxZ);

/// <summary>
/// Floods (#9880): where a river over its banks puts water. Each node of a floodplain (#9875: ground the
/// bankfull water and a storm would cover, within some channel widths of a canoe river) belongs to the
/// reach nearest it, and floods when that reach runs over its banks - to the bank's height over the
/// channel's mean surface beside it, and as much again as the overflow stands spread across the
/// floodplain's width. The water stands level over that ground, clipped by the ground itself: nothing
/// flows, and nothing floods beyond the mapped floodplain. Once the river falls back the plain drains,
/// its water going down at most <see cref="RecessionMetresPerHour"/>. Engine-free.
/// </summary>
internal sealed class FloodPlains
{
    /// <summary>The fastest a flood's water on its plain goes down (metres a game hour) once its river falls.</summary>
    internal const double RecessionMetresPerHour = 0.1;

    private readonly int[] reachOf;
    private readonly float[] channelOf;
    private readonly float[] ground;

    internal FloodPlains(WaterNetwork network, Func<int, float> elevation)
    {
        Network = network ?? throw new ArgumentNullException(nameof(network));
        MapGrid grid = network.Grid;
        reachOf = new int[grid.Count];
        channelOf = new float[grid.Count];
        ground = new float[grid.Count];
        Array.Fill(reachOf, -1);
        int[] nearest = new int[grid.Count];
        Array.Fill(nearest, int.MaxValue);
        for (int i = 0; i < grid.Count; i++) ground[i] = elevation(i);
        foreach (Reach reach in network.Reaches)
        {
            int cells = network.FloodplainCells(reach);
            if (cells == 0) continue;
            foreach (int node in reach.Nodes)
            {
                if (ground[node] < GenerationConstants.WaterLevel) continue;
                double reachable = ground[node] + reach.BankHeight + WaterNetwork.StormMargin;
                int cx = node % grid.Side, cz = node / grid.Side;
                for (int dz = -cells; dz <= cells; dz++)
                for (int dx = -cells; dx <= cells; dx++)
                {
                    int x = cx + dx, z = cz + dz, d = (dx * dx) + (dz * dz);
                    if (x < 0 || z < 0 || x > grid.Segments || z > grid.Segments || d > cells * cells) continue;
                    int i = (z * grid.Side) + x;
                    if (ground[i] < GenerationConstants.WaterLevel || ground[i] > reachable) continue;
                    // The nearest channel floods it; of two as near, the larger river.
                    if (d > nearest[i] || (d == nearest[i] && reachOf[i] >= 0 && network.Reaches[reachOf[i]].Flow >= reach.Flow)) continue;
                    nearest[i] = d;
                    reachOf[i] = reach.Id;
                    channelOf[i] = ground[node];
                }
            }
        }

        Nodes = [.. Enumerable.Range(0, grid.Count).Where(i => reachOf[i] >= 0)];
        Bounds = new WaterBounds[network.Reaches.Count];
        foreach (Reach reach in network.Reaches)
        {
            double margin = (network.FloodplainCells(reach) + 1) * grid.Spacing;
            double minX = reach.Nodes.Min(grid.X), maxX = reach.Nodes.Max(grid.X), minZ = reach.Nodes.Min(grid.Z), maxZ = reach.Nodes.Max(grid.Z);
            Bounds[reach.Id] = new(minX - margin, minZ - margin, maxX + margin, maxZ + margin);
        }
    }

    internal WaterNetwork Network { get; }

    /// <summary>The floodplain's nodes.</summary>
    internal int[] Nodes { get; }

    /// <summary>The area each reach's floodplain spans, for what must redraw as it floods and recedes.</summary>
    internal WaterBounds[] Bounds { get; }

    /// <summary>The reach a node floods from, or -1 off the floodplain.</summary>
    internal int ReachOf(int node) => reachOf[node];

    /// <summary>The ground at a node (the drainage lattice's height).</summary>
    internal float Ground(int node) => ground[node];

    /// <summary>How far a reach's flow over bankfull stands across its floodplain (metres): the overflow spread over the plain's width.</summary>
    internal static double Rise(Reach reach, double flow) => flow <= reach.BankfullFlow ? 0
        : RiverGeometry.Stage(flow - reach.BankfullFlow, 2 * WaterNetwork.FloodplainWidths * reach.Width, reach.Gradient);

    /// <summary>The water over a node with its reach's stage as given: NaN where it is not in flood (or frozen).</summary>
    internal double Level(int node, ReachStage stage)
    {
        int r = reachOf[node];
        if (r < 0 || !stage.Overbank || stage.Ice) return double.NaN;
        Reach reach = Network.Reaches[r];
        double level = channelOf[node] + reach.BankHeight + Rise(reach, stage.Flow);
        return level > ground[node] ? level : double.NaN;
    }

    /// <summary>
    /// The floods as the stage stands: each floodplain node's water, or none. Given the floods some hours
    /// before, water still on the plain goes down no faster than <see cref="RecessionMetresPerHour"/>.
    /// </summary>
    internal FloodLevels Levels(Func<int, ReachStage> stage, FloodLevels? before = null, double hours = 0)
    {
        float[] level = new float[reachOf.Length];
        Array.Fill(level, float.NaN);
        Dictionary<int, ReachStage> read = [];
        Dictionary<int, double> highest = [];
        int flooded = 0;
        foreach (int node in Nodes)
        {
            int r = reachOf[node];
            if (!read.TryGetValue(r, out ReachStage s)) read[r] = s = stage(r);
            double l = s.Ice ? double.NaN : Level(node, s);
            double was = before?.LevelAt(node) ?? double.NaN;
            if (!s.Ice && !double.IsNaN(was))
            {
                double draining = was - (RecessionMetresPerHour * Math.Max(0, hours));
                if (draining > ground[node] && (double.IsNaN(l) || draining > l)) l = draining;
            }

            if (double.IsNaN(l)) continue;
            level[node] = (float)l;
            flooded++;
            highest[r] = Math.Max(highest.GetValueOrDefault(r, double.NegativeInfinity), l);
        }

        return new(this, level, flooded, highest);
    }
}

/// <summary>
/// The floods at one time (#9880): each floodplain node's water level (NaN where dry), read anywhere by
/// interpolating the lattice. Read-only, so any thread may read it.
/// </summary>
internal sealed class FloodLevels(FloodPlains plains, float[] level, int flooded, IReadOnlyDictionary<int, double> highest)
{
    /// <summary>Each flooding reach's highest water on its plain: what must redraw as it moves.</summary>
    internal IReadOnlyDictionary<int, double> Reaches => highest;

    /// <summary>How many floodplain nodes are under water.</summary>
    internal int Count => flooded;

    /// <summary>The area under water (km²).</summary>
    internal double SquareKilometres => flooded * plains.Network.Grid.Spacing * plains.Network.Grid.Spacing / 1e6;

    /// <summary>A node's water, or NaN.</summary>
    internal double LevelAt(int node) => level.Length == 0 ? double.NaN : level[node];

    /// <summary>
    /// The flood's level at a world point and the reach it comes from, or null where no node about it is
    /// flooded: the levels of the flooded nodes of its lattice cell, weighted by nearness. Whether the
    /// ground there stands under that level is the asker's to judge.
    /// </summary>
    internal (double Level, int Reach)? At(double x, double z)
    {
        if (flooded == 0) return null;
        MapGrid grid = plains.Network.Grid;
        double fx = (x + grid.Radius) / grid.Spacing, fz = (z + grid.Radius) / grid.Spacing;
        int i0 = (int)Math.Floor(fx), j0 = (int)Math.Floor(fz);
        double tx = fx - i0, tz = fz - j0, sum = 0, weight = 0, best = 0;
        int reach = -1;
        for (int k = 0; k < 4; k++)
        {
            int i = i0 + (k & 1), j = j0 + (k >> 1);
            if (i < 0 || j < 0 || i > grid.Segments || j > grid.Segments) continue;
            double l = level[(j * grid.Side) + i];
            if (double.IsNaN(l)) continue;
            double w = ((k & 1) == 1 ? tx : 1 - tx) * ((k >> 1) == 1 ? tz : 1 - tz);
            sum += l * w;
            weight += w;
            if (w >= best) (best, reach) = (w, plains.ReachOf((j * grid.Side) + i));
        }

        return weight > 0 ? (sum / weight, reach) : null;
    }

    /// <summary>Whether any node about a world point lies on a floodplain: where water may come and go with the stage.</summary>
    internal bool Floodable(double x, double z)
    {
        MapGrid grid = plains.Network.Grid;
        int i0 = (int)Math.Floor((x + grid.Radius) / grid.Spacing), j0 = (int)Math.Floor((z + grid.Radius) / grid.Spacing);
        for (int k = 0; k < 4; k++)
        {
            int i = i0 + (k & 1), j = j0 + (k >> 1);
            if (i >= 0 && j >= 0 && i <= grid.Segments && j <= grid.Segments && plains.ReachOf((j * grid.Side) + i) >= 0) return true;
        }

        return false;
    }

    /// <summary>Whether the nearest node to a world point is under water: flooded ground, as the map judges it.</summary>
    internal bool Flooded(double x, double z) => flooded > 0 && !double.IsNaN(level[plains.Network.Grid.Nearest(x, z)]);
}

/// <summary>
/// Where floods must be drawn again (#9880 review): a reach's floodplain once its flood comes or goes, or
/// its highest water has moved more than <see cref="RedrawMetres"/> from the level last drawn - so a flood
/// creeping up a little each step is still drawn again once it has crept far enough. Engine-free.
/// </summary>
internal sealed class FloodRedraws(FloodPlains plains)
{
    /// <summary>A flood is drawn again once its level has moved this far (metres) since it last was.</summary>
    internal const double RedrawMetres = 0.05;

    private readonly Dictionary<int, double> drawn = [];

    /// <summary>The floodplains to draw again for the floods now; remembers each as drawn at its level now.</summary>
    internal List<WaterBounds> Changes(FloodLevels now)
    {
        List<WaterBounds> changed = [];
        foreach (int r in drawn.Keys.Where(r => !now.Reaches.ContainsKey(r)).ToArray())
        {
            drawn.Remove(r);
            changed.Add(plains.Bounds[r]);
        }

        foreach ((int r, double level) in now.Reaches)
        {
            if (drawn.TryGetValue(r, out double was) && Math.Abs(level - was) <= RedrawMetres) continue;
            drawn[r] = level;
            changed.Add(plains.Bounds[r]);
        }

        return changed;
    }
}
