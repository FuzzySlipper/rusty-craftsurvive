using System.Globalization;
using System.Numerics;
using System.Text;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// Creatures in the running product. Each update wakes the creatures whose ground is resident and
/// lets the rest lie dormant, reads the player and the Engine's perception, routes pursuers along
/// the Engine's navigation, decides every awake creature's step through
/// <see cref="CreatureSimulation"/>, applies blows to the player's vitals, lets the encounter
/// director release creatures that wandered out of the encounter, and publishes the roster.
///
/// The director owns membership, the roster mirrors it, and the presentation derives from the
/// roster; world positions reach the Engine only through the <see cref="WorldFrame"/>.
/// </summary>
internal sealed partial class CreatureModule : IProductModule
{
    /// <summary>Each travel ambush is its own wilderness region, numbered from here.</summary>
    private const long AmbushRegionBase = 1000;
    /// <summary>An ambush gives up on creatures it cannot place within about ten seconds.</summary>
    private const long AmbushPatienceSteps = 600;
    private long ambushes, ambushSince;
    private readonly RoutingTimes routing = new();
    private readonly HashSet<Vector2> ambushRefused = [];
    /// <summary>The columns a pending ambush has filled so far, across updates.</summary>
    private readonly List<(long X, long Z)> ambushColumns = [];
    private int ambushWaiting, ambushPlaced;

    /// <summary>Eye height above the ground, where a creature perceives from.</summary>
    private const float EyeHeightMetres = 1.5f;

    /// <summary>Height above the ground where a blow on a creature is seen landing.</summary>
    private const float StruckHeightMetres = 0.8f;

    /// <summary>Creatures stand in the middle of a cell, never on the corner four columns share.</summary>
    private const float CellCentre = 0.5f;

    /// <summary>A creature looks in every direction: a facing cosine of -1 excludes nothing.</summary>
    private const double AllAroundFacingCosine = -1.0;

    private const double FullEvidence = 1.0;

    /// <summary>How close a pursuer comes to a path cell's centre before heading for the next.</summary>
    private const float WaypointArrivalMetres = 0.5f;

    /// <summary>A path is asked for again once the player is this far from where it led (a few cells).</summary>
    private const float ReplanMetres = 3f;

    /// <summary>A pursuer that has meant to walk and not closed on its waypoint for this many steps is stuck and asks again.</summary>
    private const int StuckSteps = 30;

    /// <summary>Closing by less than this per step is not progress.</summary>
    private const float ProgressMetres = 0.01f;

    /// <summary>After a failed route a pursuer waits this many steps, doubling with each failure up to the cap.</summary>
    private const long FirstBackoffSteps = 30;
    private const long MaximumBackoffSteps = 480;

    /// <summary>An ambush tests at most this many candidate columns per update, each with this search budget.</summary>
    private const int AmbushChecksPerUpdate = 3;
    private const uint AmbushVisitedCells = 512;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly EncounterDirector director = new(EncounterPolicy.Default);
    private readonly CreatureRoster roster = new();
    private readonly CreaturePresentation presentation;
    /// <summary>The walking session as a space (#10876): the overworld and the caves under it.</summary>
    private readonly WalkingCreatureSpace walking;

    /// <summary>
    /// Each space's navigation per body capsule (#10925), made when a creature of that size first routes there: each on a
    /// grid of its own in the space's session, so a large hunter's grid does not coarsen a player-sized one's.
    /// </summary>
    private readonly Dictionary<(CreatureSpace Space, float Radius, float Height), CreatureNavigation> navigations = [];

    /// <summary>The grid id the next body's navigation takes in each space.</summary>
    private readonly Dictionary<CreatureSpace, ulong> nextGrid = [];

    /// <summary>Each hunter kind's capsule, as its body steps and its grid routes.</summary>
    private readonly Dictionary<string, CharacterControllerConfig> capsules = [];
    private int nextId = 1;
    private long step;
    private long swingSequence;
    private int defeated;
    private int spawnRefusals;
    private int released;
    private bool started;
    private string perceptionStatus = "unasked";
    private string lastEvent = "not started";
    private string lastPlayerAttack = "none";
    private string lastFailure = "none";

    /// <summary>Hostile creatures see this much further by night: the dark belongs to them.</summary>
    internal const double NightSightFactor = 1.5;

    private readonly Func<bool> isNight;
    private readonly Cues cues;

    internal CreatureModule(IEngineContext engine, TerrainWorld terrain, PlayerController player, WorldFrame frame, WalkingCreatureSpace walking, Func<bool> isNight, Cues cues)
    {
        this.walking = walking ?? throw new ArgumentNullException(nameof(walking));
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        this.isNight = isNight ?? throw new ArgumentNullException(nameof(isNight));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        presentation = new CreaturePresentation(engine, (creature, world) => SpaceOf(creature).ToLocal(world), StandingAt, creature => hunters.ContainsKey(creature.Id));
        capsule = CreatureBody.Capsule(engine.Spatial.DefaultCharacterControllerConfig());
        // A body's step carries session-local positions; when the space's origin moves, each is set down afresh from where it stands.
        walking.Rebased += () =>
        {
            foreach (Creature creature in roster.All.Where(creature => creature.Space == walking)) creature.Body = null;
        };
    }

    /// <summary>The creature capsule navigation routes and its body steps as (#9737), and each kind's at its pace.</summary>
    private readonly CharacterControllerConfig capsule;
    private readonly Dictionary<CreatureKind, CharacterControllerConfig> bodies = [];
    private int bodiesStepped, bodiesRefused;

    /// <summary>
    /// Moves a creature's body toward its waypoint by at most some metres (#9737): the Engine's character step
    /// carries its capsule against the walking session's collision, so it stops at what it meets, and its feet
    /// come from the step. A body the Engine refuses, lodged in collision, stays put; not closing on its
    /// waypoint, it is soon found stuck and routed again. A body asked for no metres is still stepped, so it
    /// falls when what it stood on goes; a creature that has not walked has no body, and <see cref="Follow"/> keeps it.
    /// </summary>
    private Vector2 MoveBody(Creature creature, Vector2 waypoint, double travel, double seconds)
    {
        // A hostile animal stands to show its threat or bite, though its body still falls.
        if (hunters.TryGetValue(creature.Id, out Hunter? hunter) && hunter.Holding) travel = 0;
        if (travel <= 0 && creature.Body is null) return creature.Position;
        if (!bodies.TryGetValue(creature.Kind, out CharacterControllerConfig config))
            bodies[creature.Kind] = config = CreatureBody.Configure(hunter is null ? capsule : BodyOf(hunter.Kind), (float)creature.Kind.PursueSpeedMetresPerSecond);
        CreatureBodyState body = creature.Body ?? Placed(creature, config);
        CreatureSpace space = SpaceOf(creature);
        Vector3 toward = space.ToLocal(waypoint.X, 0, waypoint.Y);
        bodiesStepped++;
        if (CreatureBody.Step(engine.Spatial, space.Session, body, config, new Vector2(toward.X, toward.Z), travel, seconds, creature.NextBodySequence)
            is not CreatureBodyState moved)
        {
            bodiesRefused++;
            creature.Body = body;
            return creature.Position;
        }

        creature.Body = moved;
        Vector3 feet = space.ToWorld(CreatureBody.FeetUnder(moved.Centre, config));
        creature.Feet = feet.Y;
        return new Vector2(feet.X, feet.Z);
    }

    /// <summary>A body standing where the creature stands now.</summary>
    private CreatureBodyState Placed(Creature creature, CharacterControllerConfig config)
    {
        Vector3 centre = CreatureBody.CentreOver(SpaceOf(creature).ToLocal(creature.Position.X, StandingAt(creature), creature.Position.Y), config);
        return new CreatureBodyState(centre, CreatureBody.AtRest(centre));
    }

    /// <summary>The space a separate space's creatures live in while the player is in it (#10876): a loaded dungeon's, from its owner.</summary>
    internal Func<CreatureSpace?>? SeparateSpace { get; set; }

    /// <summary>The space the player is in: the walking session, a separate space's, or none for a separate space that holds no creatures.</summary>
    private CreatureSpace? PlayerSpace => player.InSeparateSpace ? SeparateSpace?.Invoke() : walking;

    /// <summary>The space a creature lives in. Every creature the module places is given one.</summary>
    private CreatureSpace SpaceOf(Creature creature) => creature.Space as CreatureSpace ?? walking;

    /// <summary>A space's navigation for player-sized bodies: what readouts, the ambush's plain creatures and diagnosis of a point use.</summary>
    private CreatureNavigation NavigationIn(CreatureSpace space) => NavigationIn(space, capsule);

    /// <summary>A space's navigation for a body's capsule, made the first time a body of that size routes there.</summary>
    private CreatureNavigation NavigationIn(CreatureSpace space, CharacterControllerConfig body)
    {
        (CreatureSpace, float, float) key = (space, body.Shape.Radius, body.Shape.StandingHeight);
        if (navigations.TryGetValue(key, out CreatureNavigation? found)) return found;
        ulong grid = nextGrid.TryGetValue(space, out ulong next) ? next : FirstGridId;
        nextGrid[space] = grid + 1;
        return navigations[key] = new CreatureNavigation(engine, space, body, grid);
    }

