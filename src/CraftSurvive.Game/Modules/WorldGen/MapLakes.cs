namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// What a lake is: a long valley drowned behind a rock sill, a round basin that fills and spills, a closed
/// dry basin holding a salt lake or a playa, or the design's own.
/// </summary>
internal enum LakeKind { Valley, Basin, Salt, Playa, Designed }

/// <summary>
/// A lake on the map lattice (#9876): the nodes under its surface, its level, the spill level its basin
/// holds to, whether it is closed (evaporation takes its inflow before it fills), where it spills, and
/// how its area grows with its level (for routing a lake as storage, #9878).
/// </summary>
internal sealed record Lake(
    int Id,
    LakeKind Kind,
    string Name,
    int[] Nodes,
    int[] Basin,
    double Level,
    double Spill,
    bool Closed,
    int Outlet,
    double AreaSquareKilometres,
    double MaximumDepth,
    double InflowFlow,
    double EvaporationMetres,
    (double Level, double AreaSquareKilometres)[] LevelArea);

/// <summary>
/// Lakes (#9876). Erosion fills every depression as it works, so the land it leaves holds no lake; lakes
/// are carved after it - valleys drowned behind a sill of hard rock, closed basins in dry country and
/// any the design asks for - and the carving is kept in the saved elevation. Everything else about a
/// lake is found again from the saved fields whenever a map is built: its basin is the hollow the fill
/// fills, and a closed basin finds its own level where its inflow balances evaporation from its
/// surface (a salt lake; a playa if that leaves little of the basin wet); otherwise it fills and spills
/// and its river runs on. Nothing about a lake is saved but the hollow.
/// </summary>
internal static class MapLakes
{
    /// <summary>A hollow is a lake's basin where the fill stands this deep over it at its deepest; erosion's own pits are shallower (at most about 4 m).</summary>
    internal const double LeastDepth = 8;

    /// <summary>Where the fill stands less than this over the ground, it is shore, not basin.</summary>
    private const double ShoreDepth = 0.5;

    /// <summary>Open-water evaporation in metres a year: this much plus this much more at the warm end of the climate's temperature (0 polar, 1 tropical).</summary>
    private const double EvaporationBase = 0.3, EvaporationHeat = 1.7;

    /// <summary>A closed lake covering less than this share of its basin is a playa: mostly a dry salt flat.</summary>
    private const double PlayaShare = 0.25;

    private const double SecondsPerYear = 365.25 * 24 * 3600;
    private const int LevelSteps = 8;
    private const int BalanceSteps = 40;

    /// <summary>
    /// Carves the recipe's lakes into a simulated height field (metres, before the final fill), on the
    /// rain the climate gives it: valleys behind sills of hard rock on rivers, closed basins where it is
    /// dry and far from the sea, and the design's own lakes.
    /// </summary>
    internal static void Carve(MapGrid grid, double[] height, bool[] sea, double[] rain, double[] hardness, LakesRecipe recipe, ContinentDesign? design, ulong seed)
    {
        if (!recipe.Enabled) return;
        double[] filled = (double[])height.Clone();
        MapFlow flow = MapFlow.Route(grid, filled, MapFlow.Outlets(grid, height), rain, MapFlow.FillGradient);
        double cellKm2 = grid.Spacing * grid.Spacing / 1e6;
        int[] fromSea = SeaDistance(grid, sea);
        List<int> carved = [];
        bool Spaced(int node) => carved.All(c => Distance(grid, c, node) >= recipe.Spacing);

        // The design's own lakes first: they are asked for.
        foreach (DesignLake lake in design?.Lakes ?? [])
        {
            int centre = grid.Nearest(lake.At[0] * grid.Radius, lake.At[1] * grid.Radius);
            Bowl(grid, height, sea, centre, lake.Radius * grid.Radius, lake.Depth, seed);
            carved.Add(centre);
        }

        // Valleys drowned behind a sill: a river node on hard rock, inland and above the coast, with its
        // main valley upstream lowered below the sill's height for a stretch, deepest in its middle.
        List<int>[] donors = new List<int>[grid.Count];
        for (int i = 0; i < grid.Count; i++)
            if (flow.Receiver[i] >= 0) (donors[flow.Receiver[i]] ??= []).Add(i);
        IEnumerable<int> sills = Enumerable.Range(0, grid.Count)
            .Where(i => !sea[i] && flow.Discharge[i] * cellKm2 >= recipe.ValleyCatchment && height[i] >= recipe.ValleyLowest
                && hardness[i] >= recipe.ValleyHardness && fromSea[i] * grid.Spacing >= recipe.SeaDistance / 2)
            .OrderByDescending(i => hardness[i] * Math.Log10(flow.Discharge[i] * cellKm2)).ThenBy(i => i);
        int valleys = 0;
        foreach (int sill in sills)
        {
            if (valleys >= recipe.ValleyLakes) break;
            if (!Spaced(sill)) continue;
            List<int> path = [];
            for (int n = sill, guard = 0; guard < grid.Count && path.Count * grid.Spacing < recipe.ValleyLength; guard++)
            {
                if (donors[n] is not { Count: > 0 } up) break;
                n = up.MaxBy(d => flow.Discharge[d]);
                path.Add(n);
            }

            if (path.Count < 2) continue;
            DrownValley(grid, height, sea, height[sill], path, recipe);
            carved.Add(sill);
            valleys++;
        }

        // Closed basins: dry country far from the sea, between rivers rather than on one.
        IEnumerable<int> dry = Enumerable.Range(0, grid.Count)
            .Where(i => !sea[i] && rain[i] <= recipe.ClosedRain && fromSea[i] * grid.Spacing >= recipe.SeaDistance
                && flow.Discharge[i] * cellKm2 <= recipe.ClosedCatchment)
            .OrderBy(i => rain[i]).ThenByDescending(i => fromSea[i]).ThenBy(i => i);
        int basins = 0;
        foreach (int centre in dry)
        {
            if (basins >= recipe.ClosedBasins) break;
            if (!Spaced(centre)) continue;
            Bowl(grid, height, sea, centre, recipe.ClosedRadius, recipe.ClosedDepth, seed);
            carved.Add(centre);
            basins++;
        }
    }

