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
    private const uint MaximumStepCells = 2U;

    /// <summary>The box's cell budget: its square by its height must stay within it.</summary>
    private const uint MaximumCells = 262_144U;

    private const float AgentRadius = 0.3f;
    private const float AgentHeight = 1.8f;
    private const float MaximumSlopeDegrees = 45f;

    /// <summary>How far a creature's standing capsule is held clear of what it stands on.</summary>
    private const float ContactSkin = 0.02f;
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
    private readonly CollisionNavigationConfig config;
    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private Vector3? publishedAround;
    private ulong publishedEditRevision;
    /// <summary>The built pieces' collision revision the publication saw (#9734): built work changes what is walkable.</summary>
    private long publishedStaticRevision = -1;
    private float? publishedBottom;
    private CollisionNavigationReplaceReceipt lastPublished;
    private long publishes;
    private double lastPublishMilliseconds;
    private double worstPublishMilliseconds;
    private double totalPublishMilliseconds;

    internal CreatureNavigation(IEngineContext engine, TerrainWorld terrain, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        config = Configure(engine.Spatial);
        frame.Rebased += _ => publishedAround = null;
    }

    /// <summary>
    /// A creature's body as navigation stands and steps it: a capsule the player's width and a little
    /// taller, stepping up and down the ground's two-block terraces.
    /// </summary>
    private static CollisionNavigationConfig Configure(ISpatialService spatial)
    {
        CollisionNavigationConfig defaults = spatial.DefaultCollisionNavigationConfig();
        CharacterControllerConfig body = defaults.Character;
        const double stepMetres = TerrainConstants.VoxelSize * MaximumStepCells;
        return defaults with
        {
            GridId = GridId,
            CellSize = TerrainConstants.VoxelSize,
            ChunkSize = ChunkSize,
            MaximumCells = MaximumCells,
            Character = body with
            {
                Shape = body.Shape with { Radius = AgentRadius, StandingHeight = AgentHeight, ContactSkin = ContactSkin },
                Surface = body.Surface with
                {
                    MaximumStepHeight = (float)stepMetres,
                    MaximumSlopeRadians = Angles.ToRadians(MaximumSlopeDegrees),
                },
            },
            MaximumDrop = stepMetres,
            VerticalSearchCells = MaximumStepCells,
            SnapAbove = FeetSnapMetres,
            SnapBelow = FeetSnapMetres,
        };
    }

    /// <summary>Publishes navigation around the player if what was published no longer covers them or the world.</summary>
    internal void EnsurePublished(Vector3 playerFeetWorld, bool force = false)
    {
        bool stale = force
            || publishedAround is not Vector3 centre
            || Vector2.Distance(new Vector2(centre.X, centre.Z), new Vector2(playerFeetWorld.X, playerFeetWorld.Z)) > RepublishDistanceMetres
            || publishedEditRevision != terrain.EditRevision
            || publishedStaticRevision != terrain.StaticCollisionRevision;
        if (!stale)
        {
            return;
        }

        Vector3 local = frame.ToLocal(playerFeetWorld);
        float bottom = publishedBottom is float kept
            && local.Y - kept >= DepthBelow && local.Y - kept <= DepthBelow + VerticalSlackMetres
                ? kept
                : MathF.Floor(local.Y) - DepthBelow - (VerticalSlackMetres / 2f);
        const float cell = (float)TerrainConstants.VoxelSize;
        float cellX = MathF.Floor(local.X / cell) * cell;
        float cellZ = MathF.Floor(local.Z / cell) * cell;
        long started = Stopwatch.GetTimestamp();
        lastPublished = engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            terrain.Session,
            new Vector3(cellX - HalfExtent, bottom, cellZ - HalfExtent),
            new Vector3(cellX + HalfExtent, bottom + DepthBelow + HeightAbove + VerticalSlackMetres, cellZ + HalfExtent),
            config));
        lastPublishMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        worstPublishMilliseconds = Math.Max(worstPublishMilliseconds, lastPublishMilliseconds);
        totalPublishMilliseconds += lastPublishMilliseconds;
        publishes++;
        publishedAround = playerFeetWorld;
        publishedBottom = bottom;
        publishedEditRevision = terrain.EditRevision;
        publishedStaticRevision = terrain.StaticCollisionRevision;
    }

    /// <summary>One bounded step of the route from a creature's feet to the player's, in world coordinates.</summary>
    internal NavigationStepResult Step(Vector3 creatureFeetWorld, Vector3 playerFeetWorld, uint maximumVisitedCells = MaximumVisitedCells)
    {
        long started = Stopwatch.GetTimestamp();
        NavigationStepResult result = engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(
            terrain.Session,
            frame.ToLocal(creatureFeetWorld),
            frame.ToLocal(playerFeetWorld),
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
        const double cell = TerrainConstants.VoxelSize;
        Vector2[] centres = new Vector2[path.Length];
        for (int i = 0; i < path.Length; i++)
        {
            Vector3 world = frame.ToWorld(new Vector3((float)((path[i].X + 0.5) * cell), 0f, (float)((path[i].Z + 0.5) * cell)));
            centres[i] = new Vector2(world.X, world.Z);
        }
        return centres;
    }

    /// <summary>How many navigation queries have run, for the routing readout.</summary>
    internal long Queries { get; private set; }
    internal long NoPathQueries { get; private set; }
    private double queryMilliseconds, worstQueryMilliseconds;

    internal string QueryReadout() => string.Create(CultureInfo.InvariantCulture,
        $"queries={Queries} failed={NoPathQueries} queryMeanMs={(Queries == 0 ? 0 : queryMilliseconds / Queries):F2} queryWorstMs={worstQueryMilliseconds:F2}");

    internal void ResetQueries() { Queries = NoPathQueries = 0; queryMilliseconds = worstQueryMilliseconds = 0; }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"navigation publishes={publishes} walkable={lastPublished.WalkableCellCount} derived={lastPublished.DerivedColumnCount} reused={lastPublished.ReusedColumnCount} lastMs={lastPublishMilliseconds:F2} worstMs={worstPublishMilliseconds:F2} meanMs={(publishes == 0 ? 0 : totalPublishMilliseconds / publishes):F2}");

    /// <summary>Publishes around the player now, whatever is stale, and reports what that cost.</summary>
    internal string Publish(Vector3 playerFeetWorld)
    {
        EnsurePublished(playerFeetWorld, force: true);
        return Readout();
    }
}
