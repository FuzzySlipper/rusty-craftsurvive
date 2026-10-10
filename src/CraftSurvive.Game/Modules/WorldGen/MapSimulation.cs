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
    /// <summary>A designed map's lowland hills (outside every belt) are scaled so this quantile reaches the design's lowland peak.</summary>
    private const double LowlandQuantile = 0.99;
    /// <summary>A belt's crest line is the nodes within this share of its half-width of its drawn line.</summary>
    private const double CrestLineShare = 0.2;
    /// <summary>Calibration runs the simulation this many times; a belt's gain moves at most this factor a round.</summary>
    private const int CalibrationRounds = 4;
    private const double CalibrationStep = 2.5;
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
        double shore = GenerationConstants.WaterLevel + CoastLift;
        MapRelief relief;
        double scale;
        if (design is null)
        {
            relief = Evolve(configuration, recipe, seed, grid, coarse, climate, MapRelief.Build(coarse, seed, recipe.Relief), null);
            double[] land = Enumerable.Range(0, grid.Count).Where(i => !relief.Sea[i]).Select(i => relief.Height[i]).ToArray();
            double top = land.Length > 0 ? Math.Max(MapRelief.Quantile(land, tuning.PeakQuantile), 1e-6) : 1;
            scale = (peakElevation - shore) / top;
        }
        else
        {
            (relief, scale) = Calibrated(configuration, recipe, seed, grid, coarse, climate, design, shore, peakElevation);
        }

        double[] h = relief.Height;
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

    /// <summary>Erosion over a starting relief: most of it on the coarse lattice, a short refinement on the full one.</summary>
    private static MapRelief Evolve(TerrainConfiguration configuration, MapRecipe recipe, ulong seed, MapGrid grid, MapGrid coarse,
        MapClimate climate, MapRelief relief, ContinentDesign? design)
    {
        SimulationRecipe tuning = recipe.Simulation;
        MapErosion.Evolve(relief, h => climate.Rainfall(coarse, h, relief.Sea), tuning.CoarseSteps, tuning.CoarseClimateInterval);
        if (coarse == grid) return relief;
        MapRelief refined = design is null
            ? MapRelief.Refine(relief, grid, seed, recipe.Relief)
            : MapRelief.RefineDesigned(relief, grid, seed, recipe.Relief, design);
        MapErosion.Evolve(refined, h => climate.Rainfall(grid, h, refined.Sea), tuning.RefineSteps, tuning.RefineSteps);
        return refined;
    }

    /// <summary>
    /// A designed continent, eroded and calibrated (#9815). Its lowland is scaled so its hills reach the
    /// design's lowland peak; then each belt's crest line is measured against the crest the design asks
    /// for, its uplift gain adjusted, and the whole run repeated, so the heights come from erosion over the
    /// designed uplift rather than being imposed on it. Where the map's peak cannot hold the design's
    /// highest crest, the design is scaled down together (<c>fit</c>), keeping its proportions.
    /// </summary>
    private static (MapRelief Relief, double Scale) Calibrated(TerrainConfiguration configuration, MapRecipe recipe, ulong seed,
        MapGrid grid, MapGrid coarse, MapClimate climate, ContinentDesign design, double shore, double peak)
    {
        double fit = Math.Min(1, (peak - shore) / Math.Max(design.Belts.SelectMany(b => b.Points).Select(p => p[2]).DefaultIfEmpty(0).Max(), design.LowlandPeak));
        // Which belt's crest line each node lies on (within a share of its half-width), and the crest asked there.
        int[] line = new int[grid.Count];
        double[] asked = new double[grid.Count];
        bool[] lowland = new bool[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            double u = grid.X(i) / grid.Radius, v = grid.Z(i) / grid.Radius;
            line[i] = -1;
            bool inBelt = false;
            for (int b = 0; b < design.Belts.Count; b++)
            {
                (double distance, double crest) = design.Belts[b].Nearest(u, v);
                inBelt |= distance < design.Belts[b].HalfWidth;
                if (distance < design.Belts[b].HalfWidth * CrestLineShare && crest * fit > asked[i])
                {
                    line[i] = b;
                    asked[i] = crest * fit;
                }
            }

            lowland[i] = !inBelt;
        }

        double[] gains = [.. Enumerable.Repeat(1.0, design.Belts.Count)];
        MapRelief relief = null!;
        double scale = 1;
        for (int round = 0; round < CalibrationRounds; round++)
        {
            relief = Evolve(configuration, recipe, seed, grid, coarse, climate, MapRelief.Designed(coarse, seed, recipe.Relief, design, gains), design);
            double[] h = relief.Height;
            double[] low = [.. Enumerable.Range(0, grid.Count).Where(i => lowland[i] && !relief.Sea[i]).Select(i => Math.Max(0, h[i]))];
            double cap = low.Length > 0 ? Math.Max(MapRelief.Quantile(low, LowlandQuantile), 1e-6) : 1;
            scale = ((design.LowlandPeak * fit) - shore) / cap;
            if (round == CalibrationRounds - 1) break;
            for (int b = 0; b < design.Belts.Count; b++)
            {
                double want = 0, made = 0;
                for (int i = 0; i < grid.Count; i++)
                {
                    if (line[i] != b || relief.Sea[i]) continue;
                    want += asked[i];
                    made += shore + (Math.Max(0, h[i]) * scale);
                }

                if (made > 0) gains[b] *= Math.Clamp(want / made, 1 / CalibrationStep, CalibrationStep);
            }
        }

        return (relief, scale);
    }

    /// <summary>The rare summits above the target peak are compressed smoothly toward the ceiling, never flattened.</summary>
    private static double Summit(double metres, double peak, double ceiling)
    {
        double room = ceiling - CeilingHeadroom - peak;
        return metres <= peak ? metres : peak + room * Math.Tanh((metres - peak) / room);
    }

    private static float[] Round(double[] values) => values.Select(v => (float)v).ToArray();
}
