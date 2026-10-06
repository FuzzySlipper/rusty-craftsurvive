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
        MapGrid grid = Grid;
        Parallel.For(0, count, i =>
        {
            double x = grid.X(i), z = grid.Z(i);
            (double refined, double cut, MapSample broad) = RegionRelief.At(continent, x, z, octaves);
            height[i] = refined;
            surface[i] = broad.Elevation;
            incision[i] = cut;
            sea[i] = broad.Elevation < GenerationConstants.WaterLevel;
            outlet[i] = sea[i] || grid.IsEdge(i);
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
        paths = MapRivers.Trace(Grid, flow, elevation, discharge, MapRivers.SourceCatchment);
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

    internal MapGrid Grid { get; }
    internal int ReachCount => paths.Count;

    /// <summary>The network's reaches near a rectangle of the world, shaped, in world metres.</summary>
    internal MapRivers Within(double minX, double minZ, double maxX, double maxZ)
    {
        SortedSet<int> found = [];
        for (long cz = Cell(minZ); cz <= Cell(maxZ); cz++)
        for (long cx = Cell(minX); cx <= Cell(maxX); cx++)
            if (index.TryGetValue(Key(cx, cz), out List<int>? list)) found.UnionWith(list);
        return MapRivers.FromReaches(found.Select(r => MapRivers.Shape(Grid, paths[r], elevation, discharge, seed, r)));
    }

    private static long Cell(double v) => (long)Math.Floor(v / IndexCell);
    private static long Key(long x, long z) => unchecked((x << 32) ^ (z & 0xFFFF_FFFFL));
}
