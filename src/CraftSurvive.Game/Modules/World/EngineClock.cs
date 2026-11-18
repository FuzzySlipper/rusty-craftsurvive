namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The time the product has spent in Engine services it calls outside the terrain's own accounting (#10907): the save slots'
/// persistence and the textures a cave's dressing opens. A walking update's breakdown counts it as the Engine's work, not the
/// product's.
/// </summary>
internal static class EngineClock
{
    private static long ticks;

    internal static long Ticks => Interlocked.Read(ref ticks);

    internal static void Add(long elapsed) => Interlocked.Add(ref ticks, elapsed);

    /// <summary>Calls an Engine service, its time counted.</summary>
    internal static T Time<T>(Func<T> call)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return call();
        }
        finally
        {
            Add(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }
}
