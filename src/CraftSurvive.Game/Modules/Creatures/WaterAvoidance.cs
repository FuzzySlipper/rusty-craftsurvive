using System.Globalization;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Water over a navigation column, in the session's local frame: its top, how deep it stands over the bed, and whether it is frozen.</summary>
internal readonly record struct ColumnWater(double Top, double Depth, bool Ice);

/// <summary>
/// Creatures keep out of deep water (#9879 follow-up): a product rule over the water owner's samples,
/// laid on the Engine's collision-derived navigation as its traversal overlay after each publication.
/// A standing cell under water deeper than a creature wades is closed, so no route enters it; one in
/// a ford (shallower, but over the knees) costs more than dry ground. Frozen water is ground.
/// Creatures do not swim: a route across deep water needs a swim edge (an Engine request), so a
/// river with no ford parts a creature from the player.
/// <para>
/// The overlay names exact standing cells, which the Engine refuses unless the published projection
/// has them; only the wet columns are asked (<c>ExplainCollisionNavigationColumn</c>), and what each
/// column holds is kept until the collision or the frame changes. The Engine's step query honours a
/// closed cell; a cell's cost is honoured only by its weighted full-path query.
/// </para>
/// </summary>
internal sealed class WaterAvoidance
{
    /// <summary>Water this deep over a creature's feet (metres) closes the cell: past wading.</summary>
    internal const double WadingDepth = 0.7;

    /// <summary>Water at least this deep, short of wading depth, is a ford: crossable, at a cost.</summary>
    internal const double FordDepth = 0.3;

    /// <summary>What a ford's cell costs against dry ground's one.</summary>
    internal const ulong FordCost = 4;

    private readonly Dictionary<(long X, long Z), CollisionNavigationSample[]> columns = [];
    private object? columnsFor;

    internal int Closed { get; private set; }
    internal int Fords { get; private set; }
    internal int Explained { get; private set; }
    internal string? LastFailure { get; private set; }
    internal double LastMilliseconds { get; private set; }

    /// <summary>
    /// Lays the overlay over the navigation just published in a cell box (local cell columns, inclusive
    /// minimum, exclusive maximum). <paramref name="waterAt"/> gives the water over a column, or null for
    /// none; <paramref name="collision"/> is anything that changes when the collision or the frame does,
    /// so a column's supports are asked again. Returns whether the Engine took the overlay.
    /// </summary>
    internal bool Apply(ISpatialService spatial, SpatialSession session, (long X, long Z) min, (long X, long Z) max,
        Func<long, long, ColumnWater?> waterAt, object collision)
    {
        ArgumentNullException.ThrowIfNull(spatial);
        ArgumentNullException.ThrowIfNull(waterAt);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Equals(columnsFor, collision))
        {
            columns.Clear();
            columnsFor = collision;
        }

        List<NavigationTraversalCell> cells = [];
        int closed = 0, fords = 0;
        for (long z = min.Z; z < max.Z; z++)
        for (long x = min.X; x < max.X; x++)
        {
            if (waterAt(x, z) is not ColumnWater water || water.Ice || water.Depth < FordDepth) continue;
            if (!columns.TryGetValue((x, z), out CollisionNavigationSample[]? samples))
            {
                samples = spatial.ExplainCollisionNavigationColumn(new CollisionNavigationColumnRequest(session, x, z)).Samples.ToArray();
                columns[(x, z)] = samples;
                Explained++;
            }

            HashSet<PlanarNavCell> seen = [];
            foreach (CollisionNavigationSample sample in samples)
            {
                if (sample.Outcome != CollisionNavigationSampleOutcome.Support || !seen.Add(sample.Cell)) continue;
                double depth = water.Top - sample.StandingY;
                if (depth >= WadingDepth)
                {
                    cells.Add(new NavigationTraversalCell(sample.Cell, Allowed: false, TraversalCost: 1));
                    closed++;
                }
                else if (depth >= FordDepth)
                {
                    cells.Add(new NavigationTraversalCell(sample.Cell, Allowed: true, TraversalCost: FordCost));
                    fords++;
                }
            }
        }

        try
        {
            if (cells.Count == 0) spatial.ClearNavigationTraversal(new NavigationTraversalClearRequest(session));
            else spatial.ReplaceNavigationTraversal(new NavigationTraversalReplaceRequest(session, cells.ToArray()));
            (Closed, Fords, LastFailure) = (closed, fords, null);
            LastMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return true;
        }
        catch (EngineCallException refusal)
        {
            // A refused overlay leaves the navigation as published: creatures route as if there were no water.
            (Closed, Fords, LastFailure) = (0, 0, refusal.Message);
            LastMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return false;
        }
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"water closed={Closed} fords={Fords} explained={Explained} cached={columns.Count} lastMs={LastMilliseconds:F2} failure={LastFailure ?? "none"}");
}
