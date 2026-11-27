using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// Chunks generated on workers (#10907, #11179): the same voxels and densities as the update thread's,
/// drawing directly through worker-callable managed KeyedRng (rusty-engine #11177) with no pre-draw step.
/// </summary>
internal static class WorkerChecks
{
    /// <summary>How long the checks wait for the workers before calling it a hang.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    internal static void Run()
    {
        TerrainConfiguration config = TerrainConfiguration.Default;
        TerrainOverlayState overlay = new(config.Seed);
        TerrainChunkGenerator serial = new(config.CreateRecipe(EngineTerrainDraws.Shared));
        TerrainChunkGenerator parallel = new(config.CreateRecipe(EngineTerrainDraws.Shared));

        // A window round the origin a few chunks across, its ground band and a chunk either side: trees, places,
        // crossings and the overhang of features across chunk borders all decide directly on workers.
        List<TerrainChunkAddress> chunks = [];
        for (long x = -4; x <= 3; x++)
        for (long z = -4; z <= 3; z++)
        for (long y = -1; y <= 2; y++)
        {
            chunks.Add(new(x, y, z));
        }

        using (TerrainChunkPreparer preparer = new(parallel))
        {
            foreach (TerrainChunkAddress chunk in chunks)
            {
                preparer.Queue(chunk, overlay.Snapshot(), overlay.Revision, parallel.Recipe.Caves.Revision);
            }

            Dictionary<TerrainChunkAddress, TerrainChunkPreparer.Prepared> done = Drain(preparer, chunks);
            int mismatched = 0, givenBack = 0;
            foreach (TerrainChunkAddress chunk in chunks)
            {
                TerrainChunk? fromWorker = done[chunk].Chunk;
                if (fromWorker is null)
                {
                    givenBack++;
                    continue;
                }

                TerrainChunk expected = serial.Generate(chunk, overlay.Snapshot());
                if (!fromWorker.Materials.Span.SequenceEqual(expected.Materials.Span) || !fromWorker.Densities.Span.SequenceEqual(expected.Densities.Span))
                {
                    mismatched++;
                }
            }

            Check.That(preparer.Faults == 0, $"a chunk worker faulted: {preparer.Readout()}");
            Check.That(givenBack == 0, $"{givenBack} of {chunks.Count} chunks were given back by the workers");
            Check.That(mismatched == 0, $"{mismatched} of {chunks.Count} chunks generated on workers differ from the update thread's");
            Console.WriteLine($"{chunks.Count} chunks generated on {TerrainChunkPreparer.Workers} workers match the update thread's ({preparer.Readout()}).");
        }

        // Batch chunk column sampling and hoisted chunk features must match single-voxel MaterialAt exactly across the window.
        TerrainRecipe direct = config.CreateRecipe(EngineTerrainDraws.Shared);
        int voxelMismatches = 0;
        foreach (TerrainChunkAddress chunk in chunks)
        {
            TerrainChunk generated = serial.Generate(chunk, overlay.Snapshot());
            VoxelAddress origin = chunk.Origin;
            ReadOnlySpan<ushort> materials = generated.Materials.Span;
            int edge = TerrainConstants.ChunkEdgeLength;
            for (int z = 0; z < edge; z++)
            for (int y = 0; y < edge; y++)
            for (int x = 0; x < edge; x++)
            {
                ushort actual = materials[(z * TerrainConstants.ChunkPlaneLength) + (y * edge) + x];
                ushort expected = direct.MaterialAt(new VoxelAddress(origin.X + x, origin.Y + y, origin.Z + z));
                if (actual != expected) voxelMismatches++;
            }
        }

        Check.That(voxelMismatches == 0, $"{voxelMismatches} voxels from batch/hoisted chunk generation differed from direct MaterialAt");
        Console.WriteLine($"Batch column sampling and hoisted chunk features match direct MaterialAt across all {chunks.Count} chunks.");
    }

    private static Dictionary<TerrainChunkAddress, TerrainChunkPreparer.Prepared> Drain(TerrainChunkPreparer preparer, IReadOnlyList<TerrainChunkAddress> chunks)
    {
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Thread.Sleep(Poll);
            preparer.Collect();
        }
        while (preparer.Queued > 0 && waited.Elapsed < Patience);

        Check.That(preparer.Queued == 0, "the chunk workers did not finish");
        Dictionary<TerrainChunkAddress, TerrainChunkPreparer.Prepared> done = [];
        foreach (TerrainChunkAddress chunk in chunks)
        {
            if (preparer.TryTake(chunk, out TerrainChunkPreparer.Prepared? prepared))
            {
                done[chunk] = prepared;
            }
        }

        return done;
    }
}
