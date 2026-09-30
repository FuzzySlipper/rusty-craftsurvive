namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>Tuning for how the journal looks around and answers queries.</summary>
internal static class DiscoveryConstants
{
    /// <summary>
    /// How often the world asks what the player can see, in update ticks. A player at a
    /// walking pace covers a fraction of a metre per tick and the notice radius is 128 m, so
    /// every tick would do the same work ten times over.
    /// </summary>
    internal const int NoticeIntervalTicks = 10;

    /// <summary>
    /// How many rows a kind-filtered query returns. It is small on purpose, so the answer is small enough to
    /// arrive whole: the count is always reported, so a caller that needs the rest can narrow
    /// the radius rather than read every site in range.
    /// </summary>
    internal const int MaximumFindRows = 12;
}
