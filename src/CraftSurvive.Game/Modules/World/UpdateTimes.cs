using System.Diagnostics;
using System.Globalization;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// How long the product's walking updates take (#10889: a cave generated, admitted and streamed in must not hitch the
/// frame): the last updates' durations on the update thread, with their median, high percentiles, worst and how many went
/// over a frame at 60 Hz. The Engine's rendering is not in an update's duration, so the time from one update's start to the
/// next is kept too: the frame as the player sees it, the Engine's work between updates included (R10889-2).
/// </summary>
internal sealed class UpdateTimes
{
    /// <summary>How many updates are kept: about twenty seconds at 60 Hz.</summary>
    internal const int Kept = 1200;

    /// <summary>A frame at 60 Hz, in milliseconds: an update over it is a hitch.</summary>
    internal const double FrameBudgetMs = 1000d / 60d;

    private readonly double[] times = new double[Kept];
    private readonly long[] steps = new long[Kept];
    private readonly double[] intervals = new double[Kept];
    private readonly long[] begins = new long[Kept];
    /// <summary>
    /// The product's own share of the update each interval began with (#10907): its duration less the Engine services it called
    /// (the residency hand-off and readback, the scene's refresh, persistence, the horizon's commits).
    /// </summary>
    private readonly double[] leading = new double[Kept];
    private readonly double[] own = new double[Kept];
    private int count, next;
    private long started, marked, lastBegin;
    private double interval = double.NaN, lastOwn = double.NaN;

    // This update's phases so far, and the slowest updates' breakdowns (#10889 R10889-2: where a stall goes).
    private readonly List<(string Phase, double Ms)> phases = [];
    private readonly List<(double Ms, long Step, string Breakdown)> slowest = [];
    private const int SlowestKept = 8;

    // The process's memory over the window: the working set (the Engine's and the product's) and the managed heap.
    private long workingLeast = long.MaxValue, workingMost, managedLeast = long.MaxValue, managedMost;
    private const int MemoryEvery = 30;

    /// <summary>Extra detail for a slow update's breakdown (the terrain's last synchronize), asked only when one is kept.</summary>
    internal Func<string>? Detail { get; set; }

    /// <summary>The time this update spent in Engine services, in milliseconds, asked as it ends (#10907).</summary>
    internal Func<double>? EngineShare { get; set; }

    internal void Begin()
    {
        started = marked = Stopwatch.GetTimestamp();
        interval = lastBegin == 0 ? double.NaN : Stopwatch.GetElapsedTime(lastBegin, started).TotalMilliseconds;
        lastBegin = started;
        phases.Clear();
    }

    /// <summary>Ends a phase of this update, named for the breakdown.</summary>
    internal void Mark(string phase)
    {
        long now = Stopwatch.GetTimestamp();
        phases.Add((phase, Stopwatch.GetElapsedTime(marked, now).TotalMilliseconds));
        marked = now;
    }

    internal void End(long step)
    {
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        times[next] = ms;
        steps[next] = step;
        intervals[next] = interval;
        double engine = Math.Min(ms, EngineShare?.Invoke() ?? 0d);
        leading[next] = lastOwn;
        own[next] = ms - engine;
        lastOwn = ms - engine;
        begins[next] = started;
        next = (next + 1) % Kept;
        count = Math.Min(count + 1, Kept);
        if (slowest.Count < SlowestKept || ms > slowest[^1].Ms)
        {
            string breakdown = string.Join(" ", phases.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Phase}={p.Ms:F1}")))
                + string.Create(CultureInfo.InvariantCulture, $" engine={engine:F1}");
            slowest.Add((ms, step, $"{breakdown} [{Detail?.Invoke()}]"));
            slowest.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            if (slowest.Count > SlowestKept) slowest.RemoveAt(slowest.Count - 1);
        }

        if (count % MemoryEvery == 0)
        {
            long working = Environment.WorkingSet, managed = GC.GetTotalMemory(false);
            workingLeast = Math.Min(workingLeast, working);
            workingMost = Math.Max(workingMost, working);
            managedLeast = Math.Min(managedLeast, managed);
            managedMost = Math.Max(managedMost, managed);
        }
    }

    internal void Reset()
    {
        count = 0;
        next = 0;
        lastBegin = 0;
        lastOwn = double.NaN;
        slowest.Clear();
        workingLeast = managedLeast = long.MaxValue;
        workingMost = managedMost = 0;
    }

    /// <summary>The slowest updates kept, each with its phases.</summary>
    internal string Slowest() => string.Join("\n", slowest.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Ms:F1}ms step {s.Step}: {s.Breakdown}")));

    internal string Readout()
    {
        if (count == 0)
        {
            return "updates=0";
        }

        double[] sorted = [.. times.Take(count).Order()];
        double Percentile(double share) => sorted[Math.Min(count - 1, (int)(share * count))];
        int worst = Array.IndexOf(times, sorted[^1], 0, count);
        const double Mb = 1024d * 1024d;
        string memory = workingMost == 0 ? "memory=unsampled" : string.Create(CultureInfo.InvariantCulture,
            $"workingSet={workingLeast / Mb:F0}-{workingMost / Mb:F0}MB managed={managedLeast / Mb:F0}-{managedMost / Mb:F0}MB");
        return string.Create(CultureInfo.InvariantCulture,
            $"updates={count} median={Percentile(0.5):F2}ms p95={Percentile(0.95):F2}ms p99={Percentile(0.99):F2}ms max={sorted[^1]:F1}ms (step {steps[worst]}) overBudget={sorted.Count(t => t > FrameBudgetMs)} budget={FrameBudgetMs:F1}ms {Frames()} {memory}");
    }

    /// <summary>
    /// The frames between updates: how many a second, and the time from one update's start to the next (the Engine's work
    /// included), with how many took over two frames at 60 Hz (a hitch the player sees). Such a frame is the product's when
    /// its own share of the update it began with took over a frame (#10907); otherwise the Engine's work made it, between the
    /// updates or in the services the update called.
    /// </summary>
    private string Frames()
    {
        int product = Enumerable.Range(0, count).Count(i => intervals[i] > 2d * FrameBudgetMs && leading[i] > FrameBudgetMs);
        double ownMost = Enumerable.Range(0, count).Select(i => own[i]).DefaultIfEmpty(0d).Max();
        double[] gaps = [.. Enumerable.Range(0, count).Select(i => intervals[i]).Where(g => !double.IsNaN(g)).Order()];
        if (gaps.Length == 0)
        {
            return "frames=unmeasured";
        }

        int first = count < Kept ? 0 : next, last = (next + Kept - 1) % Kept;
        double seconds = Stopwatch.GetElapsedTime(begins[first], begins[last]).TotalSeconds;
        double Percentile(double share) => gaps[Math.Min(gaps.Length - 1, (int)(share * gaps.Length))];
        return string.Create(CultureInfo.InvariantCulture,
            $"frames: rate={(seconds > 0d ? (count - 1) / seconds : 0d):F1}/s interval median={Percentile(0.5):F1}ms p95={Percentile(0.95):F1}ms p99={Percentile(0.99):F1}ms max={gaps[^1]:F1}ms overTwoFrames={gaps.Count(g => g > 2d * FrameBudgetMs)} product={product} ownMax={ownMost:F1}ms");
    }
}
