using System.Globalization;
using System.Numerics;
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
internal sealed class CreatureModule : IProductModule
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
    private readonly WorldFrame frame;
    private readonly EncounterDirector director = new(EncounterPolicy.Default);
    private readonly CreatureRoster roster = new();
    private readonly CreaturePresentation presentation;
    private readonly CreatureNavigation navigation;
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

    internal CreatureModule(IEngineContext engine, TerrainWorld terrain, PlayerController player, WorldFrame frame, Func<bool> isNight, Cues cues)
    {
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        this.isNight = isNight ?? throw new ArgumentNullException(nameof(isNight));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        presentation = new CreaturePresentation(engine, frame, StandingAt, creature => hunters.ContainsKey(creature.Id));
        navigation = new CreatureNavigation(engine, terrain, frame);
        capsule = CreatureBody.Capsule(engine.Spatial.DefaultCharacterControllerConfig());
        // A body's step carries session-local positions; when the world's origin moves, each is set down afresh from where it stands.
        frame.Rebased += _ =>
        {
            foreach (Creature creature in roster.All) creature.Body = null;
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
        Vector3 toward = frame.ToLocal(waypoint.X, 0, waypoint.Y);
        bodiesStepped++;
        if (CreatureBody.Step(engine.Spatial, terrain.Session, body, config, new Vector2(toward.X, toward.Z), travel, seconds, creature.NextBodySequence)
            is not CreatureBodyState moved)
        {
            bodiesRefused++;
            creature.Body = body;
            return creature.Position;
        }

        creature.Body = moved;
        Vector3 feet = frame.ToWorld(CreatureBody.FeetUnder(moved.Centre, config));
        creature.Feet = feet.Y;
        return new Vector2(feet.X, feet.Z);
    }

    /// <summary>A body standing where the creature stands now.</summary>
    private CreatureBodyState Placed(Creature creature, CharacterControllerConfig config)
    {
        Vector3 centre = CreatureBody.CentreOver(frame.ToLocal(creature.Position.X, StandingAt(creature), creature.Position.Y), config);
        return new CreatureBodyState(centre, CreatureBody.AtRest(centre));
    }

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

        // The open world's creatures wait while the player is in a dungeon.
        if (player.InSeparateSpace)
        {
            return;
        }

        step = time.Step;
        if (player.AttackRequested)
        {
            lastPlayerAttack = PlayerAttack();
        }

        Vector3 playerWorld = player.WorldPosition;
        foreach (Creature creature in roster.All)
        {
            creature.Awake = terrain.IsResident(GroundCell(creature.Position, StandingAt(creature)));
        }

        long routingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        long queriesBefore = navigation.Queries;
        CloseAmbush();
        HashSet<int> seeing = Sense(playerWorld);
        // A pack hunts as one: what one sees they all come for, though a bite still needs its own sight.
        HashSet<int> alerted = HunterRules.Alerted(seeing, hunters, packAlarms, time.ElapsedSeconds);
        // With no steps admitted nothing has moved, and a pursuer standing still is not stuck.
        if (time.AdmittedSteps > 0) Route(player.WorldFeetPosition);
        routing.Record(System.Diagnostics.Stopwatch.GetElapsedTime(routingStarted).TotalMilliseconds, navigation.Queries - queriesBefore,
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
            CreatureSense sense = new(alerted.Contains(creature.Id));
            if (CreatureSimulation.Step(creature, sense, playerWorld, playerCanBeHit, time, SightScale(creature, alerted),
                CreatureSimulation.WaypointOrWait(creature.Position, creature.Waypoint), MoveBody) is CreatureStrike strike)
            {
                if (hunters.TryGetValue(creature.Id, out Hunter? biting)) HunterRules.Bite(biting, strike.Attack, ClipSeconds(biting, WildlifeClipRole.Bite));
                else ResolveStrike(strike);
                playerCanBeHit = !player.Vitals.IsDown;
            }

            Follow(creature);
            if (hunters.TryGetValue(creature.Id, out Hunter? hunter))
            {
                Show(creature, hunter, playerWorld, playerCanBeHit, seeing.Contains(creature.Id), time.ElapsedSeconds);
                playerCanBeHit = playerCanBeHit && !player.Vitals.IsDown;
            }
        }

        routing.RecordBodies(System.Diagnostics.Stopwatch.GetElapsedTime(bodiesStarted).TotalMilliseconds, bodiesStepped, bodiesRefused);

        IReadOnlyList<int> leaving = director.Tick(
            step,
            id => roster.TryGet(id, out Creature creature)
                ? CreatureSimulation.PlanarDistance(creature.Position, playerWorld)
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
        Vector2 heading = member.Velocity.Length() > HunterTurnsAboveMetres ? member.Velocity : state == CreatureState.Idle ? Vector2.Zero : toPlayer;
        member.Yaw = HunterRules.Turned(member.Yaw, heading, hunter.Kind.Speeds.TurnDegrees, seconds);
        member.Position = HunterRules.Drawn(creature.Position, member.Yaw, rig.Rig.HindMetres, rig.Rig.ForeMetres, member.Size);
        double runsAbove = (rig.Clip(WildlifeClipRole.Walk).Speed + rig.Clip(WildlifeClipRole.Run).Speed) / 2 * member.Size;
        member.Clip = HunterRules.Want(hunter, state, member.Velocity.Length(), runsAbove);
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
        navigation.Fit(BodyOf(kind));
        return hunterKinds[kind.Id] = HunterRules.KindOf(kind);
    }

    /// <summary>A hostile animal's capsule, its kind's size class: wide enough to hold its legs, so it is routed and stopped as what it is.</summary>
    private CharacterControllerConfig BodyOf(WildlifeKind kind)
    {
        WildlifeCapsule body = Wildlife!.CapsuleOf(kind);
        return CreatureBody.Capsule(engine.Spatial.DefaultCharacterControllerConfig(), body.Radius, body.Height);
    }

    /// <summary>The hostile animals to draw this update: only those on resident ground.</summary>
    internal IReadOnlyList<WildlifeHunter> HunterFacts() =>
        [.. roster.All.Where(creature => creature.Awake && hunters.ContainsKey(creature.Id))
            .Select(creature => new WildlifeHunter(hunters[creature.Id].Kind, hunters[creature.Id].Member, StandingAt(creature)))];

    /// <summary>
    /// A hostile animal's home has come near (#10615). It takes up its place once the ground there is
    /// resident, and one home holds one animal for as long as it is awake.
    /// </summary>
    internal void AdmitHunter(HerdRecord record, WildlifeKind kind, Vector2 point, int alive)
    {
        if (!started || hunted.ContainsKey(record.Key) || !terrain.IsResident(GroundCell(point))) return;
        PlaceHunter(record, kind, point, 0, HunterHomeMetres, count: alive);
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

        Vector3 playerWorld = player.WorldPosition;
        Creature? target = null;
        double best = double.MaxValue;
        foreach (Creature creature in roster.All)
        {
            double distance = CreatureSimulation.PlanarDistance(creature.Position, playerWorld);
            bool engaged = creature.Behavior.State == CreatureState.Attacking;
            bool bestEngaged = target?.Behavior.State == CreatureState.Attacking;
            if (target is null || (engaged && !bestEngaged) || (engaged == bestEngaged && distance < best))
            {
                target = creature;
                best = distance;
            }
        }

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
        if (hunters.TryGetValue(target.Id, out Hunter? struckHunter)) struckHunter.HurtSeconds = ClipSeconds(struckHunter, WildlifeClipRole.Hurt);
        cues.RaiseAt(struck.IsDown ? Cue.Defeat : Cue.Strike,
            frame.ToLocal(target.Position.X, StandingAt(target) + StruckHeightMetres, target.Position.Y));
        if (!struck.IsDown)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"hit {target.Id}: {struck.Health}/{struck.MaximumHealth} for {outcome.Damage}{(outcome.Critical ? " (critical)" : string.Empty)}");
        }

        if (hunters.Remove(target.Id, out Hunter? fallen))
        {
            Unhunted(fallen.Record);
            Wildlife!.HunterFell(fallen, StandingAt(target));
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
        return string.Create(CultureInfo.InvariantCulture,
            $"defeated {target.Id}; xp={progression.Experience} level={progression.Level}{(progression.Advanced ? " advanced" : string.Empty)} drops={string.Join(",", reward.Drops.Select(drop => $"{drop.ItemId}:{drop.Quantity}"))}");
    }

    /// <summary>Test aid: leaves every creature one health.</summary>
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
            $"id={creature.Id} kind={creature.Kind.Name} at={creature.Position.X:F2},{creature.Position.Y:F2} feet={StandingAt(creature):F2} awake={creature.Awake} route={creature.RouteOutcome} state={creature.Behavior.State} hp={creature.Combat.Health}/{creature.Combat.MaximumHealth} d={CreatureSimulation.PlanarDistance(creature.Position, playerWorld):F2}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"step={step}; active={director.ActiveCount}; roster={roster.Count}; awake={roster.All.Count(creature => creature.Awake)}; shown={presentation.Shown}; released={released}; spawnRefusals={spawnRefusals}; {navigation.Readout()}; ")
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
        string readout = routing.Readout() + " " + navigation.QueryReadout() + $" backingOff={roster.All.Count(creature => creature.Awake && step < creature.BackoffUntil)}";
        if (reset) { routing.Reset(); navigation.ResetQueries(); }
        return readout;
    }

    /// <summary>The route from a column to the player as navigation cells (x,y,z), for diagnosis.</summary>
    internal string RouteReadout(double x, double z)
    {
        Vector3 feet = player.WorldFeetPosition;
        navigation.EnsurePublished(feet);
        NavigationStepResult result = navigation.Step(Feet(new Vector2((float)x, (float)z)), feet, 4096);
        return $"outcome={result.Outcome} visited={result.Visited} cells=" + string.Join(" ", result.Path.ToArray().Select(cell => $"{cell.X},{cell.Y},{cell.Z}"));
    }

    internal string PublishNavigation()
    {
        try
        {
            return navigation.Publish(player.WorldFeetPosition);
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
    private void Route(Vector3 playerFeet)
    {
        Creature[] pursuers = [.. roster.All.Where(creature => creature.Awake
            && creature.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking)];
        foreach (Creature idle in roster.All.Where(creature => !pursuers.Contains(creature)))
        {
            idle.Waypoint = null;
        }

        if (pursuers.Length == 0)
        {
            return;
        }

        try
        {
            navigation.EnsurePublished(playerFeet);
            Vector2 playerGround = new(playerFeet.X, playerFeet.Z);
            foreach (Creature creature in pursuers)
            {
                // A hostile animal standing to show its threat or bite is not walking, so it is not stuck either.
                if (hunters.TryGetValue(creature.Id, out Hunter? hunter) && hunter.Holding) continue;
                if (Flanks(creature, hunter, playerGround)) continue;
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
                // A body that has stopped sliding along what it touches is set down afresh, free of the motion it carried.
                if (creature.StuckSteps >= StuckSteps) creature.Body = null;
                if (stale && !Replan(creature, playerFeet, playerGround))
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
                    Vector2.Distance(creature.Position, playerGround), creature.Kind.HaltDistanceMetres, ProgressMetres);
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
    private bool Replan(Creature creature, Vector3 playerFeet, Vector2 playerGround)
    {
        NavigationStepResult result = navigation.Step(Feet(creature), playerFeet);
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

    private Vector3 Feet(Vector2 position) => new(position.X, GroundAt(position), position.Y);

    private Vector3 Feet(Creature creature) => new(creature.Position.X, StandingAt(creature), creature.Position.Y);

    /// <summary>The block a creature stands on.</summary>
    private static VoxelAddress GroundCell(Vector2 position, float ground) =>
        new((long)Math.Floor(position.X), (long)Math.Floor(ground) - 1, (long)Math.Floor(position.Y));

    private VoxelAddress GroundCell(Vector2 position) => GroundCell(position, GroundAt(position));

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
        if (!terrain.IsResident(GroundCell(new Vector2(feet.X, feet.Z)))) return;
        try
        {
            navigation.EnsurePublished(feet);
            // A few columns per update, each with a small search; a column refused once is not tried again.
            int checks = 0;
            WildlifeKind? hunter = Wildlife?.HunterKinds.FirstOrDefault();
            int placed = Place(ambushWaiting, AmbushRegionBase + ambushes, _ => hunter is null ? CreatureKinds.Hostile : KindOf(hunter),
                CreatureSpawnPlan.AmbushMinimumDistanceMetres, CreatureSpawnPlan.AmbushMaximumDistanceMetres,
                column =>
                {
                    if (ambushRefused.Contains(column) || !terrain.IsResident(GroundCell(column)) || checks >= AmbushChecksPerUpdate) return false;
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

    /// <summary>What a navigation projection for another body costs over the creatures' box (#10567); theirs is published again on the next pursuit.</summary>
    internal string MeasureNavigation(CharacterControllerConfig character)
    {
        try
        {
            return navigation.MeasureBody(player.WorldFeetPosition, character);
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
                Creature creature = new(id, kindFor(id), new Vector2(column.X + CellCentre, column.Z + CellCentre));
                roster.Add(creature);
                joined?.Invoke(creature);
                return true;
            }, separationMetres);
        return chosen.Count;
    }

    /// <summary>
    /// The greatest height difference between a creature's eye and the player: the world's whole
    /// material span plus a standing eye.
    /// </summary>
    private double MaximumHeightDifference =>
        terrain.Recipe.MaximumMaterialY - terrain.Recipe.MinimumMaterialY + EyeHeightMetres;

    /// <summary>
    /// Asks the Engine which creatures can see the player - line of sight and occlusion only; the
    /// sight range itself is the behaviour's planar rule - in one query for the whole roster.
    /// A creature the Engine does not answer for senses nothing this update, and a refused query
    /// is reported rather than guessed around.
    /// </summary>
    private HashSet<int> Sense(Vector3 playerWorld)
    {
        HashSet<int> senses = [];
        if (!roster.All.Any(creature => creature.Awake))
        {
            return senses;
        }

        PerceptionObserver[] observers = roster.All.Where(creature => creature.Awake).Select(creature => new PerceptionObserver(
            ProductIds.CreatureObserverBase + (ulong)creature.Id,
            frame.ToLocal(creature.Position.X, StandingAt(creature) + EyeHeightMetres, creature.Position.Y),
            Vector3.UnitZ,
            CreatureSimulation.EngineSightRadius(creature.Kind.Tuning.SightRange * SightScale(creature), MaximumHeightDifference),
            AllAroundFacingCosine,
            FullEvidence)).ToArray();
        PerceptionTarget[] targets = [new PerceptionTarget(ProductIds.PlayerEntity, frame.ToLocal(playerWorld))];
        try
        {
            uint cursor = 0;
            uint casts = 0;
            uint occluded = 0;
            uint beyond = 0;
            while (true)
            {
                PerceptionReadoutResult page = engine.Perception.QueryVisibility(new PerceptionQueryRequest(
                    terrain.Session, observers, targets, ReadOnlyMemory<SpatialEntityCollider>.Empty,
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

    /// <summary>The ground of a column, from above: where a creature is first set down.</summary>
    private float GroundAt(Vector2 position) =>
        terrain.GroundAt(position.X, position.Y);

    /// <summary>The height a creature stands at: carried with it once known, else its column's ground.</summary>
    private float StandingAt(Creature creature) => creature.Feet ?? GroundAt(creature.Position);

    /// <summary>
    /// A creature not moved as a body keeps its feet on the surface below its reach from where they were
    /// (#9734), never the top of its column: under a roof it stays on the floor it walked onto. One with a
    /// body stands where each update's step leaves it (#9737).
    /// </summary>
    private void Follow(Creature creature)
    {
        if (creature.Body is not null) return;
        float from = StandingAt(creature);
        creature.Feet = terrain.GroundBelow(creature.Position.X, creature.Position.Y, from, CreatureNavigation.ClimbMetres) ?? creature.Feet;
    }
}
