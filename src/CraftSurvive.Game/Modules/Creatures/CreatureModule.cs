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
            $"tick={tick}; active={director.ActiveCount}; entities={entities.Count}; plains={terrain.Recipe.Contract.Seed}; last={lastEvent}; {rows}");
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
