using System.Globalization;
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
    internal const float NavigationHalfExtent = 16f;
    internal const float NavigationDepthBelow = 4f;
    internal const float NavigationHeightAbove = 8f;

    /// <summary>How often the walkable box around the player is republished.</summary>
    internal const int NavigationRepublishTicks = 60;

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
    private string lastEvent = "not started";
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

    internal void Update()
    {
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
            PerceptionFacts sensed = new(
                PlayerVisible: distance <= tuning.SightRange,
                DistanceToPlayer: distance,
                OwnHealth: combat[id].Health);
            CreatureBehaviorState state = CreatureBehaviorRules.Step(tuning, behavior[id], sensed, tick);
            behavior[id] = state;

            if (state.State is CreatureState.Pursuing or CreatureState.Attacking && distance > CreatureConstants.AttachDistanceMetres)
            {
                double step = CreatureConstants.PursueSpeedMetresPerSecond * CreatureConstants.TickSeconds;
                double scale = step / distance;
                positions[id] = new Vector2(
                    (float)(here.X + ((playerPosition.X - here.X) * scale)),
                    (float)(here.Y + ((playerPosition.Z - here.Y) * scale)));
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
                $"id={id} entity={entities[id]} at={at.X:F2},{at.Y:F2} state={behavior[id].State} hp={combat[id].Health}/{combat[id].MaximumHealth} d={distance:F2}");
        }));

        return string.Create(CultureInfo.InvariantCulture,
            $"tick={tick}; active={director.ActiveCount}; entities={entities.Count}; seed={terrain.Recipe.Contract.Seed}; nav={navigationStatus} cells={navigationWalkableCells} revision={navigationRevision} hash={navigationHash}; last={lastEvent}; {rows}");
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
            navigationStatus = receipt.WalkableCellCount > 0UL ? "published" : "no walkable cells";
        }
        catch (Exception exception)
        {
            navigationStatus = $"refused: {exception.Message}";
        }
    }

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
