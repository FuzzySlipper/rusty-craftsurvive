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

        // Every column keeps the chunks holding its lowest and highest generated ground, found
        // by scanning the surface directly rather than trusting the band the policy reads.
        foreach (long cx in new long[] { -3, 0, 2 })
        foreach (long cz in new long[] { -2, 0, 3 })
        {
            (long bandMinimum, long bandMaximum) = recipe.ChunkColumnBand(cx, cz);
            for (long x = cx * TerrainConstants.ChunkEdgeLength; x < (cx + 1) * TerrainConstants.ChunkEdgeLength; x++)
            for (long z = cz * TerrainConstants.ChunkEdgeLength; z < (cz + 1) * TerrainConstants.ChunkEdgeLength; z++)
            {
                long surfaceChunk = GridMath.FloorDivide(recipe.SurfaceAt(x, z), TerrainConstants.ChunkEdgeLength);
                Check.That(surfaceChunk > bandMinimum && surfaceChunk <= bandMaximum,
                    "a column's surface band must hold its ground with a chunk of footing beneath");
            }
        }

        // An edit above every generated band is still requested: edited chunks are always candidates.
        long editY = (recipe.ChunkColumnBand(0, 0).Maximum + 1) * TerrainConstants.ChunkEdgeLength;
        edited = new VoxelAddress(1, editY + 4, 1).Chunk;
        var beforeEdit = policy.PlanFor(center, state);
        Check.That(!beforeEdit.Requested.Contains(edited), "test column must begin empty");
        VoxelAddress voxel = new(1, editY + 4, 1);
        var receipt = state.Apply(new TerrainEditAccepted([new(voxel, TerrainConstants.StoneMaterial)]));
        policy.RefreshAfterOverlayChange(state, receipt);
        var afterEdit = policy.PlanFor(center, state);
        Check.That(afterEdit.Requested.Contains(edited), "newly occupied chunk was not requested");
        Check.That(afterEdit.Chunk(edited).Materials.Span[4 * TerrainConstants.ChunkEdgeLength + 1 + TerrainConstants.ChunkPlaneLength] == TerrainConstants.StoneMaterial,
            "prepared payload did not contain the edit");
        Check.That(ReferenceEquals(beforeEdit.Chunk(unchanged), afterEdit.Chunk(unchanged)), "edit regenerated untouched chunk");
        // The earlier plan either never held the edited chunk or holds its unedited payload.
        bool earlierUntouched;
        try { earlierUntouched = beforeEdit.Chunk(edited).SolidVoxelCount == 0; }
        catch (KeyNotFoundException) { earlierUntouched = true; }
        Check.That(earlierUntouched, "edit mutated an earlier plan payload");
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
        var ridge = recipe.Map.Sites.MaxBy(site => site.Geography.Elevation);
        TerrainChunkAddress ridgeSurface = new VoxelAddress((long)ridge.X, recipe.SurfaceAt((long)ridge.X, (long)ridge.Z), (long)ridge.Z).Chunk;
        var ridgePlan = policy.PlanFor(ridgeSurface, state);
        Check.That(ridgePlan.Requested[0] == ridgeSurface, "a highland arrival admits the player's supporting surface before buried chunks");
        Check.That(ridgePlan.Retained.Count <= TerrainConstants.MaximumResidentChunks, "highland columns respect the residency bound");
        var distant = policy.PlanFor(new(12, 0, 0), state);
        try { distant.Chunk(unchanged); throw new Exception("out-of-window payload was retained"); }
        catch (KeyNotFoundException) { }

        void CheckAgainstFullScan(TerrainChunkAddress location)
        {
            var snapshot = state.Snapshot();
            var populated = new List<TerrainChunkAddress>();
            // Scanned to the policy's retained radius, so the full scan covers every
            // neighbourhood the plan can retain.
            // Each column is scanned across its surface band, the player's storey and any edited
            // chunk; the band itself is checked against direct surface scans above.
            for (long x = location.X - TerrainConstants.RetainedChunkRadius; x <= location.X + TerrainConstants.RetainedChunkRadius; x++)
            for (long z = location.Z - TerrainConstants.RetainedChunkRadius; z <= location.Z + TerrainConstants.RetainedChunkRadius; z++)
            for (long y = recipe.MinimumMaterialY / TerrainConstants.ChunkEdgeLength - 1; y <= recipe.MaximumMaterialY / TerrainConstants.ChunkEdgeLength; y++)
            {
                TerrainChunkAddress address = new(x, y, z);
                (long bandMinimum, long bandMaximum) = recipe.ChunkColumnBand(x, z);
                if ((y < bandMinimum || y > bandMaximum) && Math.Abs(y - location.Y) > TerrainConstants.PlayerStoreyChunks
                    && !snapshot.TouchesChunk(address)) continue;
                var generated = chunkGenerator.Generate(address, snapshot);
                var planned = policy.PlanFor(location, state).Chunk(address);
                Check.That(generated.Materials.Span.SequenceEqual(planned.Materials.Span), "planned material payload differs from fresh generation");
                if (generated.SolidVoxelCount > 0) populated.Add(address);
            }
            var ordered = populated.OrderBy(a => ((a.X-location.X)*(a.X-location.X)+(a.Z-location.Z)*(a.Z-location.Z), Math.Abs(a.Y-location.Y), a)).ToArray();
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
