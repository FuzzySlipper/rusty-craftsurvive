using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Tests;

/// <summary>
/// The far field's shared rules (#9850), which both its voxel session and its heightfield sample by: the sink
/// is whole inside the band, gone past it, and never deepens with distance, for every near-ground reach.
/// </summary>
internal static class FarGroundChecks
{
    internal static void Run()
    {
        foreach (double near in new double[] { 48, 128, 256, 512 })
        {
            (double inner, double outer) = FarGround.SinkBand(near);
            Check.That(inner >= 0 && outer > inner && outer <= near, $"the sunk band at a {near} m near edge lies inside it ({inner}-{outer} m)");
            Check.That(FarGround.Sink(inner, inner, outer) == FarGround.SinkMetres, $"the sink is whole at the band's inner edge ({near} m)");
            Check.That(FarGround.Sink(outer, inner, outer) == 0, $"the sink is gone at the band's outer edge ({near} m)");
            double previous = double.MaxValue;
            bool rising = false;
            for (double away = 0; away <= outer + 16; away += 0.5)
            {
                double sink = FarGround.Sink(away, inner, outer);
                rising |= sink > previous + 1e-12;
                previous = sink;
            }

            Check.That(!rising, $"the sink never deepens with distance ({near} m)");
        }
    }
}
