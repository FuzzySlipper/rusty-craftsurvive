using System.Diagnostics;
using System.Globalization;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// How long the product's walking updates take (#10889: a cave generated, admitted and streamed in must not hitch the
/// frame): the last updates' durations on the update thread, with their median, high percentiles, worst and how many went
/// over a frame at 60 Hz. The Engine's rendering is not in it; its own readout (engine.renderer) has that.
/// </summary>
internal sealed class UpdateTimes
{
    /// <summary>How many updates are kept: about twenty seconds at 60 Hz.</summary>
    internal const int Kept = 1200;

    /// <summary>A frame at 60 Hz, in milliseconds: an update over it is a hitch.</summary>
    internal const double FrameBudgetMs = 1000d / 60d;

    private readonly double[] times = new double[Kept];
    private readonly long[] steps = new long[Kept];
    private int count, next;
    private long started;

    internal void Begin() => started = Stopwatch.GetTimestamp();

    internal void End(long step)
    {
        times[next] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        steps[next] = step;
        next = (next + 1) % Kept;
        count = Math.Min(count + 1, Kept);
    }

    internal void Reset()
    {
        count = 0;
        next = 0;
    }

    internal string Readout()
    {
        if (count == 0)
        {
            return "updates=0";
        }

        double[] sorted = [.. times.Take(count).Order()];
        double Percentile(double share) => sorted[Math.Min(count - 1, (int)(share * count))];
        int worst = Array.IndexOf(times, sorted[^1], 0, count);
        return string.Create(CultureInfo.InvariantCulture,
            $"updates={count} median={Percentile(0.5):F2}ms p95={Percentile(0.95):F2}ms p99={Percentile(0.99):F2}ms max={sorted[^1]:F1}ms (step {steps[worst]}) overBudget={sorted.Count(t => t > FrameBudgetMs)} budget={FrameBudgetMs:F1}ms");
    }
}
