using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>
/// Runs one encounter end to end inside the live product and reports the
/// transcript, so the loop can be observed through the ordinary debug transport
/// rather than only asserted in a check lane.
///
/// The scale is staged and stated: placement uses the **real generated terrain**
/// through <see cref="TerrainEncounterFacts"/>, and every decision comes from the
/// product's own rules, but the creature is not yet an Engine entity, so
/// perception is derived from distance and there is no navigation. It is
/// evidence that the loop resolves, not evidence that creatures walk.
/// </summary>
public sealed class EncounterProofModule : IDebugCommandModule
{
    private const int SearchStep = 4;
    private const int SearchRadius = 64;
    private const int MaximumTicks = 240;
    private const int ApproachPerTick = 1;
    private const int CreatureExperience = 120;
    private const long RespawnDelayTicks = 900;
    private const int CreatureHealth = 24;
    private const int CreatureArmour = 4;
    private const int CreatureEvasion = 60;
    private const int RollSeedSalt = 7919;

    private readonly TerrainWorld terrain;
    private readonly PlayerController player;

    internal EncounterProofModule(TerrainWorld terrain, PlayerController player, Func<string>? productHealth = null)
    {
        this.terrain = terrain;
        this.player = player;
        this.productHealth = productHealth;
    }

    private readonly Func<string>? productHealth;

    /// <summary>
    /// Reports the product's own update-boundary failures, so an exception that
    /// would otherwise only show up as a tainted runtime is named.
    /// </summary>
    [DebugCommand("craft.product.health")]
    public string Health() => productHealth is null ? "no health probe attached" : productHealth();

    [DebugCommand("craft.encounter.proof")]
    public string Proof()
    {
        TerrainEncounterFacts facts = new(terrain.Recipe);
        long playerX = (long)Math.Floor(player.WorldPosition.X);
        long playerZ = (long)Math.Floor(player.WorldPosition.Z);

        if (!TryFindSite(facts, playerX, playerZ, out long siteX, out long siteZ, out EncounterSite site))
        {
            return "encounter proof: no land site within search radius accepted a walker";
        }

        EncounterPolicy policy = EncounterPolicy.Default;
        EncounterDirector director = new(policy);
        EncounterCandidate candidate = new(Id: 1, site, CreatureTraits.Walker, TimeWindow: 0);
        if (!director.TryActivate(candidate, tick: 0, out string activated))
        {
            return $"encounter proof: the director refused the candidate: {activated}";
        }

        CharacterAttributes attributes = CharacterAttributes.Starting;
        AttackProfile swing = AttackProfile.Unarmed(attributes);
        CombatantState creature = CombatantState.Fresh(
            CreatureHealth,
            new DefenceProfile(CreatureEvasion, new ArmourProfile(CreatureArmour)));
        CreatureBehaviorState behavior = CreatureBehaviorState.Spawned;
        BehaviorTuning tuning = BehaviorTuning.Hostile;
        Random rolls = new(unchecked((int)((terrain.Recipe.Contract.Seed + (ulong)RollSeedSalt) & 0x7FFF_FFFF)));

        List<string> transcript = [];
        double distance = tuning.SightRange + 4;
        long tick = 0;
        int strikes = 0;
        for (; tick < MaximumTicks && !creature.IsDown; tick++)
        {
            distance = Math.Max(distance - ApproachPerTick, 0.5);
            PerceptionFacts sensed = new(
                PlayerVisible: distance <= tuning.SightRange,
                DistanceToPlayer: distance,
                OwnHealth: creature.Health);
            behavior = CreatureBehaviorRules.Step(tuning, behavior, sensed, tick);
            if (transcript.Count < MaximumTranscriptLines)
            {
                transcript.Add($"t{tick} d={distance:F1} {behavior.State}{(behavior.State == CreatureState.Attacking && behavior.CanAttack(tick, tuning) ? " (strikes)" : string.Empty)} hp={creature.Health}");
            }

            if (behavior.State != CreatureState.Attacking || !behavior.CanAttack(tick, tuning))
            {
                continue;
            }

            // The player's blow, drawn from a proof-scoped roll: the rules take a
            // roll as a parameter, so the proof supplies one and stays deterministic.
            int roll = rolls.Next(CombatRules.MinimumRoll, CombatRules.MaximumRoll + 1);
            (creature, AttackOutcome outcome) = EncounterResolutionRules.Strike(roll, swing, creature, tick);
            behavior = behavior.AfterAttack(tick);
            strikes++;
            if (transcript.Count < MaximumTranscriptLines)
            {
                transcript.Add($"t{tick} player roll {roll} -> {(outcome.Hit ? $"{outcome.Damage} damage" : "miss")}{(outcome.Critical ? " (critical)" : string.Empty)}; creature hp {creature.Health}");
            }
        }

        if (!creature.IsDown)
        {
            return $"encounter proof: the creature survived {MaximumTicks} ticks at {creature.Health} health after {strikes} strikes";
        }

        EncounterReward reward = EncounterResolutionRules.Reward(
            CreatureExperience,
            WolfTable,
            scope: "proof:wolf:1",
            draw: ProofDraw(terrain.Recipe.Contract.Seed));
        ProgressionOutcome progression = ProgressionRules.Award(0, CharacterLevel, reward.Experience);
        CombatantState respawned = EncounterResolutionRules.Respawn(
            creature.DownAtTick + RespawnDelayTicks,
            creature,
            RespawnDelayTicks);
        int despawned = director.Tick(tick, _ => distance, _ => true);

        string loot = reward.Drops.Length == 0
            ? "nothing"
            : string.Join(", ", reward.Drops.Select(drop => $"{drop.ItemId} x{drop.Quantity}"));

        return string.Join('\n',
        [
            $"encounter proof (staged: real terrain and real rules; perception by distance, no navigation, no entity yet)",
            $"site: ({siteX}, {siteZ}) region {site.RegionId} surface {site.SurfaceY} water {site.WaterLevel} shoreReachable {site.ShoreIsReachable}",
            $"director: {activated}; active {director.ActiveCount}",
            $"ticks {tick}, strikes {strikes}, final state {behavior.State}",
            $"transcript: {string.Join(" | ", transcript)}",
            $"defeat: xp {reward.Experience.Amount} from {reward.Experience.Source}; level {progression.PreviousLevel} -> {progression.Level} (advanced {progression.Advanced})",
            $"loot: {loot}",
            $"respawn: down at {creature.DownAtTick}, health {respawned.Health}/{respawned.MaximumHealth}, down {respawned.IsDown}",
            $"despawn sweep removed {despawned}; active now {director.ActiveCount}",
        ]);
    }

