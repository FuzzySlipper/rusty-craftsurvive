namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A continent's drainage network (#9550): the one source of every river a walker meets. Region tiles
/// are built independently, so they cannot agree where water from beyond them runs; this network
/// routes the whole continent once, at a lattice fine enough for streams of a square kilometre, over
/// the continent's geography plus the region relief it resolves. It keeps only node paths; a reach is
/// shaped on demand and deterministically, so every tile shapes a shared reach identically, and tiles
/// erode their valleys down to it.
/// </summary>
internal sealed class MapDrainage
{
    internal const double Spacing = 250;
    private const int ErosionSteps = 8;
    /// <summary>Spatial index cell for finding the reaches near a tile.</summary>
    private const double IndexCell = 2048;
    /// <summary>How far a shaped reach may stray from its node path: meander, wander and quiet banks.</summary>
    private const double ShapeMargin = 256;
    private const ulong ReachSalt = 0x2545F4914F6CDD1DUL;

    private readonly float[] elevation, discharge;
    private readonly List<int[]> paths;
    private readonly Dictionary<long, List<int>> index = [];
    private readonly ulong seed;

    internal MapDrainage(WorldMap continent)
    {
        int segments = (int)Math.Round(continent.Radius * 2 / Spacing);
        Grid = new MapGrid(segments, continent.Radius * 2 / segments, continent.Radius);
        seed = continent.Configuration.Contract.GeographyNoiseSeed ^ ReachSalt;
        int count = Grid.Count, octaves = RegionRelief.ResolvedOctaves(Grid.Spacing);
        double[] height = new double[count], surface = new double[count], incision = new double[count], hardness = new double[count], rain = new double[count];
        bool[] sea = new bool[count], outlet = new bool[count];
        MapGrid grid = Grid, coarse = continent.Grid;
        // A closed lake is a sink here as on the continent (#9876): its water goes no farther.
        HashSet<int> closed = MapLakes.ClosedNodes(continent.Lakes);
        Dictionary<int, int> lakeOf = [];
        foreach (Lake lake in continent.Lakes)
            foreach (int node in lake.Nodes) lakeOf[node] = lake.Id;
        Parallel.For(0, count, i =>
        {
            double x = grid.X(i), z = grid.Z(i);
            (double refined, double cut, MapSample broad) = RegionRelief.At(continent, x, z, octaves);
            height[i] = refined;
            surface[i] = broad.Elevation;
            incision[i] = cut;
            sea[i] = broad.Elevation < GenerationConstants.WaterLevel;
            outlet[i] = sea[i] || grid.IsEdge(i) || (closed.Count > 0 && closed.Contains(coarse.Nearest(x, z)));
            hardness[i] = Math.Clamp(continent.HardnessAt(x, z), 0, 1);
            rain[i] = RegionRelief.Rain(broad);
        });
        MapRelief relief = MapRelief.FromHeights(Grid, height, hardness, sea, outlet);
        MapErosion.Evolve(relief, _ => rain, ErosionSteps, ErosionSteps);
        double[] h = relief.Height;
        for (int i = 0; i < count; i++)
            if (!outlet[i]) h[i] = Math.Clamp(RegionRelief.LimitIncision(h[i], surface[i], incision[i]), GenerationConstants.MinimumTerrainHeight, continent.Scale.MaximumElevation - 1);
        MapFlow.Fill(Grid, h, outlet, MapFlow.FillGradient);
        elevation = h.Select(v => (float)v).ToArray();
        double[] routed = elevation.Select(v => (double)v).ToArray();
        MapFlow flow = MapFlow.Route(Grid, routed, outlet, rain, MapFlow.FillGradient);
        discharge = flow.Discharge.Select(v => (float)v).ToArray();
        int LakeAt(int i) => lakeOf.Count > 0 && lakeOf.TryGetValue(coarse.Nearest(grid.X(i), grid.Z(i)), out int lake) ? lake : -1;
        paths = SplitAtLakes(MapRivers.Trace(Grid, flow, elevation, discharge, MapRivers.SourceCatchment), LakeAt);
        Network = WaterNetwork.Build(Grid, flow, elevation, discharge, paths, (x, z) => continent.Sample(x, z).Moisture, i => sea[i],
            continent.Lakes, LakeAt);
        for (int r = 0; r < paths.Count; r++)
        {
            HashSet<long> cells = [];
            foreach (int node in paths[r])
            {
                double x = Grid.X(node), z = Grid.Z(node);
                for (long cz = Cell(z - ShapeMargin); cz <= Cell(z + ShapeMargin); cz++)
                for (long cx = Cell(x - ShapeMargin); cx <= Cell(x + ShapeMargin); cx++)
                    cells.Add(Key(cx, cz));
            }
            foreach (long cell in cells)
            {
                if (!index.TryGetValue(cell, out List<int>? list)) index[cell] = list = [];
                list.Add(r);
            }
        }
    }

    /// <summary>
    /// Reaches split where they enter and leave a lake (#9876 review), so a lake sits between the reaches
    /// that flow into it and the one that flows on: a river entering ends at the lake's first node, one
    /// inside runs between its first and last, one leaving starts at its last. Each split node is shared by
    /// the reaches either side of it, so they stay joined downstream.
    /// </summary>
    internal static List<int[]> SplitAtLakes(List<int[]> paths, Func<int, int> lakeAt)
    {
        List<int[]> split = [];
        foreach (int[] path in paths)
        {
            int start = 0;
            for (int k = 1; k < path.Length; k++)
            {
                int before = lakeAt(path[k - 1]), after = lakeAt(path[k]);
                if (before == after) continue;
                // Leaving: the last node inside closes the reach; entering: the first node inside does.
                if (before >= 0 && k - 1 > start) { split.Add(path[start..k]); start = k - 1; }
                if (after >= 0 && k > start) { split.Add(path[start..(k + 1)]); start = k; }
            }

            if (path.Length - start >= 2) split.Add(path[start..]);
        }

        return split;
    }

    internal MapGrid Grid { get; }

    /// <summary>The same drainage as data (#9875): reaches with their flow and navigability, mouths, confluences and the floodplain.</summary>
    internal WaterNetwork Network { get; }

    /// <summary>The drainage's filled ground height at a lattice node (metres).</summary>
    internal float ElevationAt(int node) => elevation[node];
    internal int ReachCount => paths.Count;

    /// <summary>The network's reaches near a rectangle of the world, shaped, in world metres.</summary>
    internal MapRivers Within(double minX, double minZ, double maxX, double maxZ) =>
        MapRivers.FromReaches([.. ReachesWithin(minX, minZ, maxX, maxZ).Select(r => (r, Shaped(r)))]);

    /// <summary>The ids of the network's reaches near a rectangle of the world, in order.</summary>
    internal SortedSet<int> ReachesWithin(double minX, double minZ, double maxX, double maxZ)
    {
        SortedSet<int> found = [];
        for (long cz = Cell(minZ); cz <= Cell(maxZ); cz++)
        for (long cx = Cell(minX); cx <= Cell(maxX); cx++)
            if (index.TryGetValue(Key(cx, cz), out List<int>? list)) found.UnionWith(list);
        return found;
    }

    /// <summary>One reach's centreline, shaped as region sampling draws it, in world metres.</summary>
    internal RiverPoint[] Shaped(int reach) => MapRivers.Shape(Grid, paths[reach], elevation, discharge, seed, reach);

    private static long Cell(double v) => (long)Math.Floor(v / IndexCell);
    private static long Key(long x, long z) => unchecked((x << 32) ^ (z & 0xFFFF_FFFFL));
}
