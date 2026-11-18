using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// Chunks generated on workers (#10907): the same voxels and densities as the update thread's, with every keyed draw made
/// ahead on the update thread, and a draw not made ahead giving the chunk back rather than drawing on a worker.
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
        TerrainChunkGenerator serial = new(config.CreateRecipe(new TestDraws(config.Seed)));
        TerrainChunkGenerator parallel = new(config.CreateRecipe(new UpdateThreadDraws(new TestDraws(config.Seed))));

        // A window round the origin a few chunks across, its ground band and a chunk either side: trees, places and the
        // overhang of features across chunk borders all come from the shared caches the workers read.
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
                parallel.Recipe.PrepareDraws(chunk);
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
            Check.That(givenBack == 0, $"{givenBack} of {chunks.Count} chunks were given back by the workers despite their draws made ahead");
            Check.That(mismatched == 0, $"{mismatched} of {chunks.Count} chunks generated on workers differ from the update thread's");
            Console.WriteLine($"{chunks.Count} chunks generated on {TerrainChunkPreparer.Workers} workers match the update thread's ({preparer.Readout()}).");
        }

        // A chunk whose draws were not made ahead: its worker may not draw, so it gives the chunk back.
        TerrainChunkGenerator unprepared = new(config.CreateRecipe(new UpdateThreadDraws(new TestDraws(config.Seed))));
        using (TerrainChunkPreparer preparer = new(unprepared))
        {
            TerrainChunkAddress chunk = new(7, 0, 7);
            preparer.Queue(chunk, overlay.Snapshot(), overlay.Revision, unprepared.Recipe.Caves.Revision);
            Dictionary<TerrainChunkAddress, TerrainChunkPreparer.Prepared> done = Drain(preparer, [chunk]);
            Check.That(done[chunk].Chunk is null && preparer.Faults == 0, "a worker generated a chunk whose keyed draws were not made ahead");
            Console.WriteLine("A chunk whose draws were not made ahead is given back to the update thread.");
        }
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

    /// <summary>Test draws that, like the Engine's, refuse a chunk worker.</summary>
    private sealed class UpdateThreadDraws(ITerrainDraws inner) : ITerrainDraws
    {
        public long Draw(string scope, string key, ulong seed, long minimum, long maximum) =>
            TerrainDrawLane.OnWorker ? throw new TerrainDrawOffUpdateException(scope, key) : inner.Draw(scope, key, seed, minimum, maximum);
    }
}
