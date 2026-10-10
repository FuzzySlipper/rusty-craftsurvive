using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The saved geography: per-node fields in metres and product units, stored at single
/// precision. Generation rounds to the same precision before building a map, so a fresh map
/// and one restored from its save derive identical rivers and fingerprints.
/// </summary>
internal sealed record MapFields(MapGrid Grid, float[] Elevation, float[] Temperature, float[] Moisture,
    float[] Hardness, float[] Discharge)
{
    internal const int FieldCount = 5;
}

/// <summary>
/// The one-time new-game simulation: tectonic relief, landscape evolution under climate-
/// weighted rainfall, metre scaling, talus relaxation, final drainage, and climate. It is
/// deliberately the slow, modelled stage; local terrain only ever samples its result.
/// </summary>
internal static class MapSimulation
{
    /// <summary>A regional world's target peak; a continent's comes from its <see cref="MapScale"/>.</summary>
    internal const double RegionalPeakElevation = 240;
    /// <summary>Metres kept free below the map ceiling, so filling and rounding never cross it.</summary>
    private const double CeilingHeadroom = 8;
    /// <summary>A recipe's peak stays this far under the headroom, so summits above it have room to compress.</summary>
    private const double MinimumSummitRoom = 16;
    /// <summary>A designed map's lowland: land under less than this share of any belt's cross-section; its hills are this quantile of it.</summary>
    private const double LowlandProfile = 0.05, LowlandQuantile = 0.99;
    /// <summary>A designed range's valleys: how far (in map units) a node looks for the ridge above it, and the lowest share of the crest a valley keeps.</summary>
    private const double DesignedValleyReach = 0.08, DesignedValleyFloor = 0.25;
    /// <summary>A designed range's ridged detail: the least share of its crest it keeps, its octaves, persistence and salt.</summary>
    private const double DesignedRidgeFloor = 0.45, DesignedRidgePersistence = 0.55;
    private const int DesignedRidgeOctaves = 5;
    private const ulong DesignedRidgeSalt = 0x7C15_9E3A_42D8_B6F1UL;
    private const double CoastLift = 0.6;
    private const double SeaFloorDrop = 4;
    private const double RiparianMoisture = 0.22;
    private const double RiparianDischarge = 400;

    internal static MapFields Run(TerrainConfiguration configuration) => Run(configuration, MapRecipe.Default);

    /// <summary>
    /// A map made with a recipe's tuning (#9814), and drawn to a continent design if one is given
    /// (#9815); <see cref="MapRecipe.Default"/> and no design make the world its seed always has.
    /// </summary>
    internal static MapFields Run(TerrainConfiguration configuration, MapRecipe recipe, ContinentDesign? design = null)
    {
        SimulationRecipe tuning = recipe.Simulation;
        configuration.Validate();
        ulong seed = configuration.Contract.GeographyNoiseSeed;
        MapGrid grid = MapGrid.For(configuration.Size);
        MapGrid coarse = grid.Coarsened();
        MapScale mapScale = MapScale.For(configuration.Size);
        double peakElevation = Math.Min(tuning.PeakElevation ?? mapScale.PeakElevation, mapScale.MaximumElevation - CeilingHeadroom - MinimumSummitRoom);
        MapClimate climate = new(seed, mapScale, design?.Climate);
        MapRelief relief = MapRelief.Build(coarse, seed, recipe.Relief, design);
        MapErosion.Evolve(relief, h => climate.Rainfall(coarse, h, relief.Sea), tuning.CoarseSteps, tuning.CoarseClimateInterval);
        if (coarse != grid)
        {
            relief = MapRelief.Refine(relief, grid, seed, recipe.Relief);
            MapRelief refined = relief;
            MapErosion.Evolve(refined, h => climate.Rainfall(grid, h, refined.Sea), tuning.RefineSteps, tuning.RefineSteps);
        }

        double[] h = relief.Height;
        double[] land = Enumerable.Range(0, grid.Count).Where(i => !relief.Sea[i]).Select(i => h[i]).ToArray();
        double top = land.Length > 0 ? Math.Max(MapRelief.Quantile(land, tuning.PeakQuantile), 1e-6) : 1;
        double shore = GenerationConstants.WaterLevel + CoastLift;
        double scale = (peakElevation - shore) / top;
        double deepest = Math.Min(-1e-6, Enumerable.Range(0, grid.Count).Where(i => relief.Sea[i]).Select(i => h[i]).DefaultIfEmpty(-1).Min());
        double[]? designed = design is null ? null : DesignedHeights(grid, h, relief.Sea, design, shore, peakElevation, seed);
        for (int i = 0; i < grid.Count; i++)
        {
            h[i] = relief.Sea[i]
                ? Math.Max(GenerationConstants.MinimumTerrainHeight, GenerationConstants.WaterLevel - 1 - SeaFloorDrop * h[i] / deepest)
                : Summit(designed?[i] ?? shore + Math.Max(0, h[i]) * scale, peakElevation, mapScale.MaximumElevation);
        }
        MapErosion.Relax(grid, h, relief.Sea, tuning.TalusSlope, tuning.TalusIterations);

        double[] rain = climate.Rainfall(grid, h, relief.Sea);
        // Save filled heights, then route exactly as a restored map will: from the rounded
        // single-precision heights alone, so saved discharge always matches rebuilt drainage.
        MapFlow.Fill(grid, h, MapFlow.Outlets(grid, h), MapFlow.FillGradient);
        float[] elevation = Round(h);
        double[] routed = elevation.Select(v => (double)v).ToArray();
        MapFlow flow = MapFlow.Route(grid, routed, MapFlow.Outlets(grid, routed), rain, MapFlow.FillGradient);
        double[] temperature = new double[grid.Count];
        for (int i = 0; i < grid.Count; i++) temperature[i] = climate.Temperature(grid.X(i), grid.Z(i), grid.Radius, h[i], peakElevation);
        double[] moisture = MapClimate.Moisture(rain, relief.Sea, temperature);
        for (int i = 0; i < grid.Count; i++)
            if (!relief.Sea[i]) moisture[i] = Math.Min(1, moisture[i] + RiparianMoisture * WorldMap.Smooth(Math.Clamp(flow.Discharge[i] / RiparianDischarge, 0, 1)));

        return new(grid, elevation, Round(temperature), Round(moisture), Round(relief.Hardness), Round(flow.Discharge));
    }

