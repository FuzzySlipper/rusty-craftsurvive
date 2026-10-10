using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The horizon's erosion filter (#9822): surface form only, deterministic, cut deeper the steeper the land,
/// crest relief only on high ground, and nothing at all on a river's channel or the coast.
/// </summary>
internal static class ErosionFilterChecks
{
    private const ulong Seed = 9822;

    internal static void Run()
    {
        ErosionFilter filter = new(Seed, ErosionFilterSettings.Continent);
        Check.That(filter.Offset(1234, -5678, 0, 0) == 0, "flat land stays flat");
        Check.That(filter.Offset(1234, -5678, 0.4, 0.2) == new ErosionFilter(Seed, ErosionFilterSettings.Continent).Offset(1234, -5678, 0.4, 0.2),
            "the filter is a pure function of its seed and point");

        // Across a slope, the furrows deepen with it.
        double Spread(double slope)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (int k = 0; k < 400; k++)
            {
                double offset = filter.Offset(k * 97.0, k * 31.0, slope, slope * 0.5);
                low = Math.Min(low, offset);
                high = Math.Max(high, offset);
            }

            return high - low;
        }

        double gentle = Spread(0.05), steep = Spread(0.6);
        Check.That(steep > gentle * 5 && steep < ErosionFilterSettings.Continent.LongestMetres, $"gullies cut deeper on steep ground ({gentle:F0} m against {steep:F0} m)");

        ErosionFilterSettings settings = ErosionFilterSettings.Continent;
        Check.That(filter.Crest(500, 500, settings.CrestFromMetres - 1) == 0, "no crest relief below its height");
        double[] crests = [.. Enumerable.Range(0, 200).Select(k => filter.Crest(k * 613.0, k * 211.0, settings.CrestFullMetres + 100))];
        Check.That(crests.Max() > 0 && crests.Min() < 0 && crests.All(c => Math.Abs(c) <= settings.CrestReliefMetres),
            "high ground is broken into peaks and cols within its relief");

        // On a generated world: nothing moves on a river's channel, at sea or on the shore.
        TerrainConfiguration configuration = new(Seed, 8192);
        WorldMap map = new(configuration, MapSimulation.Run(configuration));
        ErodedHeights eroded = new(map, filter);
        int rivers = 0, shores = 0;
        for (double z = -map.Radius + 64; z < map.Radius - 64; z += 128)
        for (double x = -map.Radius + 64; x < map.Radius - 64; x += 128)
        {
            MapSample sample = map.Sample(x, z);
            double height = eroded.Height(x, z, sample);
            if (sample.InRiver && height != sample.Elevation) rivers++;
            if (sample.Elevation <= GenerationConstants.WaterLevel && height != sample.Elevation) shores++;
        }

        Check.That(rivers == 0, $"no river channel is moved ({rivers})");
        Check.That(shores == 0, $"no sea or shore below the water is moved ({shores})");
    }

    /// <summary>
    /// R9822-2: on the frontier peninsula the filter leaves every pass's notch exactly as generated (gullies
    /// and crest relief alike), while the Wall's flanks around it still take detail.
    /// </summary>
    internal static void PassesAreSheltered()
    {
        TerrainConfiguration configuration = new(12345, MapScale.DefaultContinentalSize);
        WorldMap map = WorldMapGenerator.Generate(configuration);
        ContinentDesign design = ContinentDesign.For(configuration.Size)!;
        ErodedHeights eroded = new(map, new ErosionFilter(configuration.Contract.GeographyNoiseSeed, ErosionFilterSettings.Continent), design);
        int corridor = 0, moved = 0, flanks = 0, detailed = 0;
        for (int p = 0; p < design.Passes.Count; p++)
        {
            DesignPass pass = design.Passes[p];
            for (double dz = -0.08; dz <= 0.08; dz += 0.004)
            for (double dx = -0.08; dx <= 0.08; dx += 0.004)
            {
                double u = pass.At[0] + dx, v = pass.At[1] + dz, x = u * map.Radius, z = v * map.Radius;
                MapSample sample = map.Sample(x, z);
                double reach = design.PassReach(u, v);
                double height = eroded.Height(x, z, sample);
                if (reach >= 0.5)
                {
                    corridor++;
                    if (height != sample.Elevation) moved++;
                }
                else if (reach < 0.05 && sample.Elevation > 2000)
                {
                    flanks++;
                    if (Math.Abs(height - sample.Elevation) > 5) detailed++;
                }
            }

            double at = eroded.Height(pass.At[0] * map.Radius, pass.At[1] * map.Radius, map.Sample(pass.At[0] * map.Radius, pass.At[1] * map.Radius));
            Check.That(at == map.Sample(pass.At[0] * map.Radius, pass.At[1] * map.Radius).Elevation, $"{pass.Name}'s saddle is unchanged");
        }

        Check.That(corridor > 0 && moved == 0, $"nothing in a pass's notch moves ({moved} of {corridor})");
        Check.That(flanks > 0 && detailed > flanks / 2, $"the high flanks about the passes still take detail ({detailed} of {flanks})");
    }
}
