using CraftSurvive.Game.Modules.Rpg;

// The adventurer's rules, checked without a host: damage and armour arithmetic,
// attack resolution boundaries, loot determinism, progression sources, and the
// placement rule that keeps a non-swimming creature out of the water.

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

// --- damage and armour arithmetic -------------------------------------------------
Require(ArmourRules.Reduce(100, DamageType.Cutting, new ArmourProfile(0)) == 100,
    "no armour must pass the whole blow");
Require(ArmourRules.Reduce(100, DamageType.Cutting, new ArmourProfile(10)) == 80,
    "ten points of cutting armour at two percent each must leave eighty");
Require(ArmourRules.Reduce(100, DamageType.Blunt, new ArmourProfile(10)) == 90,
    "blunt must find the gaps and use half the armour");
Require(ArmourRules.Reduce(100, DamageType.Piercing, new ArmourProfile(10)) == 85,
    "piercing must use three quarters of the armour");
Require(ArmourRules.Reduce(100, DamageType.Frost, new ArmourProfile(10)) == 90,
    "frost must be slowed by half the armour");
Require(ArmourRules.Reduce(100, DamageType.Fire, new ArmourProfile(40)) == 100,
    "fire must ignore armour entirely");
Require(ArmourRules.Reduce(100, DamageType.Cutting, new ArmourProfile(100)) == 25,
    "armour must never remove more than three quarters of a blow");
Require(ArmourRules.Reduce(1, DamageType.Cutting, new ArmourProfile(100)) == ArmourRules.MinimumDamage,
    "a landed blow must always do at least one point");
Require(ArmourRules.Reduce(0, DamageType.Cutting, new ArmourProfile(10)) == 0,
    "nothing incoming must stay nothing");

// --- attack resolution -------------------------------------------------------------
CharacterAttributes starting = CharacterAttributes.Starting;
AttackProfile unarmed = AttackProfile.Unarmed(starting);
DefenceProfile bare = new(Evasion: 60, Armour: ArmourProfile.None);
int exactHitRoll = bare.Evasion - unarmed.Accuracy;
Require(!CombatRules.Resolve(1, unarmed, bare).Hit, "the lowest roll must miss a starting evasion");
Require(CombatRules.Resolve(exactHitRoll, unarmed, bare).Hit, "meeting the evasion exactly must hit");
Require(!CombatRules.Resolve(exactHitRoll - 1, unarmed, bare).Hit, "one under the evasion must miss");
Require(unarmed.Accuracy < bare.Evasion,
    "an unarmed attack must not outclass a starting evasion, or the roll would not matter");
AttackOutcome ordinary = CombatRules.Resolve(50, unarmed, bare);
Require(ordinary.Hit && !ordinary.Critical, "a middling roll must land without being critical");
Require(ordinary.RawDamage == unarmed.Power + 5, "raw damage must be power plus one point per full ten of the roll");
AttackOutcome critical = CombatRules.Resolve(CombatRules.CriticalRoll, unarmed, bare);
Require(critical.Critical && critical.RawDamage == (unarmed.Power + (CombatRules.CriticalRoll / CombatRules.RollDamageDivisor)) * 2,
    "a critical blow must double the raw damage");
Require(CombatRules.Resolve(100, unarmed, new DefenceProfile(60, new ArmourProfile(10))).Damage
    == ArmourRules.Reduce(CombatRules.Resolve(100, unarmed, bare).RawDamage, unarmed.Type, new ArmourProfile(10)),
    "armour must apply to the raw damage the roll produced");
try
{
    CombatRules.Resolve(0, unarmed, bare);
    throw new InvalidOperationException("a roll of zero must be refused");
}
catch (ArgumentOutOfRangeException)
{
}

// --- derived statistics and progression --------------------------------------------
DerivedStatistics level1 = CharacterRules.Derive(starting, 1);
Require(level1.MaximumHealth == 30 && level1.MaximumStamina == 35,
    $"starting health and stamina are wrong: {level1}");