    /// <summary>
    /// Keeps the lakes' hollows in a filled height field: where the fill of the carved field stands at
    /// least <see cref="LeastDepth"/> deep, the basin keeps its carved heights; erosion's own shallow pits
    /// stay filled, as they always were.
    /// </summary>
    internal static void KeepHollows(MapGrid grid, double[] carved, double[] filled)
    {
        foreach (int[] basin in Basins(grid, carved, filled))
            foreach (int node in basin) filled[node] = carved[node];
    }

    /// <summary>
    /// The lakes a map holds, from its saved fields: each hollow deep enough to be a basin, filled to its
    /// spill unless its inflow (the saved discharge into it) is less than evaporation from its surface,
    /// when it stands at the level where they balance.
    /// </summary>
    internal static IReadOnlyList<Lake> Find(MapGrid grid, IReadOnlyList<float> elevation, IReadOnlyList<float> discharge,
        IReadOnlyList<float> temperature, ContinentDesign? design)
    {
        double[] raw = new double[grid.Count];
        for (int i = 0; i < grid.Count; i++) raw[i] = elevation[i];
        double[] filled = (double[])raw.Clone();
        MapFlow.Fill(grid, filled, MapFlow.Outlets(grid, raw), MapFlow.FillGradient);
        double cellKm2 = grid.Spacing * grid.Spacing / 1e6;
        List<Lake> lakes = [];
        foreach (int[] basin in Basins(grid, raw, filled))
        {
            double spill = basin.Max(n => filled[n]);
            double Area(double level) => basin.Count(n => raw[n] < level) * cellKm2;
            double catchment = basin.Max(n => discharge[n]) * cellKm2;
            double inflow = WaterBudget.Flow(catchment);
            double evaporation = EvaporationBase + (EvaporationHeat * basin.Average(n => temperature[n]));
            double Loss(double level) => evaporation * Area(level) * 1e6 / SecondsPerYear;
            double bed = basin.Min(n => raw[n]);
            bool closed = inflow < Loss(spill);
            double level = spill;
            if (closed)
            {
                double low = bed, high = spill;
                for (int k = 0; k < BalanceSteps; k++)
                {
                    double mid = (low + high) / 2;
                    if (Loss(mid) > inflow) high = mid;
                    else low = mid;
                }

                level = low;
            }

            int[] wet = [.. basin.Where(n => raw[n] < level)];
            if (wet.Length == 0) continue;
            int lowest = basin.MinBy(n => raw[n]);
            LakeKind kind = InDesignLake(grid, design, lowest) is DesignLake designed ? LakeKind.Designed
                : closed ? (Area(level) < PlayaShare * Area(spill) ? LakeKind.Playa : LakeKind.Salt)
                : Elongation(grid, wet) >= ValleyElongation ? LakeKind.Valley : LakeKind.Basin;
            string name = InDesignLake(grid, design, lowest)?.Name ?? "";
            (double, double)[] levels = [.. Enumerable.Range(0, LevelSteps + 1).Select(k =>
            {
                double l = bed + ((spill - bed) * k / LevelSteps);
                return (l, Area(l));
            })];
            lakes.Add(new(lakes.Count, kind, name, wet, basin, level, spill, closed, closed ? -1 : SpillNode(grid, basin, raw, spill),
                Area(level), level - bed, inflow, evaporation, levels));
        }

        return lakes;
    }

