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
    private const double PeakQuantile = 0.995;
    /// <summary>Metres kept free below the map ceiling, so filling and rounding never cross it.</summary>
    private const double CeilingHeadroom = 8;
    private const double CoastLift = 0.6;
    private const double SeaFloorDrop = 4;
    // Most evolution runs on a lattice of twice the spacing; a short refinement at full
    // resolution then organises the finer valleys. This keeps a 10 km world to seconds.
    private const int CoarseSteps = 80;
    private const int CoarseClimateInterval = 27;
    private const int RefineSteps = 10;
    /// <summary>Angle of repose for map-scale slopes, about 40 degrees.</summary>
    private const double TalusSlope = 0.84;
    private const int TalusIterations = 40;
    private const double RiparianMoisture = 0.22;
    private const double RiparianDischarge = 400;

    internal static MapFields Run(TerrainConfiguration configuration)
    {
        configuration.Validate();
        ulong seed = configuration.Contract.GeographyNoiseSeed;
        MapGrid grid = MapGrid.For(configuration.Size);
        MapGrid coarse = grid.Coarsened();
        MapScale mapScale = MapScale.For(configuration.Size);
        double peakElevation = mapScale.PeakElevation;
        MapClimate climate = new(seed, mapScale);
        MapRelief relief = MapRelief.Build(coarse, seed);
        MapErosion.Evolve(relief, h => climate.Rainfall(coarse, h, relief.Sea), CoarseSteps, CoarseClimateInterval);
        if (coarse != grid)
        {
            relief = MapRelief.Refine(relief, grid, seed);
            MapRelief refined = relief;
            MapErosion.Evolve(refined, h => climate.Rainfall(grid, h, refined.Sea), RefineSteps, RefineSteps);
        }

        double[] h = relief.Height;
        double[] land = Enumerable.Range(0, grid.Count).Where(i => !relief.Sea[i]).Select(i => h[i]).ToArray();
        double top = land.Length > 0 ? Math.Max(MapRelief.Quantile(land, PeakQuantile), 1e-6) : 1;
        double shore = GenerationConstants.WaterLevel + CoastLift;
        double scale = (peakElevation - shore) / top;
        double deepest = Math.Min(-1e-6, Enumerable.Range(0, grid.Count).Where(i => relief.Sea[i]).Select(i => h[i]).DefaultIfEmpty(-1).Min());
        for (int i = 0; i < grid.Count; i++)
        {
            h[i] = relief.Sea[i]
                ? Math.Max(GenerationConstants.MinimumTerrainHeight, GenerationConstants.WaterLevel - 1 - SeaFloorDrop * h[i] / deepest)
                : Summit(shore + Math.Max(0, h[i]) * scale, peakElevation, mapScale.MaximumElevation);
        }
        MapErosion.Relax(grid, h, relief.Sea, TalusSlope, TalusIterations);

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