Require(level1.Evasion == 60 && level1.MeleePower == 12 && level1.CarryCapacity == 45,
    $"starting evasion, melee power or carry capacity is wrong: {level1}");
Require(CharacterRules.Derive(starting, 3).MaximumHealth == level1.MaximumHealth + (2 * CharacterRules.HealthPerLevel),
    "each level must add its health");

Require(ProgressionRules.ExperienceForLevel(1) == 0 && ProgressionRules.ExperienceForLevel(2) == 100
    && ProgressionRules.ExperienceForLevel(3) == 300 && ProgressionRules.ExperienceForLevel(4) == 600,
    "level thresholds are wrong");
Require(ProgressionRules.LevelFor(0) == 1 && ProgressionRules.LevelFor(99) == 1, "level one must start at zero experience");
Require(ProgressionRules.LevelFor(100) == 2 && ProgressionRules.LevelFor(299) == 2, "the second level threshold is wrong");
Require(ProgressionRules.LevelFor(300) == 3, "the third level threshold is wrong");

ProgressionOutcome combatAward = ProgressionRules.Award(0, 1, new ExperienceAward(ExperienceSource.Combat, 100));
Require(combatAward.Advanced && combatAward.Level == 2 && combatAward.Experience == 100,
    "a combat award of exactly one threshold must advance the character");
ProgressionOutcome discounted = ProgressionRules.Award(0, 1, new ExperienceAward(ExperienceSource.Enchanting, 1000));
Require(!discounted.Advanced && discounted.Experience == 0 && discounted.Level == 1,
    "experience from a source this product does not award must be refused");
Require(!ProgressionRules.AwardsExperience(ExperienceSource.Enchanting),
    "enchanting must not be an awarding source");
ProgressionOutcome empty = ProgressionRules.Award(50, 1, new ExperienceAward(ExperienceSource.Discovery, 0));
Require(!empty.Advanced && empty.Experience == 50, "an empty award must change nothing");

// --- loot determinism ---------------------------------------------------------------
LootTable table = new(
    Id: "wolf",
    MinimumRolls: 1,
    MaximumRolls: 2,
    Entries:
    [
        new LootEntry("pelt", 1, 2, 2),
        new LootEntry("fang", 1, 1, 5),
        new LootEntry("relic", 1, 1, 50),
    ]);

static LootRules.Draw SeededDraw(ulong seed) => (scope, minimum, maximum) =>
{
    // Deterministic per scope, mirroring the product's keyed draws: the same scope
    // and seed always produce the same number, so a table is reproducible.
    ulong hash = seed;
    foreach (char character in scope)
    {
        hash = (hash * 1099511628211UL) ^ character;
    }

    Random random = new((int)(hash & 0x7FFF_FFFF));
    return random.Next(minimum, maximum + 1);
};

LootDrop[] first = LootRules.Roll(table, "encounter:1", SeededDraw(1234));
LootDrop[] second = LootRules.Roll(table, "encounter:1", SeededDraw(1234));
Require(first.Length == second.Length, "the same seed must produce the same number of drops");
for (int index = 0; index < first.Length; index++)
{
    Require(first[index] == second[index], $"drop {index} differs between identical seeds");
}

LootDrop[] otherScope = LootRules.Roll(table, "encounter:2", SeededDraw(1234));
Require(!first.SequenceEqual(otherScope) || first.Length == 0,
    "a different scope must not be assumed to reproduce the same drops");
foreach (LootDrop drop in first)
{
    LootEntry entry = table.Entries.Single(candidate => candidate.ItemId == drop.ItemId);
    Require(drop.Quantity >= entry.Minimum && drop.Quantity <= entry.Maximum,
        $"{drop.ItemId} dropped {drop.Quantity}, outside {entry.Minimum}..{entry.Maximum}");
}

