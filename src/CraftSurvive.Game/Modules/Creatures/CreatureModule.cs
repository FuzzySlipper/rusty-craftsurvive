using System.Globalization;
using System.Text;
using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;
using Rusty.Engine.Debugging;
using Rusty.Engine.Entities;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>The creature's Engine-side row: where it is and what it is doing.</summary>
internal readonly record struct CreatureRuntimeComponent(
    float X,
    float Y,
    float Z,
    int State,
    int Health,
    int MaximumHealth);

internal static class CreatureConstants
{
    /// <summary>Distinct from the player's component key space, which uses 1.</summary>
    internal const uint RuntimeComponentLocalId = 0x1001U;

    internal const int InitialCreatureCount = 3;
    internal const double PursueSpeedMetresPerSecond = 2.0;

    /// <summary>Product ticks are paced by the Engine; this is the step the movement maths assumes.</summary>
    internal const double TickSeconds = 1.0 / 60.0;

    internal const int SearchStep = 4;
    internal const int SearchRadius = 96;
    internal const int CreatureHealth = 24;
    internal const int CreatureArmour = 4;
    internal const int CreatureEvasion = 60;
    internal const int AttachDistanceMetres = 2;

    // Collision-derived navigation, from the substrate proof's recipe: the Engine
    // does not derive navigation on its own, the product publishes a walkable
    // projection and queries cells relative to that published box.
    internal const ulong NavigationGridId = 1UL;
    internal const uint NavigationChunkSize = 16U;
    internal const uint NavigationMaxStepCells = 1U;
    internal const uint NavigationMaximumCells = 65_536U;
    internal const double NavigationAgentRadius = 0.3d;
    internal const double NavigationAgentHeight = 1.8d;
    internal const double NavigationMaximumSlopeDegrees = 45d;
    /// <summary>
    /// The published box must contain the creatures that ask about routes, not
    /// only the player: an index outside the box is a question about a cell that
    /// does not exist, and the Engine rightly refuses it.
    /// </summary>
    internal const float NavigationHalfExtent = 64f;

    internal const long NavigationCalibrationMinimumLevel = -4;

    internal const long NavigationCalibrationMaximumLevel = 12;

    /// <summary>Stride of the entry-point scan over the cell index space.</summary>
    internal const long NavigationEntryStride = 4;

    /// <summary>The player's own health pool, owned by the product.</summary>
    internal const int PlayerMaximumHealth = 40;

    /// <summary>Damage a creature's landed attack deals to the player.</summary>
    internal const int CreatureAttackDamage = 4;

    /// <summary>Ticks between a creature's attacks once it is in range.</summary>
    internal const long CreatureAttackCooldownTicks = 60;

    /// <summary>
    /// How close a creature must be to land a hit. Strictly wider than the distance
    /// a pursuer halts at, because a creature stops *at* the attach distance.
    /// </summary>
    internal const double CreatureAttackReachMetres = 3.0;

    /// <summary>Eye height above the surface, where a creature perceives from.</summary>
    internal const float EyeHeightMetres = 1.5f;

    internal const int PlayerAttackDamage = 6;

    /// <summary>Unarmed accuracy for the player's swing, as the rules define it.</summary>
    internal const int PlayerAttackAccuracy = 30;

    internal const double PlayerAttackReachMetres = 4.0;

    internal const int CreatureLootValue = 3;

    /// <summary>Perception pairs requested per query.</summary>
    internal const uint PerceptionPageSize = 8;
    internal const float NavigationDepthBelow = 4f;
    internal const float NavigationHeightAbove = 8f;

    /// <summary>How often the walkable box around the player is republished.</summary>
    internal const int NavigationRepublishTicks = 60;

    /// <summary>How often a creature re-asks whether the player is reachable.</summary>
    internal const int NavigationQueryTicks = 30;

    internal const uint NavigationMaxVisitedCells = 4_096U;

    /// <summary>How far above and below the creature's own level to sweep.</summary>
    internal const long NavigationLevelSweep = 3;

    /// <summary>
    /// Appearance facts are numbered by the product, and these start well clear of
    /// the player's 1 and the platform's 2.
    /// </summary>
    internal const ulong AppearanceIdBase = 0x1000UL;

    internal static readonly Vector3 BodyScale = new(0.8f, 1.6f, 0.8f);
    internal static readonly Color BodyColor = new(0.55f, 0.12f, 0.12f, 1f);

    /// <summary>
    /// Creatures are placed beyond the sight range a hostile creature uses, so a
    /// world spawn starts unaware instead of arriving already in the player's face.
    /// </summary>
    internal const int MinimumSpawnDistanceMetres = 56;
}

/// <summary>
/// The Engine half of the encounter: creatures exist as entities in an
/// <see cref="EntityStore"/> with a Transform and a creature row, their behaviour
/// comes from the product's own rules, and the encounter director is driven from
/// the product's update rather than from a proof command.
///
/// Perception and navigation are deliberately still simple - sight is distance
/// and a chase is a straight line - so that what this proves is the entity
/// binding and the loop running live, not a senses or pathfinding claim.
/// </summary>
public sealed class CreatureModule : IDebugCommandModule
{
    private static readonly ComponentType<CreatureRuntimeComponent> RuntimeComponent =
        ComponentType<CreatureRuntimeComponent>.Create(ProductComponentKeys.Create(CreatureConstants.RuntimeComponentLocalId));

    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly IEngineContext engine;
    private readonly EntityStore entityWorld = new([RuntimeComponent]);
    private readonly EncounterDirector director = new(EncounterPolicy.Default);
    private readonly Dictionary<int, EntityId> entities = [];
    private readonly Dictionary<int, CreatureState> states = [];
    private readonly Dictionary<int, CombatantState> combat = [];
    private readonly Dictionary<int, CreatureBehaviorState> behavior = [];
    private readonly Dictionary<int, Vector2> positions = [];
    private readonly Dictionary<int, Appearance> appearances = [];
    private int nextId = 1;
    private long tick;
    private ulong navigationRevision;
    private ulong navigationWalkableCells;
    private ulong navigationHash;
    private string navigationStatus = "not published";
    private Vector3 navigationWorldMin;

