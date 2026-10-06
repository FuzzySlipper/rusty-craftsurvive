using System.Runtime.CompilerServices;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>
/// What crossing each map cell costs an expedition, as a multiplier on open-ground pace, and the
/// journey pace itself. Map travel compresses time rather than distance: <see cref="JourneyScale"/>
/// multiplies walking time, so a 10 km world takes about two in-game days to cross
/// (Den design/overland-travel-mode, owner decisions 2026-10-05).
/// </summary>
internal sealed class TravelCostModel
{
    internal const double BasePaceMetresPerHour = 4000;
    internal const double JourneyScale = 8;
    internal const double MetresPerHour = BasePaceMetresPerHour / JourneyScale;
    /// <summary>Night travel is allowed but very slow, so camping is encouraged rather than required.</summary>
    internal const double NightMultiplier = 3;
    /// <summary>Slopes past the map's angle of repose (about 40 degrees) are not crossed.</summary>
    internal const double ImpassableSlope = 0.84;
    private const double SlopePenalty = 3;
    private const double FordMultiplier = 3;

    private readonly WorldMap map;
    private readonly double[] multiplier;
    private readonly Func<MapBiome, double>? transport;
    private readonly ConditionalWeakTable<WorldMap, TravelCostModel> regionCosts = [];

    /// <param name="transport">A further multiplier per environment for how the party travels (a hitched
    /// sled, #9473); null for a party on foot.</param>
    internal TravelCostModel(WorldMap map, Func<MapBiome, double>? transport = null)
    {
        Transported = transport is not null;
        this.transport = transport;
        this.map = map;
        Regions = map.Scale.Continental ? MapRegions.For(map) : null;
        MapGrid grid = map.Grid;
        multiplier = new double[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            float elevation = map.Fields.Elevation[i];
            if (elevation < GenerationConstants.WaterLevel) { multiplier[i] = double.PositiveInfinity; continue; }
            double slope = Slope(i);
            if (slope > ImpassableSlope) { multiplier[i] = double.PositiveInfinity; continue; }
            MapBiome biome = WorldMap.Biome(map.Node(i));
            double cost = (1 + SlopePenalty * WorldMap.Smooth(slope / ImpassableSlope)) * Environment(biome) * (transport?.Invoke(biome) ?? 1);
            if (MapRivers.CatchmentSquareKilometres(grid, map.Fields.Discharge[i]) >= map.Scale.SourceCatchment) cost *= FordMultiplier;
            multiplier[i] = cost;
        }
    }

    internal WorldMap Map => map;

    /// <summary>A continent's region tiles, over which a route's head is refined (#9552); null on a regional map.</summary>
    internal MapRegions? Regions { get; }

    /// <summary>The same costs, for the same party, over one region tile's 32 m lattice; built once per tile.</summary>
    internal TravelCostModel RegionCost(WorldMap tile) => regionCosts.GetValue(tile, map => new TravelCostModel(map, transport));
    internal bool Transported { get; }

    /// <summary>The cost multiplier of a lattice node; infinity where the expedition cannot go.</summary>
    internal double Multiplier(int node) => multiplier[node];

    internal bool Passable(int node) => double.IsFinite(multiplier[node]);

    /// <summary>
    /// What the country under a world point costs, for fatigue on foot (#9553): the region tile's finer
    /// cost on a continent where it is built, else the map node's. Ground the map will not route across
    /// (water, cliffs) still tires a walker, as a ford does.
    /// </summary>
    internal double MultiplierAt(System.Numerics.Vector2 world)
    {
        if (Regions is MapRegions regions && regions.IsReady(MapRegions.TileAt(world.X, world.Y)))
        {
            (int X, int Z) key = MapRegions.TileAt(world.X, world.Y);
            (double cx, double cz) = MapRegions.Centre(key);
            return RegionCost(regions.Tile(key).Map).MultiplierAt(world - new System.Numerics.Vector2((float)cx, (float)cz));
        }
        MapGrid grid = map.Grid;
        int x = Math.Clamp((int)Math.Round((world.X + grid.Radius) / grid.Spacing), 0, grid.Segments);
        int z = Math.Clamp((int)Math.Round((world.Y + grid.Radius) / grid.Spacing), 0, grid.Segments);
        double cost = multiplier[z * grid.Side + x];
        return double.IsFinite(cost) ? cost : FordMultiplier;
    }

    /// <summary>Environment multipliers on open-ground pace.</summary>
    internal static double Environment(MapBiome biome) => biome switch
    {
        MapBiome.TemperateForest or MapBiome.BorealForest => 1.5,
        MapBiome.Rainforest => 2,
        MapBiome.Desert or MapBiome.Shrubland => 1.4,
        MapBiome.IceField or MapBiome.Tundra => 1.8,
        MapBiome.Alpine => 2.5,
        _ => 1,
    };

    private double Slope(int i)
    {
        MapGrid grid = map.Grid;
        int x = i % grid.Side, z = i / grid.Side;
        float[] h = map.Fields.Elevation;
        int w = z * grid.Side + Math.Max(0, x - 1), e = z * grid.Side + Math.Min(grid.Segments, x + 1);
        int n = Math.Max(0, z - 1) * grid.Side + x, s = Math.Min(grid.Segments, z + 1) * grid.Side + x;
        double dx = (h[e] - h[w]) / ((Math.Min(grid.Segments, x + 1) - Math.Max(0, x - 1)) * grid.Spacing);
        double dz = (h[s] - h[n]) / ((Math.Min(grid.Segments, z + 1) - Math.Max(0, z - 1)) * grid.Spacing);
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
