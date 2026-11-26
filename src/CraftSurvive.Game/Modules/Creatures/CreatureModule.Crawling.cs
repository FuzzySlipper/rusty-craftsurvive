using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Wildlife;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The wall runner on walls and ceilings (#10975), as settled with Patch on the wiki's Ruins wildlife page ("The wall runner
/// on walls and ceilings"). A crawling hunter's body is the Engine's surface crawler, a ball whose up follows the floor, wall
/// or ceiling it holds, routed over one surface grid per space; its behaviour states are the other hunters', and its
/// position across the floor and its feet (the surface it holds) are what the shared rules read. The rules are
/// <see cref="CrawlRules"/>.
/// </summary>
internal sealed partial class CreatureModule
{
    /// <summary>One crawling hunter's body: the ball's centre in its space, what the Engine carries between steps, and its fight.</summary>
    private sealed class Crawler
    {
        internal Vector3 Local { get; set; }
        internal CrawlerMotion Motion { get; set; }
        internal Quaternion Rotation { get; set; } = Quaternion.Identity;
        internal CrawlHold Hold { get; set; }
        internal CrawlPhase Phase { get; set; }
        internal double DazedLeft { get; set; }
        internal double RetreatLeft { get; set; }

        /// <summary>The fall under way is a drop onto the player, whose landing strikes.</summary>
        internal bool Dropping { get; set; }

        /// <summary>Where it rests or runs back to, in its space; null where it has none.</summary>
        internal Vector3? Perch { get; set; }

        internal SurfaceWaypoint[] Path { get; set; } = [];
        internal int Next { get; set; }
        internal Vector3? PathGoal { get; set; }
        internal long PathAt { get; set; } = long.MinValue;
        internal string PathOutcome { get; set; } = "none";
        internal ulong Sequence { get; set; }
        internal long Drops { get; set; }
        internal long DropsLanded { get; set; }
        internal long KnockedLoose { get; set; }

        /// <summary>Where it last moved from, and how long it has stayed near there while it means to go somewhere.</summary>
        internal Vector3 Anchor { get; set; }

        internal double StuckSeconds { get; set; }
        internal long Unstuck { get; set; }
    }

    /// <summary>One space's surface grid (#10975): where it was centred, and what its last derivation installed.</summary>
    private sealed class SurfaceGrid
    {
        internal Vector3 Centre { get; set; }
        internal bool Installed { get; set; }
        internal bool Pending { get; set; }
        internal ulong Places { get; set; }
        internal ulong Edges { get; set; }
        internal double DeriveMilliseconds { get; set; }
        internal int Publications { get; set; }
        internal int Refusals { get; set; }
        internal long RequestedAt { get; set; }
    }

    /// <summary>A space's surface grid has its own id, clear of the planar grids, which count up from <see cref="FirstGridId"/>.</summary>
    private const ulong SurfaceGridId = 9001UL;

    /// <summary>The surface grid covers this far each way across from where the player stood, and this far below and above.</summary>
    private const float SurfaceHalfExtent = 32f, SurfaceBelow = 12f, SurfaceAbove = 20f;

    /// <summary>A surface grid is derived again once the player stands this far from where it was centred.</summary>
    private const float SurfaceRepublishMetres = 12f;

    /// <summary>A crawler asks for a new path this often, or sooner when its goal has moved this far or its path is used up.</summary>
    private const double CrawlRepathSeconds = 0.75;

    private const float CrawlRepathMetres = 1f;

    /// <summary>A waypoint is passed within this far of it.</summary>
    private const float CrawlArrivedMetres = 0.35f;

    /// <summary>The most places a surface path may visit.</summary>
    private const uint CrawlMaximumVisited = 20_000;

    /// <summary>A crawler searching walks at this share of its hunting pace, as a searching hunter does.</summary>
    private const float CrawlSearchShare = 0.5f;

    /// <summary>
    /// A crawler that comes no nearer its waypoint for this long (wedged in a corner between two walls) takes the next one; one
    /// stuck this long lets go and drops to the floor.
    /// </summary>
    private const double CrawlSkipSeconds = 1.0, CrawlLetGoSeconds = 2.5;

    /// <summary>A crawler that has moved this far from where it was has not stalled.</summary>
    private const float CrawlMovedMetres = 0.3f;

    /// <summary>A crawler runs back toward a perch this far from the player, on the ceiling over it.</summary>
    private const float CrawlRetreatMetres = 4f;

    private readonly Dictionary<int, Crawler> crawlers = [];
    private readonly Dictionary<CreatureSpace, SurfaceGrid> surfaceGrids = [];
    private long crawlSteps, crawlRefused, crawlTicks;

    /// <summary>Whether a creature crawls: a hunter whose kind runs on walls and ceilings.</summary>
    private bool Crawls(Creature creature) => hunters.TryGetValue(creature.Id, out Hunter? hunter) && hunter.Kind.Hunt!.Crawls is not null;

    private CrawlerConfig CrawlerConfigOf(WildlifeCrawling crawling, double speed) =>
        engine.Spatial.DefaultCrawlerConfig() with { Radius = (float)crawling.RadiusMetres, Speed = (float)Math.Max(0.5, speed) };

    /// <summary>A crawler's body, made the first time it moves: on the ceiling over it if it rests under one, else where it stands.</summary>
    private Crawler CrawlerOf(Creature creature, Hunter hunter, WildlifeCrawling crawling)
    {
        if (crawlers.TryGetValue(creature.Id, out Crawler? found)) return found;
        CreatureSpace space = SpaceOf(creature);
        float feet = StandingAt(creature);
        float radius = (float)crawling.RadiusMetres;
        Crawler made = new() { Local = space.ToLocal(creature.Position.X, feet + radius, creature.Position.Y), Hold = CrawlHold.Floor };
        // A resting wall runner clings overhead (#10975): placed at rest, it starts on the ceiling over where it was put.
        if (creature.Behavior.State == CreatureState.Resting && CeilingOver(creature) is float ceiling)
        {
            made.Local = space.ToLocal(creature.Position.X, feet + ceiling - radius, creature.Position.Y);
            made.Motion = new CrawlerMotion(true, -Vector3.UnitY, Vector3.UnitZ, Vector3.Zero, 0);
            made.Hold = CrawlHold.Ceiling;
            made.Perch = made.Local;
        }

        crawlers[creature.Id] = made;
        hunter.Member.SurfaceFeet = null;
        return made;
    }

    /// <summary>
    /// A crawling hunter's move (#10975), in place of the upright body's: it falls while it falls, lies dazed, drops when it is
    /// over the player, and otherwise runs its surface path toward where it is going. Returns where it now stands across the
    /// floor; its feet are the surface it holds.
    /// </summary>
    private Vector2 CrawlBody(Creature creature, Vector2 waypoint, double travel, double seconds)
    {
        if (!hunters.TryGetValue(creature.Id, out Hunter? hunter) || hunter.Kind.Hunt!.Crawls is not WildlifeCrawling crawling) return creature.Position;
        long started = Stopwatch.GetTimestamp();
        CreatureSpace space = SpaceOf(creature);
        Crawler crawler = CrawlerOf(creature, hunter, crawling);
        Vector3 playerFeet = player.WorldFeetPosition;
        Vector3 playerLocal = space.ToLocal(playerFeet);
        CreatureState state = creature.Behavior.State;
        bool hunting = state is CreatureState.Pursuing or CreatureState.Attacking;
        crawler.RetreatLeft = Math.Max(0, crawler.RetreatLeft - seconds);
        if (crawler.Phase == CrawlPhase.Dazed)
        {
            crawler.DazedLeft -= seconds;
            if (crawler.DazedLeft <= 0)
            {
                crawler.Phase = CrawlPhase.Clinging;
                StartRetreat(creature, crawler, crawling, playerFeet);
            }
        }

        double planar = CreatureSimulation.PlanarDistance(creature.Position, playerFeet);
        if (CrawlRules.Drops(crawling, crawler.Phase, crawler.Hold, hunting, creature.Behavior.CanAttack(step, creature.Kind.Tuning), crawler.RetreatLeft > 0, planar))
        {
            crawler.Phase = CrawlPhase.Falling;
            crawler.Dropping = true;
            crawler.Drops++;
            creature.Behavior = creature.Behavior.AfterAttack(step);
            lastEvent = $"wall runner {creature.Id} drops from the ceiling";
        }

        Vector3 direction = Vector3.Zero;
        bool cling = crawler.Phase != CrawlPhase.Falling;
        float pace = (float)creature.Kind.PursueSpeedMetresPerSecond;
        if (crawler.Phase == CrawlPhase.Clinging && !hunter.Holding)
        {
            Vector3? goal = crawler.RetreatLeft > 0 && crawler.Perch is Vector3 perch ? perch
                : hunting ? CrawlRules.Goal(crawling, playerLocal, CeilingAt(space, playerFeet))
                : state == CreatureState.Searching && creature.LastSeen is Vector3 seen ? space.ToLocal(seen) + new Vector3(0, (float)crawling.RadiusMetres, 0)
                : null;
            if (state == CreatureState.Searching) pace *= CrawlSearchShare;
            bool letGo = false;
            if (goal is Vector3 to) (direction, letGo) = SteerCrawler(space, crawler, to, seconds);
            if (letGo || crawler.StuckSeconds >= CrawlLetGoSeconds)
            {
                // Wedged: it lets go, drops to the floor and goes on from there.
                crawler.Phase = CrawlPhase.Falling;
                crawler.Dropping = false;
                crawler.StuckSeconds = 0;
                crawler.Unstuck++;
                crawler.PathAt = long.MinValue;
                cling = false;
                direction = Vector3.Zero;
                lastEvent = letGo ? $"wall runner {creature.Id} lets go to the floor below it" : $"wall runner {creature.Id} was wedged and let go";
            }
        }

        CrawlerConfig config = CrawlerConfigOf(crawling, pace);
        CrawlerStepReceipt receipt;
        try
        {
            receipt = engine.Spatial.ProposeCrawlerStep(new CrawlerStepRequest(space.Session, crawler.Local, crawler.Motion, config,
                new CrawlerCommand(direction, cling, (float)Math.Clamp(seconds, 0.001, 1.0), ++crawler.Sequence)));
        }
        catch (EngineCallException refusal)
        {
            // Lodged too deep to push out (an edit filled its space), or a step the Engine will not take: it stays where it was.
            crawlRefused++;
            lastEvent = $"wall runner {creature.Id}'s step was refused: {refusal.Message}";
            return creature.Position;
        }

        crawlSteps++;
        crawler.Local = receipt.Position;
        crawler.Motion = receipt.Motion;
        crawler.Rotation = receipt.Rotation == default ? Quaternion.Identity : receipt.Rotation;
        crawler.Hold = receipt.Surface switch
        {
            CrawlerSurface.Floor => CrawlHold.Floor,
            CrawlerSurface.Wall => CrawlHold.Wall,
            CrawlerSurface.Ceiling => CrawlHold.Ceiling,
            _ => CrawlHold.None,
        };
        if (crawler.Phase == CrawlPhase.Clinging && receipt.Transition == CrawlerTransition.Lost) crawler.Phase = CrawlPhase.Falling;
        if (crawler.Phase == CrawlPhase.Falling && receipt.Transition == CrawlerTransition.Landed) Landed(creature, hunter, crawler, crawling, playerFeet);

        Vector3 world = space.ToWorld(crawler.Local);
        // Its feet are the surface it holds: the floor under it, the wall beside it, the ceiling over it.
        Vector3 contact = space.ToWorld(crawler.Local - (crawler.Motion.Up * (float)crawling.RadiusMetres));
        creature.Feet = crawler.Hold == CrawlHold.None ? world.Y - (float)crawling.RadiusMetres : contact.Y;
        crawlTicks += Stopwatch.GetTimestamp() - started;
        return new Vector2(world.X, world.Z);
    }

    /// <summary>A fall ends on a surface: a drop lands on the player if they are under it; either way it lies dazed.</summary>
    private void Landed(Creature creature, Hunter hunter, Crawler crawler, WildlifeCrawling crawling, Vector3 playerFeet)
    {
        crawler.Phase = CrawlPhase.Dazed;
        crawler.DazedLeft = crawling.DazedSeconds;
        if (!crawler.Dropping) return;
        crawler.Dropping = false;
        Vector3 at = SpaceOf(creature).ToWorld(crawler.Local);
        bool canBeHit = !player.Vitals.IsDown && !player.Vitals.IsInvulnerable(step);
        if (canBeHit && CrawlRules.DropLands(CrawlRules.ToBody(at, playerFeet), creature.Kind.ReachMetres))
        {
            AttackProfile bite = creature.Kind.Attack;
            crawler.DropsLanded++;
            lastEvent = $"wall runner {creature.Id} dropped onto the player";
            ResolveStrike(new CreatureStrike(creature.Id, bite with { Power = (int)Math.Round(bite.Power * crawling.DropPower) }));
        }
        else
        {
            cues.Raise(Cue.Swing);
            lastEvent = canBeHit ? $"wall runner {creature.Id} dropped and missed" : $"wall runner {creature.Id} dropped onto a player who cannot be hit";
        }
    }

    /// <summary>After a strike it runs back up the walls a while (#10975): toward the ceiling over a point away from the player.</summary>
    private void StartRetreat(Creature creature, Crawler crawler, WildlifeCrawling crawling, Vector3 playerFeet)
    {
        CreatureSpace space = SpaceOf(creature);
        Vector2 away = creature.Position - new Vector2(playerFeet.X, playerFeet.Z);
        away = away.LengthSquared() < 1e-4f ? Vector2.UnitX : Vector2.Normalize(away);
        Vector2 ground = creature.Position + (away * CrawlRetreatMetres);
        Vector3 groundFeet = new(ground.X, playerFeet.Y, ground.Y);
        crawler.Perch = CeilingAt(space, groundFeet) is float ceiling
            ? space.ToLocal(groundFeet + new Vector3(0, ceiling - (float)crawling.RadiusMetres, 0))
            : space.ToLocal(groundFeet + new Vector3(0, (float)crawling.RadiusMetres, 0));
        crawler.RetreatLeft = crawling.RetreatSeconds;
        crawler.PathAt = long.MinValue;
    }

    /// <summary>A crawling hunter's bite starts (#10975): once it is done it runs off up the walls, as after a drop.</summary>
    private void CrawlerBit(Creature creature)
    {
        if (!crawlers.TryGetValue(creature.Id, out Crawler? crawler) || !hunters.TryGetValue(creature.Id, out Hunter? hunter) || hunter.Kind.Hunt!.Crawls is not WildlifeCrawling crawling) return;
        StartRetreat(creature, crawler, crawling, player.WorldFeetPosition);
    }

    /// <summary>Whether a crawling hunter may bite the player now: clinging to a floor or wall within its reach of their body.</summary>
    private bool CrawlerBites(Creature creature)
    {
        if (!crawlers.TryGetValue(creature.Id, out Crawler? crawler)) return true;
        Vector3 at = SpaceOf(creature).ToWorld(crawler.Local);
        return CrawlRules.Bites(crawler.Phase, crawler.Hold, CrawlRules.ToBody(at, player.WorldFeetPosition), creature.Kind.ReachMetres);
    }

    /// <summary>A blow lands on a crawling hunter (#10975): clinging to a wall, it is knocked loose and falls.</summary>
    private void CrawlerStruck(Creature creature)
    {
        if (!crawlers.TryGetValue(creature.Id, out Crawler? crawler) || !CrawlRules.KnockedLoose(crawler.Phase, crawler.Hold)) return;
        crawler.Phase = CrawlPhase.Falling;
        crawler.Dropping = false;
        crawler.KnockedLoose++;
        lastEvent = $"wall runner {creature.Id} was knocked off the wall";
    }

    /// <summary>A crawling hunter's eye: the ball's centre, so one on a ceiling does not look out from inside the rock.</summary>
    private Vector3? CrawlerEye(Creature creature) => crawlers.TryGetValue(creature.Id, out Crawler? crawler) ? crawler.Local : null;

    /// <summary>How a crawling hunter is drawn (#10975): its feet on the surface it holds, turned to it; and how fast it runs, for its clip.</summary>
    private void ShowCrawler(Creature creature, Hunter hunter)
    {
        if (!crawlers.TryGetValue(creature.Id, out Crawler? crawler) || hunter.Kind.Hunt!.Crawls is not WildlifeCrawling crawling) return;
        CreatureSpace space = SpaceOf(creature);
        WildlifeMember member = hunter.Member;
        member.SurfaceFeet = space.ToWorld(crawler.Local - (crawler.Motion.Up * (float)crawling.RadiusMetres));
        member.SurfaceTurn = crawler.Rotation;
        Vector3 velocity = crawler.Motion.Velocity;
        member.Velocity = new Vector2(velocity.Length(), 0);
    }

    /// <summary>Falling, it spreads its limbs; dazed, it flinches; otherwise its clip is a hunter's.</summary>
    private void CrawlerClip(Creature creature, Hunter hunter)
    {
        if (!crawlers.TryGetValue(creature.Id, out Crawler? crawler)) return;
        if (crawler.Phase == CrawlPhase.Falling) hunter.Member.Clip = WildlifeClipRole.Threat;
        else if (crawler.Phase == CrawlPhase.Dazed) hunter.Member.Clip = WildlifeClipRole.Hurt;
    }

    /// <summary>Toward its goal along its surface path, asking for a new one when it is stale; straight at the goal while no grid answers.</summary>
    /// <returns>The way to drive it, and whether it should let go: on a ceiling over a low slot whose floor is its next place, no way along the ceiling leads down to it.</returns>
    private (Vector3 Direction, bool LetGo) SteerCrawler(CreatureSpace space, Crawler crawler, Vector3 goal, double seconds)
    {
        // Stalled: it means to go somewhere and has stayed near where it was.
        if (Vector3.Distance(crawler.Anchor, crawler.Local) >= CrawlMovedMetres)
        {
            crawler.Anchor = crawler.Local;
            crawler.StuckSeconds = 0;
        }
        else
        {
            crawler.StuckSeconds += seconds;
        }

        bool stale = crawler.PathGoal is not Vector3 last || Vector3.Distance(last, goal) > CrawlRepathMetres
            || crawler.Next >= crawler.Path.Length || (step - crawler.PathAt) / HunterRules.StepsPerSecond >= CrawlRepathSeconds;
        if (stale && SurfaceGridReady(space))
        {
            crawler.PathAt = step;
            crawler.PathGoal = goal;
            SurfaceNavigationPathResult path = engine.Spatial.RequestSurfaceNavigationPath(new SurfaceNavigationPathRequest(space.Session, SurfaceGridId, crawler.Local, goal, CrawlMaximumVisited));
            crawler.PathOutcome = path.Outcome.ToString();
            crawler.Path = path.Waypoints.ToArray();
            // No way to the goal itself: as near to it as it can get.
            if (crawler.Path.Length == 0 && path.Outcome != SurfaceNavigationPathOutcome.Unavailable && path.Outcome != SurfaceNavigationPathOutcome.StartOffSurface)
                crawler.Path = [path.Nearest];
            crawler.Next = 0;
        }

        while (crawler.Next < crawler.Path.Length && Vector3.Distance(crawler.Path[crawler.Next].Position, crawler.Local) < CrawlArrivedMetres) crawler.Next++;
        // Stalled a while: past its waypoint to the next, which a wedged ball can often reach round the corner.
        if (crawler.StuckSeconds >= CrawlSkipSeconds && crawler.StuckSeconds - seconds < CrawlSkipSeconds && crawler.Next + 1 < crawler.Path.Length) crawler.Next++;
        if (crawler.Next < crawler.Path.Length)
        {
            SurfaceWaypoint next = crawler.Path[crawler.Next];
            Vector3 steer = CrawlRules.Steer(crawler.Local, crawler.Motion.Up, next.Position, next.Normal);
            return (steer, CrawlRules.LetsGoTo(crawler.Motion.Up, steer, next.Surface == CrawlerSurface.Floor, next.Position.Y < crawler.Local.Y));
        }

        crawler.StuckSeconds = 0;

        return (Vector3.Distance(goal, crawler.Local) < CrawlArrivedMetres ? Vector3.Zero : Vector3.Normalize(goal - crawler.Local), false);
    }

    /// <summary>
    /// Before the creatures move: a space whose crawlers are awake has its surface grid, derived on the Engine's workers about
    /// the player and again once they have gone far from where it was centred.
    /// </summary>
    private void TendSurfaceGrids(Vector3 playerFeet, CreatureSpace? here)
    {
        if (here is null || !roster.All.Any(creature => creature.Awake && creature.Space == here && Crawls(creature))) return;
        if (!surfaceGrids.TryGetValue(here, out SurfaceGrid? grid)) surfaceGrids[here] = grid = new SurfaceGrid();
        Vector3 local = here.ToLocal(playerFeet);
        if (grid.Pending)
        {
            SurfaceNavigationReadout readout = engine.Spatial.ReadSurfaceNavigation(new SurfaceNavigationReadRequest(here.Session, SurfaceGridId));
            if (readout.Pending) return;
            grid.Pending = false;
            if (readout.Refused)
            {
                grid.Refusals++;
                lastEvent = "the surface grid's derivation was refused";
                return;
            }

            grid.Installed = true;
            grid.Places = readout.Installed.SurfaceCellCount;
            grid.Edges = readout.Installed.EdgeCount;
            grid.DeriveMilliseconds = readout.Installed.DerivationMicroseconds / 1000.0;
            return;
        }

        if (grid.Publications > 0 && Vector3.Distance(new Vector3(grid.Centre.X, 0, grid.Centre.Z), new Vector3(local.X, 0, local.Z)) < SurfaceRepublishMetres) return;
        WildlifeCrawling crawling = hunters.Values.Select(hunter => hunter.Kind.Hunt!.Crawls).First(crawls => crawls is not null)!;
        CrawlerConfig crawler = CrawlerConfigOf(crawling, 1);
        SurfaceNavigationConfig config = engine.Spatial.DefaultSurfaceNavigationConfig() with
        {
            GridId = SurfaceGridId,
            Crawler = crawler,
            FloorCost = CrawlRules.CostScale,
            WallCost = CrawlRules.Cost(crawling.WallCost),
            CeilingCost = CrawlRules.Cost(crawling.CeilingCost),
        };
        Vector3 centre = new(MathF.Floor(local.X), MathF.Floor(local.Y), MathF.Floor(local.Z));
        engine.Spatial.ReplaceSurfaceNavigation(new SurfaceNavigationReplaceRequest(here.Session,
            centre - new Vector3(SurfaceHalfExtent, SurfaceBelow, SurfaceHalfExtent), centre + new Vector3(SurfaceHalfExtent, SurfaceAbove, SurfaceHalfExtent), config, true));
        grid.Centre = centre;
        grid.Pending = true;
        grid.Publications++;
        grid.RequestedAt = step;
    }

    private bool SurfaceGridReady(CreatureSpace space) => surfaceGrids.TryGetValue(space, out SurfaceGrid? grid) && grid.Installed;

    /// <summary>How far over a point's height the first ceiling above it is, or null for none within reach.</summary>
    private float? CeilingAt(CreatureSpace space, Vector3 feetWorld)
    {
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            space.Session, space.ToLocal(feetWorld + new Vector3(0, CeilingFromMetres, 0)), Vector3.UnitY, CeilingReachMetres,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present ? (float)(CeilingFromMetres + hit.Distance) : null;
    }

    private void ForgetCrawler(int id) => crawlers.Remove(id);

    private void ClearCrawlers()
    {
        crawlers.Clear();
        surfaceGrids.Clear();
    }

    /// <summary>For the readout: each space's surface grid, every crawler's phase, hold, goal and path, and the steps' cost.</summary>
    internal string CrawlersReadout()
    {
        Vector3 playerFeet = player.WorldFeetPosition;
        IEnumerable<string> grids = surfaceGrids.Select(entry => string.Create(CultureInfo.InvariantCulture,
            $"{entry.Key.Name} installed={entry.Value.Installed} pending={entry.Value.Pending} places={entry.Value.Places} edges={entry.Value.Edges} deriveMs={entry.Value.DeriveMilliseconds:F1} publications={entry.Value.Publications} refused={entry.Value.Refusals}"));
        IEnumerable<string> each = crawlers.Select(entry =>
        {
            Crawler c = entry.Value;
            string state = roster.TryGet(entry.Key, out Creature creature) ? creature.Behavior.State.ToString() : "gone";
            Vector3 world = creature is not null && roster.TryGet(entry.Key, out _) ? SpaceOf(creature).ToWorld(c.Local) : c.Local;
            return string.Create(CultureInfo.InvariantCulture,
                $"{entry.Key} {c.Phase} hold={c.Hold} state={state} at={world.X:F1},{world.Y:F1},{world.Z:F1} up={c.Motion.Up.X:F1},{c.Motion.Up.Y:F1},{c.Motion.Up.Z:F1} toBody={CrawlRules.ToBody(world, playerFeet):F1}m path={c.PathOutcome} {c.Next}/{c.Path.Length}{(c.Next < c.Path.Length ? $" next={c.Path[c.Next].Position.X:F1},{c.Path[c.Next].Position.Y:F1},{c.Path[c.Next].Position.Z:F1}({c.Path[c.Next].Surface},{c.Path[c.Next].Edge})" : "")} stuck={c.StuckSeconds:F1}s unstuck={c.Unstuck} retreat={c.RetreatLeft:F1}s dazed={Math.Max(0, c.DazedLeft):F1}s drops={c.Drops} landed={c.DropsLanded} knocked={c.KnockedLoose}");
        });
        double microseconds = crawlSteps > 0 ? crawlTicks * 1e6 / Stopwatch.Frequency / crawlSteps : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"grids=[{string.Join("; ", grids)}] crawlers=[{string.Join(" | ", each)}] steps={crawlSteps} refused={crawlRefused} stepUs={microseconds:F1} last=[{lastEvent}]");
    }
}
