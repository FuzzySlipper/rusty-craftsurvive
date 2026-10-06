using System.Globalization;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Per-update cost of creature routing, for the routing readout (#9531).</summary>
internal sealed class RoutingTimes
{
    private long updates, queries, pursuerUpdates;
    private double totalMilliseconds, worstMilliseconds, lastMilliseconds;
    private long worstQueries;

    internal void Record(double milliseconds, long queriesThisUpdate, int pursuers)
    {
        updates++;
        queries += queriesThisUpdate;
        pursuerUpdates += pursuers;
        lastMilliseconds = milliseconds;
        totalMilliseconds += milliseconds;
        worstMilliseconds = Math.Max(worstMilliseconds, milliseconds);
        worstQueries = Math.Max(worstQueries, queriesThisUpdate);
    }

    internal void Reset() { updates = queries = pursuerUpdates = worstQueries = 0; totalMilliseconds = worstMilliseconds = lastMilliseconds = 0; }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"routing updates={updates} meanMs={(updates == 0 ? 0 : totalMilliseconds / updates):F2} worstMs={worstMilliseconds:F2} lastMs={lastMilliseconds:F2} queriesPerUpdate={(updates == 0 ? 0 : (double)queries / updates):F2} worstQueries={worstQueries} meanPursuers={(updates == 0 ? 0 : (double)pursuerUpdates / updates):F1}");
}
