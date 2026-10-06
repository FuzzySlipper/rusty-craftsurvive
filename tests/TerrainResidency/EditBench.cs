using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// editbench: the post-edit residency refresh (#9578) for blast-sized edits on the default world,
/// with the plan's requested chunks produced first as the streamer would.
/// </summary>
internal static class EditBench
{
    private const int Blasts = 12;
    private const int Radius = 4;

    internal static void Run()
    {
        var configuration = TerrainConfiguration.Default;
        var recipe = configuration.CreateRecipe(new TestDraws(configuration.Seed));
        var state = new TerrainOverlayState(configuration.Seed);
        var policy = new TerrainResidencyPolicy(recipe, new TerrainChunkGenerator(recipe));
        long surface = recipe.SurfaceAt(8, 12);
        TerrainChunkAddress center = new VoxelAddress(8, surface, 12).Chunk;
        Stopwatch clock = Stopwatch.StartNew();
        TerrainResidencyPlan plan = policy.PlanFor(center, state);
        foreach (TerrainChunkAddress address in plan.Requested) plan.Chunk(address);
        Console.WriteLine($"first plan and requested chunks: {clock.Elapsed.TotalMilliseconds:F0} ms, {plan.Requested.Count} requested, {plan.Retained.Count} retained");
        List<double> totals = [];
        for (int blast = 0; blast < Blasts; blast++)
        {
            long x = 8 + (blast % 4) * 5 - 6, z = 12 + (blast / 4) * 5 - 5;
            long y = recipe.SurfaceAt(x, z);
            List<TerrainVoxelEdit> carved = [];
            for (long dx = -Radius; dx <= Radius; dx++)
            for (long dy = -Radius; dy <= Radius; dy++)
            for (long dz = -Radius; dz <= Radius; dz++)
                if (dx * dx + dy * dy + dz * dz <= Radius * Radius) carved.Add(new(new VoxelAddress(x + dx, y + dy, z + dz), TerrainConstants.EmptyMaterial));
            var receipt = state.Apply(new TerrainEditAccepted(carved));
            clock.Restart();
            policy.RefreshAfterOverlayChange(state, receipt);
            double total = clock.Elapsed.TotalMilliseconds;
            totals.Add(total);
            Console.WriteLine($"blast {blast}: {carved.Count} voxels, {receipt.AppliedEdits.Select(e => e.Address.Chunk).Distinct().Count()} chunks, refresh {total:F1} ms ({policy.LastRefresh})");
        }
        totals.Sort();
        Console.WriteLine($"refresh median {totals[totals.Count / 2]:F1} ms, worst {totals[^1]:F1} ms");
    }
}
