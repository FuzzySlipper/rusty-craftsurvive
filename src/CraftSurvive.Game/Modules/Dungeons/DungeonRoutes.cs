using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The body and rules a route is checked with: the Engine's collision-navigation configuration,
/// named so a comparison of several reads plainly.
/// </summary>
internal sealed record NavigationProfile(string Name, CollisionNavigationConfig Config)
{
    /// <summary>
    /// How far a sculpted floor may lie off its cell's boundary and still be stood on from a query
    /// at the cell's floor: dual-contoured rock settles up to about a third of a cell either way.
    /// </summary>
    internal const double SculptedFloorSnapMetres = 0.35d;

    private const ulong GridId = 2UL;

    /// <summary>
    /// How far below the top of the player's jump a ledge the route check accepts must stay: room for
    /// a jump that is not perfectly timed.
    /// </summary>
    internal const float JumpMarginMetres = 0.25f;

    /// <summary>
    /// The highest step the route check accepts: a ledge the player jumps up, short of the jump's
    /// peak by <see cref="JumpMarginMetres"/>. Navigation has no jump edges yet (rusty-engine #9123),
    /// so a jump stands in as a tall step; the player's own controller keeps its step height.
    /// </summary>
    internal static float JumpableStepMetres =>
        (PlayerConstants.JumpSpeed * PlayerConstants.JumpSpeed / (2f * PlayerConstants.Gravity)) - JumpMarginMetres;

    /// <summary>
    /// The player's own body, as the character controller has it, over a dungeon's volume: it walks
    /// off the drops the generator's walk allows and climbs what the player can step or jump up.
    /// </summary>
    internal static NavigationProfile Player(ISpatialService spatial, DungeonVolume volume)
    {
        CollisionNavigationConfig defaults = spatial.DefaultCollisionNavigationConfig();
        CharacterControllerConfig body = PlayerBody.Configure(spatial.DefaultCharacterControllerConfig());
        return new NavigationProfile("player", defaults with
        {
            GridId = GridId,
            CellSize = TerrainConstants.VoxelSize,
            ChunkSize = (uint)TerrainConstants.ChunkEdgeLength,
            MaximumCells = checked((uint)(volume.SizeX * volume.SizeZ)),
            Character = body with { Surface = body.Surface with { MaximumStepHeight = Math.Max(body.Surface.MaximumStepHeight, JumpableStepMetres) } },
            MaximumDrop = DungeonWalk.MaximumDrop * TerrainConstants.VoxelSize,
            VerticalSearchCells = (uint)DungeonWalk.MaximumDrop,
            SnapAbove = SculptedFloorSnapMetres,
            SnapBelow = SculptedFloorSnapMetres,
        });
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Name}(r={Config.Character.Shape.Radius:0.##} h={Config.Character.Shape.StandingHeight:0.##} slope={Angles.ToDegrees(Config.Character.Surface.MaximumSlopeRadians):0} step={Config.Character.Surface.MaximumStepHeight:0.##} drop={Config.MaximumDrop:0.##} snap={Config.SnapBelow:0.##}{(Config.DiagonalNeighbors ? " diagonal" : string.Empty)})");
}

/// <summary>One route the flow promises, and the Engine's answer to it.</summary>
internal sealed record DungeonRoute(string Name, DungeonCell From, DungeonCell To, NavigationPathOutcome Outcome, int PathLength, DungeonCell? Nearest)
{
    internal bool Reached => Outcome == NavigationPathOutcome.Reached;

    public override string ToString() => Reached || Nearest is not DungeonCell nearest
        ? $"{Name}={Outcome}"
        : $"{Name}={Outcome}(got to {nearest.X},{nearest.Y},{nearest.Z})";
}

/// <summary>Where the Engine first refuses a step the generator's own walk takes, and how.</summary>
internal sealed record RouteHangUp(string Route, int StepIndex, int StepCount, DungeonCell From, DungeonCell To, string Why, string Around)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Route} step {StepIndex}/{StepCount} ({From.X},{From.Y},{From.Z})->({To.X},{To.Y},{To.Z}) rise={To.Y - From.Y}: {Why} [{Around}]");
}

/// <summary>The Engine's word on a loaded dungeon: every promised route, what navigation saw and what it cost.</summary>
internal sealed record DungeonRouteVerdict(NavigationProfile Profile, IReadOnlyList<DungeonRoute> Routes, ulong WalkableCells, double PublishMilliseconds, double QueryMilliseconds)
{
    internal bool Walkable => Routes.All(route => route.Reached);