    [DebugCommand("craft.encounter.readout")]
    public string Readout()
    {
        TerrainEncounterFacts facts = new(terrain.Recipe);
        long x = (long)Math.Floor(player.WorldPosition.X);
        long z = (long)Math.Floor(player.WorldPosition.Z);
        bool described = facts.TryDescribe(RegionKind.Wilderness, 0, x, z, out EncounterSite here);
        SpawnVerdict walker = described
            ? SpawnRules.Evaluate(here.ToSpawnSite(), CreatureTraits.Walker)
            : new SpawnVerdict(false, SpawnRefusal.NoGround, "outside the world");
        return string.Join(';',
            $"tick={terrain.Recipe.Contract.Seed}",
            $"profile={facts.WaterLevel}",
            $"player={x},{z}",
            $"described={described}",
            $"surface={here.SurfaceY}",
            $"water={here.WaterLevel}",
            $"shore={here.ShoreIsReachable}",
            $"walkerAllowed={walker.Allowed}",
            $"refusal={walker.Refusal}");
    }

    private const int MaximumTranscriptLines = 14;
    private const int CharacterLevel = 1;

    private static readonly LootTable WolfTable = new(
        Id: "wolf",
        MinimumRolls: 1,
        MaximumRolls: 2,
        Entries:
        [
            new LootEntry("pelt", 1, 2, 2),
            new LootEntry("fang", 1, 1, 4),
            new LootEntry("relic", 1, 1, 40),
        ]);

    private static LootRules.Draw ProofDraw(ulong seed) => (scope, minimum, maximum) =>
    {
        ulong hash = seed;
        foreach (char character in scope)
        {
            hash = unchecked((hash * 1099511628211UL) ^ character);
        }

        return new Random(unchecked((int)(hash & 0x7FFF_FFFF))).Next(minimum, maximum + 1);
    };

    private static bool TryFindSite(
        TerrainEncounterFacts facts,
        long originX,
        long originZ,
        out long x,
        out long z,
        out EncounterSite site)
    {
        for (int ring = 0; ring <= SearchRadius; ring += SearchStep)
        {
            for (long offsetX = -ring; offsetX <= ring; offsetX += SearchStep)
            {
                for (long offsetZ = -ring; offsetZ <= ring; offsetZ += SearchStep)
                {
                    long candidateX = originX + offsetX;
                    long candidateZ = originZ + offsetZ;
                    if (!facts.TryDescribe(RegionKind.Wilderness, 1, candidateX, candidateZ, out EncounterSite candidate))
                    {
                        continue;
                    }

                    if (SpawnRules.Evaluate(candidate.ToSpawnSite(), CreatureTraits.Walker).Allowed)
                    {
                        x = candidateX;
                        z = candidateZ;
                        site = candidate;
                        return true;
                    }
                }
            }
        }

        x = 0;
        z = 0;
        site = default;
        return false;
    }
}