    /// <summary>
    /// Publishes a grid about the player if it is stale (#10925): one never published goes at once, but of those published
    /// before, one republishes an update, so several body sizes' grids do not all derive in one frame. A grid waiting its
    /// turn still answers from where it was published.
    /// </summary>
    private void Publish(CreatureNavigation navigation, Vector3 playerFeet)
    {
        if (!navigation.Stale(playerFeet)) return;
        if (navigation.Published)
        {
            if (republishedAt == step) return;
            republishedAt = step;
        }

        navigation.EnsurePublished(playerFeet);
    }

    /// <summary>The update a grid last republished in.</summary>
    private long republishedAt = -1;

    /// <summary>The first grid a space's creatures route on; each further body size takes the next.</summary>
    private const ulong FirstGridId = 1UL;

    /// <summary>The navigation a creature routes on: its own space's, for its own body (#10925).</summary>
    private CreatureNavigation NavigationOf(Creature creature) => NavigationIn(SpaceOf(creature), CapsuleOf(creature));

    /// <summary>A creature's capsule: a hostile animal's kind's, or the plain creatures' player-sized one.</summary>
    private CharacterControllerConfig CapsuleOf(Creature creature) => hunters.TryGetValue(creature.Id, out Hunter? hunter) ? BodyOf(hunter.Kind) : capsule;

    /// <summary>The navigations of a space, every body size routed there so far.</summary>
    private IEnumerable<CreatureNavigation> NavigationsIn(CreatureSpace space) =>
        navigations.Where(entry => entry.Key.Space == space).Select(entry => entry.Value).OrderBy(navigation => navigation.GridId);

    /// <summary>Creatures of a space that has been let go go with it; so does the space's navigation.</summary>
    private void ReleaseGone()
    {
        int[] gone = [.. roster.All.Where(creature => SpaceOf(creature).Gone).Select(creature => creature.Id)];
        foreach (int id in gone)
        {
            ForgetHunter(id);
            director.Remove(id);
        }

        released += roster.Release(gone);
        foreach ((CreatureSpace, float, float) key in navigations.Keys.Where(key => key.Space.Gone).ToArray()) navigations.Remove(key);
        foreach (CreatureSpace space in nextGrid.Keys.Where(space => space.Gone).ToArray()) nextGrid.Remove(space);
    }

    /// <summary>The navigation of the space the player is in (the walking session's when that space holds no creatures), for readouts.</summary>
    private CreatureNavigation NavigationHere => NavigationIn(PlayerSpace ?? walking);

    internal CreatureRoster Roster => roster;

    /// <summary>The awake creatures within a distance of a world point: each one's id and where its feet stand (world metres).</summary>
    internal IEnumerable<(int Id, Vector3 Feet)> AwakeWithin(Vector3 world, double metres) => roster.All
        .Where(creature => creature.Awake && CreatureSimulation.PlanarDistance(creature.Position, world) <= metres)
        .Select(creature => (creature.Id, new Vector3(creature.Position.X, StandingAt(creature), creature.Position.Y)));

    /// <summary>How far the nearest awake hostile creature stands from a point, in metres, or infinity when none is awake.</summary>
    internal double NearestAwakeHostileMetres(Vector3 world) => roster.All
        .Where(creature => creature.Awake && creature.Kind.Tuning.Disposition == CreatureDisposition.Hostile)
        .Select(creature => CreatureSimulation.PlanarDistance(creature.Position, world))
        .DefaultIfEmpty(double.PositiveInfinity)
        .Min();

    /// <summary>How much further than its kind's sight a creature sees now: further for hostiles at night.</summary>
    private double SightScale(Creature creature) =>
        isNight() && creature.Kind.Tuning.Disposition == CreatureDisposition.Hostile ? NightSightFactor : 1d;

    /// <summary>Whether a creature is near enough to the player for its own sight to reach, as the behaviour rules judge it.</summary>
    private bool InOwnSight(int id, Vector3 playerWorld) => roster.TryGet(id, out Creature creature)
        && CreatureSimulation.PlanarDistance(creature.Position, playerWorld) <= creature.Kind.Tuning.SightRange * SightScale(creature);

    /// <summary>The same, for this update: a pack animal called in by another comes from as far as the call carries.</summary>
    private double SightScale(Creature creature, HashSet<int> alerted) =>
        alerted.Contains(creature.Id) && hunters.TryGetValue(creature.Id, out Hunter? hunter) && packAlarms.ContainsKey(hunter.Record.Key)
            ? Math.Max(SightScale(creature), hunter.Kind.Hunt!.Pack!.AlarmMetres / creature.Kind.Tuning.SightRange)
            : SightScale(creature);

    internal Rusty.Engine.Entities.EntityStore EntityStore => presentation.Entities;

    internal IReadOnlyList<AppearanceFact> AppearanceFacts => presentation.Facts;

    /// <summary>Called by the product root once it has published a snapshot of <see cref="AppearanceFacts"/>.</summary>
    internal void AfterAppearanceSnapshot() => presentation.ReleaseRetired();

