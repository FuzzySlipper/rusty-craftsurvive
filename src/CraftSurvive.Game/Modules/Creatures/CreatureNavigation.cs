using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The route creatures take to the player. It publishes the Engine's collision-derived navigation
/// over a box around the player - only while something is pursuing, and again when the player has
/// moved far from the box, the world origin has moved, or the player's edits have changed what is
/// solid - and asks the Engine for the next waypoint from a creature's feet to the player's. Both
/// run in the session's local frame, as the Engine's world-aligned grid requires. The Engine keeps
/// the previous publication and re-derives only the columns that are new to the box or near
/// changed collision, provided the box's vertical range is unchanged - so the range stays put
/// while the player's feet stay within <see cref="VerticalSlackMetres"/> of where it was set. Each publication is timed and its derived and reused columns
/// are kept, because its cost is what bounds how often it can run.
/// </summary>
internal sealed class CreatureNavigation
{
    private const ulong GridId = 1UL;
    private const uint ChunkSize = 16U;

    /// <summary>How high a creature steps: the ground's own terraces are up to two blocks.</summary>
    private const uint MaximumStepCells = CreatureBody.MaximumStepCells;

    /// <summary>How far a creature steps up or down in one move, metres: what its standing height follows.</summary>
    internal const double ClimbMetres = CreatureBody.StepMetres;

    /// <summary>The box's cell budget: its square by its height must stay within it.</summary>
    private const uint MaximumCells = 262_144U;

    private const uint MaximumVisitedCells = 4_096U;

    /// <summary>
    /// How far a creature's feet may lie off the navigation's support and still stand on it. A creature
    /// stands on the generator's column height, while dual-contoured ground is reconstructed up to
    /// half a voxel either side of it; the Engine's default snap (about 0.1 m) misses that, and the
    /// creature reads as standing nowhere (StartNotWalkable).
    /// </summary>
    private const double FeetSnapMetres = 0.6;

    /// <summary>
    /// Half the width of the published box. The box - this square by <see cref="DepthBelow"/>,
    /// <see cref="HeightAbove"/> and <see cref="VerticalSlackMetres"/> - must stay within
    /// <see cref="MaximumCells"/>, or the Engine's grid does not cover it.
    /// </summary>
    private const float HalfExtent = 32f;

    /// <summary>How far from the player a creature can be routed.</summary>
    internal const float ReachMetres = HalfExtent;

    /// <summary>How far the box reaches below and above the player's feet: the ground's relief across the box.</summary>
    private const float DepthBelow = 16f;
    private const float HeightAbove = 16f;

    /// <summary>
    /// How far the player's feet may rise or fall before the box's vertical range moves. A
    /// publication over a different vertical range re-derives the whole box, so the range is set
    /// with the feet in the middle of this band and kept while they stay in it; the box is this
    /// much taller so it still spans <see cref="DepthBelow"/> and <see cref="HeightAbove"/>.
    /// </summary>
    private const float VerticalSlackMetres = 16f;

    /// <summary>How far one evaluated step may propose to move.</summary>
    private const float MaximumStepMetres = 4f;

    /// <summary>How far the player may move from the box's centre before it is published again.</summary>
    private const float RepublishDistanceMetres = 12f;

    private readonly IEngineContext engine;
    private CollisionNavigationConfig config;
    /// <summary>The space it routes in (#10876): its session, frame, collision and water.</summary>
    private readonly CreatureSpace space;
    /// <summary>Deep water closed to creatures, laid over each publication (#9879 follow-up).</summary>
    private readonly WaterAvoidance water = new();
    /// <summary>The water over each world column as last sampled, kept until the water's stage moves on.</summary>
    private readonly Dictionary<(long X, long Z), (double Top, double Depth, bool Ice)?> waterSamples = [];
    private long sampledWaterRevision = -1;

    /// <summary>Kept water samples past this many are dropped together (the box moves with the player).</summary>
    private const int MostWaterSamples = 65_536;
    private Vector3? publishedAround;
    /// <summary>The space's collision revision the publication saw (#9734: built work and edits change what is walkable).</summary>
    private object? publishedCollision;
    private float? publishedBottom;
    private CollisionNavigationReplaceReceipt lastPublished;
    private long publishes;
    private double lastPublishMilliseconds;
    private double worstPublishMilliseconds;
    private double totalPublishMilliseconds;