    /// <summary>Routing outlets with the closed lakes as sinks: their water goes no farther.</summary>
    internal static bool[] Outlets(MapGrid grid, IReadOnlyList<double> height, IReadOnlyList<Lake> lakes)
    {
        bool[] outlet = MapFlow.Outlets(grid, height);
        foreach (Lake lake in lakes)
            if (lake.Closed)
                foreach (int node in lake.Nodes) outlet[node] = true;
        return outlet;
    }

    /// <summary>The closed lakes' nodes, for checking two findings route alike.</summary>
    internal static HashSet<int> ClosedNodes(IReadOnlyList<Lake> lakes) => [.. lakes.Where(l => l.Closed).SelectMany(l => l.Nodes)];

    /// <summary>Connected hollows where the fill stands over the ground, kept where it stands deep enough to be a lake's basin.</summary>
    private static List<int[]> Basins(MapGrid grid, double[] raw, double[] filled)
    {
        List<int[]> basins = [];
        bool[] seen = new bool[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            if (seen[i] || filled[i] - raw[i] < ShoreDepth) continue;
            List<int> basin = [];
            Queue<int> open = new();
            open.Enqueue(i);
            seen[i] = true;
            double deepest = 0;
            while (open.Count > 0)
            {
                int c = open.Dequeue();
                basin.Add(c);
                deepest = Math.Max(deepest, filled[c] - raw[c]);
                for (int k = 0; k < 8; k++)
                {
                    int n = grid.Neighbour(c, k);
                    if (n < 0 || seen[n] || filled[n] - raw[n] < ShoreDepth) continue;
                    seen[n] = true;
                    open.Enqueue(n);
                }
            }

            if (deepest >= LeastDepth) basins.Add([.. basin.Order()]);
        }

        return basins;
    }

    /// <summary>Where a full basin spills: the lowest node beside it.</summary>
    private static int SpillNode(MapGrid grid, int[] basin, double[] raw, double spill)
    {
        HashSet<int> inside = [.. basin];
        int best = -1;
        double lowest = double.MaxValue;
        foreach (int node in basin)
            for (int k = 0; k < 8; k++)
            {
                int n = grid.Neighbour(node, k);
                if (n < 0 || inside.Contains(n) || raw[n] >= lowest) continue;
                lowest = raw[n];
                best = n;
            }

        return best;
    }

    /// <summary>A lake at least this many times longer than it is wide fills a valley; a rounder one fills a basin.</summary>
    private const double ValleyElongation = 2;

    /// <summary>How elongated a set of nodes is: the ratio of its principal spreads.</summary>
    private static double Elongation(MapGrid grid, int[] nodes)
    {
        if (nodes.Length < 3) return 1;
        double mx = nodes.Average(n => grid.X(n)), mz = nodes.Average(n => grid.Z(n));
        double xx = nodes.Average(n => Math.Pow(grid.X(n) - mx, 2)), zz = nodes.Average(n => Math.Pow(grid.Z(n) - mz, 2));
        double xz = nodes.Average(n => (grid.X(n) - mx) * (grid.Z(n) - mz));
        double mean = (xx + zz) / 2, spread = Math.Sqrt((((xx - zz) / 2) * ((xx - zz) / 2)) + (xz * xz));
        double major = mean + spread, minor = Math.Max(mean - spread, 1e-9);
        return Math.Sqrt(major / minor);
    }

    private static DesignLake? InDesignLake(MapGrid grid, ContinentDesign? design, int node) =>
        design?.Lakes.FirstOrDefault(lake =>
            Math.Sqrt(Math.Pow((grid.X(node) / grid.Radius) - lake.At[0], 2) + Math.Pow((grid.Z(node) / grid.Radius) - lake.At[1], 2)) <= lake.Radius);

    /// <summary>The ring a basin's rim is read on, as shares of its (shaped) radius.</summary>
    private const double RimInner = 0.9, RimOuter = 1.15;

