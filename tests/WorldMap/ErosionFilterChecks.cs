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
}