    internal CreatureNavigation(IEngineContext engine, CreatureSpace space)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.space = space ?? throw new ArgumentNullException(nameof(space));
        config = Configure(engine.Spatial);
        space.Rebased += () => publishedAround = null;
    }

    /// <summary>The space it routes in.</summary>
    internal CreatureSpace Space => space;

    /// <summary>
    /// A creature's body as navigation stands and steps it: a capsule the player's width and a little
    /// taller, stepping up and down the ground's two-block terraces.
    /// </summary>
    private static CollisionNavigationConfig Configure(ISpatialService spatial)
    {
        CollisionNavigationConfig defaults = spatial.DefaultCollisionNavigationConfig();
        return defaults with
        {
            GridId = GridId,
            CellSize = TerrainConstants.VoxelSize,
            ChunkSize = ChunkSize,
            MaximumCells = MaximumCells,
            // Routed with the very capsule its body steps as (#9737).
            Character = CreatureBody.Capsule(defaults.Character),
            MaximumDrop = CreatureBody.StepMetres,
            VerticalSearchCells = MaximumStepCells,
            SnapAbove = FeetSnapMetres,
            SnapBelow = FeetSnapMetres,
        };
    }

    /// <summary>
    /// Routes for a larger body from now on (#10615). A session holds one navigation projection, so it
    /// is the largest body pursuing: routes that fit it fit a smaller one. The Engine wants a cell at
    /// least the body's diameter, so a body wider than a voxel is routed on whole-metre cells that fit it.
    /// </summary>
    internal void Fit(CharacterControllerConfig character)
    {
        CharacterShapeConfig held = config.Character.Shape;
        (float radius, float height, double cell) = HunterRules.Envelope(held.Radius, held.StandingHeight, character.Shape.Radius, character.Shape.StandingHeight, TerrainConstants.VoxelSize);
        if (radius == held.Radius && height == held.StandingHeight) return;
        config = config with
        {
            Character = CreatureBody.Capsule(engine.Spatial.DefaultCharacterControllerConfig(), radius, height),
            CellSize = cell,
            VerticalSearchCells = (uint)Math.Max(1, Math.Ceiling(CreatureBody.StepMetres / cell)),
        };
        publishedAround = null;
        publishedBottom = null;
    }

    /// <summary>Publishes navigation around the player if what was published no longer covers them or the world.</summary>
    internal void EnsurePublished(Vector3 playerFeetWorld, bool force = false)
    {
        bool stale = force
            || publishedAround is not Vector3 centre
            || Vector2.Distance(new Vector2(centre.X, centre.Z), new Vector2(playerFeetWorld.X, playerFeetWorld.Z)) > RepublishDistanceMetres
            || !Equals(publishedCollision, space.CollisionRevision);
        if (!stale)
        {
            return;
        }

        Vector3 local = space.ToLocal(playerFeetWorld);
        float bottom = publishedBottom is float kept
            && local.Y - kept >= DepthBelow && local.Y - kept <= DepthBelow + VerticalSlackMetres
                ? kept
                : MathF.Floor(local.Y) - DepthBelow - (VerticalSlackMetres / 2f);
        float cell = (float)config.CellSize;
        float cellX = MathF.Floor(local.X / cell) * cell;
        float cellZ = MathF.Floor(local.Z / cell) * cell;
        long started = Stopwatch.GetTimestamp();
        publishedMinimum = new Vector3(cellX - HalfExtent, bottom, cellZ - HalfExtent);
        publishedMaximum = new Vector3(cellX + HalfExtent, bottom + DepthBelow + HeightAbove + VerticalSlackMetres, cellZ + HalfExtent);
        lastPublished = engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            space.Session, publishedMinimum, publishedMaximum, config));
        // Creatures keep out of deep water: the overlay goes on after every publication, which clears it.
        (long X, long Z) minCell = ((long)MathF.Floor((cellX - HalfExtent) / cell), (long)MathF.Floor((cellZ - HalfExtent) / cell));
        (long X, long Z) maxCell = ((long)MathF.Floor((cellX + HalfExtent) / cell), (long)MathF.Floor((cellZ + HalfExtent) / cell));
        water.Apply(engine.Spatial, space.Session, minCell, maxCell, WaterAt, (space.CollisionRevision, bottom));
        lastPublishMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        worstPublishMilliseconds = Math.Max(worstPublishMilliseconds, lastPublishMilliseconds);
        totalPublishMilliseconds += lastPublishMilliseconds;
        publishes++;
        publishedAround = playerFeetWorld;
        publishedBottom = bottom;
        publishedCollision = space.CollisionRevision;
    }

    /// <summary>The water over a local cell column, in the local frame, or null where it is dry.</summary>
    private ColumnWater? WaterAt(long x, long z)
    {
        double cell = config.CellSize;
        Vector3 world = space.ToWorld(new Vector3((float)((x + 0.5) * cell), 0f, (float)((z + 0.5) * cell)));
        if (sampledWaterRevision != space.WaterRevision || waterSamples.Count > MostWaterSamples)
        {
            waterSamples.Clear();
            sampledWaterRevision = space.WaterRevision;
        }

        (long X, long Z) column = ((long)Math.Floor(world.X), (long)Math.Floor(world.Z));
        if (!waterSamples.TryGetValue(column, out (double Top, double Depth, bool Ice)? sample)) waterSamples[column] = sample = space.WaterAt(column.X, column.Z);
        if (sample is not (double top, double depth, bool ice)) return null;
        return new ColumnWater(space.ToLocal(world.X, top, world.Z).Y, depth, ice);
    }

    /// <summary>One bounded step of the route from a creature's feet to the player's, in world coordinates.</summary>
    internal NavigationStepResult Step(Vector3 creatureFeetWorld, Vector3 playerFeetWorld, uint maximumVisitedCells = MaximumVisitedCells)
    {
        long started = Stopwatch.GetTimestamp();
        NavigationStepResult result = engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(
            space.Session,
            space.ToLocal(creatureFeetWorld),
            space.ToLocal(playerFeetWorld),
            MaximumStepMetres,
            maximumVisitedCells));
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Queries++;
        queryMilliseconds += milliseconds;
        worstQueryMilliseconds = Math.Max(worstQueryMilliseconds, milliseconds);
        if (result.Outcome != NavigationPathOutcome.Reached) NoPathQueries++;
        return result;
    }

    /// <summary>How many times navigation has been published: a path asked of an earlier one is stale.</summary>
    internal long Publishes => publishes;

    /// <summary>A path's cells as world X/Z centres, for a mover to walk.</summary>
    internal Vector2[] CellCentres(ReadOnlySpan<PlanarNavCell> path)
    {
        double cell = config.CellSize;
        Vector2[] centres = new Vector2[path.Length];
        for (int i = 0; i < path.Length; i++)
        {
            Vector3 world = space.ToWorld(new Vector3((float)((path[i].X + 0.5) * cell), 0f, (float)((path[i].Z + 0.5) * cell)));
            centres[i] = new Vector2(world.X, world.Z);
        }
        return centres;
    }

    /// <summary>The box last published, in the session's local frame.</summary>
    private Vector3 publishedMinimum, publishedMaximum;

    /// <summary>A support this near the feet, up or down, counts as theirs; others are searched for this many cells about them.</summary>
    private const double DiagnoseLevelMetres = 1.5;
    private const int DiagnoseRingCells = 4;

    /// <summary>
    /// Why a point (a creature's feet, a home's floor or the player's) is or is not walkable on the navigation published now
    /// (#10924): outside the published box, or how its column reads at that height - a support it stands on, a standing
    /// capsule overlapping collision (and what), too steep, a higher support holding the cell, nothing sampled - and the
    /// nearest support within a few cells on about the same level.
    /// </summary>
    internal string Diagnose(Vector3 feetWorld)
    {
        if (publishedAround is null) return "no navigation published";
        Vector3 local = space.ToLocal(feetWorld);
        double cell = config.CellSize;
        float outX = Math.Max(publishedMinimum.X - local.X, local.X - publishedMaximum.X), outZ = Math.Max(publishedMinimum.Z - local.Z, local.Z - publishedMaximum.Z);
        if (outX > 0 || outZ > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"outside the published box by {Math.Max(outX, outZ):F1} m across (it reaches {HalfExtent:F0} m from where the player stood)");
        }

        if (local.Y < publishedMinimum.Y || local.Y > publishedMaximum.Y)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"outside the published box by {(local.Y < publishedMinimum.Y ? publishedMinimum.Y - local.Y : local.Y - publishedMaximum.Y):F1} m {(local.Y < publishedMinimum.Y ? "below" : "above")}");
        }

        long cx = (long)Math.Floor(local.X / cell), cz = (long)Math.Floor(local.Z / cell);
        CollisionNavigationColumnResult column = engine.Spatial.ExplainCollisionNavigationColumn(new CollisionNavigationColumnRequest(space.Session, cx, cz, GridId));
        CollisionNavigationSample[] samples = column.Samples.ToArray();
        string here;
        if (samples.Any(s => s.Outcome == CollisionNavigationSampleOutcome.Support && Math.Abs(s.StandingY - local.Y) <= FeetSnapMetres))
        {
            here = "its column supports it there (walkable)";
        }
        else if (samples.Where(s => Math.Abs(s.StandingY - local.Y) <= DiagnoseLevelMetres).OrderBy(s => Math.Abs(s.StandingY - local.Y)).FirstOrDefault() is { } near
            && Math.Abs(near.StandingY - local.Y) <= DiagnoseLevelMetres)
        {
            here = string.Create(CultureInfo.InvariantCulture,
                $"its column reads {near.Outcome}{(near.Outcome == CollisionNavigationSampleOutcome.CapsuleOverlap ? $" ({near.OverlapKind})" : "")} {near.StandingY - local.Y:+0.00;-0.00} m from it");
        }
        else
        {
            here = samples.Length == 0
                ? $"its column samples nothing in the box's height{(column.BudgetExhausted ? " (budget exhausted)" : "")}"
                : string.Create(CultureInfo.InvariantCulture, $"its column has no surface within {DiagnoseLevelMetres:F1} m (nearest {samples.Min(s => Math.Abs(s.StandingY - local.Y)):F1} m)");
        }

        (double Distance, Vector3 At)? nearest = null;
        for (long dz = -DiagnoseRingCells; dz <= DiagnoseRingCells; dz++)
        for (long dx = -DiagnoseRingCells; dx <= DiagnoseRingCells; dx++)
        {
            if (dx == 0 && dz == 0) continue;
            foreach (CollisionNavigationSample s in engine.Spatial.ExplainCollisionNavigationColumn(new CollisionNavigationColumnRequest(space.Session, cx + dx, cz + dz, GridId)).Samples.Span)
            {
                if (s.Outcome != CollisionNavigationSampleOutcome.Support || Math.Abs(s.StandingY - local.Y) > DiagnoseLevelMetres) continue;
                Vector3 at = new((float)((cx + dx + 0.5) * cell), (float)s.StandingY, (float)((cz + dz + 0.5) * cell));
                double distance = Vector3.Distance(at, local);
                if (nearest is null || distance < nearest.Value.Distance) nearest = (distance, at);
            }
        }

        string beside = nearest is (double d, Vector3 support)
            ? string.Create(CultureInfo.InvariantCulture, $"nearest support {d:F1} m away at {space.ToWorld(support).X:F1},{space.ToWorld(support).Y:F1},{space.ToWorld(support).Z:F1}")
            : $"no support within {DiagnoseRingCells} cells on its level";
        return $"{here}; {beside}";
    }

    /// <summary>
    /// How near a route from one point gets to another (#10924): the outcome, and for a search that ran out (no path, or its
    /// budget spent) the cell it reached nearest the goal - how far from the goal across the ground and how far above or
    /// below it - which says whether the floors are joined by steps a body can take.
    /// </summary>
    internal string Reach(Vector3 fromWorld, Vector3 toWorld)
    {
        NavigationStepResult step = Step(fromWorld, toWorld);
        if (step.Outcome == NavigationPathOutcome.Reached) return "a route reaches it";
        if (!step.NearestPresent) return $"{step.Outcome}: no cell visited";
        double cell = config.CellSize;
        PlanarNavCell nearest = step.NearestCell;
        Vector3 at = space.ToWorld(new Vector3((float)((nearest.X + 0.5) * cell), (float)(nearest.Y * cell), (float)((nearest.Z + 0.5) * cell)));
        float across = Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(toWorld.X, toWorld.Z));
        return string.Create(CultureInfo.InvariantCulture,
            $"{step.Outcome}: its search got within {across:F1} m of the player across the ground, {at.Y - toWorld.Y:+0.0;-0.0} m from their feet, at {at.X:F1},{at.Y:F1},{at.Z:F1}");
    }

    /// <summary>How many navigation queries have run, for the routing readout.</summary>
    internal long Queries { get; private set; }
    internal long NoPathQueries { get; private set; }
    private double queryMilliseconds, worstQueryMilliseconds;

    internal string QueryReadout() => string.Create(CultureInfo.InvariantCulture,
        $"queries={Queries} failed={NoPathQueries} queryMeanMs={(Queries == 0 ? 0 : queryMilliseconds / Queries):F2} queryWorstMs={worstQueryMilliseconds:F2}");

    internal void ResetQueries() { Queries = NoPathQueries = 0; queryMilliseconds = worstQueryMilliseconds = 0; }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"navigation cell={config.CellSize:F1} radius={config.Character.Shape.Radius:F2} height={config.Character.Shape.StandingHeight:F2} publishes={publishes} walkable={lastPublished.WalkableCellCount} derived={lastPublished.DerivedColumnCount} reused={lastPublished.ReusedColumnCount} lastMs={lastPublishMilliseconds:F2} worstMs={worstPublishMilliseconds:F2} meanMs={(publishes == 0 ? 0 : totalPublishMilliseconds / publishes):F2} {water.Readout()}");

    /// <summary>
    /// What publishing this box costs for another body (#10567: wildlife's size classes). A session
    /// holds one collision-navigation projection, so this replaces the creatures' for the measure and
    /// leaves the box stale: the next pursuit publishes theirs again.
    /// </summary>
    internal string MeasureBody(Vector3 playerFeetWorld, CharacterControllerConfig character)
    {
        Vector3 local = space.ToLocal(playerFeetWorld);
        const float cell = (float)TerrainConstants.VoxelSize;
        float cellX = MathF.Floor(local.X / cell) * cell;
        float cellZ = MathF.Floor(local.Z / cell) * cell;
        float bottom = MathF.Floor(local.Y) - DepthBelow - (VerticalSlackMetres / 2f);
        // The Engine wants a cell at least the agent's diameter: a body wider than a voxel is measured on whole-metre cells that fit it.
        double cellSize = Math.Max(TerrainConstants.VoxelSize, Math.Ceiling(2 * character.Shape.Radius));
        long started = Stopwatch.GetTimestamp();
        CollisionNavigationReplaceReceipt receipt = engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            space.Session,
            new Vector3(cellX - HalfExtent, bottom, cellZ - HalfExtent),
            new Vector3(cellX + HalfExtent, bottom + DepthBelow + HeightAbove + VerticalSlackMetres, cellZ + HalfExtent),
            config with { Character = character, CellSize = cellSize }));
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        publishedAround = null;
        publishedBottom = null;
        return string.Create(CultureInfo.InvariantCulture,
            $"cell={cellSize:F0}m ms={milliseconds:F2} walkable={receipt.WalkableCellCount} derived={receipt.DerivedColumnCount} reused={receipt.ReusedColumnCount}");
    }

    /// <summary>Publishes around the player now, whatever is stale, and reports what that cost.</summary>
    internal string Publish(Vector3 playerFeetWorld)
    {
        EnsurePublished(playerFeetWorld, force: true);
        return Readout();
    }
}
