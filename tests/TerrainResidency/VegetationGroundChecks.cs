using System.Diagnostics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The vegetation tile sampler (#10016) against the near ground: on a one-metre lattice its top block is
/// the block the recipe generates at each column's surface, structures aside, and its cost is reported.
/// </summary>
internal static class VegetationGroundChecks
{
    private const int Side = 67;
    private const int TimedTilesPerSide = 8, TimedTiles = TimedTilesPerSide * TimedTilesPerSide;
    private const double CoarseStep = 4;

    internal static void Run()
    {
        TerrainConfiguration config = TerrainConfiguration.Default with { Seed = 12345 };
        TerrainRecipe recipe = config.CreateRecipe(new TestDraws(config.Seed));
        VegetationGround ground = new(recipe);
        float[] heights = new float[Side * Side];
        byte[] slots = new byte[Side * Side];
        int agree = 0, compared = 0, grass = 0;
        foreach ((double x0, double z0) in new[] { (-32d, 512d), (0d, 0d), (384d, -320d), (-1500d, 900d) })
        {
            ground.Fill(x0, z0, 1, Side, heights, slots);
            for (int j = 1; j < Side - 1; j++)
            for (int i = 1; i < Side - 1; i++)
            {
                long x = (long)x0 + i, z = (long)z0 + j;
                TerrainColumn column = recipe.ColumnAt(x, z);
                ushort generated = recipe.MaterialAt(new VoxelAddress(x, column.Surface, z), column);
                // Structures (sites, crossings) and the trees' cores replace the natural top; the sampler leaves them to the near ground.
                if (generated == (ushort)BlockId.TreeCore || generated == TerrainConstants.EmptyMaterial) continue;
                compared++;
                if (BlockRegistry.Get((BlockId)generated).Slot == slots[(j * Side) + i]) agree++;
                if (slots[(j * Side) + i] == BlockRegistry.Get(BlockId.Grass).Slot) grass++;
            }
        }

        Check.That(compared > 0 && agree >= compared * 0.99,
            $"the sampler's top block must match the generated surface on a metre lattice: {agree}/{compared}");
        Check.That(grass > 0, "the sampled lattices must include grass");

        // Fresh tiles inside the world, marching away from the checked ones so no column repeats.
        double span = (Side - 3) * CoarseStep, start = -recipe.Radius + span;
        int outside = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int t = 0; t < TimedTiles; t++)
        {
            ground.Fill(start + (t % TimedTilesPerSide * span), start + (t / TimedTilesPerSide * span), CoarseStep, Side, heights, slots);
            outside += heights.Count(float.IsNaN);
        }

        Check.That(outside == 0, $"the timed tiles must lie inside the world: {outside} columns outside");
        double perSample = watch.Elapsed.TotalMicroseconds / (TimedTiles * (double)Side * Side);
        Console.WriteLine(FormattableString.Invariant(
            $"Vegetation ground: {agree}/{compared} metre columns agree, {grass} grass; {perSample:F2} µs a sample ({Side}x{Side} lattice at {CoarseStep} m, one thread, fresh tiles; world radius {recipe.Radius} m)."));
    }
}