// Rarity arithmetic, without depending on luck: a one-in-one entry always drops and
// an entry rarer than the scale can never drop.
LootTable edges = new("edges", 1, 1, [new LootEntry("always", 1, 1, 1), new LootEntry("never", 1, 1, LootRules.RarityScale * 2)]);
LootDrop[] edgeDrops = LootRules.Roll(edges, "edges", SeededDraw(7));
Require(edgeDrops.Any(drop => drop.ItemId == "always"), "a one-in-one entry must always drop");
Require(!edgeDrops.Any(drop => drop.ItemId == "never"), "an entry rarer than the rarity scale must never drop");

// --- placement: never strand a creature that cannot survive the site ---------------
SpawnVerdict dryLand = SpawnRules.Evaluate(new SpawnSite(GroundY: 5, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker);
Require(dryLand.Allowed, "a walker on dry ground must be allowed");
SpawnVerdict lake = SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker);
Require(!lake.Allowed && lake.Refusal == SpawnRefusal.SubmergedWithoutSwimming,
    "a walker must never be placed under water");
SpawnVerdict shoreline = SpawnRules.Evaluate(new SpawnSite(GroundY: 2, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker);
Require(shoreline.Allowed, "a shoreline at the water line is fair game for a walker");
SpawnVerdict swimming = SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Amphibious);
Require(swimming.Allowed, "a swimmer with a reachable shore may be placed in water");
SpawnVerdict stranded = SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false), CreatureTraits.Amphibious);
Require(!stranded.Allowed && stranded.Refusal == SpawnRefusal.StrandedInWater,
    "a swimmer that cannot climb must not be placed where the shore is out of reach");
// The slice's rule is absolute for non-swimmers, and a climb out of a lake does not
// change that: the placement rule refuses the water, not merely the stranding.
SpawnVerdict climbingNonSwimmer = SpawnRules.Evaluate(
    new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false),
    new CreatureTraits(CanSwim: false, CanClimb: true, CanFly: false));
Require(!climbingNonSwimmer.Allowed && climbingNonSwimmer.Refusal == SpawnRefusal.SubmergedWithoutSwimming,
    "a creature that cannot swim must stay out of the water even when it could climb out");
Require(SpawnRules.Evaluate(
    new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false),
    new CreatureTraits(CanSwim: true, CanClimb: true, CanFly: false)).Allowed,
    "a swimmer that can climb out is not stranded");
Require(SpawnRules.Evaluate(
    new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false),
    new CreatureTraits(CanSwim: false, CanClimb: false, CanFly: true)).Allowed,
    "a flying creature cannot be stranded by water");
Require(SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: false, ShoreIsReachable: false), CreatureTraits.Walker).Refusal == SpawnRefusal.NoGround,
    "a site without ground must be refused outright");


// --- encounter policy: region and time, caps, and despawn ---------------------------
EncounterPolicy policy = EncounterPolicy.Default;
EncounterSite meadow = new(RegionKind.Wilderness, RegionId: 7, SurfaceY: 6, WaterLevel: 2, HasGround: true, ShoreIsReachable: true);
EncounterSite lakeBed = new(RegionKind.Wilderness, RegionId: 7, SurfaceY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true);
EncounterCandidate wolf = new(Id: 1, meadow, CreatureTraits.Walker, TimeWindow: 12);
EncounterCandidate drowned = new(Id: 2, lakeBed, CreatureTraits.Walker, TimeWindow: 12);

Require(EncounterRules.IsEligible(wolf, 12), "a candidate must be eligible in its own time window");
Require(!EncounterRules.IsEligible(wolf, 13), "a candidate must not be eligible outside its time window");
Require(!EncounterRules.CanActivate(policy, wolf, 13, 0, 0).Act,
    "activation must be refused outside the current time window even with room to spare");
Require(EncounterRules.CanActivate(policy, wolf, 12, 0, 0).Act, "an eligible candidate in a region with room must activate");
Require(!EncounterRules.CanActivate(policy, wolf, 12, policy.MaximumActive, 0).Act,
    "the world-wide cap must refuse activation");
Require(!EncounterRules.CanActivate(policy, wolf, 12, 1, policy.MaximumActivePerRegion).Act,
    "the per-region cap must refuse activation");