    /// <summary>
    /// Cells the Engine accepted when asked about themselves. World-to-cell mapping
    /// has been unguessable in practice, so routes are queried between cells the
    /// Engine has already confirmed are inside its projection, and nearest is
    /// decided in cell index space where both endpoints are expressed alike.
    /// </summary>
    private readonly List<PlanarNavCell> navigationEntryCells = [];

    private ulong navigationScannedRevision;

    private string navigationScanStatus = "not scanned";

    private PlayerDefeatState playerDefeat = PlayerDefeatState.Full(CreatureConstants.PlayerMaximumHealth);

    private readonly Dictionary<int, long> lastAttackTick = [];

    private long playerGraceUntilTick = long.MinValue;

    private string perceptionStatus = "unasked";

    private int defeated;

    private int lootAwarded;

    private readonly List<Appearance> retiringAppearances = [];

    private long swingSequence;

    private int playerExperience;

    private int playerLevel = 1;

    private const int CombatExperienceAward = 120;

    /// <summary>
    /// Placeholder creature drops: the shape of a table, not designed content. The
    /// entries exist so the reward path is exercised end to end; what a creature
    /// should actually drop is content work (task #8700).
    /// </summary>
    private static readonly LootTable CreatureLootTable = new(
        Id: "creature-placeholder",
        MinimumRolls: 1,
        MaximumRolls: 2,
        Entries:
        [
            new LootEntry("hide", 1, 2, 2),
            new LootEntry("claw", 1, 1, 4),
        ]);

    /// <summary>A deterministic draw in [minimum, maximum], keyed by the scope it is asked about.</summary>
    private static LootRules.Draw SeededDraw => (scope, minimum, maximum) =>
    {
        ulong hash = 14695981039346656037UL;
        foreach (char character in scope)
        {
            hash = unchecked((hash * 1099511628211UL) ^ character);
        }

        return new Random(unchecked((int)(hash & 0x7FFF_FFFF))).Next(minimum, maximum + 1);
    };

    private string lastPlayerAttack = "none";

    /// <summary>Where the player began, so a defeat can send them home.</summary>
    private Vector3 spawnPosition;
    private readonly Dictionary<int, string> routes = [];
    private string lastEvent = "not started";
    private string lastFailure = "none";
    private bool started;

