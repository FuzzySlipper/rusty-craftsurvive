using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The body a route is checked for, in the terms the Engine's collision navigation takes: a capsule,
/// the steepest slope it stands on, and how many cells it steps up or down between neighbours.
/// </summary>
internal sealed record NavigationProfile(string Name, double AgentRadius, double AgentHeight, double SlopeDegrees, uint StepCells)
{
    /// <summary>The player's own capsule and limits.</summary>
    internal static readonly NavigationProfile Player = new(
        "player", PlayerConstants.CapsuleRadius, PlayerConstants.StandingHeight, PlayerConstants.MaximumSlopeDegrees, 1U);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Name}(r={AgentRadius:0.##} h={AgentHeight:0.##} slope={SlopeDegrees:0} step={StepCells})");
}

/// <summary>One route the flow promises, and the Engine's answer to it.</summary>
internal sealed record DungeonRoute(string Name, DungeonCell From, DungeonCell To, NavigationPathOutcome Outcome, int PathLength)
{
    internal bool Reached => Outcome == NavigationPathOutcome.Reached;

    public override string ToString() => $"{Name}={Outcome}";
}

/// <summary>Where the Engine first refuses a step the generator's own walk takes, and how.</summary>
internal sealed record RouteHangUp(string Route, int StepIndex, int StepCount, DungeonCell From, DungeonCell To, NavigationPathOutcome Outcome, string Around)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Route} step {StepIndex}/{StepCount} {From}->{To} rise={To.Y - From.Y} {Outcome} [{Around}]");
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
/// <see cref="HangUps"/> follows the walk's route step by step to the first step the Engine refuses.
/// Places are read as the generator reads them, at the nearest standing cell, and asked from the
/// ground actually there: a sculpted floor lies a little above or below its cell's boundary.
/// </summary>
internal static class DungeonRoutes
{
    private const ulong GridId = 2UL;

    /// <summary>A route query may cross the whole space in one step.</summary>
    private const float QueryStepMetres = 4096f;

    private const uint QueryMaxVisited = 1_000_000U;

    /// <summary>The ground under a cell is looked for from this far above its floor, down twice as far.</summary>
    private const float GroundSearchMetres = 1f;

    /// <summary>Publishes navigation over the space for a profile, replacing any before it.</summary>
    internal static CollisionNavigationReplaceReceipt Publish(IEngineContext engine, SpatialSession session, DungeonVolume volume, NavigationProfile profile) =>
        engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            session,
            DungeonCollision.Origin,
            DungeonCollision.Origin + new Vector3(volume.SizeX, volume.SizeY, volume.SizeZ),
            new CollisionNavigationConfig(
                GridId, TerrainConstants.VoxelSize, (uint)TerrainConstants.ChunkEdgeLength, profile.StepCells,
                profile.AgentRadius, profile.AgentHeight, profile.SlopeDegrees,
                (uint)(volume.SizeX * volume.SizeY * volume.SizeZ))));

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
            routes.Add(new DungeonRoute(name, from, to, step.Outcome, step.Path.Length));
        }

        return new DungeonRouteVerdict(profile, routes, published.WalkableCellCount, publishMs,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// For each route the Engine refused, the generator's walk of it and the first step along that
    /// walk the Engine cannot make on its own, with the blocks around it. The walk is over the same
    /// voxel copy <see cref="Check"/> read. Uses the navigation already published.
    /// </summary>
    internal static IReadOnlyList<RouteHangUp> HangUps(IEngineContext engine, SpatialSession session, DungeonVolume walkable, DungeonRouteVerdict verdict)
    {
        List<RouteHangUp> found = [];
        foreach (DungeonRoute route in verdict.Routes.Where(route => !route.Reached))
        {
            IReadOnlyList<DungeonCell> walk = DungeonWalk.Route(walkable, route.From, route.To);
            if (walk.Count == 0)
            {
                found.Add(new RouteHangUp(route.Name, 0, 0, route.From, route.To, route.Outcome, "the walk check has no route either"));
                continue;
            }

            for (int index = 1; index < walk.Count; index++)
            {
                NavigationStepResult step = Query(engine, session, walk[index - 1], walk[index]);
                if (step.Outcome != NavigationPathOutcome.Reached)
                {
                    found.Add(new RouteHangUp(route.Name, index, walk.Count - 1, walk[index - 1], walk[index], step.Outcome, Around(walkable, walk[index - 1], walk[index])));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>Where a body stands in a cell: on the ground under the middle of its column, as collision has it.</summary>
    internal static Vector3 Ground(IEngineContext engine, SpatialSession session, DungeonCell cell)
    {
        Vector3 feet = DungeonCollision.InSession(new Vector3(cell.X + 0.5f, cell.Y, cell.Z + 0.5f));
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            session, feet + new Vector3(0f, GroundSearchMetres, 0f), -Vector3.UnitY, 2d * GroundSearchMetres,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present && !hit.StartSolid ? feet with { Y = hit.Point.Y } : feet;
    }

    private static NavigationStepResult Query(IEngineContext engine, SpatialSession session, DungeonCell from, DungeonCell to) =>
        engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(
            session, Ground(engine, session, from), Ground(engine, session, to), QueryStepMetres, QueryMaxVisited));

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
