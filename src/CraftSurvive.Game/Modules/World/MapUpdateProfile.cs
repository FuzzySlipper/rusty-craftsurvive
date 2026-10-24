using System.Diagnostics;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// Where an open map's product update spends its time (#10346): each <see cref="Mark"/> charges the time since the
/// last to a named phase, and the slowest updates since the last <see cref="Reset"/> keep their breakdowns, with the
/// collector's pause and gen-2 collections in each.
/// </summary>
internal sealed class MapUpdateProfile
{
    private const int KeptSlowest = 4;

    private readonly Dictionary<string, long> phases = [];
    private readonly List<(double Milliseconds, string Breakdown)> slowest = [];
    private long started, last;
    private TimeSpan pausedAtStart;
    private int gen2AtStart;
    private bool running;
    private long updates;

    internal void Begin()
    {
        phases.Clear();
        started = last = Stopwatch.GetTimestamp();
        pausedAtStart = GC.GetTotalPauseDuration();
        gen2AtStart = GC.CollectionCount(2);
        running = true;
    }

    internal void Mark(string phase)
    {
        if (!running) return;
        long now = Stopwatch.GetTimestamp();
        phases[phase] = phases.GetValueOrDefault(phase) + (now - last);
        last = now;
    }

    internal void End()
    {
        if (!running) return;
        Mark("rest");
        running = false;
        updates++;
        double total = Stopwatch.GetElapsedTime(started, last).TotalMilliseconds;
        if (slowest.Count == KeptSlowest && total <= slowest[^1].Milliseconds) return;
        double paused = (GC.GetTotalPauseDuration() - pausedAtStart).TotalMilliseconds;
        string breakdown = FormattableString.Invariant($"{total:F1}ms gcPause={paused:F1}ms gen2={GC.CollectionCount(2) - gen2AtStart} [")
            + string.Join(' ', phases.Where(phase => phase.Value > 0).OrderByDescending(phase => phase.Value)
                .Select(phase => FormattableString.Invariant($"{phase.Key}={Stopwatch.GetElapsedTime(0, phase.Value).TotalMilliseconds:F1}")))
            + "]";
        slowest.Add((total, breakdown));
        slowest.Sort((a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
        if (slowest.Count > KeptSlowest) slowest.RemoveAt(KeptSlowest);
    }

    internal void Reset()
    {
        slowest.Clear();
        updates = 0;
    }

    internal string Readout => FormattableString.Invariant($"profiled updates={updates} slowest: ") + string.Join(" ; ", slowest.Select(entry => entry.Breakdown));
}
