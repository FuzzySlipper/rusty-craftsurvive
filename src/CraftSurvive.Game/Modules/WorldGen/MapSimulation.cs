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
    private const double CoastLift = 0.6;
    private const double SeaFloorDrop = 4;
    private const double RiparianMoisture = 0.22;
    private const double RiparianDischarge = 400;

    internal static MapFields Run(TerrainConfiguration configuration) => Run(configuration, MapRecipe.Default);

    /// <summary>A map made with a recipe's tuning (#9814); <see cref="MapRecipe.Default"/> makes the world its seed always has.</summary>
    internal static MapFields Run(TerrainConfiguration configuration, MapRecipe recipe)
    {
        SimulationRecipe tuning = recipe.Simulation;
        configuration.Validate();
        ulong seed = configuration.Contract.GeographyNoiseSeed;
        MapGrid grid = MapGrid.For(configuration.Size);
        MapGrid coarse = grid.Coarsened();
        MapScale mapScale = MapScale.For(configuration.Size);
        double peakElevation = Math.Min(tuning.PeakElevation ?? mapScale.PeakElevation, mapScale.MaximumElevation - CeilingHeadroom - MinimumSummitRoom);
        MapClimate climate = new(seed, mapScale);
        MapRelief relief = MapRelief.Build(coarse, seed, recipe.Relief);
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
        for (int i = 0; i < grid.Count; i++)
        {
            h[i] = relief.Sea[i]
                ? Math.Max(GenerationConstants.MinimumTerrainHeight, GenerationConstants.WaterLevel - 1 - SeaFloorDrop * h[i] / deepest)
                : Summit(shore + Math.Max(0, h[i]) * scale, peakElevation, mapScale.MaximumElevation);
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

    /// <summary>The rare summits above the target peak are compressed smoothly toward the ceiling, never flattened.</summary>
    private static double Summit(double metres, double peak, double ceiling)
    {
        double room = ceiling - CeilingHeadroom - peak;
        return metres <= peak ? metres : peak + room * Math.Tanh((metres - peak) / room);
    }

    private static float[] Round(double[] values) => values.Select(v => (float)v).ToArray();
}