EncounterDecision submergedActivation = EncounterRules.CanActivate(policy, drowned, 12, 0, 0);
Require(!submergedActivation.Act && submergedActivation.Reason.Contains("cannot swim"),
    $"a walker must not be activated under water: {stranded.Reason}");

ActiveEncounter active = new(Id: 1, RegionKind.Wilderness, RegionId: 7, ActivatedAtTick: 0, AwaySinceTick: -1);
Require(!EncounterRules.ShouldDespawn(policy, 100, active, distanceToPlayer: 10, regionResident: true).Act,
    "an encounter the player is standing next to must stay");
Require(EncounterRules.ShouldDespawn(policy, 100, active, distanceToPlayer: 10, regionResident: false).Act,
    "an encounter whose region is no longer resident must go, wherever the player is");
Require(!EncounterRules.ShouldDespawn(policy, 100, active, policy.DespawnDistance + 1, regionResident: true).Act,
    "being out of reach is not on its own enough to despawn");
ActiveEncounter away = EncounterRules.MarkAway(active, tick: 100, policy.DespawnDistance + 1, policy);
Require(away.IsAway && away.AwaySinceTick == 100, "leaving reach must start the grace period once");
Require(!EncounterRules.ShouldDespawn(policy, 100 + policy.DespawnGraceTicks - 1, away, policy.DespawnDistance + 1, true).Act,
    "the grace period must be honoured to the tick");
Require(EncounterRules.ShouldDespawn(policy, 100 + policy.DespawnGraceTicks, away, policy.DespawnDistance + 1, true).Act,
    "the encounter must despawn once the grace period elapses");
Require(!EncounterRules.MarkPresent(away, policy.DespawnDistance - 1, policy).IsAway,
    "coming back into reach must clear the grace period");
ActiveEncounter settled = EncounterRules.MarkAway(active, tick: 100, distanceToPlayer: 5, policy);
Require(!settled.IsAway, "an encounter the player is near must not start a grace period");


// --- creature behaviour: the state machine over sensed facts ------------------------
BehaviorTuning hostile = BehaviorTuning.Hostile;
BehaviorTuning neutral = BehaviorTuning.Neutral;
CreatureBehaviorState spawned = CreatureBehaviorState.Spawned;

CreatureBehaviorState unaware = CreatureBehaviorRules.Step(
    hostile, spawned, new PerceptionFacts(PlayerVisible: false, DistanceToPlayer: 1.0, OwnHealth: 10), tick: 0);
Require(unaware.State == CreatureState.Idle, "a creature that cannot see the player must stay idle");

CreatureBehaviorState distant = CreatureBehaviorRules.Step(
    hostile, spawned, new PerceptionFacts(true, DistanceToPlayer: hostile.SightRange + 1, OwnHealth: 10), tick: 0);
Require(distant.State == CreatureState.Idle, "a creature must not notice the player beyond its sight range");

CreatureBehaviorState alerted = CreatureBehaviorRules.Step(
    hostile, spawned, new PerceptionFacts(true, DistanceToPlayer: hostile.SightRange - 1, OwnHealth: 10), tick: 0);
Require(alerted.State == CreatureState.Alert, "first sight within range must raise the alarm, not start a chase");

CreatureBehaviorState pursuing = CreatureBehaviorRules.Step(
    hostile, alerted, new PerceptionFacts(true, DistanceToPlayer: 10.0, OwnHealth: 10), tick: 1);
Require(pursuing.State == CreatureState.Pursuing, "an alerted creature must close on the player");

CreatureBehaviorState striking = CreatureBehaviorRules.Step(
    hostile, pursuing, new PerceptionFacts(true, DistanceToPlayer: hostile.AttackRange, OwnHealth: 10), tick: 2);
Require(striking.State == CreatureState.Attacking, "a creature in reach must attack");
Require(striking.CanAttack(tick: 2, hostile), "a creature that has not struck yet must be able to strike");
CreatureBehaviorState afterStrike = striking.AfterAttack(tick: 2);
Require(!afterStrike.CanAttack(tick: 2 + hostile.AttackCooldownTicks - 1, hostile),
    "the attack cooldown must hold the creature back");
