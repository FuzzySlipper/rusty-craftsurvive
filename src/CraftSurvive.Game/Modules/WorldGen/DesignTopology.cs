namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>What a designed continent's land must be (#9815, R9815-1), and whether it is.</summary>
/// <param name="Connected">The peninsula meets the forbidden mainland.</param>
/// <param name="BridgeOutsideNeck">It still meets it with the neck taken away: a second land bridge.</param>
/// <param name="NeckWidthMetres">The narrowest row of the neck's land that joins the two.</param>
/// <param name="Islands">Land not joined to either.</param>
internal readonly record struct DesignTopologyReport(bool Connected, bool BridgeOutsideNeck, double NeckWidthMetres, int Islands)
{
    internal bool Holds(ContinentDesign design) => Connected && !BridgeOutsideNeck
        && NeckWidthMetres >= design.NeckWidthMetres[0] && NeckWidthMetres <= design.NeckWidthMetres[1];

    public override string ToString() => FormattableString.Invariant(
        $"peninsula meets the mainland {(Connected ? "yes" : "NO")}; a bridge outside the neck {(BridgeOutsideNeck ? "YES" : "no")}; narrowest row of the neck {NeckWidthMetres / 1000:F0} km of land; islands {Islands}");
}

/// <summary>
/// The hard rules of a designed continent's land (#9815, R9815-1), on a lattice's land and sea:
/// <list type="bullet">
/// <item>the peninsula meets the forbidden mainland (each anchored on its deepest land outside the neck);</item>
/// <item>only through the neck: with the neck's land taken away they no longer meet;</item>
/// <item>the neck's narrowest row of joining land is within the design's width range.</item>
/// </list>
/// <see cref="Enforce"/> repairs a stray bridge by drowning a moat about the mainland outside the neck
/// (deterministic: it depends only on the design and the lattice), and refuses a world it cannot repair,
/// so a continent that breaks them is never generated. Land is 4-connected: diagonal touches do not join.
/// </summary>
internal static class DesignTopology
{
    /// <summary>The moat a stray bridge is cut with: sea within this distance (map units) of the mainland's outline, outside the neck.</summary>
    internal const double MoatWidth = 0.015;

    /// <summary>
    /// Makes a lattice's sea mask obey the rules: drowns a moat if the peninsula reaches the mainland
    /// outside the neck, then checks again. Throws <see cref="InvalidOperationException"/> for land that
    /// still breaks them, naming what failed.
    /// </summary>
    internal static DesignTopologyReport Enforce(MapGrid grid, bool[] sea, ContinentDesign design)
    {
        DesignTopologyReport report = Check(grid, i => !sea[i], design);
        if (report.BridgeOutsideNeck && design.Land.FirstOrDefault(land => land.Forbidden) is DesignLand mainland)
        {
            for (int i = 0; i < grid.Count; i++)
            {
                double u = grid.X(i) / grid.Radius, v = grid.Z(i) / grid.Radius;
                if (!sea[i] && !design.InNeck(u, v) && Math.Abs(mainland.SignedDistance(u, v)) < MoatWidth) sea[i] = true;
            }

            report = Check(grid, i => !sea[i], design);
        }

        if (!report.Holds(design))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Continent design '{design.Name}' cannot be generated on this lattice: {report}; the neck must be {design.NeckWidthMetres[0] / 1000:F0}-{design.NeckWidthMetres[1] / 1000:F0} km."));
        }

        return report;
    }

    /// <summary>Measures the rules on a lattice whose land is given.</summary>
    internal static DesignTopologyReport Check(MapGrid grid, Func<int, bool> land, ContinentDesign design)
    {
        DesignLand? mainland = design.Land.FirstOrDefault(l => l.Forbidden);
        if (mainland is null) return new(true, false, double.PositiveInfinity, 0);
        bool[] neck = new bool[grid.Count];
        for (int i = 0; i < grid.Count; i++) neck[i] = design.InNeck(grid.X(i) / grid.Radius, grid.Z(i) / grid.Radius);
        // Each side is anchored on its deepest land outside the neck, so taking the neck away never takes an anchor with it.
        int peninsula = Deepest(grid, land, neck, (u, v) => design.Land.Where(l => !l.Forbidden).Select(l => l.SignedDistance(u, v)).DefaultIfEmpty(-1).Max());
        int inland = Deepest(grid, land, neck, mainland.SignedDistance);
        if (peninsula < 0 || inland < 0) return new(false, false, 0, 0);

        int[] all = Components(grid, land, _ => true, out int count);
        bool connected = all[peninsula] >= 0 && all[peninsula] == all[inland];
        int[] outside = Components(grid, land, i => !neck[i], out _);
        bool bridge = outside[peninsula] >= 0 && outside[peninsula] == outside[inland];

        // The neck's width: across its rows, the fewest nodes of joining land in any one.
        int narrowest = int.MaxValue;
        for (int z = 0; z < grid.Side; z++)
        {
            int across = 0, inNeck = 0;
            for (int x = 0; x < grid.Side; x++)
            {
                int i = (z * grid.Side) + x;
                if (!neck[i]) continue;
                inNeck++;
                if (land(i) && all[i] == all[peninsula]) across++;
            }

            if (inNeck > 0) narrowest = Math.Min(narrowest, across);
        }

        int islands = Enumerable.Range(0, count).Count(c => c != all[peninsula] && c != all[inland]);
        return new(connected, bridge, narrowest == int.MaxValue ? 0 : narrowest * grid.Spacing, islands);
    }

    /// <summary>The land node outside the neck deepest inside an outline (largest signed distance), or -1 if none is inside.</summary>
    private static int Deepest(MapGrid grid, Func<int, bool> land, bool[] neck, Func<double, double, double> inside)
    {
        int best = -1;
        double deepest = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            if (neck[i] || !land(i)) continue;
            double depth = inside(grid.X(i) / grid.Radius, grid.Z(i) / grid.Radius);
            if (depth > deepest)
            {
                deepest = depth;
                best = i;
            }
        }

        return best;
    }

    /// <summary>The 4-connected components of the nodes that are land and allowed; -1 where not.</summary>
    private static int[] Components(MapGrid grid, Func<int, bool> land, Func<int, bool> allowed, out int count)
    {
        int[] id = new int[grid.Count];
        Array.Fill(id, -1);
        count = 0;
        Stack<int> open = new();
        int side = grid.Side;
        for (int start = 0; start < grid.Count; start++)
        {
            if (id[start] >= 0 || !land(start) || !allowed(start)) continue;
            id[start] = count;
            open.Push(start);
            while (open.TryPop(out int at))
            {
                int x = at % side, z = at / side;
                foreach (int next in (ReadOnlySpan<int>)[x > 0 ? at - 1 : -1, x < side - 1 ? at + 1 : -1, z > 0 ? at - side : -1, z < side - 1 ? at + side : -1])
                {
                    if (next < 0 || id[next] >= 0 || !land(next) || !allowed(next)) continue;
                    id[next] = count;
                    open.Push(next);
                }
            }

            count++;
        }

        return id;
    }
}
