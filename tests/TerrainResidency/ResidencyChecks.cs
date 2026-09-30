using System.Buffers.Binary;
using System.Security.Cryptography;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>The residency plan against a full scan: overlap reuse, request priority, edits, restore and eviction.</summary>
internal static class ResidencyChecks
{
    internal static void Run()
    {
        var configuration = TerrainConfiguration.Default;
        var recipe = configuration.CreateRecipe(new TestDraws(configuration.Seed));
        var chunkGenerator = new TerrainChunkGenerator(recipe);
        var state = new TerrainOverlayState(configuration.Seed);
        var policy = new TerrainResidencyPolicy(recipe, chunkGenerator);
        TerrainChunkAddress center = new(0, 0, 0);
        TerrainChunkAddress unchanged = new(0, 0, 0);
        TerrainChunkAddress edited = new(0, 1, 0);
        var first = policy.PlanFor(center, state);
        var neighbor = policy.PlanFor(new(1, 0, 0), state);
        Check.That(ReferenceEquals(first.Chunk(unchanged), neighbor.Chunk(unchanged)), "boundary crossing regenerated overlap");
        Check.That(ReferenceEquals(neighbor, policy.PlanFor(new(1, 0, 0), state)), "stationary plan was rebuilt");
        foreach (long x in new long[] { -3, -1, 0, 1, 2, 5, 0 })
            CheckAgainstFullScan(new(x, 0, 0));

        // Every requested chunk is one the plan retains, so nothing is admitted to be evicted next update.
        TerrainResidencyPolicy boundedPolicy = new(recipe, new TerrainChunkGenerator(recipe));
        foreach (long x in new long[] { -3, 0, 4 })
        {
            TerrainResidencyPlan plan = boundedPolicy.PlanFor(new(x, 0, 0), state);
            Check.That(plan.Requested.All(plan.Retained.Contains), $"a plan at x={x} requests a chunk it would not retain");
        }

        var beforeEdit = policy.PlanFor(center, state);
        Check.That(!beforeEdit.Requested.Contains(edited), "test column must begin empty");
        VoxelAddress voxel = new(1, 20, 1);
        var receipt = state.Apply(new TerrainEditAccepted([new(voxel, TerrainConstants.StoneMaterial)]));
        policy.RefreshAfterOverlayChange(state, receipt);
        var afterEdit = policy.PlanFor(center, state);
        Check.That(afterEdit.Requested.Contains(edited), "newly occupied chunk was not requested");
        Check.That(afterEdit.Chunk(edited).Materials.Span[4 * TerrainConstants.ChunkEdgeLength + 1 + TerrainConstants.ChunkPlaneLength] == TerrainConstants.StoneMaterial,
            "prepared payload did not contain the edit");
        Check.That(ReferenceEquals(beforeEdit.Chunk(unchanged), afterEdit.Chunk(unchanged)), "edit regenerated untouched chunk");
        Check.That(beforeEdit.Chunk(edited).SolidVoxelCount == 0, "edit mutated an earlier plan payload");
        CheckAgainstFullScan(center);
        receipt = state.Apply(new TerrainEditAccepted([new(voxel, TerrainConstants.EmptyMaterial)]));
        policy.RefreshAfterOverlayChange(state, receipt);
        Check.That(!policy.PlanFor(center, state).Requested.Contains(edited), "cleared chunk remained requested");
        // An unreported revision change/restore must not reuse stale cached payloads.
        state.Apply(new TerrainEditAccepted([new(voxel, TerrainConstants.StoneMaterial)]));
        Check.That(policy.PlanFor(center, state).Requested.Contains(edited), "unreported edit reused stale payload");
        state.Restore(new TerrainOverlaySnapshot(configuration.Seed, []));
        Check.That(!policy.PlanFor(center, state).Requested.Contains(edited), "restore reused stale payload");
        CheckAgainstFullScan(new(-2, 1, -1));
        var distant = policy.PlanFor(new(12, 0, 0), state);
        try { distant.Chunk(unchanged); throw new Exception("out-of-window payload was retained"); }
        catch (KeyNotFoundException) { }

        void CheckAgainstFullScan(TerrainChunkAddress location)
        {
            var snapshot = state.Snapshot();
            var populated = new List<TerrainChunkAddress>();
            // Scanned to the policy's retained radius, so the full scan covers every
            // neighbourhood the plan can retain.
            for (long x = location.X - TerrainConstants.RetainedChunkRadius; x <= location.X + TerrainConstants.RetainedChunkRadius; x++)
            for (long z = location.Z - TerrainConstants.RetainedChunkRadius; z <= location.Z + TerrainConstants.RetainedChunkRadius; z++)
            for (long y = -1; y <= 1; y++)
            {
                TerrainChunkAddress address = new(x, y, z);
                var generated = chunkGenerator.Generate(address, snapshot);
                var planned = policy.PlanFor(location, state).Chunk(address);
                Check.That(generated.Materials.Span.SequenceEqual(planned.Materials.Span), "planned material payload differs from fresh generation");
                if (generated.SolidVoxelCount > 0) populated.Add(address);
            }
            var ordered = populated.OrderBy(a => ((a.X-location.X)*(a.X-location.X)+(a.Z-location.Z)*(a.Z-location.Z), a.Y, a)).ToArray();
            // Derived from the policy's own radius, so widening the stream window does not
            // silently invalidate this check.
            var expectedRequested = ordered.Where(a =>
                Math.Abs(a.X - location.X) <= TerrainConstants.RequestedChunkRadius
                && Math.Abs(a.Z - location.Z) <= TerrainConstants.RequestedChunkRadius);
            var plan = policy.PlanFor(location, state);
            Check.That(plan.Requested.SequenceEqual(expectedRequested), "request priority/occupancy differs from full scan");
            Check.That(plan.Retained.SequenceEqual(ordered.Take(TerrainConstants.MaximumResidentChunks)), "retained priority/occupancy differs from full scan");
        }
    }
}