Require(afterStrike.CanAttack(tick: 2 + hostile.AttackCooldownTicks, hostile),
    "the attack cooldown must expire on its tick");

CreatureBehaviorState backedOff = CreatureBehaviorRules.Step(
    hostile, striking, new PerceptionFacts(true, DistanceToPlayer: 10.0, OwnHealth: 10), tick: 3);
Require(backedOff.State == CreatureState.Pursuing, "a player who backs off must be chased again");

CreatureBehaviorState lost = CreatureBehaviorRules.Step(
    hostile, pursuing, new PerceptionFacts(false, DistanceToPlayer: 1.0, OwnHealth: 10), tick: 4);
Require(lost.State == CreatureState.Idle, "losing sight must return the creature to idle");

CreatureBehaviorState wounded = CreatureBehaviorRules.Step(
    hostile, striking, new PerceptionFacts(true, DistanceToPlayer: 1.0, OwnHealth: CreatureBehaviorRules.DeathHealth), tick: 5);
Require(wounded.State == CreatureState.Dead, "a creature at zero health must die");
CreatureBehaviorState remainsDead = CreatureBehaviorRules.Step(
    hostile, wounded, new PerceptionFacts(true, DistanceToPlayer: 0.5, OwnHealth: 5), tick: 6);
Require(remainsDead.State == CreatureState.Dead, "death must be terminal, even if health is restored");

CreatureBehaviorState calm = CreatureBehaviorRules.Step(
    neutral, spawned, new PerceptionFacts(true, DistanceToPlayer: 0.5, OwnHealth: 10), tick: 0);
Require(calm.State == CreatureState.Alert, "a neutral creature must notice the player");
Require(!calm.CanAttack(tick: 0, neutral), "a neutral creature must never attack, even in reach");
CreatureBehaviorState stillCalm = CreatureBehaviorRules.Step(
    neutral, calm, new PerceptionFacts(true, DistanceToPlayer: 0.5, OwnHealth: 10), tick: 10);
Require(stillCalm.State == CreatureState.Alert, "a neutral creature must not start pursuing");


// --- one encounter end to end, in policy: fight, die, loot, respawn, advance --------
CombatantState wolf2 = CombatantState.Fresh(maximumHealth: 24, new DefenceProfile(Evasion: 60, Armour: new ArmourProfile(4)));
AttackProfile swing = AttackProfile.Unarmed(starting);
const long respawnDelay = 900;

int strikes = 0;
while (!wolf2.IsDown && strikes < 40)
{
    (wolf2, _) = EncounterResolutionRules.Strike(roll: 50, swing, wolf2, tick: strikes);
    strikes++;
}

Require(wolf2.IsDown, $"the encounter never resolved: {strikes} strikes left {wolf2.Health} health");
Require(strikes > 1, "an ordinary blow must not end a fight in one strike");
Require(EncounterResolutionRules.Strike(roll: 100, swing, CombatantState.Fresh(24, new DefenceProfile(60, new ArmourProfile(4))), tick: 0).Target.IsDown,
    "a critical blow against a weak creature may end it outright");
Require(wolf2.Health == 0, "a downed creature must be at zero health");

(CombatantState _, AttackOutcome afterDown) = EncounterResolutionRules.Strike(roll: 100, swing, wolf2, tick: 50);
Require(!afterDown.Hit && afterDown.Reason.Contains("already down"),
    "a downed creature must not be struck again, so it cannot be farmed");

long downTick = wolf2.DownAtTick;
Require(downTick >= 0 && downTick < respawnDelay, "a downed creature must record when it fell");
Require(EncounterResolutionRules.Respawn(downTick + respawnDelay - 1, wolf2, respawnDelay).IsDown,
    "a creature must stay down until its delay elapses");
CombatantState back = EncounterResolutionRules.Respawn(downTick + respawnDelay, wolf2, respawnDelay);
Require(!back.IsDown && back.Health == back.MaximumHealth, "a respawned creature must return at full health");