    public void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        lastEvent = "started";
    }

    public void Update(ProductStep time)
    {
        if (!started)
        {
            return;
        }

        step = time.Step;
        // Each creature lives in its space (#10876, Do 2): awake while that space is live and its floor loaded, asleep with it
        // otherwise, whichever space the player is in; a space let go takes its creatures with it.
        ReleaseGone();
        CreatureSpace? here = PlayerSpace;
        if (player.AttackRequested)
        {
            lastPlayerAttack = PlayerAttack();
        }

        Vector3 playerWorld = player.WorldPosition;
        foreach (Creature creature in roster.All)
        {
            creature.Awake = CreatureSpaceRules.Awake(creature.Space, Feet(creature));
        }

        long routingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        long queriesBefore = here is null ? 0 : NavigationsIn(here).Sum(navigation => navigation.Queries);
        if (here == walking) CloseAmbush();
        // While the test aid hides the player (#10878) nothing sees them, so a hunt turns to a search where they stand.
        HashSet<int> seeing = here is null || step < unseenUntil ? [] : Sense(playerWorld, here);
        // A pack hunts as one: what one sees they all come for, though a bite still needs its own sight. One still lying at
        // rest calls nothing (#10878): it must wake first, at its own wake range, which a draft shortens from downwind.
        HashSet<int> alerted = HunterRules.Alerted(seeing, hunters, packAlarms, time.ElapsedSeconds,
            id => InOwnSight(id, playerWorld) && roster.All.FirstOrDefault(creature => creature.Id == id)?.Behavior.State != CreatureState.Resting);
        (lastSeeing, lastAlerted) = (seeing, alerted);
        Vector3 playerFeet = player.WorldFeetPosition;
        foreach (Creature creature in roster.All.Where(creature => creature.Awake))
        {
            // Where it last saw the player is where it searches (#10876); a pack's call wakes one lying at rest.
            if (seeing.Contains(creature.Id)) creature.LastSeen = playerFeet;
            // One that sees the player itself is still woken by its pack (#10878): its own sight wakes it only within its
            // draft-shortened range, and a pack-mate that has woken calls them all.
            if (creature.Behavior.State == CreatureState.Resting && hunters.TryGetValue(creature.Id, out Hunter? called) && packAlarms.ContainsKey(called.Record.Key)
                && !(overrides.Count > 0 && (Frozen(creature) || ForcedState(creature) is not null)))
            {
                creature.Behavior = creature.Behavior with { State = CreatureState.Alert };
                creature.LastSeen ??= playerFeet;
            }
        }

        // With no steps admitted nothing has moved, and a pursuer standing still is not stuck.
        // A test aid sending a creature somewhere (#10926) has it search its way there.
        if (overrides.Count > 0) SteerOverrides();
        if (time.AdmittedSteps > 0) Route(player.WorldFeetPosition, here);
        routing.Record(System.Diagnostics.Stopwatch.GetElapsedTime(routingStarted).TotalMilliseconds, (here is null ? 0 : NavigationsIn(here).Sum(navigation => navigation.Queries)) - queriesBefore,
            roster.All.Count(creature => creature.Awake && creature.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking));
        bool playerCanBeHit = !player.Vitals.IsDown && !player.Vitals.IsInvulnerable(step);
        long bodiesStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        (bodiesStepped, bodiesRefused) = (0, 0);
        foreach (Creature creature in roster.All)
        {
            // A sleeping creature's body is set down afresh when it wakes, from wherever it was left.
            if (!creature.Awake) creature.Body = null;
        }

        foreach (Creature creature in roster.All.Where(creature => creature.Awake))
        {
            // A creature a test aid froze (#10926) does nothing: no rules, no move, its clip held.
            if (overrides.Count > 0 && Frozen(creature)) continue;
            CreatureSense sense = new(alerted.Contains(creature.Id));
            // An animal showing its display does not bite until it has shown it (#10876).
            // Nor does one still hanging or falling from its ceiling (#10877).
            bool displaying = hunters.TryGetValue(creature.Id, out Hunter? shown) && ((shown.ThreatSeconds > 0 && shown.Kind.Hunt!.Display is not null) || shown.Member.Lift > 0);
            if (CreatureSimulation.Step(creature, sense, playerWorld, playerCanBeHit && !displaying, time, SightScale(creature, alerted),
                CreatureSimulation.WaypointOrWait(creature.Position, creature.Waypoint), MoveBody, WakeShare(shown, creature, playerWorld), ForcedState(creature)) is CreatureStrike strike)
            {
                if (hunters.TryGetValue(creature.Id, out Hunter? biting)) HunterRules.Bite(biting, strike.Attack, ClipSeconds(biting, WildlifeClipRole.Bite));
                else ResolveStrike(strike);
                playerCanBeHit = !player.Vitals.IsDown;
            }

            Follow(creature);
            if (hunters.TryGetValue(creature.Id, out Hunter? hunter))
            {
                Show(creature, hunter, playerWorld, playerCanBeHit, seeing.Contains(creature.Id), time.ElapsedSeconds);
                if (overrides.Count > 0) HoldClip(creature, hunter);
                playerCanBeHit = playerCanBeHit && !player.Vitals.IsDown;
            }
        }

        routing.RecordBodies(System.Diagnostics.Stopwatch.GetElapsedTime(bodiesStarted).TotalMilliseconds, bodiesStepped, bodiesRefused);
        // The test aids that watch and label creatures (#10926): nothing while none is asked for.
        if (watchUntil > step) WatchUpdate();
        PublishLabels(here);
        CheckCaveHomes(here);

        // A creature in another space than the player's is kept while its space sleeps: it is not away from the encounter.
        IReadOnlyList<int> leaving = director.Tick(
            step,
            id => roster.TryGet(id, out Creature creature)
                ? creature.Space == here ? CreatureSimulation.PlanarDistance(creature.Position, playerWorld) : 0d
                : double.MaxValue,
            _ => true);
        foreach (int id in leaving) ForgetHunter(id);
        released += roster.Release(leaving);
        presentation.Sync(roster);
    }

    /// <summary>A hostile animal's threat, bite and flinch move on, and what it shows follows what it is doing.</summary>
    /// <param name="seesPlayer">What the Engine's perception says now: a bite begun in sight does not reach round a corner.</param>
    private void Show(Creature creature, Hunter hunter, Vector3 playerWorld, bool playerCanBeHit, bool seesPlayer, double seconds)
    {
        double distance = CreatureSimulation.PlanarDistance(creature.Position, playerWorld);
        CreatureState state = creature.Behavior.State;
        if (HunterRules.Advance(hunter, state, distance, ClipSeconds(hunter, WildlifeClipRole.Threat), seconds) is AttackProfile blow)
        {
            if (playerCanBeHit && HunterRules.Lands(hunter.Kind.Hunt!, distance, seesPlayer)) ResolveStrike(new CreatureStrike(creature.Id, blow));
            else cues.Raise(Cue.Swing);
        }

        WildlifeMember member = hunter.Member;
        if (seconds > 0)
        {
            member.Velocity = (creature.Position - hunter.LastPosition) / (float)seconds;
            hunter.LastPosition = creature.Position;
        }

        WildlifeRig rig = Wildlife!.RigOf(hunter.Kind);
        Vector2 toPlayer = new Vector2(playerWorld.X, playerWorld.Z) - creature.Position;
        // At rest it faces into its home's draft, if one blows there (#10878); searching it faces where it goes or stays as
        // it was; hunting, it faces the player.
        Vector2 heading = member.Velocity.Length() > HunterTurnsAboveMetres ? member.Velocity
            : state is CreatureState.Idle or CreatureState.Resting or CreatureState.Searching ? HunterRules.RestHeading(hunter, state) : toPlayer;
        member.Yaw = HunterRules.Turned(member.Yaw, heading, hunter.Kind.Speeds.TurnDegrees, seconds);
        member.Position = HunterRules.Drawn(creature.Position, member.Yaw, rig.Rig.HindMetres, rig.Rig.ForeMetres, member.Size);
        double runsAbove = (rig.Clip(WildlifeClipRole.Walk).Speed + rig.Clip(WildlifeClipRole.Run).Speed) / 2 * member.Size;
        if (hunter.Kind.Hunt?.Rest is { Hangs: true })
        {
            if ((state == CreatureState.Resting && hunter.CeilingFrom is null) || (hunter.CeilingFrom is Vector2 measured && Vector2.Distance(measured, creature.Position) > CeilingRemeasureMetres))
            {
                hunter.CeilingLift = CeilingOver(creature);
                hunter.CeilingFrom = creature.Position;
            }

            HunterRules.Hang(hunter, state, distance > hunter.Kind.Hunt.SightMetres, seconds);
        }

        member.Clip = HunterRules.Want(hunter, state, member.Velocity.Length(), runsAbove);
    }

    /// <summary>How far over its floor a creature's body is now: a hanging or falling hunter's lowest point (R10877-2), else nothing.</summary>
    private float BodyOffFloor(Creature creature) => hunters.TryGetValue(creature.Id, out Hunter? hunter) ? HunterRules.BodyOffFloor(hunter) : 0f;

    /// <summary>A hanging hunter's ceiling and lift for the readout (#10877); nothing for any other.</summary>
    /// <summary>
    /// A hunter resting into a draft (#10878): the way the draft comes from and the way it faces, in degrees (0 faces -z,
    /// 90 faces +x), and the share of its wake range it wakes at for where the player stands.
    /// </summary>
    private static string DraftReadout(Hunter hunter, Creature creature, Vector3 playerWorld) => hunter.Record.Upwind is Vector2 upwind
        ? string.Create(CultureInfo.InvariantCulture,
            $" upwind={HunterRules.HeadingDegrees(upwind):F0} faces={Math.IEEERemainder(hunter.Member.Yaw * 180 / Math.PI, 360):F0} wake={WakeShare(hunter, creature, playerWorld):F2}")
        : "";

    private static string HangReadout(Hunter hunter) => hunter.Kind.Hunt?.Rest is { Hangs: true }
        ? string.Create(CultureInfo.InvariantCulture, $" ceiling={(hunter.CeilingLift is float ceiling ? ceiling.ToString("F1", CultureInfo.InvariantCulture) : "none")} lift={hunter.Member.Lift:F1}")
        : "";

    /// <summary>A hanging animal's ceiling is found again once it stands this far from where it was last found (#10877).</summary>
    private const float CeilingRemeasureMetres = 0.5f;

    /// <summary>How far up a hanging animal looks for a ceiling to hang from.</summary>
    private const float CeilingReachMetres = 14f;

    /// <summary>A ceiling is looked for from this far over the feet, clear of the floor's own surface.</summary>
    private const float CeilingFromMetres = 0.3f;

    /// <summary>How far over a creature's feet the first ceiling above it is (#10877), or null for none within reach: cast straight up through its space.</summary>
    private float? CeilingOver(Creature creature)
    {
        CreatureSpace space = SpaceOf(creature);
        float feet = StandingAt(creature);
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            space.Session, space.ToLocal(creature.Position.X, feet + CeilingFromMetres, creature.Position.Y), Vector3.UnitY, CeilingReachMetres,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present ? (float)(CeilingFromMetres + hit.Distance) : null;
    }

    private double ClipSeconds(Hunter hunter, WildlifeClipRole role) => Wildlife!.RigOf(hunter.Kind).Clip(role).Seconds;

    /// <summary>
    /// Navigation is published only around the player, and a hostile animal sees further than that. Out
    /// past it, in the empty country it keeps to, it comes straight on, its body stopping at what it meets,
    /// until it is near enough to be routed. Any other creature waits.
    /// </summary>
    private Vector2? BeyondNavigation(Creature creature, Vector2 playerGround) =>
        hunters.ContainsKey(creature.Id) && Vector2.Distance(creature.Position, playerGround) > CreatureNavigation.ReachMetres ? playerGround : null;

    /// <summary>
    /// A pack animal swings wide of the straight line while it has room, heading over open ground rather
    /// than along a path. Stopped by what it meets, it gives that up and is routed in like any other.
    /// </summary>
    private bool Flanks(Creature creature, Hunter? hunter, Vector2 playerGround)
    {
        if (hunter is null || hunter.FlankBlocked || hunter.Kind.Hunt!.Pack is not { } pack
            || HunterRules.Flank(creature.Position, playerGround, hunter.Side, pack) is not Vector2 aim) return false;
        bool stuck = CreatureSimulation.NotClosing(creature.Waypoint is null ? null : creature.RoutedFrom, creature.Position, aim,
            Vector2.Distance(creature.Position, playerGround), creature.Kind.HaltDistanceMetres, ProgressMetres);
        creature.StuckSteps = stuck ? creature.StuckSteps + 1 : 0;
        if (creature.StuckSteps >= StuckSteps)
        {
            hunter.FlankBlocked = true;
            creature.StuckSteps = 0;
            return false;
        }

        creature.Path = null;
        creature.RoutedFrom = creature.Position;
        creature.Waypoint = aim;
        return true;
    }

    /// <summary>
    /// Near the player, a hunter other than the first of those from its home goes to its own place round the player
    /// (#10878, <see cref="HunterRules.Ring"/>), straight there over the little ground left. Stopped by what it meets, it
    /// gives that up and closes in like any other.
    /// </summary>
    private bool Rings(Creature creature, Hunter? hunter, Vector2 playerGround)
    {
        if (hunter is null || hunter.RingBlocked || hunter.Side == 0) return false;
        WildlifePack? pack = hunter.Kind.Hunt!.Pack;
        Creature? first = roster.All.FirstOrDefault(other => other.Awake && other.Space == creature.Space
            && hunters.TryGetValue(other.Id, out Hunter? its) && its.Side == 0 && its.Record.Key == hunter.Record.Key);
        if (first is null || HunterRules.Ring(creature.Position, playerGround, first.Position, hunter.Side, pack, creature.Kind.HaltDistanceMetres) is not Vector2 place) return false;
        bool stuck = CreatureSimulation.NotClosing(creature.Waypoint is null ? null : creature.RoutedFrom, creature.Position, place,
            Vector2.Distance(creature.Position, place) + creature.Kind.HaltDistanceMetres, creature.Kind.HaltDistanceMetres, ProgressMetres);
        creature.StuckSteps = stuck ? creature.StuckSteps + 1 : 0;
        if (creature.StuckSteps >= StuckSteps)
        {
            hunter.RingBlocked = true;
            creature.StuckSteps = 0;
            return false;
        }

        creature.Path = null;
        creature.RoutedFrom = creature.Position;
        creature.Waypoint = place;
        return true;
    }

    /// <summary>The share of its wake range a resting hunter wakes at for where the player stands against its home's draft (#10878).</summary>
    private static double WakeShare(Hunter? hunter, Creature creature, Vector3 playerWorld) => hunter is null ? 1
        : HunterRules.WakeShare(creature.Position, new Vector2(playerWorld.X, playerWorld.Z), hunter.Record.Upwind, hunter.Kind.Hunt!.Rest?.DownwindShare);

    /// <summary>A hostile animal going this slowly turns to face the player, not the way it is drifting.</summary>
    private const float HunterTurnsAboveMetres = 0.3f;

    /// <summary>Hostile animals near their homes need this much around them to stand on: the plan's candidates about the home.</summary>
    private const int HunterHomeMetres = 24;

    /// <summary>Each hostile animal placed from a region, or by an ambush, is its own wilderness region, numbered from here.</summary>
    private const long HunterRegionBase = 1_000_000;

    /// <summary>Wildlife, which places and draws hostile animals (#10615). The product root joins the two.</summary>
    internal WildlifeModule? Wildlife { get; set; }

    /// <summary>Hostile animals by creature id, and the herd records of those awake, so one home holds one animal.</summary>
    private readonly Dictionary<int, Hunter> hunters = [];

    /// <summary>How many of each record's animals are on the ground: a pack is one record.</summary>
    private readonly Dictionary<string, int> hunted = [];

    /// <summary>The packs that have been called to the player, and how long the call has left.</summary>
    private readonly Dictionary<string, double> packAlarms = [];
    private readonly Dictionary<string, CreatureKind> hunterKinds = [];
    private long hunterRegions;

    private CreatureKind KindOf(WildlifeKind kind)
    {
        if (hunterKinds.TryGetValue(kind.Id, out CreatureKind? found)) return found;
        return hunterKinds[kind.Id] = HunterRules.KindOf(kind);
    }

    /// <summary>A hostile animal's capsule, its kind's size class: wide enough to hold its legs, so it is routed and stopped as what it is.</summary>
    private CharacterControllerConfig BodyOf(WildlifeKind kind)
    {
        if (capsules.TryGetValue(kind.Id, out CharacterControllerConfig known)) return known;
        WildlifeCapsule body = Wildlife!.CapsuleOf(kind);
        return capsules[kind.Id] = CreatureBody.Capsule(engine.Spatial.DefaultCharacterControllerConfig(), body.Radius, body.Height);
    }

    /// <summary>The hostile animals to draw this update: only those on resident ground.</summary>
    internal IReadOnlyList<WildlifeHunter> HunterFacts() =>
        [.. roster.All.Where(creature => creature.Awake && hunters.ContainsKey(creature.Id))
            .Select(creature => new WildlifeHunter(hunters[creature.Id].Kind, hunters[creature.Id].Member, StandingAt(creature), SpaceOf(creature)))];

    /// <summary>
    /// A hostile animal's home has come near (#10615). It takes up its place once the ground there is
    /// resident, and one home holds one animal for as long as it is awake.
    /// </summary>
    internal void AdmitHunter(HerdRecord record, WildlifeKind kind, Vector2 point, int alive)
    {
        int on = hunted.GetValueOrDefault(record.Key);
        if (!started || on >= alive) return;
        if (record.Floor is float floor)
        {
            // A cave animal (#10876) takes up its home on its own floor in the walking session, under whatever ground lies over it.
            // A pack whose places were not all on loaded floor when it woke takes up the rest as the floor loads (#10925): it
            // used to stay one animal short for good.
            if (walking.IsResident(new Vector3(point.X, floor - 1f, point.Y))) PlaceCaveHunter(record, kind, new Vector3(point.X, floor, point.Y), alive - on, walking, first: on);
            return;
        }

        if (on > 0) return;

        if (!walking.IsResident(Feet(point) - Vector3.UnitY)) return;
        PlaceHunter(record, kind, point, 0, HunterHomeMetres, count: alive);
    }

    /// <summary>A cave home names a point in the air over its floor; the floor is looked for from this far above it.</summary>
    private const float CaveFloorLookAboveMetres = 0.5f;

    /// <summary>
    /// A cave pack's places stand within this height of their home's floor, so none is set on a ledge or in a pit: one step,
    /// as a body climbs (#10925). At two metres a glass-courser was set on a passage 2.2 m under its home that no step joined.
    /// </summary>
    private const float CaveFloorReachMetres = (float)CreatureNavigation.ClimbMetres;

    /// <summary>A cave pack stands in rings about its home, this many places to a ring.</summary>
    private const int CaveRingPlaces = 6;

    /// <summary>A cave home's group is set down this many rings out at most.</summary>
    private const int CaveRings = 3;

    /// <summary>
    /// Sets a cave home's animals down (#10876): the first on the floor under its home, the rest about it at its kind's
    /// spacing, in rings, on resident floor near the home's height. Each is its own encounter in the home's region. A home
    /// whose floor is not found yet waits for the next wake pass.
    /// </summary>
    /// <param name="first">How many of its animals are on the ground already: the rest take places none of them stands in.</param>
    private void PlaceCaveHunter(HerdRecord record, WildlifeKind kind, Vector3 home, int count, CreatureSpace space, int first = 0)
    {
        // A home moved to its animals' navigation (#10925) is stood on at the height its support reads, which can lie a little
        // over the floor the voxels give.
        if ((record.ResitedMetres > 0 ? home.Y : space.FloorBelow(home.X, home.Z, home.Y, CaveFloorLookAboveMetres)) is not float homeFloor) return;
        float spacing = (float)Math.Max(kind.SpacingMetres, 1d);
        long region = HunterRegionBase + ++hunterRegions;
        int order = first, placed = 0;
        Vector2[] standing = first == 0 ? [] : [.. roster.All.Where(c => hunters.TryGetValue(c.Id, out Hunter? h) && h.Record.Key == record.Key).Select(c => c.Position)];
        for (int ring = 0; ring <= CaveRings && placed < count; ring++)
        {
            int places = ring == 0 ? 1 : CaveRingPlaces * ring;
            for (int at = 0; at < places && placed < count; at++)
            {
                double angle = 2 * Math.PI * at / places;
                Vector2 point = new(home.X + (float)(ring * spacing * Math.Sin(angle)), home.Z - (float)(ring * spacing * Math.Cos(angle)));
                float? found = ring == 0 ? homeFloor
                    : space.IsResident(new Vector3(point.X, homeFloor - 1f, point.Y)) ? space.FloorBelow(point.X, point.Y, homeFloor + CaveFloorReachMetres, CaveFloorLookAboveMetres) : null;
                if (found is not float floor || Math.Abs(floor - homeFloor) > CaveFloorReachMetres) continue;
                if (standing.Any(taken => Vector2.Distance(taken, point) < spacing / 2)) continue;
                int id = nextId;
                EncounterSite site = new(RegionKind.Dungeon, region, (long)Math.Floor(floor), long.MinValue, HasGround: true, ShoreIsReachable: true);
                if (!director.TryActivate(new EncounterCandidate(id, site, CreatureTraits.Walker, TimeWindow: 0), step, out EncounterDecision refusal))
                {
                    spawnRefusals++;
                    lastEvent = $"refused cave home {record.Key}: {refusal.Reason}";
                    return;
                }

                nextId++;
                Creature creature = new(id, KindOf(kind), point, space) { Feet = floor };
                roster.Add(creature);
                Admit(creature, kind, record, order++);
                placed++;
            }
        }

        if (placed == 0 && first > 0) return;
        lastEvent = string.Create(CultureInfo.InvariantCulture, $"cave home {record.Key} took up {placed} of {count}{(first > 0 ? $" more ({first} already up)" : "")} at {home.X:F0},{home.Y:F0},{home.Z:F0} in {space.Name}");
    }

    private void PlaceHunter(HerdRecord record, WildlifeKind kind, Vector2 around, int minimumMetres, int maximumMetres,
        Func<Vector2, bool>? usable = null, List<(long X, long Z)>? placed = null, int count = 1)
    {
        int order = 0;
        // A pack stands together, at its kind's own spacing.
        double separation = kind.Hunt!.Pack is null ? CreatureSpawnPlan.MinimumSeparationMetres : kind.SpacingMetres;
        Place(count, HunterRegionBase + ++hunterRegions, _ => KindOf(kind), minimumMetres, maximumMetres, usable, placed, around, creature =>
            Admit(creature, kind, record, order++), separation);
    }

    private void Admit(Creature creature, WildlifeKind kind, HerdRecord record, int order)
    {
        hunters[creature.Id] = new Hunter(kind, record, Wildlife!.WakeHunter(record, kind, creature.Position, order), order);
        hunted[record.Key] = hunted.GetValueOrDefault(record.Key) + 1;
    }

    /// <summary>One of a record's animals has left the ground.</summary>
    private void Unhunted(HerdRecord record)
    {
        int left = hunted[record.Key] - 1;
        if (left > 0) hunted[record.Key] = left;
        else hunted.Remove(record.Key);
    }

    private void ForgetHunter(int id)
    {
        if (!hunters.Remove(id, out Hunter? hunter)) return;
        Unhunted(hunter.Record);
        Wildlife?.ForgetHunter(hunter.Member);
    }

    /// <summary>A hostile animal placed by hand, a distance east and south of the player, in no region's population.</summary>
    internal string PlaceHunterNear(string kindId, double east, double south)
    {
        if (Wildlife is null) return "no wildlife";
        WildlifeKind? kind = Wildlife.HunterKinds.FirstOrDefault(candidate => candidate.Id == kindId);
        if (kind is null) return $"no hunter '{kindId}'; hunters: {string.Join(",", Wildlife.HunterKinds.Select(k => k.Id))}";
        Vector3 at = player.WorldPosition;
        Vector2 point = new(at.X + (float)east, at.Z + (float)south);
        int count = kind.GroupMaximum;
        Vector3 feet = player.WorldFeetPosition;
        if (PlayerSpace is not CreatureSpace space) return "no creatures live in the space the player is in";
        if (space != walking || walking.UnderRock(feet))
        {
            // In a separate space, or under the ground (#10876), it is set down on the player's own floor, as a cave home is.
            HerdRecord underground = new($"{kind.Id}@near#{++hunterRegions}", kind.Id, point, count) { Floor = feet.Y + 1f };
            PlaceCaveHunter(underground, kind, new Vector3(point.X, feet.Y + 1f, point.Y), count, space);
            return Readout();
        }

        PlaceHunter(new HerdRecord($"{kind.Id}@near#{++hunterRegions}", kind.Id, point, count), kind, point, 0, HunterHomeMetres, count: count);
        return Readout();
    }

    public void Restart()
    {
        ambushWaiting = 0;
        foreach (int id in hunters.Keys.ToArray()) ForgetHunter(id);
        roster.Clear();
        director.Clear();
        presentation.Clear();
        defeated = 0;
        released = 0;
        spawnRefusals = 0;
        lastPlayerAttack = "none";
        homeRouting.Clear();
        unroutableHomes = 0;
        resitedHomes = 0;
        started = false;
        Start();
    }

    public void Dispose()
    {
        roster.Clear();
        director.Clear();
        presentation.Dispose();
        started = false;
    }

    /// <summary>A creature's swing at the player, rolled and resolved against the player's defence.</summary>
    private void ResolveStrike(CreatureStrike strike)
    {
        int roll = KeyedDraws.AttackRoll(Seed, string.Create(CultureInfo.InvariantCulture, $"creature:{strike.CreatureId}:strike:{step}"));
        AttackOutcome outcome = CombatRules.Resolve(roll, strike.Attack, player.Sheet.Defence);
        if (!outcome.Hit)
        {
            // A blow that lands is heard as the player's hurt; one that misses is the swing going by.
            cues.Raise(Cue.Swing);
            lastEvent = string.Create(CultureInfo.InvariantCulture,
                $"creature {strike.CreatureId} missed the player: roll {outcome.Roll} total {outcome.Total} vs evasion {outcome.Defence}");
            return;
        }

        PlayerDefeatState after = player.Vitals.TakeHit(outcome.Damage, step);
        lastEvent = string.Create(CultureInfo.InvariantCulture,
            $"creature {strike.CreatureId} hit the player for {outcome.Damage}{(outcome.Critical ? " (critical)" : string.Empty)}; health {after.Health}/{after.MaximumHealth}");
    }

    private ulong Seed => terrain.Recipe.Contract.Seed;

    /// <summary>
    /// The player's swing at the nearest creature, preferring one that is already attacking. It
    /// resolves through the combat rules, and a defeat pays out through the player's progress.
    /// </summary>
    internal string PlayerAttack()
    {
        if (player.Vitals.IsDown)
        {
            return "the player is down";
        }

        // Only a creature on the player's own floor in the player's own space can be reached (R10876-2): one in a cave under
        // the player, however near across the ground, is not a target and takes no swing from one that is.
        CreatureSpace? here = PlayerSpace;
        (Creature Creature, double Distance)? chosen = CreatureSimulation.MeleeTarget(
            roster.All.Where(creature => creature.Awake && creature.Space == here).Select(creature => (creature, StandingAt(creature) + BodyOffFloor(creature))), player.WorldFeetPosition);
        Creature? target = chosen?.Creature;
        double best = chosen?.Distance ?? double.MaxValue;

        if (target is null || best > CharacterSheet.UnarmedReachMetres)
        {
            // With no creature in reach, the swing may find an animal instead (#10567).
            if (SwingElsewhere?.Invoke() is string elsewhere) return elsewhere;
            return target is null ? "no creatures"
                : string.Create(CultureInfo.InvariantCulture, $"out of reach: nearest {target.Id} at {best:F1}m");
        }

        // Each swing has its own scope, so two swings in one step do not share an outcome.
        swingSequence++;
        int roll = KeyedDraws.AttackRoll(Seed, string.Create(CultureInfo.InvariantCulture, $"player:swing:{swingSequence}"));
        (CombatantState struck, AttackOutcome outcome) = EncounterResolutionRules.Strike(roll, player.Sheet.Unarmed, target.Combat, step);
        if (!outcome.Hit)
        {
            cues.Raise(Cue.Swing);
            return string.Create(CultureInfo.InvariantCulture, $"missed {target.Id}: roll {outcome.Roll} vs defence {outcome.Defence}");
        }

        target.Combat = struck;
        // A blow wakes an animal lying at rest, and it knows where the player is (#10876).
        if (target.Behavior.State is CreatureState.Resting or CreatureState.Searching && !struck.IsDown)
        {
            target.Behavior = target.Behavior with { State = CreatureState.Alert };
            target.LastSeen = player.WorldFeetPosition;
        }

        if (hunters.TryGetValue(target.Id, out Hunter? struckHunter)) struckHunter.HurtSeconds = ClipSeconds(struckHunter, WildlifeClipRole.Hurt);
        cues.RaiseAt(struck.IsDown ? Cue.Defeat : Cue.Strike,
            SpaceOf(target).ToLocal(target.Position.X, StandingAt(target) + StruckHeightMetres, target.Position.Y));
        if (!struck.IsDown)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"hit {target.Id}: {struck.Health}/{struck.MaximumHealth} for {outcome.Damage}{(outcome.Critical ? " (critical)" : string.Empty)}");
        }

        if (hunters.Remove(target.Id, out Hunter? fallen))
        {
            Unhunted(fallen.Record);
            Wildlife!.HunterFell(fallen, StandingAt(target), SpaceOf(target));
        }

        roster.Remove(target.Id);
        director.Remove(target.Id);
        presentation.Sync(roster);
        defeated++;
        EncounterReward reward = EncounterResolutionRules.Reward(
            target.Kind.ExperienceAward,
            target.Kind.Loot,
            string.Create(CultureInfo.InvariantCulture, $"creature:{target.Id}"),
            KeyedDraws.For(Seed));
        ProgressionOutcome progression = player.Progress.Award(reward);
        lastDefeated = target.Id;
        return string.Create(CultureInfo.InvariantCulture,
            $"defeated {target.Id}; xp={progression.Experience} level={progression.Level}{(progression.Advanced ? " advanced" : string.Empty)} drops={string.Join(",", reward.Drops.Select(drop => $"{drop.ItemId}:{drop.Quantity}"))}");
    }

    /// <summary>Test aid (#10876): every creature at rest wakes to the player, as if it had seen them, so a cave's whole population hunts.</summary>
    internal string RouseAll()
    {
        int roused = 0;
        foreach (Creature creature in roster.All.Where(creature => creature.Behavior.State is CreatureState.Resting or CreatureState.Idle or CreatureState.Searching))
        {
            // A creature another test aid holds (#10926) keeps what it is held to.
            if (overrides.Count > 0 && (Frozen(creature) || ForcedState(creature) is not null)) continue;
            creature.Behavior = creature.Behavior with { State = CreatureState.Alert };
            creature.LastSeen = player.WorldFeetPosition;
            roused++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"roused {roused}");
    }

    /// <summary>Test aid: leaves every creature one health.</summary>
    /// <summary>
    /// Why creatures cannot route (#10924), on the navigation published around the player now: for <c>all</c> every awake
    /// creature whose last route was not reached (or one by id), how its feet and the player's read; for <c>homes</c>, how
    /// each held cave home's floor reads, flagging those no creature could start a route from.
    /// </summary>
    internal string WhyReadout(string which)
    {
        CreatureSpace here = walking;
        CreatureNavigation navigation = NavigationIn(here);
        navigation.EnsurePublished(player.WorldFeetPosition);
        StringBuilder lines = new();
        lines.Append("player: ").Append(navigation.Diagnose(player.WorldFeetPosition));
        if (which == "homes")
        {
            foreach (HerdRecord home in Wildlife?.CaveHomes ?? [])
            {
                float floor = HomeFloor(home) ?? (home.Floor ?? 0f);
                Vector3 site = new(home.Home.X, floor, home.Home.Y);
                // Read on its own kind's grid (#10925).
                CreatureNavigation its = HomeNavigation(home);
                its.EnsurePublished(player.WorldFeetPosition);
                string reading = its.Diagnose(site);
                // A home the published box does not reach is unread, not unroutable (R10924-1).
                string flag = reading.StartsWith(RoutableReading, StringComparison.Ordinal) ? "ok" : its.Covers(site) is null ? "UNROUTABLE" : "unread";
                lines.Append(string.Create(CultureInfo.InvariantCulture,
                    $" | {flag} {home.Key} at {home.Home.X:F1},{floor:F1},{home.Home.Y:F1}: {reading}"));
                // Where it could move to (#10925): each floor cell about it, and whether a search from there leads anywhere.
                if (flag == "UNROUTABLE") lines.Append("; open floor ").Append(its.OpenReadout(site, CreatureNavigation.DiagnoseLevelMetres, ResiteRingCells, ResiteTries));
            }

            return lines.ToString();
        }

        IEnumerable<Creature> asked = which == "all"
            ? roster.All.Where(creature => creature.Awake && SpaceOf(creature) == here && creature.RouteOutcome is not ("Reached" or "none"))
            : int.TryParse(which, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? roster.All.Where(creature => creature.Id == id) : [];
        foreach (Creature creature in asked)
        {
            Vector3 feet = Feet(creature);
            CreatureNavigation its = NavigationOf(creature);
            its.EnsurePublished(player.WorldFeetPosition);
            string reading = its.Diagnose(feet);
            // A start that is walkable but finds no way: how near the search got says whether the floors are joined.
            string reach = reading.StartsWith("its column supports it", StringComparison.Ordinal) ? $"; {its.Reach(feet, player.WorldFeetPosition)}" : "";
            // The player's footing on its own grid (#10925): a body this size may not fit where the player stands.
            string goal = its.GridId == NavigationIn(here).GridId ? "" : $"; the player's footing on its grid: {its.Diagnose(player.WorldFeetPosition)}";
            lines.Append(string.Create(CultureInfo.InvariantCulture,
                $" | id={creature.Id} kind={creature.Kind.Name} grid={its.GridId} route={creature.RouteOutcome} feet={feet.X:F1},{feet.Y:F1},{feet.Z:F1}: {reading}{reach}{goal}"));
        }

        return lines.ToString();
    }

    /// <summary>The navigation a cave home's kind routes on in the walking session (#10925).</summary>
    private CreatureNavigation HomeNavigation(HerdRecord home) =>
        Wildlife?.HunterKinds.FirstOrDefault(kind => kind.Id == home.KindId) is WildlifeKind kind ? NavigationIn(walking, BodyOf(kind)) : NavigationIn(walking);

    /// <summary>A home's floor is looked for this far under its site.</summary>
    private const double HomeFloorSearchMetres = 1.5;

    private float StandingAtHome(HerdRecord home) => GroundAt(home.Home);

    /// <summary>The step until which the test aid hides the player from every creature (#10878).</summary>
    private long unseenUntil;

    /// <summary>
    /// Test aid (#10878): no creature sees the player for some seconds, so hunters lose them where they stand and search
    /// there, by the player's own light, for captures; 0 ends it.
    /// </summary>
    internal string Unseen(double seconds)
    {
        unseenUntil = seconds <= 0 ? 0 : step + (long)Math.Ceiling(seconds * HunterRules.StepsPerSecond);
        return string.Create(CultureInfo.InvariantCulture, $"the player is unseen until step {unseenUntil} (now {step})");
    }

    internal string WeakenAll()
    {
        foreach (Creature creature in roster.All) creature.Combat = creature.Combat with { Health = 1 };
        return string.Create(CultureInfo.InvariantCulture, $"weakened {roster.All.Count()}");
    }

    internal string Readout()
    {
        if (!started)
        {
            return "creatures: not started";
        }

        Vector3 playerWorld = player.WorldPosition;
        PlayerDefeatState vitals = player.Vitals.State;
        string rows = string.Join(" | ", roster.All.Select(creature => string.Create(CultureInfo.InvariantCulture,
            $"id={creature.Id} kind={creature.Kind.Name} at={creature.Position.X:F2},{creature.Position.Y:F2} feet={StandingAt(creature):F2} awake={creature.Awake} route={creature.RouteOutcome} state={creature.Behavior.State} hp={creature.Combat.Health}/{creature.Combat.MaximumHealth} d={CreatureSimulation.PlanarDistance(creature.Position, playerWorld):F2}{(hunters.TryGetValue(creature.Id, out Hunter? shown) ? $" shows={shown.Member.Clip} display={shown.ThreatSeconds:F2}{HangReadout(shown)}{DraftReadout(shown, creature, playerWorld)}" : "")}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"step={step}; active={director.ActiveCount}; roster={roster.Count}; awake={roster.All.Count(creature => creature.Awake)}; shown={presentation.Shown}; released={released}; spawnRefusals={spawnRefusals}; unroutableHomes={unroutableHomes}; resitedHomes={resitedHomes}; settled={settled}; walkedToSupport={walkedToSupport}; goalsBeside={goalsMoved}; space={PlayerSpace?.Name ?? "none"} navigation=[{string.Join(" / ", NavigationsIn(PlayerSpace ?? walking).Select(navigation => navigation.Readout()))}]; ")
            + string.Create(CultureInfo.InvariantCulture,
            $"defeated={defeated} items={player.Progress.ItemsCollected} experience={player.Progress.Experience} level={player.Progress.Level} ")
            + string.Create(CultureInfo.InvariantCulture,
            $"player={vitals.Health}/{vitals.MaximumHealth} defeats={vitals.Defeats} outcome={player.Vitals.Outcome(step)} ")
            + $"attack={lastPlayerAttack} perception={perceptionStatus} failure={lastFailure}; last={lastEvent}; {rows}";
    }

    /// <summary>Publishes navigation around the player now and reports the publication.</summary>
    /// <summary>What routing has cost per update since the last reset: time (ambush, sensing and routing), navigation queries, pursuers.</summary>
    internal string RoutingReadout(bool reset)
    {
        // The cost per grid (#10925): each body size's queries and publications.
        CreatureNavigation[] grids = [.. NavigationsIn(PlayerSpace ?? walking)];
        string readout = routing.Readout() + " | " + (grids.Length == 0 ? "no grids yet" : string.Join(" | ", grids.Select(navigation => navigation.QueryReadout())))
            + $" | backingOff={roster.All.Count(creature => creature.Awake && step < creature.BackoffUntil)}";
        if (reset) foreach (CreatureNavigation navigation in grids) navigation.ResetQueries();
        if (reset) routing.Reset();
        return readout;
    }

    /// <summary>The route from a column to the player as navigation cells (x,y,z), for diagnosis.</summary>
    internal string RouteReadout(double x, double z, double? y = null)
    {
        Vector3 feet = player.WorldFeetPosition;
        CreatureNavigation navigation = NavigationHere;
        navigation.EnsurePublished(feet);
        Vector3 from = y is double height ? new Vector3((float)x, (float)height, (float)z) : Feet(new Vector2((float)x, (float)z));
        NavigationStepResult result = navigation.Step(from, feet, 4096);
        return $"outcome={result.Outcome} visited={result.Visited} cells=" + string.Join(" ", result.Path.ToArray().Select(cell => $"{cell.X},{cell.Y},{cell.Z}"));
    }

    internal string PublishNavigation()
    {
        try
        {
            return NavigationHere.Publish(player.WorldFeetPosition);
        }
        catch (EngineCallException exception)
        {
            return $"navigation refused: {exception.Message}";
        }
    }

    /// <summary>
    /// Gives each awake pursuer its next waypoint along the Engine's navigation (#9531). Navigation is
    /// published only while someone pursues. A pursuer keeps the whole path one query returns and
    /// walks it cell by cell, asking again only when the path is used up, the player has moved
    /// <see cref="ReplanMetres"/> from where it led, navigation was published again, or the pursuer
    /// is stuck. A failed query backs off, doubling to <see cref="MaximumBackoffSteps"/>, so a pursuer
    /// with no route waits instead of asking every update. Only a routed waypoint moves a pursuer.
    /// </summary>
    private void Route(Vector3 playerFeet, CreatureSpace? here)
    {
        // A searcher (#10876) is routed as a pursuer is, to where it last saw the player; routes are the player's space's.
        Creature[] pursuers = [.. roster.All.Where(creature => creature.Awake && here is not null && creature.Space == here
            && (creature.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking
                || (creature.Behavior.State == CreatureState.Searching && creature.LastSeen is not null)))];
        foreach (Creature idle in roster.All.Where(creature => !pursuers.Contains(creature)))
        {
            idle.Waypoint = null;
        }

        if (pursuers.Length == 0)
        {
            return;
        }

        // Each body size routes on its own grid (#10925), published once an update about the player while one of its size pursues.
        HashSet<CreatureNavigation> published = [];
        try
        {
            Vector2 hunted = new(playerFeet.X, playerFeet.Z);
            foreach (Creature creature in pursuers)
            {
                CreatureNavigation navigation = NavigationOf(creature);
                if (published.Add(navigation)) Publish(navigation, playerFeet);
                bool searching = creature.Behavior.State == CreatureState.Searching;
                Vector3 goalFeet = searching ? creature.LastSeen!.Value : playerFeet;
                Vector2 playerGround = searching ? new Vector2(goalFeet.X, goalFeet.Z) : hunted;
                double halt = searching ? WaypointArrivalMetres : creature.Kind.HaltDistanceMetres;
                // A hostile animal standing to show its threat or bite is not walking, so it is not stuck either.
                if (hunters.TryGetValue(creature.Id, out Hunter? hunter) && hunter.Holding) continue;
                if (!searching && Flanks(creature, hunter, playerGround)) continue;
                if (!searching && Rings(creature, hunter, playerGround)) continue;
                if (step < creature.BackoffUntil)
                {
                    creature.Waypoint = BeyondNavigation(creature, playerGround);
                    continue;
                }

                // A path used up beside the player needs no new one: the pursuer closes in directly.
                bool usedUp = creature.Path is Vector2[] walked && creature.PathIndex >= walked.Length
                    && Vector2.Distance(creature.Position, playerGround) > ReplanMetres;
                bool stale = creature.Path is null || usedUp
                    || Vector2.Distance(creature.PathGoal, playerGround) > ReplanMetres
                    || creature.PathRevision != navigation.Publishes
                    || creature.StuckSteps >= StuckSteps;
                // Outside the box its grid is published over (#10925), a query can only fail: it heads straight for the player as
                // one beyond navigation does, and asks once it is inside.
                if (navigation.Covers(Feet(creature)) is not null)
                {
                    creature.RouteOutcome = OutsideBox;
                    creature.Path = null;
                    creature.Waypoint = BeyondNavigation(creature, playerGround);
                    continue;
                }

                // A body that has stopped sliding along what it touches is set down afresh, free of the motion it carried.
                if (creature.StuckSteps >= StuckSteps) creature.Body = null;
                if (stale && !Replan(navigation, creature, goalFeet, playerGround))
                {
                    creature.Waypoint = BeyondNavigation(creature, playerGround);
                    continue;
                }

                Vector2[] cells = creature.Path!;
                while (creature.PathIndex < cells.Length && Vector2.Distance(cells[creature.PathIndex], creature.Position) <= WaypointArrivalMetres)
                {
                    creature.PathIndex++;
                }

                // Past the last cell the pursuer closes on the player directly.
                bool following = creature.PathIndex < cells.Length;
                Vector2 next = following ? cells[creature.PathIndex] : playerGround;
                // Following its path or closing on the player, a blocked pursuer is stuck; one at striking range has stopped.
                bool stuck = CreatureSimulation.NotClosing(creature.Waypoint is null ? null : creature.RoutedFrom, creature.Position, next,
                    Vector2.Distance(creature.Position, playerGround), halt, ProgressMetres);
                creature.StuckSteps = stuck ? creature.StuckSteps + 1 : 0;
                creature.RoutedFrom = creature.Position;
                creature.Waypoint = next;
            }
        }
        catch (EngineCallException failure)
        {
            lastFailure = $"navigation: {failure.Message}";
            foreach (Creature creature in pursuers)
            {
                creature.Waypoint = null;
            }
        }
    }

    /// <summary>Ask for a fresh path; a failure backs the pursuer off. Returns whether it has a path.</summary>
    private bool Replan(CreatureNavigation navigation, Creature creature, Vector3 playerFeet, Vector2 playerGround)
    {
        NavigationStepResult result = navigation.Step(Feet(creature), playerFeet);
        // Where its own grid's body cannot stand (#10925) - set down against rock, under a face too steep, dropped between
        // cells - the creature is settled on the nearest cell its grid supports and asked again; where the player stands no
        // such body could, it is routed to the nearest cell beside them that one could.
        if (result.Outcome == NavigationPathOutcome.StartNotWalkable && Settle(navigation, creature, playerFeet))
        {
            settled++;
            result = navigation.Step(Feet(creature), playerFeet);
        }

        // One set down where its grid's nearest support is too far to settle on walks to it, and is routed from there.
        if (result.Outcome == NavigationPathOutcome.StartNotWalkable && navigation.NearestSupport(Feet(creature)) is Vector3 footing)
        {
            creature.RouteOutcome = result.Outcome.ToString();
            creature.WaypointStep = step;
            creature.StuckSteps = 0;
            creature.RoutedFrom = null;
            creature.Path = [new Vector2(footing.X, footing.Z)];
            creature.PathIndex = 0;
            creature.PathGoal = playerGround;
            creature.PathRevision = navigation.Publishes;
            walkedToSupport++;
            return true;
        }

        if (result.Outcome == NavigationPathOutcome.GoalNotWalkable && navigation.NearestSupport(playerFeet) is Vector3 beside)
        {
            goalsMoved++;
            result = navigation.Step(Feet(creature), beside);
        }

        creature.RouteOutcome = result.Outcome.ToString();
        creature.WaypointStep = step;
        creature.StuckSteps = 0;
        creature.RoutedFrom = null;
        if (result.Outcome == NavigationPathOutcome.StartBlocked)
        {
            // A creature already in deep water has no route out by the map: it wades straight for the player
            // until it stands where routes start again.
            creature.Path = [];
            creature.PathIndex = 0;
            creature.PathGoal = playerGround;
            creature.PathRevision = navigation.Publishes;
            return true;
        }

        if (result.Outcome != NavigationPathOutcome.Reached)
        {
            creature.Path = null;
            creature.Waypoint = null;
            creature.BackoffSteps = creature.BackoffSteps == 0 ? FirstBackoffSteps : Math.Min(MaximumBackoffSteps, creature.BackoffSteps * 2);
            creature.BackoffUntil = step + creature.BackoffSteps;
            return false;
        }

        creature.Path = navigation.CellCentres(result.Path.Span);
        creature.PathIndex = 0;
        creature.PathGoal = playerGround;
        creature.PathRevision = navigation.Publishes;
        creature.BackoffSteps = 0;
        return true;
    }

    /// <summary>
    /// Sets a creature down on the nearest cell its grid supports on its level (#10925), one with a route to the player first, if one is within
    /// <see cref="SettleMetres"/>: its body is set
    /// down afresh there. Returns whether it moved.
    /// </summary>
    private bool Settle(CreatureNavigation navigation, Creature creature, Vector3 toward)
    {
        // Once in a while: settled, then refused again a step on, it walks to its footing instead of being set back.
        if (step - creature.SettledStep < SettleAgainSteps) return false;
        if (navigation.SettleSupport(Feet(creature), toward, CreatureNavigation.ClimbMetres, SettleMetres) is not Vector3 support) return false;
        creature.Position = new Vector2(support.X, support.Z);
        creature.Feet = support.Y;
        creature.Body = null;
        creature.SettledStep = step;
        return true;
    }

    /// <summary>The route outcome of a creature outside its grid's published box: no query is asked for it.</summary>
    internal const string OutsideBox = "OutsideBox";

    /// <summary>A creature is settled at most once in this many update steps (ten seconds).</summary>
    private const long SettleAgainSteps = 600;

    /// <summary>The furthest a creature is settled: a cell or two, so it is seen to shift, not to jump.</summary>
    private const double SettleMetres = 2.0;

    /// <summary>How many creatures a refused start has settled onto their grid, and how many routes went to a cell beside the player (#10925).</summary>
    private int settled, goalsMoved, walkedToSupport;

    private Vector3 Feet(Vector2 position) => new(position.X, GroundAt(position), position.Y);

    private Vector3 Feet(Creature creature) => new(creature.Position.X, StandingAt(creature), creature.Position.Y);

    /// <summary>
    /// A travel encounter (#9470): hostile creatures close in around the player, under the same
    /// encounter rules as the starting ones, in a wilderness region of their own. The player has
    /// just arrived, so the ambush waits until the ground around them is streamed, and takes only
    /// ground with a route to the player; it gives up after <see cref="AmbushPatienceSteps"/>.
    /// </summary>
    internal void Ambush(int count)
    {
        ambushes++;
        ambushWaiting = count;
        ambushSince = step;
        ambushRefused.Clear();
        ambushColumns.Clear();
        lastEvent = $"ambush: {count} hostile creature(s) waiting for the ground to stream";
    }

    /// <summary>Place what an ambush still owes, on streamed ground with a route to the player.</summary>
    private void CloseAmbush()
    {
        if (ambushWaiting <= 0) return;
        Vector3 feet = player.WorldFeetPosition;
        if (!walking.IsResident(Feet(new Vector2(feet.X, feet.Z)) - Vector3.UnitY)) return;
        WildlifeKind? hunter = Wildlife?.HunterKinds.FirstOrDefault();
        // The ambush's columns are tried on its hunter's own grid (#10925).
        CreatureNavigation navigation = hunter is null ? NavigationIn(walking) : NavigationIn(walking, BodyOf(hunter));
        try
        {
            Publish(navigation, feet);
            // A few columns per update, each with a small search; a column refused once is not tried again.
            int checks = 0;
            int placed = Place(ambushWaiting, AmbushRegionBase + ambushes, _ => hunter is null ? CreatureKinds.Hostile : KindOf(hunter),
                CreatureSpawnPlan.AmbushMinimumDistanceMetres, CreatureSpawnPlan.AmbushMaximumDistanceMetres,
                column =>
                {
                    if (ambushRefused.Contains(column) || !walking.IsResident(Feet(column) - Vector3.UnitY) || checks >= AmbushChecksPerUpdate) return false;
                    checks++;
                    bool routed = navigation.Step(Feet(column), feet, AmbushVisitedCells).Outcome == NavigationPathOutcome.Reached;
                    if (!routed) ambushRefused.Add(column);
                    return routed;
                }, ambushColumns, null, hunter is null ? null : creature =>
                {
                    HerdRecord record = new($"{hunter.Id}@ambush#{++hunterRegions}", hunter.Id, creature.Position, 1);
                    Admit(creature, hunter, record, 0);
                });
            ambushWaiting -= placed;
            ambushPlaced += placed;
        }
        catch (EngineCallException failure)
        {
            lastFailure = $"ambush navigation: {failure.Message}";
        }
        if (ambushWaiting > 0 && step - ambushSince < AmbushPatienceSteps) return;
        lastEvent = $"ambush: {ambushPlaced} hostile creature(s) close in{(ambushWaiting > 0 ? $", {ambushWaiting} found no way to the player" : "")}";
        ambushWaiting = 0;
        ambushPlaced = 0;
    }

    /// <summary>What the player's swing strikes when no creature is in reach (#10567: wildlife); null when nothing is.</summary>
    internal Func<string?>? SwingElsewhere { get; set; }

    /// <summary>What a navigation projection for another body costs over the creatures' box (#10567), on a grid of its own that no creature routes on.</summary>
    internal string MeasureNavigation(CharacterControllerConfig character)
    {
        try
        {
            return NavigationHere.MeasureBody(player.WorldFeetPosition, character);
        }
        catch (EngineCallException exception)
        {
            return $"navigation refused: {exception.Message}";
        }
    }

    /// <summary>
    /// Ground where no creature is placed (#9895): a town and its surroundings are a sanctuary. The
    /// product decides where towns stand; null places creatures anywhere the encounter rules allow.
    /// </summary>
    internal Func<double, double, bool>? Sanctuary { get; set; }

    /// <param name="placed">Columns this placement has already filled, kept across updates by a pending
    /// ambush (#9531): none is filled again, and new creatures keep their separation from all of them.</param>
    private int Place(int count, long regionId, Func<int, CreatureKind> kindFor, int minimumMetres, int maximumMetres,
        Func<Vector2, bool>? usable = null, List<(long X, long Z)>? placed = null, Vector2? around = null, Action<Creature>? joined = null,
        double separationMetres = CreatureSpawnPlan.MinimumSeparationMetres)
    {
        TerrainEncounterFacts facts = new(terrain.Recipe);
        Vector3 at = player.WorldPosition;
        Vector2 origin = around ?? new Vector2(at.X, at.Z);
        List<(long X, long Z)> chosen = CreatureSpawnPlan.Choose(
            CreatureSpawnPlan.Candidates((long)Math.Floor(origin.X), (long)Math.Floor(origin.Y), minimumMetres, maximumMetres),
            placed ?? [], count, column =>
            {
                if (usable?.Invoke(new Vector2(column.X + CellCentre, column.Z + CellCentre)) == false
                    || Sanctuary?.Invoke(column.X + CellCentre, column.Z + CellCentre) == true
                    || !facts.TryDescribe(RegionKind.Wilderness, regionId, column.X, column.Z, out EncounterSite site))
                {
                    return false;
                }

                int id = nextId;
                if (!director.TryActivate(new EncounterCandidate(id, site, CreatureTraits.Walker, TimeWindow: 0), step, out EncounterDecision refusal))
                {
                    spawnRefusals++;
                    lastEvent = $"refused ({column.X}, {column.Z}): {refusal.Reason}";
                    return false;
                }

                nextId++;
                Creature creature = new(id, kindFor(id), new Vector2(column.X + CellCentre, column.Z + CellCentre), walking);
                roster.Add(creature);
                joined?.Invoke(creature);
                return true;
            }, separationMetres);
        return chosen.Count;
    }

    /// <summary>
    /// Asks the Engine which creatures can see the player - line of sight and occlusion only; the
    /// sight range itself is the behaviour's planar rule - in one query for the whole roster.
    /// A creature the Engine does not answer for senses nothing this update, and a refused query
    /// is reported rather than guessed around.
    /// </summary>
    /// <param name="here">The player's space: only its creatures look, on its session (#10876).</param>
    private HashSet<int> Sense(Vector3 playerWorld, CreatureSpace here)
    {
        HashSet<int> senses = [];
        bool Looks(Creature creature) => creature.Space == here && Senses(creature);
        if (!roster.All.Any(Looks))
        {
            return senses;
        }

        PerceptionObserver[] observers = roster.All.Where(Looks).Select(creature => new PerceptionObserver(
            ProductIds.CreatureObserverBase + (ulong)creature.Id,
            here.ToLocal(creature.Position.X, StandingAt(creature) + EyeHeightMetres, creature.Position.Y),
            Vector3.UnitZ,
            CreatureSimulation.EngineSightRadius(creature.Kind.Tuning.SightRange * SightScale(creature), here.MaximumHeightDifference),
            AllAroundFacingCosine,
            FullEvidence)).ToArray();
        PerceptionTarget[] targets = [new PerceptionTarget(ProductIds.PlayerEntity, here.ToLocal(playerWorld))];
        try
        {
            uint cursor = 0;
            uint casts = 0;
            uint occluded = 0;
            uint beyond = 0;
            while (true)
            {
                PerceptionReadoutResult page = engine.Perception.QueryVisibility(new PerceptionQueryRequest(
                    here.Session, observers, targets, ReadOnlyMemory<SpatialEntityCollider>.Empty,
                    cursor, (uint)observers.Length));
                foreach (PerceptionPair pair in page.Pairs.Span)
                {
                    if (pair.Target == ProductIds.PlayerEntity)
                    {
                        senses.Add((int)(pair.Observer - ProductIds.CreatureObserverBase));
                    }
                }

                casts += page.VisibilityCasts;
                occluded += page.OcclusionRejects;
                beyond += page.DistanceRejects;
                if (!page.HasNextPairCursor)
                {
                    break;
                }

                cursor = page.NextPairCursor;
            }

            perceptionStatus = string.Create(CultureInfo.InvariantCulture,
                $"visible={senses.Count} casts={casts} occlusionRejects={occluded} beyondQueryRadius={beyond}");
        }
        catch (EngineCallException exception)
        {
            perceptionStatus = "refused";
            lastFailure = $"perception: {exception.Message}";
        }

        return senses;
    }

    /// <summary>
    /// Whether a creature looks for the player this update: any awake one, but one lying at rest only once the player is
    /// near enough to wake it (#10876), so a cave's resting population costs no sight.
    /// </summary>
    private bool Senses(Creature creature) => creature.Awake
        && (creature.Behavior.State != CreatureState.Resting
            || Vector3.Distance(Feet(creature), player.WorldFeetPosition) <= creature.Kind.Tuning.WakeRange * SightScale(creature) + EyeHeightMetres);

    /// <summary>The overworld ground of a column, from above: where a creature placed on the open ground is first set down.</summary>
    private float GroundAt(Vector2 position) =>
        walking.GroundAt(position.X, position.Y);

    /// <summary>The height a creature stands at: carried with it once known, else its column's ground in its space.</summary>
    private float StandingAt(Creature creature) => creature.Feet ?? SpaceOf(creature).GroundAt(creature.Position.X, creature.Position.Y);

    /// <summary>
    /// A creature not moved as a body keeps its feet on the surface below its reach from where they were
    /// (#9734), never the top of its column: under a roof it stays on the floor it walked onto. One with a
    /// body stands where each update's step leaves it (#9737).
    /// </summary>
    private void Follow(Creature creature)
    {
        if (creature.Body is not null) return;
        creature.Feet = CreatureSpaceRules.Followed(creature.Space, Feet(creature), CreatureNavigation.ClimbMetres) ?? creature.Feet;
    }
}
