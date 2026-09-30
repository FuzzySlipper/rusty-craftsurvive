using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// Creatures in the running product. Each update reads the player and the Engine's perception,
/// decides every creature's step through <see cref="CreatureSimulation"/>, applies blows to the
/// player's vitals, lets the encounter director release creatures that wandered out of the
/// encounter, and publishes the roster to the Engine.
///
/// The director owns membership, the roster mirrors it, and the presentation derives from the
/// roster; world positions reach the Engine only through the <see cref="WorldFrame"/>.
/// </summary>
internal sealed class CreatureModule : IProductModule
{
    /// <summary>Region the starting encounters are placed in.</summary>
    private const long StartingRegionId = 1;

    /// <summary>How many creatures a fresh session starts with.</summary>
    internal const int InitialCreatureCount = 3;

    /// <summary>Eye height above the ground, where a creature perceives from.</summary>
    private const float EyeHeightMetres = 1.5f;

    /// <summary>Creatures stand in the middle of a cell, never on the corner four columns share.</summary>
    private const float CellCentre = 0.5f;

    /// <summary>A creature looks in every direction: a facing cosine of -1 excludes nothing.</summary>
    private const double AllAroundFacingCosine = -1.0;

    private const double FullEvidence = 1.0;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly WorldFrame frame;
    private readonly EncounterDirector director = new(EncounterPolicy.Default);
    private readonly CreatureRoster roster = new();
    private readonly CreaturePresentation presentation;
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

    internal CreatureModule(IEngineContext engine, TerrainWorld terrain, PlayerController player, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        presentation = new CreaturePresentation(engine, frame, GroundAt);
        NavigationProbe = new CreatureNavigationProbe(engine, terrain, frame);
    }

    internal CreatureRoster Roster => roster;

