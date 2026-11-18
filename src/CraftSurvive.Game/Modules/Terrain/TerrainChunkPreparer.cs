using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Generates residency's chunks on workers (#10907), so streaming the world in does not stall an update. The update thread
/// makes a chunk's keyed draws ahead (the Engine's draws are its own, <see cref="TerrainRecipe.PrepareDraws"/>) and queues it;
/// a worker generates it from the recipe, which is safe to read from several threads; the update thread takes it once done.
/// A worker that meets a draw it was not given gives its chunk back, and the update thread generates that one itself. What a
/// worker generated against a world that has changed since under that chunk (an edit in it, a cave admitted or let go) is
/// dropped by the residency, which asks again.
/// </summary>
internal sealed class TerrainChunkPreparer : IDisposable
{
    /// <summary>Chunks generated at once: a quarter of the machine's threads, at least one and at most four, as the caves (#10888).</summary>
    internal static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>
    /// Chunks queued or generating at once: enough that the workers never wait, few enough that a player turning back leaves
    /// little generated for nothing, and that the nearest chunks are always the next asked.
    /// </summary>
    internal static readonly int MostQueued = Workers * 3;

    private static readonly TaskScheduler Background = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Workers).ConcurrentScheduler;

    private readonly TerrainChunkGenerator generator;
    private readonly HashSet<TerrainChunkAddress> queued = [];
    private readonly ConcurrentQueue<Prepared> finished = new();
    private readonly Dictionary<TerrainChunkAddress, Prepared> ready = [];
    private readonly CancellationTokenSource stopping = new();
    private long workerTicks;
    private long taken, givenBack, dropped, faults;
    private string lastFault = "";

    /// <summary>Faults in generating on a worker so far: none is expected.</summary>
    internal long Faults => faults;

    internal TerrainChunkPreparer(TerrainChunkGenerator generator) =>
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));

    /// <summary>
    /// A chunk a worker finished, with the world it was generated against: the overlay's revision and whether its edits reached
    /// the chunk, and the caves' revision. Its payload is null when the worker gave it back (a draw it was not given).
    /// </summary>
    internal sealed record Prepared(TerrainChunkAddress Address, TerrainChunk? Chunk, ulong OverlayRevision, bool Edited, long CavesRevision,
        string? Fault = null);

    /// <summary>Chunks queued or generating now.</summary>
    internal int Queued => queued.Count;

    /// <summary>Whether another chunk may be queued now.</summary>
    internal bool CanQueue => queued.Count < MostQueued;

    /// <summary>Whether a chunk is queued, generating or finished and not yet taken.</summary>
    internal bool Holds(TerrainChunkAddress address) => queued.Contains(address) || ready.ContainsKey(address);

    /// <summary>Queues a chunk whose draws the update thread has made ahead, against the overlay as it stands.</summary>
    internal void Queue(TerrainChunkAddress address, TerrainOverlaySnapshot overlay, ulong overlayRevision, long cavesRevision)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!queued.Add(address))
        {
            return;
        }

        bool edited = overlay.TouchesChunk(address);
        CancellationToken token = stopping.Token;
        _ = Task.Factory.StartNew(() =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            long started = Stopwatch.GetTimestamp();
            TerrainDrawLane.OnWorker = true;
            try
            {
                finished.Enqueue(new Prepared(address, generator.GenerateUncached(address, overlay), overlayRevision, edited, cavesRevision));
            }
            catch (TerrainDrawOffUpdateException)
            {
                finished.Enqueue(new Prepared(address, null, overlayRevision, edited, cavesRevision));
            }
            catch (Exception failure)
            {
                // A fault in generating on a worker gives the chunk back to the update thread, which generates it as before; the
                // readout names the fault, and the checks require none.
                finished.Enqueue(new Prepared(address, null, overlayRevision, edited, cavesRevision, $"{failure.GetType().Name}: {failure.Message}"));
            }
            finally
            {
                TerrainDrawLane.OnWorker = false;
                Interlocked.Add(ref workerTicks, Stopwatch.GetTimestamp() - started);
            }
        }, token, TaskCreationOptions.DenyChildAttach, Background);
    }

    /// <summary>Takes what the workers finished since the last update into the ready set.</summary>
    internal void Collect()
    {
        while (finished.TryDequeue(out Prepared? done))
        {
            queued.Remove(done.Address);
            if (done.Fault is not null)
            {
                faults++;
                lastFault = done.Fault;
            }

            ready[done.Address] = done;
        }
    }

    /// <summary>A finished chunk, taken out of the ready set.</summary>
    internal bool TryTake(TerrainChunkAddress address, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Prepared? prepared)
    {
        if (!ready.Remove(address, out prepared))
        {
            return false;
        }

        if (prepared.Chunk is null)
        {
            givenBack++;
        }
        else
        {
            taken++;
        }

        return true;
    }

    /// <summary>Notes a taken chunk the residency dropped because the world changed under it.</summary>
    internal void Dropped() => dropped++;

    /// <summary>Lets go of finished chunks no longer wanted (the player moved on), keeping those still asked for.</summary>
    internal void Keep(Func<TerrainChunkAddress, bool> wanted)
    {
        foreach (TerrainChunkAddress address in ready.Keys.Where(address => !wanted(address)).ToArray())
        {
            ready.Remove(address);
            dropped++;
        }
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"workers={Workers} queued={queued.Count} ready={ready.Count} taken={taken} givenBack={givenBack} dropped={dropped} faults={faults}{(faults > 0 ? $" lastFault=\"{lastFault}\"" : "")} workerMs={Stopwatch.GetElapsedTime(0, Interlocked.Read(ref workerTicks)).TotalMilliseconds:F0}");

    public void Dispose()
    {
        stopping.Cancel();
        ready.Clear();
        queued.Clear();
    }
}
