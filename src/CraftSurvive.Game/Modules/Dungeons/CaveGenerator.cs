using System.Diagnostics;
using System.Globalization;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// Generates caves and dungeons off the update (#10888). A request names a cave by a key that says everything its content
/// depends on, and a pure function that generates it; the work runs on a background scheduler capped below the machine's
/// cores, one generation at a time in the order asked, and the finished candidate is taken on a later update. Nothing about
/// a cave depends on the thread or the time it was made on: a generator copies its settings when it starts
/// (<see cref="KarstCave.Settings"/>) and makes no Engine calls. Finished caves are kept, the most recently used few, so
/// coming back to a cave takes it as it was.
/// </summary>
internal sealed class CaveGenerator
{
    /// <summary>
    /// How many threads a background generation may use: a quarter of the machine's, at least one and at most four, so the
    /// frame's own workers (the Engine's meshing, the product's other background work) keep the rest (#10888).
    /// </summary>
    internal static int Workers { get; } = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>
    /// The scheduler generations run on. Their parallel stages (<c>Parallel.For</c>) schedule on the current scheduler, so
    /// inside a generation they use at most <see cref="Workers"/> threads.
    /// </summary>
    private static readonly TaskScheduler Background = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Workers).ConcurrentScheduler;

    /// <summary>How many finished caves are kept for coming back to (a default cave and its walked copy are a few megabytes).</summary>
    internal const int Kept = 8;

    private sealed class Job
    {
        internal required string Key { get; init; }
        internal required Task<DungeonCandidate> Work { get; init; }
        internal required long Asked { get; init; }
        internal long Started { get; set; }
        internal long Finished { get; set; }
    }

    private readonly Dictionary<string, Job> jobs = [];
    private readonly LinkedList<string> used = [];
    private Task tail = Task.CompletedTask;
    private long generated, failed, waited;

    /// <summary>Asks for a cave in the background, unless it is already finished or on its way.</summary>
    internal void Request(string key, Func<DungeonCandidate> generate)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(generate);
        if (jobs.ContainsKey(key))
        {
            Touch(key);
            return;
        }

        long asked = Stopwatch.GetTimestamp();
        Job? job = null;
        // One at a time, in the order asked: each generation follows the last on the capped scheduler.
        Task<DungeonCandidate> work = tail.ContinueWith(_ =>
        {
            job!.Started = Stopwatch.GetTimestamp();
            try
            {
                return generate();
            }
            finally
            {
                job.Finished = Stopwatch.GetTimestamp();
            }
        }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, Background);
        job = new Job { Key = key, Work = work, Asked = asked };
        tail = work;
        generated++;
        jobs[key] = job;
        Touch(key);
        Trim();
    }

    /// <summary>Whether a cave was asked for (finished or not).</summary>
    internal bool Asked(string key) => jobs.ContainsKey(key);

    /// <summary>
    /// The cave if it is finished: null while it is still being generated (or was never asked for). A generation that failed
    /// throws its failure here, once, and is forgotten so it can be asked for again.
    /// </summary>
    internal DungeonCandidate? Finished(string key)
    {
        if (!jobs.TryGetValue(key, out Job? job) || !job.Work.IsCompleted)
        {
            return null;
        }

        Touch(key);
        if (job.Work.IsFaulted || job.Work.IsCanceled)
        {
            jobs.Remove(key);
            used.Remove(key);
            failed++;
            job.Work.GetAwaiter().GetResult();
        }

        return job.Work.Result;
    }

    /// <summary>
    /// Waits for a cave the caller cannot go on without (a restored session standing in it, or the bank's synchronous
    /// comparison), generating it here if it was never asked for.
    /// </summary>
    internal DungeonCandidate Wait(string key, Func<DungeonCandidate> generate)
    {
        Request(key, generate);
        waited++;
        jobs[key].Work.Wait();
        return Finished(key)!;
    }

    /// <summary>How long a finished cave took to generate, and how long it waited for the generations ahead of it.</summary>
    internal (double GenerateMs, double QueuedMs)? Timing(string key) =>
        jobs.TryGetValue(key, out Job? job) && job.Finished != 0
            ? (Stopwatch.GetElapsedTime(job.Started, job.Finished).TotalMilliseconds, Stopwatch.GetElapsedTime(job.Asked, job.Started).TotalMilliseconds)
            : null;

    /// <summary>What the generator holds and has done, for the readouts.</summary>
    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"generator workers={Workers} kept={jobs.Values.Count(j => j.Work.IsCompletedSuccessfully)} running={jobs.Values.Count(j => j.Started != 0 && j.Finished == 0)} queued={jobs.Values.Count(j => j.Started == 0)} generated={generated} failed={failed} waited={waited}");

    private void Touch(string key)
    {
        used.Remove(key);
        used.AddFirst(key);
    }

    /// <summary>Forgets the least recently used finished caves past <see cref="Kept"/>; unfinished ones are never dropped.</summary>
    private void Trim()
    {
        LinkedListNode<string>? node = used.Last;
        int finished = jobs.Values.Count(j => j.Work.IsCompleted);
        while (node is not null && finished > Kept)
        {
            LinkedListNode<string>? previous = node.Previous;
            if (jobs[node.Value].Work.IsCompleted)
            {
                jobs.Remove(node.Value);
                used.Remove(node);
                finished--;
            }

            node = previous;
        }
    }
}