    internal DungeonRoute? FirstRefused => Routes.FirstOrDefault(route => !route.Reached);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"navigation {Profile} walkable={WalkableCells} publishMs={PublishMilliseconds:F0} queryMs={QueryMilliseconds:F0} {string.Join(' ', Routes)}");
}

/// <summary>
/// The dungeon route check, made by the Engine: collision navigation published over the whole
/// loaded space, then every route the flow promises asked of it - from the arrival to the breach,
/// each floor and the loot, and from the loot back. A dungeon is walkable when the Engine reaches
/// them all. The generator's own voxel walk is the cheap filter before this; when the two disagree,
/// <see cref="HangUps"/> follows the walk's route step by step to the first step the Engine refuses
/// and asks the Engine why. Places are read as the generator reads them, at the nearest standing cell.
/// </summary>
internal static class DungeonRoutes
{
    /// <summary>A route query may cross the whole space in one step.</summary>
    private const float QueryStepMetres = 4096f;

    private const uint QueryMaxVisited = 1_000_000U;

    /// <summary>How far, up or down, a column's surface may be from a cell's floor to be that cell's support.</summary>
    private const double SupportNearFloorMetres = 0.5d;

    /// <summary>Publishes navigation over the space for a profile, replacing any before it.</summary>
    internal static CollisionNavigationReplaceReceipt Publish(IEngineContext engine, SpatialSession session, DungeonVolume volume, NavigationProfile profile) =>
        engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            session,
            DungeonCollision.Origin,
            DungeonCollision.Origin + new Vector3(volume.SizeX, volume.SizeY, volume.SizeZ),
            profile.Config));

    /// <summary>The routes a plan's flow promises: there and back.</summary>
    internal static IEnumerable<(string Name, DungeonCell From, DungeonCell To)> Promised(DungeonPlan plan)
    {
        yield return ("arrival->breach", plan.Arrival, plan.Breach);
        for (int floor = 0; floor < plan.FloorAnchors.Count; floor++)
        {
            yield return ($"arrival->floor{floor}", plan.Arrival, plan.FloorAnchors[floor]);
        }

        yield return ("arrival->loot", plan.Arrival, plan.Loot);
        yield return ("loot->arrival", plan.Loot, plan.Arrival);
    }

    /// <summary>
    /// Publishes navigation for a profile and asks every promised route of it. The walkable volume is
    /// a voxel copy of the whole space (a sculpted dungeon's rock re-voxelised), which places the
    /// plan's loosely named places on standing cells.
    /// </summary>
    internal static DungeonRouteVerdict Check(IEngineContext engine, SpatialSession session, DungeonVolume walkable, DungeonPlan plan, NavigationProfile profile)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        CollisionNavigationReplaceReceipt published = Publish(engine, session, walkable, profile);
        double publishMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        HashSet<DungeonCell> standing = DungeonWalk.Reachable(walkable, plan.Arrival);
        List<DungeonRoute> routes = [];
        foreach ((string name, DungeonCell place, DungeonCell otherPlace) in Promised(plan))
        {
            DungeonCell from = DungeonWalk.Nearest(standing, place) ?? place;
            DungeonCell to = DungeonWalk.Nearest(standing, otherPlace) ?? otherPlace;
            NavigationStepResult step = Query(engine, session, from, to);
            routes.Add(new DungeonRoute(name, from, to, step.Outcome, step.Path.Length, step.NearestPresent ? InLayout(step.NearestCell) : null));
        }

        return new DungeonRouteVerdict(profile, routes, published.WalkableCellCount, publishMs,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// For each route the Engine refused, the generator's walk of it and the first step along that
    /// walk the Engine cannot make on its own, with the Engine's reason and the blocks around it. The
    /// walk is over the same voxel copy <see cref="Check"/> read. Uses the navigation already published.
    /// </summary>
    internal static IReadOnlyList<RouteHangUp> HangUps(IEngineContext engine, SpatialSession session, DungeonVolume walkable, DungeonRouteVerdict verdict)
    {
        List<RouteHangUp> found = [];
        foreach (DungeonRoute route in verdict.Routes.Where(route => !route.Reached))
        {
            IReadOnlyList<DungeonCell> walk = DungeonWalk.Route(walkable, route.From, route.To);
            if (walk.Count == 0)
            {
                found.Add(new RouteHangUp(route.Name, 0, 0, route.From, route.To, route.Outcome.ToString(), "the walk check has no route either"));
                continue;
            }

            for (int index = 1; index < walk.Count; index++)
            {
                NavigationStepResult step = Query(engine, session, walk[index - 1], walk[index]);
                if (step.Outcome != NavigationPathOutcome.Reached)
                {
                    found.Add(new RouteHangUp(route.Name, index, walk.Count - 1, walk[index - 1], walk[index],
                        Why(engine, session, walk[index - 1], walk[index], step.Outcome), Around(walkable, walk[index - 1], walk[index])));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The Engine's reason a step is refused: the edge between the two cells' supports as navigation
    /// judges it, or, when a cell has no support, what its column's surface near the floor is instead.
    /// </summary>
    private static string Why(IEngineContext engine, SpatialSession session, DungeonCell from, DungeonCell to, NavigationPathOutcome outcome)
    {
        (PlanarNavCell? fromSupport, string fromColumn) = Support(engine, session, from);
        (PlanarNavCell? toSupport, string toColumn) = Support(engine, session, to);
        if (fromSupport is not PlanarNavCell start || toSupport is not PlanarNavCell end)
        {
            return $"{outcome}; from {fromColumn}; to {toColumn}";
        }

        CollisionNavigationEdgeReadout edge = engine.Spatial.ExplainCollisionNavigationEdge(new CollisionNavigationEdgeRequest(session, start, end));
        return string.Create(CultureInfo.InvariantCulture,
            $"{outcome}; edge {edge.Outcome}{(edge.Admitted ? " (admitted)" : string.Empty)} from {fromColumn} to {toColumn}");
    }

    /// <summary>The support standing for a cell - the column's surface nearest its floor - and how the column reads there.</summary>
    private static (PlanarNavCell? Support, string Reading) Support(IEngineContext engine, SpatialSession session, DungeonCell cell)
    {
        Vector3 floor = DungeonCollision.InSession(new Vector3(cell.X, cell.Y, cell.Z));
        CollisionNavigationColumnResult column = engine.Spatial.ExplainCollisionNavigationColumn(
            new CollisionNavigationColumnRequest(session, (long)floor.X, (long)floor.Z));
        CollisionNavigationSample? nearest = null;
        foreach (CollisionNavigationSample sample in column.Samples.Span)
        {
            if (Math.Abs(sample.SurfaceY - floor.Y) <= SupportNearFloorMetres
                && (nearest is not CollisionNavigationSample best || Math.Abs(sample.SurfaceY - floor.Y) < Math.Abs(best.SurfaceY - floor.Y)))
            {
                nearest = sample;
            }
        }

        if (nearest is not CollisionNavigationSample found)
        {
            return (null, column.BudgetExhausted ? "no surface near the floor (layer budget exhausted)" : "no surface near the floor");
        }

        string reading = string.Create(CultureInfo.InvariantCulture,
            $"{found.Outcome} {found.HitKind} at {found.SurfaceY - floor.Y:+0.00;-0.00} m normal.y {found.NormalY:F2}");
        if (found.Outcome == CollisionNavigationSampleOutcome.CapsuleOverlap)
        {
            Vector3 contact = found.OverlapPoint - floor;
            reading += string.Create(CultureInfo.InvariantCulture, $" against {found.OverlapKind} at ({contact.X:F2},{contact.Y:F2},{contact.Z:F2})");
        }

        return (found.Outcome == CollisionNavigationSampleOutcome.Support ? found.Cell : null, reading);
    }

    private static DungeonCell InLayout(PlanarNavCell cell) => new(
        (int)(cell.X - (long)DungeonCollision.Origin.X),
        (int)(cell.Y - (long)DungeonCollision.Origin.Y),
        (int)(cell.Z - (long)DungeonCollision.Origin.Z));

    private static NavigationStepResult Query(IEngineContext engine, SpatialSession session, DungeonCell from, DungeonCell to) =>
        engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(session, Feet(from), Feet(to), QueryStepMetres, QueryMaxVisited));

    private static Vector3 Feet(DungeonCell cell) => DungeonCollision.InSession(new Vector3(cell.X + 0.5f, cell.Y, cell.Z + 0.5f));

    /// <summary>The two columns of a step, from the floor below the lower to headroom above the higher, as block initials.</summary>
    private static string Around(DungeonVolume volume, DungeonCell from, DungeonCell to)
    {
        int bottom = Math.Min(from.Y, to.Y) - 1;
        int top = Math.Max(from.Y, to.Y) + DungeonWalk.Headroom;
        string Column(DungeonCell cell) => string.Concat(Enumerable.Range(bottom, top - bottom + 1)
            .Select(y => volume.At(cell.X, y, cell.Z).ToString()[0]));
        return $"from {Column(from)} to {Column(to)} (bottom up from y={bottom})";
    }
}