EncounterReward reward = EncounterResolutionRules.Reward(
    experience: 120,
    table,
    scope: "wolf:1",
    SeededDraw(99));
Require(reward.Experience.Source == ExperienceSource.Combat && reward.Experience.Amount == 120,
    "a defeat must be worth a combat award");
ProgressionOutcome advanced = ProgressionRules.Award(0, 1, reward.Experience);
Require(advanced.Advanced && advanced.Level == 2,
    "defeating one creature worth a threshold must advance the character");
foreach (LootDrop drop in reward.Drops)
{
    Require(table.Entries.Any(entry => entry.ItemId == drop.ItemId), $"loot {drop.ItemId} is not on the creature's table");
}
try
{
    EncounterResolutionRules.Reward(experience: 0, table, "wolf:2", SeededDraw(1));
    throw new InvalidOperationException("a defeat worth nothing must be refused");
}
catch (ArgumentOutOfRangeException)
{
}


// --- the director owns what is in the world, and the caps hold ----------------------
EncounterPolicy tight = EncounterPolicy.Default with { MaximumActive = 3, MaximumActivePerRegion = 2 };
EncounterDirector director = new(tight);
EncounterSite meadow12 = new(RegionKind.Wilderness, RegionId: 12, SurfaceY: 6, WaterLevel: 2, HasGround: true, ShoreIsReachable: true);

EncounterCandidate Candidate(int id, long regionId, CreatureTraits traits, long window = 1) =>
    new(id, new EncounterSite(RegionKind.Wilderness, regionId, 6, 2, true, true) with { RegionId = regionId }, traits, window);

Require(director.ActiveCount == 0, "a fresh director must hold nothing");
Require(director.TryActivate(Candidate(1, 12, CreatureTraits.Walker), tick: 0, out _), "the first candidate must activate");
Require(!director.TryActivate(Candidate(1, 12, CreatureTraits.Walker), tick: 0, out string duplicate) && duplicate.Contains("already"),
    "the same encounter must not be placed twice");
Require(director.TryActivate(Candidate(2, 12, CreatureTraits.Walker), tick: 0, out _), "a second candidate fits the per-region cap");
Require(!director.TryActivate(Candidate(3, 12, CreatureTraits.Walker), tick: 0, out string regionFull) && regionFull.Contains("region 12"),
    $"the per-region cap must refuse a third: {regionFull}");
Require(director.TryActivate(Candidate(4, 13, CreatureTraits.Walker), tick: 0, out _), "another region has its own room");
Require(!director.TryActivate(Candidate(5, 13, CreatureTraits.Walker), tick: 0, out string worldFull) && worldFull.Contains("3 encounters"),
    $"the world cap must refuse a fourth: {worldFull}");
Require(director.ActiveCount == 3 && director.ActiveCountIn(12) == 2, "the director's counts must agree with what it placed");

