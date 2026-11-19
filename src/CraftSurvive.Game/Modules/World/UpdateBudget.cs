using System.Diagnostics;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The walking update's time so far, for work that can wait for a lighter update (#10907): a cave's next dressing step, the
/// chemistry window's next chunk, a tree tile's draws, a place's layout, the near trees' next slice, a party sent. Such work
/// starts only while the update has spent less than <see cref="LightUpdateMs"/> (the Engine's calls in it included), so the
/// update's must-do work and the Engine's frame still fit two frames; and it waits at most <see cref="MostDeferrals"/>
/// updates in a row, so it always comes. Outside a walking update (the map, a town) every update counts as light.
/// </summary>
internal static class UpdateBudget
{
    /// <summary>
    /// Deferrable work starts only while the update has spent less than this: about a third of a frame, leaving the rest of
    /// it for the update's own must-do work (the player, the modules, the snapshot) and that work's slice.
    /// </summary>
    internal const double LightUpdateMs = 6d;

    /// <summary>A deferrable piece of work waits at most this many heavy updates in a row (half a second), then runs.</summary>
    internal const int MostDeferrals = 30;

    private static long began;

    /// <summary>A walking update begins.</summary>
    internal static void Begin() => began = Stopwatch.GetTimestamp();

    /// <summary>A walking update has ended: until the next begins, every update counts as light.</summary>
    internal static void End() => began = 0;

    /// <summary>What the walking update has spent so far, in milliseconds (0 outside one).</summary>
    internal static double SpentMs => began == 0 ? 0d : Stopwatch.GetElapsedTime(began).TotalMilliseconds;

    /// <summary>Whether the update is light enough for deferrable work to start.</summary>
    internal static bool Light => SpentMs < LightUpdateMs;
}

/// <summary>A deferrable piece of work's count of the heavy updates it has waited (#10907), so it waits a bounded while.</summary>
internal struct Deferral
{
    private int waited;

    /// <summary>Whether the work runs this update: in a light one, or once it has waited its most.</summary>
    internal bool Runs()
    {
        if (UpdateBudget.Light || waited >= UpdateBudget.MostDeferrals)
        {
            waited = 0;
            return true;
        }

        waited++;
        return false;
    }
}
