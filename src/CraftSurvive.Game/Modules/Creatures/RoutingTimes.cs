using System.Globalization;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Per-update cost of creature routing, and of stepping their bodies (#9737), for the routing readout (#9531).</summary>
internal sealed class RoutingTimes
{
    private long updates, queries, pursuerUpdates;
    private double totalMilliseconds, worstMilliseconds, lastMilliseconds;
    private long worstQueries;
    private long bodyUpdates, bodySteps, bodiesRefused;
    private double bodyMilliseconds, worstBodyMilliseconds;

    /// <summary>One update's body steps: their cost, how many creatures stepped, and how many the Engine refused (lodged in collision).</summary>
    internal void RecordBodies(double milliseconds, int stepped, int refused)
    {
        bodyUpdates++;
        bodySteps += stepped;
        bodiesRefused += refused;
        bodyMilliseconds += milliseconds;
        worstBodyMilliseconds = Math.Max(worstBodyMilliseconds, milliseconds);
    }

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

    internal void Reset()
    {
        updates = queries = pursuerUpdates = worstQueries = bodyUpdates = bodySteps = bodiesRefused = 0;
        totalMilliseconds = worstMilliseconds = lastMilliseconds = bodyMilliseconds = worstBodyMilliseconds = 0;
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"routing updates={updates} meanMs={(updates == 0 ? 0 : totalMilliseconds / updates):F2} worstMs={worstMilliseconds:F2} lastMs={lastMilliseconds:F2} queriesPerUpdate={(updates == 0 ? 0 : (double)queries / updates):F2} worstQueries={worstQueries} meanPursuers={(updates == 0 ? 0 : (double)pursuerUpdates / updates):F1} "
        + $"bodyMeanMs={(bodyUpdates == 0 ? 0 : bodyMilliseconds / bodyUpdates):F2} bodyWorstMs={worstBodyMilliseconds:F2} bodiesPerUpdate={(bodyUpdates == 0 ? 0 : (double)bodySteps / bodyUpdates):F1} bodiesRefused={bodiesRefused}");
}