    internal CreatureNavigationProbe NavigationProbe { get; }

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
        Spawn();
    }

    public void Update(ProductStep time)
    {
        if (!started)
        {
            return;
        }

        step = time.Step;
        if (player.AttackRequested)
        {
            lastPlayerAttack = PlayerAttack();
        }

        Vector3 playerWorld = player.WorldPosition;
        HashSet<int> seeing = Sense(playerWorld);
        bool playerCanBeHit = !player.Vitals.IsDown && !player.Vitals.IsInvulnerable(step);
        foreach (Creature creature in roster.All)
        {
            CreatureSense sense = new(seeing.Contains(creature.Id));
            if (CreatureSimulation.Step(creature, sense, playerWorld, playerCanBeHit, time) is CreatureStrike strike)
            {
                ResolveStrike(strike);
                playerCanBeHit = !player.Vitals.IsDown;
            }
        }

        IReadOnlyList<int> leaving = director.Tick(
            step,
            id => roster.TryGet(id, out Creature creature)
                ? CreatureSimulation.PlanarDistance(creature.Position, playerWorld)
                : double.MaxValue,
            _ => true);
        released += roster.Release(leaving);
        presentation.Sync(roster);
    }

    public void Restart()
    {
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

        if (target is null)
        {
            return "no creatures";
        }

        if (best > CharacterSheet.UnarmedReachMetres)
        {
            return string.Create(CultureInfo.InvariantCulture, $"out of reach: nearest {target.Id} at {best:F1}m");
        }

        // Each swing has its own scope, so two swings in one step do not share an outcome.
        swingSequence++;
        int roll = KeyedDraws.AttackRoll(Seed, string.Create(CultureInfo.InvariantCulture, $"player:swing:{swingSequence}"));
        (CombatantState struck, AttackOutcome outcome) = EncounterResolutionRules.Strike(roll, player.Sheet.Unarmed, target.Combat, step);
        if (!outcome.Hit)
        {
            return string.Create(CultureInfo.InvariantCulture, $"missed {target.Id}: roll {outcome.Roll} vs defence {outcome.Defence}");
        }

        target.Combat = struck;
        if (!struck.IsDown)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"hit {target.Id}: {struck.Health}/{struck.MaximumHealth} for {outcome.Damage}{(outcome.Critical ? " (critical)" : string.Empty)}");
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

    internal string Readout()
    {
        if (!started)
        {
            return "creatures: not started";
        }

        Vector3 playerWorld = player.WorldPosition;
        PlayerDefeatState vitals = player.Vitals.State;
        string rows = string.Join(" | ", roster.All.Select(creature => string.Create(CultureInfo.InvariantCulture,
            $"id={creature.Id} kind={creature.Kind.Name} at={creature.Position.X:F2},{creature.Position.Y:F2} state={creature.Behavior.State} hp={creature.Combat.Health}/{creature.Combat.MaximumHealth} d={CreatureSimulation.PlanarDistance(creature.Position, playerWorld):F2}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"step={step}; active={director.ActiveCount}; roster={roster.Count}; shown={presentation.Shown}; released={released}; spawnRefusals={spawnRefusals}; ")
            + string.Create(CultureInfo.InvariantCulture,
            $"defeated={defeated} items={player.Progress.ItemsCollected} experience={player.Progress.Experience} level={player.Progress.Level} ")
            + string.Create(CultureInfo.InvariantCulture,
            $"player={vitals.Health}/{vitals.MaximumHealth} defeats={vitals.Defeats} outcome={player.Vitals.Outcome(step)} ")
            + $"attack={lastPlayerAttack} perception={perceptionStatus} failure={lastFailure}; last={lastEvent}; {rows}";
    }

    /// <summary>Evaluates one navigation step from the nearest creature toward the player.</summary>
    internal string ProbeNavigation()
    {
        Creature? first = roster.All.FirstOrDefault();
        if (first is null)
        {
            return "no creatures";
        }

        Vector3 feet = new(first.Position.X, GroundAt(first.Position), first.Position.Y);
        try
        {
            return $"creature {first.Id}: {NavigationProbe.Probe(feet, player.WorldFeetPosition)}";
        }
        catch (EngineCallException exception)
        {
            return $"navigation refused: {exception.Message}";
        }
    }

    private void Spawn()
    {
        TerrainEncounterFacts facts = new(terrain.Recipe);
        Vector3 origin = player.WorldPosition;
        List<(long X, long Z)> placed = [];
        foreach ((long x, long z) in CreatureSpawnPlan.Candidates((long)Math.Floor(origin.X), (long)Math.Floor(origin.Z)))
        {
            if (placed.Count >= InitialCreatureCount)
            {
                break;
            }

            if (!CreatureSpawnPlan.FarEnoughFrom(placed, (x, z))
                || !facts.TryDescribe(RegionKind.Wilderness, StartingRegionId, x, z, out EncounterSite site))
            {
                continue;
            }

            int id = nextId;
            if (!director.TryActivate(new EncounterCandidate(id, site, CreatureTraits.Walker, TimeWindow: 0), step, out string reason))
            {
                spawnRefusals++;
                lastEvent = $"refused ({x}, {z}): {reason}";
                continue;
            }

            nextId++;
            placed.Add((x, z));
            roster.Add(new Creature(id, CreatureKinds.ForSpawn(id), new Vector2(x + CellCentre, z + CellCentre)));
        }

        presentation.Sync(roster);
        lastEvent = $"started with {roster.Count} creature(s), {spawnRefusals} site(s) refused by the rules";
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
        if (roster.Count == 0)
        {
            return senses;
        }

        PerceptionObserver[] observers = roster.All.Select(creature => new PerceptionObserver(
            ProductIds.CreatureObserverBase + (ulong)creature.Id,
            frame.ToLocal(creature.Position.X, GroundAt(creature.Position) + EyeHeightMetres, creature.Position.Y),
            Vector3.UnitZ,
            CreatureSimulation.EngineSightRadius(creature.Kind.Tuning.SightRange, MaximumHeightDifference),
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
                    0UL, cursor, (uint)observers.Length));
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

    /// <summary>The height a creature stands at: the ground of the column it is over.</summary>
    private float GroundAt(Vector2 position) =>
        terrain.GroundAt((long)Math.Floor(position.X), (long)Math.Floor(position.Y));
}