EncounterCandidate submerged = new(9, new EncounterSite(RegionKind.Wilderness, 14, SurfaceY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker, 1);
EncounterDirector roomy = new(EncounterPolicy.Default);
Require(!roomy.TryActivate(submerged, tick: 0, out string water) && water.Contains("cannot swim"),
    $"the director must refuse a candidate the placement rule refuses: {water}");
Require(roomy.ActiveCount == 0, "a refused candidate must not be recorded");

// Despawn: a region that stops being resident takes its encounters with it.
EncounterDirector leaving = new(EncounterPolicy.Default);
leaving.TryActivate(Candidate(21, 30, CreatureTraits.Walker), tick: 0, out _);
leaving.TryActivate(Candidate(22, 31, CreatureTraits.Walker), tick: 0, out _);
int removed = leaving.Tick(tick: 10, _ => 1.0, regionId => regionId == 30);
Require(removed == 1 && leaving.ActiveCount == 1 && leaving.IsActive(21) && !leaving.IsActive(22),
    "a non-resident region's encounters must be removed and the resident region's kept");

// Despawn: out of reach long enough, with the grace period respected.
EncounterPolicy patient = EncounterPolicy.Default with { DespawnDistance = 10.0, DespawnGraceTicks = 5 };
EncounterDirector drifting = new(patient);
drifting.TryActivate(new EncounterCandidate(31, meadow12, CreatureTraits.Walker, 1), tick: 0, out _);
Require(drifting.Tick(tick: 1, _ => 50.0, _ => true) == 0, "leaving reach must not despawn immediately");
Require(drifting.Tick(tick: 5, _ => 50.0, _ => true) == 0, "the grace period must be honoured");
Require(drifting.Tick(tick: 6, _ => 50.0, _ => true) == 1 && drifting.ActiveCount == 0,
    "the encounter must despawn once the grace period elapses");

// Coming back into reach clears the grace period.
EncounterDirector returns = new(patient);
returns.TryActivate(new EncounterCandidate(41, meadow12, CreatureTraits.Walker, 1), tick: 0, out _);
returns.Tick(tick: 1, _ => 50.0, _ => true);
returns.Tick(tick: 3, _ => 1.0, _ => true);
Require(returns.Tick(tick: 20, _ => 50.0, _ => true) == 0,
    "an encounter the player returned to must start its grace period afresh");
returns.Tick(tick: 24, _ => 50.0, _ => true);
Require(returns.Tick(tick: 25, _ => 50.0, _ => true) == 1, "the restarted grace period must still expire");


// Death is the other half of "win or die": lethal damage, a delay, then respawn.
PlayerDefeatState alivePlayer = PlayerDefeatState.Full(40);
Require(PlayerDefeatRules.Outcome(alivePlayer, 0) == PlayerDefeatOutcome.Alive, "a healthy player is alive");
PlayerDefeatState hurtPlayer = PlayerDefeatRules.Strike(alivePlayer, 15, tick: 10);
Require(hurtPlayer.Health == 25 && hurtPlayer.Defeats == 0, "damage that does not kill must not count a defeat");
PlayerDefeatState deadPlayer = PlayerDefeatRules.Strike(hurtPlayer, 999, tick: 100);
Require(deadPlayer.Health == 0 && deadPlayer.Defeats == 1, "lethal damage must zero health and count one defeat");
Require(PlayerDefeatRules.Outcome(deadPlayer, 100) == PlayerDefeatOutcome.Defeated, "a fresh defeat is not respawnable");
Require(!PlayerDefeatRules.CanRespawn(deadPlayer, 299), "respawn must wait out its delay");
Require(PlayerDefeatRules.CanRespawn(deadPlayer, 300), "respawn must be available once the delay has passed");
Require(PlayerDefeatRules.Outcome(deadPlayer, 300) == PlayerDefeatOutcome.RespawnReady, "a matured defeat reports respawn ready");
PlayerDefeatState risenPlayer = PlayerDefeatRules.Respawn(deadPlayer);
Require(risenPlayer.Health == 20, "respawn must restore half of maximum health");
Require(risenPlayer.Defeats == 1, "respawning must not erase the defeat count");
Require(PlayerDefeatRules.Outcome(risenPlayer, 300) == PlayerDefeatOutcome.Alive, "a respawned player is alive again");

Require(PlayerDefeatRules.Strike(deadPlayer, 4, tick: 150) == deadPlayer,
    "a defeated player must absorb further damage without counting another defeat");
Require(PlayerDefeatRules.Strike(deadPlayer, 4, tick: 150).RespawnTick == deadPlayer.RespawnTick,
    "further damage must not move a defeated player's respawn schedule");
PlayerDefeatState stillRisen = PlayerDefeatRules.Respawn(PlayerDefeatRules.Strike(deadPlayer, 4, tick: 299));
Require(stillRisen.Health == 20 && stillRisen.Defeats == 1,
    "a defeated player must still respawn on schedule at half health with one defeat counted");

Console.WriteLine("RPG rules: damage, armour, attacks, progression, loot determinism, spawn placement, encounter policy, creature behaviour, end-to-end resolution, the encounter director and player defeat passed.");