    /// <summary>
    /// A basin's shape, so none is a crater: stretched up to this much along a seeded bearing, and its
    /// shore waved by two seeded lobes of these shares of its radius.
    /// </summary>
    private const double MostStretch = 1.8, FirstLobe = 0.22, SecondLobe = 0.12;
    private const ulong ShapeSalt = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// A basin that holds water: everything within its shaped outline is cut below the lowest point of its
    /// rim, deepest at the centre, so it fills to the rim (or, closed, to its own level) whatever slope it
    /// was cut into. Its outline is an ellipse turned to a seeded bearing, with a waved shore.
    /// </summary>
    private static void Bowl(MapGrid grid, double[] height, bool[] sea, int centre, double radius, double depth, ulong seed)
    {
        ulong shape = seed ^ ShapeSalt;
        double stretch = 1 + ((MostStretch - 1) * MapNoise.Unit(shape, centre, 0));
        double turn = Math.PI * MapNoise.Unit(shape, centre, 1);
        double phase1 = 2 * Math.PI * MapNoise.Unit(shape, centre, 2), phase2 = 2 * Math.PI * MapNoise.Unit(shape, centre, 3);
        // The relative radius of an offset: the ellipse's, against a shore waved by two lobes.
        double Reach(double ox, double oz)
        {
            double u = (ox * Math.Cos(turn)) + (oz * Math.Sin(turn)), v = (-ox * Math.Sin(turn)) + (oz * Math.Cos(turn));
            double r = Math.Sqrt((u * u / (stretch * stretch)) + (v * v)) / radius;
            double angle = Math.Atan2(v, u);
            return r / (1 + (FirstLobe * Math.Sin((3 * angle) + phase1)) + (SecondLobe * Math.Sin((5 * angle) + phase2)));
        }

        int cells = Math.Max(1, (int)Math.Ceiling(radius * stretch * RimOuter * (1 + FirstLobe + SecondLobe) / grid.Spacing));
        int cx = centre % grid.Side, cz = centre / grid.Side;
        double rim = double.MaxValue;
        List<(int Node, double R)> inside = [];
        for (int dz = -cells; dz <= cells; dz++)
        for (int dx = -cells; dx <= cells; dx++)
        {
            int x = cx + dx, z = cz + dz;
            if (x <= 0 || z <= 0 || x >= grid.Segments || z >= grid.Segments) continue;
            int i = (z * grid.Side) + x;
            double r = Reach(dx * grid.Spacing, dz * grid.Spacing);
            if (r is >= RimInner and <= RimOuter && !sea[i]) rim = Math.Min(rim, height[i]);
            if (r < 1 && !sea[i]) inside.Add((i, r));
        }

        if (rim == double.MaxValue) return;
        foreach ((int node, double r) in inside)
            height[node] = Math.Min(height[node], rim - 1 - (depth * (1 - (r * r))));
    }

    /// <summary>
    /// Drowns a valley behind its sill: the valley floor about the upstream path, up to some height above
    /// the sill, is lowered below the sill's level - deepest in the middle of the stretch and of the valley.
    /// </summary>
    private static void DrownValley(MapGrid grid, double[] height, bool[] sea, double level, List<int> path, LakesRecipe recipe)
    {
        int cells = Math.Max(1, (int)Math.Ceiling(recipe.ValleyHalfWidth / grid.Spacing));
        for (int k = 0; k < path.Count; k++)
        {
            double along = Math.Sqrt(Math.Sin(Math.PI * (k + 1) / (path.Count + 1)));
            int cx = path[k] % grid.Side, cz = path[k] / grid.Side;
            for (int dz = -cells; dz <= cells; dz++)
            for (int dx = -cells; dx <= cells; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if (x <= 0 || z <= 0 || x >= grid.Segments || z >= grid.Segments) continue;
                int i = (z * grid.Side) + x;
                double across = Math.Sqrt((dx * dx) + (dz * dz)) * grid.Spacing / recipe.ValleyHalfWidth;
                if (sea[i] || across >= 1 || height[i] > level + recipe.ValleyRise) continue;
                height[i] = Math.Min(height[i], level - 1 - (recipe.ValleyDepth * along * (1 - (across * across))));
            }
        }
    }

    /// <summary>Lattice steps from the sea (4-connected), for keeping basins inland.</summary>
    private static int[] SeaDistance(MapGrid grid, bool[] sea)
    {
        int[] distance = Enumerable.Repeat(int.MaxValue, grid.Count).ToArray();
        Queue<int> open = new();
        for (int i = 0; i < grid.Count; i++)
            if (sea[i]) { distance[i] = 0; open.Enqueue(i); }
        while (open.Count > 0)
        {
            int c = open.Dequeue();
            for (int k = 0; k < 4; k++)
            {
                int n = grid.Neighbour(c, k);
                if (n < 0 || distance[n] != int.MaxValue) continue;
                distance[n] = distance[c] + 1;
                open.Enqueue(n);
            }
        }

        return distance;
    }

    private static double Distance(MapGrid grid, int a, int b) => Math.Sqrt(Math.Pow(grid.X(a) - grid.X(b), 2) + Math.Pow(grid.Z(a) - grid.Z(b), 2));
}