    /// <summary>
    /// A designed continent's land heights in metres (#9815). Instead of one normalised peak, the eroded
    /// lowland is scaled so its hills reach the design's <see cref="ContinentDesign.LowlandPeak"/>, and
    /// each belt rises from it to its drawn crest, textured by the eroded valleys: a node's eroded height
    /// over the highest nearby says how far up the range it stands. Where the map's peak cannot hold the
    /// design's highest crest, every height is scaled down together, so the shapes keep their proportion.
    /// </summary>
    private static double[] DesignedHeights(MapGrid grid, double[] h, bool[] sea, ContinentDesign design, double shore, double peak, ulong seed)
    {
        double fit = Math.Min(1, (peak - shore) / Math.Max(design.HighestCrest, design.LowlandPeak));
        double[] crest = new double[grid.Count], profile = new double[grid.Count];
        List<double> lowland = [];
        for (int i = 0; i < grid.Count; i++)
        {
            (crest[i], profile[i]) = design.CrestAndProfile(grid.X(i) / grid.Radius, grid.Z(i) / grid.Radius);
            if (!sea[i] && profile[i] < LowlandProfile) lowland.Add(Math.Max(0, h[i]));
        }

        double cap = lowland.Count > 0 ? Math.Max(MapRelief.Quantile([.. lowland], LowlandQuantile), 1e-6) : 1;
        double low = (design.LowlandPeak - shore) * fit / cap;
        int reach = Math.Max(1, (int)Math.Round(DesignedValleyReach * grid.Radius / grid.Spacing));
        double[] nearby = LocalMaximum(grid, h, reach);
        double[] heights = new double[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            double eroded = Math.Max(0, h[i]);
            double land = shore + (low * Math.Min(eroded, cap));
            double u = grid.X(i) / grid.Radius, v = grid.Z(i) / grid.Radius;
            // The ridged noise carves spurs, summits and saddles the coarse erosion lattice cannot.
            double ridges = MapNoise.Ridged(seed ^ DesignedRidgeSalt, u / design.RidgeWavelength, v / design.RidgeWavelength, DesignedRidgeOctaves, DesignedRidgePersistence);
            double texture = Math.Clamp(eroded / Math.Max(nearby[i], 1e-6), DesignedValleyFloor, 1) * (DesignedRidgeFloor + ((1 - DesignedRidgeFloor) * ridges));
            heights[i] = land + (Math.Max(0, (crest[i] * fit) - land) * texture);
        }

        return heights;
    }

    /// <summary>Each node's highest value within <paramref name="reach"/> nodes along both axes (a square window).</summary>
    private static double[] LocalMaximum(MapGrid grid, double[] values, int reach)
    {
        int side = grid.Side;
        double[] across = new double[values.Length], result = new double[values.Length];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            double best = double.NegativeInfinity;
            for (int k = Math.Max(0, x - reach); k <= Math.Min(side - 1, x + reach); k++) best = Math.Max(best, values[(z * side) + k]);
            across[(z * side) + x] = best;
        }

        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            double best = double.NegativeInfinity;
            for (int k = Math.Max(0, z - reach); k <= Math.Min(side - 1, z + reach); k++) best = Math.Max(best, across[(k * side) + x]);
            result[(z * side) + x] = best;
        }

        return result;
    }

    /// <summary>The rare summits above the target peak are compressed smoothly toward the ceiling, never flattened.</summary>
    private static double Summit(double metres, double peak, double ceiling)
    {
        double room = ceiling - CeilingHeadroom - peak;
        return metres <= peak ? metres : peak + room * Math.Tanh((metres - peak) / room);
    }

    private static float[] Round(double[] values) => values.Select(v => (float)v).ToArray();
}