    internal CreatureModule(IEngineContext engine, TerrainWorld terrain, PlayerController player)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain;
        this.player = player;
    }

    internal int Count => creatures_active;

    private int creatures_active => positions.Count;

    internal void Start()
    {
        if (started)
        {
            return;
        }

        spawnPosition = player.WorldPosition;
        TerrainEncounterFacts facts = new(terrain.Recipe);
        long originX = (long)Math.Floor(player.WorldPosition.X);
        long originZ = (long)Math.Floor(player.WorldPosition.Z);
        BehaviorTuning tuning = BehaviorTuning.Hostile;
        int placed = 0;
        int refused = 0;
        for (int ring = CreatureConstants.MinimumSpawnDistanceMetres; ring <= CreatureConstants.SearchRadius && placed < CreatureConstants.InitialCreatureCount; ring += CreatureConstants.SearchStep)
        {
            for (long offsetX = -ring; offsetX <= ring && placed < CreatureConstants.InitialCreatureCount; offsetX += CreatureConstants.SearchStep)
            {
                for (long offsetZ = -ring; offsetZ <= ring && placed < CreatureConstants.InitialCreatureCount; offsetZ += CreatureConstants.SearchStep)
                {
                    long x = originX + offsetX;
                    long z = originZ + offsetZ;
                    if (!facts.TryDescribe(RegionKind.Wilderness, 1, x, z, out EncounterSite site))
                    {
                        continue;
                    }

                    int id = nextId;
                    EncounterCandidate candidate = new(id, site, CreatureTraits.Walker, TimeWindow: 0);
                    if (!director.TryActivate(candidate, tick, out string reason))
                    {
                        refused++;
                        lastEvent = $"refused ({x}, {z}): {reason}";
                        continue;
                    }

                    nextId++;
                    placed++;
                    EntityId entity = entityWorld.Create();
                    appearances[id] = engine.Graphics.CreatePrimitive(new PrimitiveAppearanceRequest(
                        PrimitiveGeometry.Cube,
                        false,
                        CreatureConstants.BodyColor));
                    entities[id] = entity;
                    positions[id] = new Vector2(x, z);
                    states[id] = CreatureState.Idle;
                    behavior[id] = CreatureBehaviorState.Spawned;
                    combat[id] = CombatantState.Fresh(
                        CreatureConstants.CreatureHealth,
                        new DefenceProfile(CreatureConstants.CreatureEvasion, new ArmourProfile(CreatureConstants.CreatureArmour)));
                    Apply(id, tuning);
                    lastEvent = $"placed creature {id} at ({x}, {z}) surface {site.SurfaceY}";
                }
            }
        }

        PublishNavigation();
        started = true;
        lastEvent = $"started with {placed} creature(s), {refused} site(s) refused by the rules; {lastEvent}";
    }

    /// <summary>
    /// Runs the creature step, recording rather than propagating a failure: a
    /// thrown exception in here used to break the whole live session silently.
    /// </summary>
    internal void Update()
    {
        try
        {
            UpdateCore();
        }
        catch (Exception exception)
        {
            lastFailure = $"update {exception.GetType().Name}: {exception.Message}";
        }
    }

    private void RetireAppearances()
    {
        if (retiringAppearances.Count == 0)
        {
            return;
        }

        Appearance[] retiring = [.. retiringAppearances];
        retiringAppearances.Clear();
        foreach (Appearance appearance in retiring)
        {
            appearance.Dispose();
        }
    }

    private void UpdateCore()
    {
        // Released only after a frame has published a snapshot without them.
        RetireAppearances();

        if (player.AttackRequested)
        {
            lastPlayerAttack = AttackNearestCore();
        }

        ScanEntryPoints();
        if (PlayerDefeatRules.CanRespawn(playerDefeat, tick))
        {
            playerDefeat = PlayerDefeatRules.Respawn(playerDefeat);
            playerGraceUntilTick = PlayerDefeatRules.GraceUntil(tick);
            lastAttackTick.Clear();

            // Coming back where they fell means coming back inside the killer's
            // reach: measured live as a death every few seconds, and disinterest
            // alone cannot fix it because the behaviour rules re-derive "the player
            // is visible" from proximity every frame. A defeated player returns to
            // where they started, outside the ring the encounters spawn at.
            player.Teleport(spawnPosition.X, spawnPosition.Y, spawnPosition.Z);
            lastEvent = string.Create(CultureInfo.InvariantCulture,
                $"player respawned at {playerDefeat.Health} health after defeat {playerDefeat.Defeats}");
        }
        if (!started)
        {
            return;
        }

        tick++;
        if (tick % CreatureConstants.NavigationRepublishTicks == 0)
        {
            PublishNavigation();
        }

        BehaviorTuning tuning = BehaviorTuning.Hostile;
        Vector3 playerPosition = player.WorldPosition;
        foreach (int id in positions.Keys.ToArray())
        {
            Vector2 here = positions[id];
            double distance = Math.Sqrt(
                Math.Pow(here.X - playerPosition.X, 2) + Math.Pow(here.Y - playerPosition.Z, 2));
            // The Engine owns sight: it evaluates distance, facing and occlusion and
            // reports which observers can see which targets. The product's own
            // distance test stands only as the fallback, so a projection that cannot
            // answer degrades instead of blinding every creature.
            bool visible = false;
            double perceivedDistance = distance;
            try
            {
                float eyeY = terrain.Recipe.SurfaceAt((long)Math.Round(here.X), (long)Math.Round(here.Y))
                    + CreatureConstants.EyeHeightMetres;
                PerceptionObserver[] observers =
                    [new PerceptionObserver((ulong)id, new Vector3(here.X, eyeY, here.Y), Vector3.UnitZ,
                        tuning.SightRange, -1.0, 1.0)];
                PerceptionTarget[] targets =
                    [new PerceptionTarget(PlayerConstants.PlayerEntityId, playerPosition)];
                PerceptionReadoutLeaseReceipt receipt = engine.Perception.QueryVisibility(
                    new PerceptionQueryRequest(
                        terrain.Session, observers, targets, System.ReadOnlyMemory<Rusty.Engine.SpatialEntityCollider>.Empty, 0UL, 0, CreatureConstants.PerceptionPageSize));
                visible = false;
                foreach (PerceptionPair pair in receipt.Pairs.Span)
                {
                    if (pair.Target == PlayerConstants.PlayerEntityId)
                    {
                        visible = true;
                        perceivedDistance = pair.Distance;
                    }
                }

                perceptionStatus = string.Create(CultureInfo.InvariantCulture,
                    $"pairs={receipt.PairTotal} casts={receipt.VisibilityCasts} distanceRejects={receipt.DistanceRejects} occlusionRejects={receipt.OcclusionRejects}");
            }
            catch (Exception exception)
            {
                perceptionStatus = $"refused: {exception.GetType().Name}: {exception.Message}";
            }

            PerceptionFacts sensed = new(
                PlayerVisible: visible,
                DistanceToPlayer: perceivedDistance,
                OwnHealth: combat[id].Health);
            CreatureBehaviorState state = CreatureBehaviorRules.Step(tuning, behavior[id], sensed, tick);
            behavior[id] = state;

            // Navigation is consulted before closing, but never gates movement. A
            // definitive "no route" means the Engine found no path, not that the
            // creature should stand still: a pursuer closes directly instead, and the
            // route is reported for diagnosis rather than obeyed. The query is
            // throttled because it costs work, not because its answer decides.
            if (state.State is CreatureState.Pursuing or CreatureState.Attacking && distance > CreatureConstants.AttachDistanceMetres)
            {
                if (tick % CreatureConstants.NavigationQueryTicks == 0 || !routes.ContainsKey(id))
                {
                    float standingY = terrain.Recipe.SurfaceAt(
                        (long)Math.Round(here.X),
                        (long)Math.Round(here.Y)) + 1f;
                    routes[id] = QueryRoute(here, standingY, playerPosition, out bool definitive);
                }

                double step = CreatureConstants.PursueSpeedMetresPerSecond * CreatureConstants.TickSeconds;
                double scale = step / distance;
                positions[id] = new Vector2(
                    (float)(here.X + ((playerPosition.X - here.X) * scale)),
                    (float)(here.Y + ((playerPosition.Z - here.Y) * scale)));
            }

            // A creature in range attacks on its own cooldown, and the player's health
            // is the product's own state: this is how an encounter is lost. The
            // cooldown test must not subtract a sentinel: `tick - long.MinValue`
            // overflows, which silently made this step never fire.
            bool ready = !lastAttackTick.TryGetValue(id, out long previousAttack)
                || tick - previousAttack >= CreatureConstants.CreatureAttackCooldownTicks;
            if (state.State == CreatureState.Attacking
                && distance <= CreatureConstants.CreatureAttackReachMetres
                && ready
                && !PlayerDefeatRules.IsInvulnerable(playerGraceUntilTick, tick))
            {
                lastAttackTick[id] = tick;
                playerDefeat = PlayerDefeatRules.Strike(playerDefeat, CreatureConstants.CreatureAttackDamage, tick);
                lastEvent = string.Create(CultureInfo.InvariantCulture,
                    $"creature {id} hit the player for {CreatureConstants.CreatureAttackDamage}; health {playerDefeat.Health}/{playerDefeat.MaximumHealth}");
            }

            Apply(id, tuning);
        }

        director.Tick(
            tick,
            id => positions.TryGetValue(id, out Vector2 at)
                ? Math.Sqrt(Math.Pow(at.X - playerPosition.X, 2) + Math.Pow(at.Y - playerPosition.Z, 2))
                : double.MaxValue,
            _ => true);
    }

    /// <summary>
    /// The creature appearances for this frame. The product publishes one snapshot
    /// per frame, so creatures join that publication rather than a projection -
    /// a projection replaces the whole snapshot and would drop the player.
    /// </summary>
    internal AppearanceFact[] AppearanceFacts
    {
        get
        {
            AppearanceFact[] facts = new AppearanceFact[positions.Count];
            int index = 0;
            foreach ((int id, Vector2 at) in positions.OrderBy(pair => pair.Key))
            {
                long surface = terrain.Recipe.SurfaceAt((long)Math.Round(at.X), (long)Math.Round(at.Y));
                facts[index++] = new AppearanceFact(
                    CreatureConstants.AppearanceIdBase + (ulong)id,
                    false,
                    0,
                    new Transform(
                        new Vector3(at.X, surface + 1f, at.Y),
                        Quaternion.Identity,
                        CreatureConstants.BodyScale),
                    appearances[id],
                    Visible: true,
                    RenderLayer.Scene);
            }

            return facts;
        }
    }

    internal void Dispose()
    {
        // Handles retired by a kill but not yet re-published are released here too,
        // so a teardown within a frame of a kill does not leak them.
        RetireAppearances();

        foreach (Appearance appearance in appearances.Values)
        {
            appearance.Dispose();
        }

        appearances.Clear();
        foreach (EntityId entity in entities.Values)
        {
            entityWorld.Destroy(entity, entityWorld.GetEntityRevision(entity));
        }

        entities.Clear();
        positions.Clear();
        states.Clear();
        combat.Clear();
        behavior.Clear();
        director.Clear();
        started = false;
    }

    /// <summary>What the live lane can read: every creature, where it is, and what it is doing.</summary>
    [DebugCommand("craft.creatures.readout")]
    public string Readout()
    {
        try
        {
            return ReadoutCore();
        }
        catch (Exception exception)
        {
            lastFailure = $"readout {exception.GetType().Name}: {exception.Message}";
            return $"creatures readout failed: {lastFailure}";
        }
    }

    private string ReadoutCore()
    {
        if (!started)
        {
            return "creatures: not started";
        }

        Vector3 playerPosition = player.WorldPosition;
        string rows = string.Join(" | ", positions.OrderBy(pair => pair.Key).Select(pair =>
        {
            int id = pair.Key;
            Vector2 at = pair.Value;
            double distance = Math.Sqrt(
                Math.Pow(at.X - playerPosition.X, 2) + Math.Pow(at.Y - playerPosition.Z, 2));
            return string.Create(CultureInfo.InvariantCulture,
                $"id={id} entity={entities[id]} at={at.X:F2},{at.Y:F2} state={behavior[id].State} hp={combat[id].Health}/{combat[id].MaximumHealth} d={distance:F2} route={(routes.TryGetValue(id, out string? route) ? route : "unasked")}");
        }));

        return string.Create(CultureInfo.InvariantCulture,
            $"tick={tick}; active={director.ActiveCount}; entities={entities.Count}; seed={terrain.Recipe.Contract.Seed}; nav={navigationStatus} cells={navigationWalkableCells} defeated={defeated} loot={lootAwarded} reward=rules-experience-and-drops experience={playerExperience} level={playerLevel} attack={lastPlayerAttack} perception={perceptionStatus} player={playerDefeat.Health}/{playerDefeat.MaximumHealth} defeats={playerDefeat.Defeats} outcome={PlayerDefeatRules.Outcome(playerDefeat, tick)} {navigationScanStatus} revision={navigationRevision} hash={navigationHash}; last={lastEvent}; {rows}");
    }

    /// <summary>
    /// Publishes the walkable box around the player that the Engine derives
    /// navigation from. A refusal is recorded rather than thrown: a missing
    /// projection must not take the product down, and the readout has to say so.
    /// </summary>
    private void PublishNavigation()
    {
        Vector3 position = player.WorldPosition;
        CollisionNavigationConfig config = new(
            CreatureConstants.NavigationGridId,
            TerrainConstants.VoxelSize,
            CreatureConstants.NavigationChunkSize,
            CreatureConstants.NavigationMaxStepCells,
            CreatureConstants.NavigationAgentRadius,
            CreatureConstants.NavigationAgentHeight,
            CreatureConstants.NavigationMaximumSlopeDegrees,
            CreatureConstants.NavigationMaximumCells);
        Vector3 worldMin = position - new Vector3(
            CreatureConstants.NavigationHalfExtent,
            CreatureConstants.NavigationDepthBelow,
            CreatureConstants.NavigationHalfExtent);
        Vector3 worldMax = position + new Vector3(
            CreatureConstants.NavigationHalfExtent,
            CreatureConstants.NavigationHeightAbove,
            CreatureConstants.NavigationHalfExtent);

        try
        {
            NavigationReplaceReceipt receipt = engine.Spatial.ReplaceCollisionNavigation(
                new CollisionNavigationReplaceRequest(terrain.Session, worldMin, worldMax, config));
            navigationWalkableCells = receipt.WalkableCellCount;
            navigationRevision = receipt.NavigationRevision;
            navigationHash = receipt.ProjectionHash;
            navigationWorldMin = worldMin;
            navigationStatus = receipt.WalkableCellCount > 0UL ? "published" : "no walkable cells";
        }
        catch (Exception exception)
        {
            navigationStatus = $"refused: {exception.Message}";
        }
    }

    /// <summary>
    /// Asks the Engine for a route between the creature and the player, sweeping
    /// candidate levels around the **creature's own standing height** - the box
    /// origin is the player's, so seeding from the player's height offsets every
    /// level by the difference between the two. Every attempt is recorded, so one
    /// live sample shows which levels were tried and what each answered.
    /// </summary>
    private string QueryRoute(Vector2 from, float fromY, Vector3 to, out bool definitive)
    {
        long baseLevel = (long)Math.Floor((fromY - navigationWorldMin.Y) / TerrainConstants.VoxelSize);
        if (navigationEntryCells.Count > 0)
        {
            definitive = false;
            // Both endpoints must be cells the Engine itself accepted.
            PlanarNavCell entryStart = NearestEntry(from, fromY);
            PlanarNavCell entryGoal = NearestEntry(new Vector2(to.X, to.Z), to.Y);
            try
            {
                NavigationPathReadout entryPath = engine.Spatial.RequestNavigationPath(new NavigationPathRequest(
                    terrain.Session,
                    entryStart,
                    entryGoal,
                    CreatureConstants.NavigationMaxVisitedCells));
                definitive = true;
                return $"{entryPath.Outcome}({entryPath.PathLen}c,{entryPath.Visited}v) entries={navigationEntryCells.Count} start=({entryStart.X},{entryStart.Y},{entryStart.Z}) goal=({entryGoal.X},{entryGoal.Y},{entryGoal.Z})";
            }
            catch (Exception exception)
            {
                return $"refused:{exception.Message}";
            }
        }
        List<string> trace = [];
        definitive = false;

        // A cell outside the published box is not a walk question at all, so it is
        // treated as navigation not engaging rather than as "no path".
        long boxColumns = (long)Math.Max(1, (2 * CreatureConstants.NavigationHalfExtent) / TerrainConstants.VoxelSize);
        long startColumn = (long)Math.Floor((from.X - navigationWorldMin.X) / TerrainConstants.VoxelSize);
        long startRow = (long)Math.Floor((from.Y - navigationWorldMin.Z) / TerrainConstants.VoxelSize);
        long goalColumn = (long)Math.Floor((to.X - navigationWorldMin.X) / TerrainConstants.VoxelSize);
        long goalRow = (long)Math.Floor((to.Z - navigationWorldMin.Z) / TerrainConstants.VoxelSize);
        if (startColumn < 0 || startColumn >= boxColumns || startRow < 0 || startRow >= boxColumns
            || goalColumn < 0 || goalColumn >= boxColumns || goalRow < 0 || goalRow >= boxColumns)
        {
            return $"outofbox(start={startColumn},{startRow} goal={goalColumn},{goalRow} box={boxColumns})";
        }
        for (long delta = 0; delta <= CreatureConstants.NavigationLevelSweep; delta++)
        {
            long[] levels = delta == 0 ? [baseLevel] : [baseLevel - delta, baseLevel + delta];
            foreach (long level in levels)
            {
                try
                {
                    NavigationPathReadout path = engine.Spatial.RequestNavigationPath(new NavigationPathRequest(
                        terrain.Session,
                        CellAt(from.X, from.Y, level),
                        CellAt(to.X, to.Z, level),
                        CreatureConstants.NavigationMaxVisitedCells));
                    string verdict = $"{path.Outcome}({path.PathLen}c,{path.Visited}v)";
                    trace.Add($"{level}:{path.Outcome}");
                    if (!path.Outcome.ToString().Contains("Start", StringComparison.Ordinal))
                    {
                        definitive = true;
                        return $"{verdict} trace=[{string.Join(",", trace)}]";
                    }
                }
                catch (Exception exception)
                {
                    return $"refused:{exception.Message}";
                }
            }
        }

        return $"noanswer trace=[{string.Join(",", trace)}]";
    }

    /// <summary>
    /// What the Engine actually built for the box the product published: the
    /// geometry it is using and whether a navigation projection is present in it.
    /// The product supplies the box, so this is how the Engine's view of that box
    /// gets read back instead of assumed.
    /// </summary>
    [DebugCommand("craft.creatures.projection")]
    public string Projection()
    {
        try
        {
            uint columns = (uint)Math.Max(1, (2 * CreatureConstants.NavigationHalfExtent) / TerrainConstants.VoxelSize);
            SpatialMapRequest request = new(
                terrain.Session,
                navigationWorldMin,
                TerrainConstants.VoxelSize,
                columns,
                columns,
                navigationWorldMin.Y,
                navigationWorldMin.Y + CreatureConstants.NavigationDepthBelow + CreatureConstants.NavigationHeightAbove,
                navigationWorldMin.Y,
                navigationWorldMin.Y + CreatureConstants.NavigationDepthBelow + CreatureConstants.NavigationHeightAbove,
                System.ReadOnlyMemory<Rusty.Engine.SpatialEntityCollider>.Empty);
            SpatialMapSnapshot snapshot = SpatialMapSnapshot.Capture(
                engine.Spatial,
                request,
                new SpatialMapObservation("s4", player.WorldPosition, Vector3.UnitZ),
                ReadOnlySpan<SpatialMapAnnotation>.Empty,
                MaximumAnnotations);
            string ascii = snapshot.ToAscii();
            ReadOnlySpan<SpatialMapCell> cells = snapshot.Cells.Span;
            uint sampled = 0;
            uint allowed = 0;
            uint maximumSamples = 0;
            uint maximumAllowed = 0;
            foreach (SpatialMapCell cell in cells)
            {
                if (cell.NavigationSamples > 0)
                {
                    sampled++;
                }

                if (cell.NavigationAllowedSamples > 0)
                {
                    allowed++;
                }

                maximumSamples = Math.Max(maximumSamples, cell.NavigationSamples);
                maximumAllowed = Math.Max(maximumAllowed, cell.NavigationAllowedSamples);
            }
            return string.Create(CultureInfo.InvariantCulture,
                $"published min={navigationWorldMin} voxel={TerrainConstants.VoxelSize}; " +
                $"engine origin={snapshot.Geometry.Origin} cell={snapshot.Geometry.CellSize} columns={snapshot.Geometry.Columns} rows={snapshot.Geometry.Rows} " +
                $"navMinY={snapshot.Geometry.NavigationMinY} navMaxY={snapshot.Geometry.NavigationMaxY}; " +
                $"navigationPresent={snapshot.NavigationPresent} navRevision={snapshot.NavigationRevision} collisionRevision={snapshot.CollisionRevision}; " +
                $"cells total={cells.Length} sampled={sampled} allowed={allowed} maxSamples={maximumSamples} maxAllowed={maximumAllowed}; " +
                $"ascii[{Math.Min(ascii.Length, MaximumAsciiChars)}]={ascii[..Math.Min(ascii.Length, MaximumAsciiChars)]}");
        }
        catch (Exception exception)
        {
            return $"projection read refused: {exception.GetType().Name}: {exception.Message}";
        }
    }

    private const int MaximumAnnotations = 64;
    private const int MaximumAsciiChars = 700;

    /// <summary>
    /// The collision geometry's own support heights around a column, from the
    /// spatial map's cells. The generator's surface is not the collision surface -
    /// placing anything on the former leaves it inside terrain - so this is the
    /// source placement must use.
    ///
    /// The cell layout is not assumed: the command reports the candidate indices
    /// and their support ranges so the mapping can be read from one live sample.
    /// </summary>
    [DebugCommand("craft.terrain.support")]
    public string Support(double worldX, double worldZ)
    {
        try
        {
            // The debug spatial map caps at 1024 cells, so a window inside the
            // published box is captured rather than the whole box.
            long boxColumns = (long)Math.Max(1, (2 * CreatureConstants.NavigationHalfExtent) / TerrainConstants.VoxelSize);
            long queryColumn = (long)Math.Floor((worldX - navigationWorldMin.X) / TerrainConstants.VoxelSize);
            long queryRow = (long)Math.Floor((worldZ - navigationWorldMin.Z) / TerrainConstants.VoxelSize);
            long window = Math.Min(32, boxColumns);
            long windowColumn = Math.Clamp(queryColumn - (window / 2), 0, Math.Max(0, boxColumns - window));
            long windowRow = Math.Clamp(queryRow - (window / 2), 0, Math.Max(0, boxColumns - window));
            Vector3 windowOrigin = navigationWorldMin + new Vector3(
                (float)(windowColumn * TerrainConstants.VoxelSize),
                0f,
                (float)(windowRow * TerrainConstants.VoxelSize));
            float navMinY = navigationWorldMin.Y;
            float navMaxY = navigationWorldMin.Y + CreatureConstants.NavigationDepthBelow + CreatureConstants.NavigationHeightAbove;
            SpatialMapRequest request = new(
                terrain.Session,
                windowOrigin,
                TerrainConstants.VoxelSize,
                (uint)window,
                (uint)window,
                navMinY,
                navMaxY,
                navMinY,
                navMaxY,
                System.ReadOnlyMemory<Rusty.Engine.SpatialEntityCollider>.Empty);
            SpatialMapSnapshot snapshot = SpatialMapSnapshot.Capture(
                engine.Spatial,
                request,
                new SpatialMapObservation("support", player.WorldPosition, Vector3.UnitZ),
                ReadOnlySpan<SpatialMapAnnotation>.Empty,
                MaximumAnnotations);

            ReadOnlySpan<SpatialMapCell> cells = snapshot.Cells.Span;
            long column = queryColumn - windowColumn;
            long row = queryRow - windowRow;
            long rowMajor = (row * window) + column;
            long columnMajor = (column * window) + row;
            string rowMajorText = rowMajor >= 0 && rowMajor < cells.Length
                ? string.Create(CultureInfo.InvariantCulture,
                    $"rowMajor[{rowMajor}]={cells[(int)rowMajor].MinimumSupportY:F2}..{cells[(int)rowMajor].MaximumSupportY:F2}/samples={cells[(int)rowMajor].NavigationSamples}")
                : $"rowMajor[{rowMajor}] outside";
            string columnMajorText = columnMajor >= 0 && columnMajor < cells.Length
                ? string.Create(CultureInfo.InvariantCulture,
                    $"columnMajor[{columnMajor}]={cells[(int)columnMajor].MinimumSupportY:F2}..{cells[(int)columnMajor].MaximumSupportY:F2}/samples={cells[(int)columnMajor].NavigationSamples}")
                : $"columnMajor[{columnMajor}] outside";

            return string.Create(CultureInfo.InvariantCulture,
                $"window={window}x{window} origin={windowOrigin} query=({worldX:F1},{worldZ:F1}) -> cell=({column},{row}) of {cells.Length}; {rowMajorText} {columnMajorText}");
        }
        catch (Exception exception)
        {
            return $"support read refused: {exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>
    /// Queries a route from a creature's cell plus an offset, to the player. If the
    /// occupied column is excluded from the projection, an offset start should be
    /// the difference between a start-cell refusal and a real answer.
    /// </summary>
    [DebugCommand("craft.creatures.route")]
    public string Route(long offsetX, long offsetZ)
    {
        if (positions.Count == 0)
        {
            return "no creatures";
        }

        Vector2 from = positions.OrderBy(pair => pair.Key).First().Value;
        Vector3 to = player.WorldPosition;
        long creatureColumn = (long)Math.Floor((from.X - navigationWorldMin.X) / TerrainConstants.VoxelSize);
        long creatureRow = (long)Math.Floor((from.Y - navigationWorldMin.Z) / TerrainConstants.VoxelSize);
        long playerColumn = (long)Math.Floor((to.X - navigationWorldMin.X) / TerrainConstants.VoxelSize);
        long playerRow = (long)Math.Floor((to.Z - navigationWorldMin.Z) / TerrainConstants.VoxelSize);
        long baseLevel = (long)Math.Floor((terrain.Recipe.SurfaceAt((long)Math.Round(from.X), (long)Math.Round(from.Y)) + 1d - navigationWorldMin.Y) / TerrainConstants.VoxelSize);

        List<string> trace = [];
        for (long delta = 0; delta <= CreatureConstants.NavigationLevelSweep; delta++)
        {
            foreach (long level in delta == 0 ? new[] { baseLevel } : new[] { baseLevel - delta, baseLevel + delta })
            {
                try
                {
                    NavigationPathReadout path = engine.Spatial.RequestNavigationPath(new NavigationPathRequest(
                        terrain.Session,
                        new PlanarNavCell(creatureColumn + offsetX, level, creatureRow + offsetZ),
                        new PlanarNavCell(playerColumn, level, playerRow),
                        CreatureConstants.NavigationMaxVisitedCells));
                    trace.Add($"{level}:{path.Outcome}");
                    if (!path.Outcome.ToString().Contains("Start", StringComparison.Ordinal))
                    {
                        return $"offset=({offsetX},{offsetZ}) start=({creatureColumn + offsetX},{level},{creatureRow + offsetZ}) -> {path.Outcome}({path.PathLen}c,{path.Visited}v) trace=[{string.Join(",", trace)}]";
                    }
                }
                catch (Exception exception)
                {
                    return $"offset=({offsetX},{offsetZ}) refused: {exception.Message}";
                }
            }
        }

        return $"offset=({offsetX},{offsetZ}) start=({creatureColumn},{creatureRow}) noanswer trace=[{string.Join(",", trace)}]";
    }

    /// <summary>
    /// Searches cell coordinates for any that the navigation projection accepts,
    /// by asking for a path from a cell to itself. A cell that is not in the
    /// projection answers with a start refusal; one that is, answers about the
    /// walk. This finds where the projection actually is instead of assuming it
    /// shares the product's box arithmetic.
    /// </summary>
    /// <summary>Applies damage to the player, so an encounter can be lost as well as won.</summary>
    [DebugCommand("craft.player.strike")]
    public string StrikePlayer(int damage)
    {
        playerDefeat = PlayerDefeatRules.Strike(playerDefeat, damage, tick);
        return string.Create(CultureInfo.InvariantCulture,
            $"health={playerDefeat.Health}/{playerDefeat.MaximumHealth} defeats={playerDefeat.Defeats} respawnTick={playerDefeat.RespawnTick} outcome={PlayerDefeatRules.Outcome(playerDefeat, tick)}");
    }

    [DebugCommand("craft.player.attack")]
    public string AttackNearest() => AttackNearestCore();

    /// <summary>
    /// The attack itself, shared by the debug command and the input path so the two
    /// cannot drift: a key press and a console line resolve identically.
    /// </summary>
    internal string AttackNearestCore()
    {
        if (playerDefeat.Health <= 0)
        {
            // A defeated player swings at nothing: the same guard the defeat rules
            // apply on the receiving side, applied on the dealing side.
            return "the player is down";
        }

        if (positions.Count == 0)
        {
            return "no creatures";
        }

        Vector3 playerPosition = player.WorldPosition;
        string Roster() => string.Join(" ", positions.OrderBy(pair => pair.Key).Select(pair =>
        {
            double at = Math.Sqrt(Math.Pow(pair.Value.X - playerPosition.X, 2) + Math.Pow(pair.Value.Y - playerPosition.Z, 2));
            return string.Create(CultureInfo.InvariantCulture, $"{pair.Key}@{at:F1}m/{behavior[pair.Key].State}/hp{combat[pair.Key].Health}");
        }));

        int target = -1;
        double best = double.MaxValue;
        foreach ((int id, Vector2 at) in positions)
        {
            double distance = Math.Sqrt(
                Math.Pow(at.X - playerPosition.X, 2) + Math.Pow(at.Y - playerPosition.Z, 2));
            bool engaged = behavior[id].State == CreatureState.Attacking;
            bool bestEngaged = target >= 0 && behavior[target].State == CreatureState.Attacking;
            if (target < 0 || (engaged && !bestEngaged) || (engaged == bestEngaged && distance < best))
            {
                target = id;
                best = distance;
            }
        }

        if (target < 0 || best > CreatureConstants.PlayerAttackReachMetres)
        {
            return string.Create(CultureInfo.InvariantCulture, $"out of reach: {Roster()}");
        }

        // The player's blow resolves through the same rules the creature's attacks
        // and the unit lane use, so accuracy against evasion, criticals and armour
        // reduction all apply here too - a flat subtraction would make roughly a
        // third of swings land that the rules say should miss.
        AttackProfile swing = new(
            Accuracy: CreatureConstants.PlayerAttackAccuracy,
            Power: CreatureConstants.PlayerAttackDamage,
            Type: DamageType.Blunt);
        int range = CombatRules.MaximumRoll - CombatRules.MinimumRoll + 1;
        swingSequence++;
        // The nonce keeps two swings in one tick from sharing an outcome, which a
        // tick-only roll would do (two debug commands between frames resolve on the
        // same tick).
        int roll = (int)((tick * 37 + target + (swingSequence * 17)) % range) + CombatRules.MinimumRoll;
        (CombatantState struck, AttackOutcome outcome) = EncounterResolutionRules.Strike(
            roll, swing, combat[target], tick);
        if (!outcome.Hit)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"missed {target}: roll {outcome.Roll} vs defence {outcome.Defence}");
        }

        combat[target] = struck;
        if (!struck.IsDown)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"hit {target}: {struck.Health}/{struck.MaximumHealth} for {outcome.Damage}{(outcome.Critical ? " (critical)" : string.Empty)}");
        }

        // The creature leaves the world for good: its entity is destroyed through the
        // same call the dispose path uses, and every per-id store is cleared rather
        // than only the ones the readout happens to print.
        if (entities.TryGetValue(target, out EntityId leaving))
        {
            entityWorld.Destroy(leaving, entityWorld.GetEntityRevision(leaving));
        }

        positions.Remove(target);
        entities.Remove(target);
        behavior.Remove(target);
        combat.Remove(target);
        routes.Remove(target);
        states.Remove(target);
        lastAttackTick.Remove(target);
        if (appearances.TryGetValue(target, out Appearance? departing))
        {
            // Removal comes first and disposal comes later: the Engine refuses to
            // dispose an appearance that the last published snapshot still
            // references, so the creature is dropped from the published set now
            // and its handle is released once a frame has published without it.
            appearances.Remove(target);
            if (departing is not null)
            {
                retiringAppearances.Add(departing);
            }
        }
        // And it leaves the encounter director, so a killed creature is not still
        // counted as an active encounter.
        director.Remove(target);
        defeated++;
        // Reward is resolved in one call so the two halves cannot drift: the
        // experience award and the drops come from the same table and draw.
        EncounterReward reward = EncounterResolutionRules.Reward(
            CombatExperienceAward,
            CreatureLootTable,
            string.Create(CultureInfo.InvariantCulture, $"creature:{target}"),
            SeededDraw);
        ProgressionOutcome progression = ProgressionRules.Award(playerExperience, playerLevel, reward.Experience);
        playerExperience = progression.Experience;
        playerLevel = progression.Level;
        int dropped = 0;
        foreach (LootDrop drop in reward.Drops)
        {
            dropped += drop.Quantity;
        }

        lootAwarded += dropped;
        return string.Create(CultureInfo.InvariantCulture,
            $"defeated {target}; defeated={defeated} loot={lootAwarded} drops={reward.Drops.Length} xp={playerExperience} level={playerLevel}{(progression.Advanced ? " advanced" : string.Empty)} drops={string.Join(",", reward.Drops.Select(d => $"{d.ItemId}:{d.Quantity}"))}");
    }

    [DebugCommand("craft.creatures.scan")]
    public string Scan(long stride, long level)
    {
        long limit = (long)Math.Max(1, (2 * CreatureConstants.NavigationHalfExtent) / TerrainConstants.VoxelSize);
        List<string> accepted = [];
        List<string> refusedSamples = [];
        long asked = 0;
        for (long x = 0; x < limit; x += stride)
        {
            for (long z = 0; z < limit; z += stride)
            {
                asked++;
                try
                {
                    NavigationPathReadout path = engine.Spatial.RequestNavigationPath(new NavigationPathRequest(
                        terrain.Session,
                        new PlanarNavCell(x, level, z),
                        new PlanarNavCell(x, level, z),
                        CreatureConstants.NavigationMaxVisitedCells));
                    if (path.Outcome.ToString().Contains("Start", StringComparison.Ordinal))
                    {
                        if (refusedSamples.Count < 2)
                        {
                            refusedSamples.Add($"({x},{z})={path.Outcome}");
                        }
                    }
                    else
                    {
                        accepted.Add($"({x},{level},{z})={path.Outcome}");
                        if (accepted.Count >= 6)
                        {
                            return string.Create(CultureInfo.InvariantCulture,
                                $"stride={stride} level={level} asked={asked} accepted={string.Join(" ", accepted)}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    return $"scan refused at ({x},{z}): {exception.Message}";
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"stride={stride} level={level} limit={limit} asked={asked} accepted={accepted.Count} acceptedCells=[{string.Join(" ", accepted)}] refusedSamples=[{string.Join(" ", refusedSamples)}]");
    }

    /// <summary>
    /// Asks the Engine which cells are in its projection, by requesting a path from
    /// a cell to itself. Asked over an index space it can answer, unlike world
    /// positions, which have failed to map four times.
    /// </summary>
    private void ScanEntryPoints()
    {
        if (navigationWalkableCells == 0UL || navigationScannedRevision == navigationRevision)
        {
            return;
        }

        navigationScannedRevision = navigationRevision;
        navigationEntryCells.Clear();
        long limit = (long)Math.Max(1, (2 * CreatureConstants.NavigationHalfExtent) / TerrainConstants.VoxelSize);
        int asked = 0;
        for (long level = CreatureConstants.NavigationCalibrationMinimumLevel;
             level <= CreatureConstants.NavigationCalibrationMaximumLevel && navigationEntryCells.Count < 24;
             level++)
        {
            for (long column = 0; column < limit && navigationEntryCells.Count < 24; column += CreatureConstants.NavigationEntryStride)
            {
                for (long row = 0; row < limit && navigationEntryCells.Count < 24; row += CreatureConstants.NavigationEntryStride)
                {
                    asked++;
                    try
                    {
                        NavigationPathReadout path = engine.Spatial.RequestNavigationPath(new NavigationPathRequest(
                            terrain.Session,
                            new PlanarNavCell(column, level, row),
                            new PlanarNavCell(column, level, row),
                            CreatureConstants.NavigationMaxVisitedCells));
                        if (!path.Outcome.ToString().Contains("Start", StringComparison.Ordinal))
                        {
                            navigationEntryCells.Add(new PlanarNavCell(column, level, row));
                        }
                    }
                    catch (Exception exception)
                    {
                        navigationScanStatus = $"scan refused: {exception.GetType().Name}";
                        return;
                    }
                }
            }
        }

        navigationScanStatus = string.Create(CultureInfo.InvariantCulture,
            $"entries={navigationEntryCells.Count} asked={asked} revision={navigationRevision}");
    }

    /// <summary>The entry cell nearest a position's assumed cell, in index space.</summary>
    private PlanarNavCell NearestEntry(Vector2 from, float fromY)
    {
        long column = (long)Math.Floor((from.X - navigationWorldMin.X) / TerrainConstants.VoxelSize);
        long row = (long)Math.Floor((from.Y - navigationWorldMin.Z) / TerrainConstants.VoxelSize);
        PlanarNavCell best = navigationEntryCells[0];
        long bestDistance = long.MaxValue;
        foreach (PlanarNavCell cell in navigationEntryCells)
        {
            long distance = Math.Abs(cell.X - column) + Math.Abs(cell.Z - row);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = cell;
            }
        }

        return best;
    }

    private PlanarNavCell CellAt(float worldX, float worldZ, long level) => new(
        (long)Math.Floor((worldX - navigationWorldMin.X) / TerrainConstants.VoxelSize),
        level,
        (long)Math.Floor((worldZ - navigationWorldMin.Z) / TerrainConstants.VoxelSize));

    private void Apply(int id, BehaviorTuning tuning)
    {
        Vector2 at = positions[id];
        int surface = (int)terrain.Recipe.SurfaceAt((long)Math.Round(at.X), (long)Math.Round(at.Y));
        CreatureState state = behavior[id].State;
        states[id] = state;
        entityWorld.Set(
            entities[id],
            RuntimeComponent,
            new CreatureRuntimeComponent(
                at.X,
                surface + 1,
                at.Y,
                (int)state,
                combat[id].Health,
                combat[id].MaximumHealth));
    }
}
