using System.Numerics;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Tests;

// The adventurer's rules, checked without a host: damage and armour arithmetic,
// attack resolution boundaries, loot determinism, progression sources, and the
// placement rule that keeps a non-swimming creature out of the water.

// --- damage and armour arithmetic -------------------------------------------------
Check.That(ArmourRules.Reduce(100, DamageType.Cutting, new ArmourProfile(0)) == 100,
    "no armour must pass the whole blow");
Check.That(ArmourRules.Reduce(100, DamageType.Cutting, new ArmourProfile(10)) == 80,
    "ten points of cutting armour at two percent each must leave eighty");
Check.That(ArmourRules.Reduce(100, DamageType.Blunt, new ArmourProfile(10)) == 90,
    "blunt must find the gaps and use half the armour");
Check.That(ArmourRules.Reduce(100, DamageType.Piercing, new ArmourProfile(10)) == 85,
    "piercing must use three quarters of the armour");
Check.That(ArmourRules.Reduce(100, DamageType.Frost, new ArmourProfile(10)) == 90,
    "frost must be slowed by half the armour");
Check.That(ArmourRules.Reduce(100, DamageType.Fire, new ArmourProfile(40)) == 100,
    "fire must ignore armour entirely");
Check.That(ArmourRules.Reduce(100, DamageType.Cutting, new ArmourProfile(100)) == 25,
    "armour must never remove more than three quarters of a blow");
Check.That(ArmourRules.Reduce(1, DamageType.Cutting, new ArmourProfile(100)) == ArmourRules.MinimumDamage,
    "a landed blow must always do at least one point");
Check.That(ArmourRules.Reduce(0, DamageType.Cutting, new ArmourProfile(10)) == 0,
    "nothing incoming must stay nothing");

// --- attack resolution -------------------------------------------------------------
CharacterAttributes starting = CharacterAttributes.Starting;
AttackProfile unarmed = AttackProfile.Unarmed(starting);
DefenceProfile bare = new(Evasion: 60, Armour: ArmourProfile.None);
int exactHitRoll = bare.Evasion - unarmed.Accuracy;
Check.That(!CombatRules.Resolve(1, unarmed, bare).Hit, "the lowest roll must miss a starting evasion");
Check.That(CombatRules.Resolve(exactHitRoll, unarmed, bare).Hit, "meeting the evasion exactly must hit");
Check.That(!CombatRules.Resolve(exactHitRoll - 1, unarmed, bare).Hit, "one under the evasion must miss");
Check.That(unarmed.Accuracy < bare.Evasion,
    "an unarmed attack must not outclass a starting evasion, or the roll would not matter");
AttackOutcome ordinary = CombatRules.Resolve(50, unarmed, bare);
Check.That(ordinary.Hit && !ordinary.Critical, "a middling roll must land without being critical");
Check.That(ordinary.RawDamage == unarmed.Power + 5, "raw damage must be power plus one point per full ten of the roll");
AttackOutcome critical = CombatRules.Resolve(CombatRules.CriticalRoll, unarmed, bare);
Check.That(critical.Critical && critical.RawDamage == (unarmed.Power + (CombatRules.CriticalRoll / CombatRules.RollDamageDivisor)) * 2,
    "a critical blow must double the raw damage");
Check.That(CombatRules.Resolve(100, unarmed, new DefenceProfile(60, new ArmourProfile(10))).Damage
    == ArmourRules.Reduce(CombatRules.Resolve(100, unarmed, bare).RawDamage, unarmed.Type, new ArmourProfile(10)),
    "armour must apply to the raw damage the roll produced");
Check.Throws<ArgumentOutOfRangeException>(() => CombatRules.Resolve(0, unarmed, bare), "a roll of zero must be refused");

// --- derived statistics and progression --------------------------------------------
DerivedStatistics level1 = CharacterRules.Derive(starting, 1);
Check.That(level1.MaximumHealth == 30 && level1.MaximumStamina == 35,
    $"starting health and stamina are wrong: {level1}");
Check.That(level1.Evasion == 60 && level1.MeleePower == 12 && level1.CarryCapacity == 45,
    $"starting evasion, melee power or carry capacity is wrong: {level1}");
Check.That(CharacterRules.Derive(starting, 3).MaximumHealth == level1.MaximumHealth + (2 * CharacterRules.HealthPerLevel),
    "each level must add its health");

Check.That(ProgressionRules.ExperienceForLevel(1) == 0 && ProgressionRules.ExperienceForLevel(2) == 100
    && ProgressionRules.ExperienceForLevel(3) == 300 && ProgressionRules.ExperienceForLevel(4) == 600,
    "level thresholds are wrong");
Check.That(ProgressionRules.LevelFor(0) == 1 && ProgressionRules.LevelFor(99) == 1, "level one must start at zero experience");
Check.That(ProgressionRules.LevelFor(100) == 2 && ProgressionRules.LevelFor(299) == 2, "the second level threshold is wrong");
Check.That(ProgressionRules.LevelFor(300) == 3, "the third level threshold is wrong");

ProgressionOutcome combatAward = ProgressionRules.Award(0, 1, new ExperienceAward(ExperienceSource.Combat, 100));
Check.That(combatAward.Advanced && combatAward.Level == 2 && combatAward.Experience == 100,
    "a combat award of exactly one threshold must advance the character");
ProgressionOutcome discounted = ProgressionRules.Award(0, 1, new ExperienceAward(ExperienceSource.Enchanting, 1000));
Check.That(!discounted.Advanced && discounted.Experience == 0 && discounted.Level == 1,
    "experience from a source this product does not award must be refused");
Check.That(!ProgressionRules.AwardsExperience(ExperienceSource.Enchanting),
    "enchanting must not be an awarding source");
ProgressionOutcome empty = ProgressionRules.Award(50, 1, new ExperienceAward(ExperienceSource.Discovery, 0));
Check.That(!empty.Advanced && empty.Experience == 50, "an empty award must change nothing");

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

// The product's one keyed draw: the same seed and scope always give the same number.
static LootRules.Draw SeededDraw(ulong seed) => KeyedDraws.For(seed);
// Pinned values rather than a call compared with itself: a change to the keyed draw or the loot
// roll moves these, and a draw that were not a pure function of seed and scope could not hold them.
Check.Equal(86L, KeyedDraws.Draw(7, "a", 1, 100), "the keyed draw for seed 7, scope \"a\" is pinned");
Check.That(Enumerable.Range(0, 200).Select(i => KeyedDraws.Draw(7, $"roll:{i}", 1, 6)).Distinct().Count() == 6,
    "a keyed draw must reach every value in its range");
Check.That(Enumerable.Range(0, 200).All(i => KeyedDraws.Draw(7, $"roll:{i}", 1, 6) is >= 1 and <= 6), "a keyed draw must stay in its range");

LootDrop[] first = LootRules.Roll(table, "encounter:6", SeededDraw(1234));
Check.That(first.SequenceEqual([new LootDrop("pelt", 1), new LootDrop("pelt", 2), new LootDrop("fang", 1)]),
    $"the loot roll for encounter:6 is pinned, rolled {string.Join(";", first.Select(drop => $"{drop.ItemId}x{drop.Quantity}"))}");

// The scope is part of the key: across many encounters, the rolls must not all come out alike.
int distinctRolls = Enumerable.Range(0, 32)
    .Select(i => string.Join(";", LootRules.Roll(table, $"encounter:{i}", SeededDraw(1234)).Select(drop => $"{drop.ItemId}x{drop.Quantity}")))
    .Distinct()
    .Count();
Check.That(distinctRolls > 1, "different encounter scopes must not all roll the same drops");
foreach (LootDrop drop in first)
{
    LootEntry entry = table.Entries.Single(candidate => candidate.ItemId == drop.ItemId);
    Check.That(drop.Quantity >= entry.Minimum && drop.Quantity <= entry.Maximum,
        $"{drop.ItemId} dropped {drop.Quantity}, outside {entry.Minimum}..{entry.Maximum}");
}

// Rarity arithmetic, without depending on luck: a one-in-one entry always drops and
// an entry rarer than the scale can never drop.
LootTable edges = new("edges", 1, 1, [new LootEntry("always", 1, 1, 1), new LootEntry("never", 1, 1, LootRules.RarityScale * 2)]);
LootDrop[] edgeDrops = LootRules.Roll(edges, "edges", SeededDraw(7));
Check.That(edgeDrops.Any(drop => drop.ItemId == "always"), "a one-in-one entry must always drop");
Check.That(!edgeDrops.Any(drop => drop.ItemId == "never"), "an entry rarer than the rarity scale must never drop");

// --- placement: never strand a creature that cannot survive the site ---------------
SpawnVerdict dryLand = SpawnRules.Evaluate(new SpawnSite(GroundY: 5, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker);
Check.That(dryLand.Allowed, "a walker on dry ground must be allowed");
SpawnVerdict lake = SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker);
Check.That(!lake.Allowed && lake.Refusal == SpawnRefusal.SubmergedWithoutSwimming,
    "a walker must never be placed under water");
SpawnVerdict shoreline = SpawnRules.Evaluate(new SpawnSite(GroundY: 2, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker);
Check.That(shoreline.Allowed, "a shoreline at the water line is fair game for a walker");
SpawnVerdict swimming = SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Amphibious);
Check.That(swimming.Allowed, "a swimmer with a reachable shore may be placed in water");
SpawnVerdict stranded = SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false), CreatureTraits.Amphibious);
Check.That(!stranded.Allowed && stranded.Refusal == SpawnRefusal.StrandedInWater,
    "a swimmer that cannot climb must not be placed where the shore is out of reach");
// The slice's rule is absolute for non-swimmers, and a climb out of a lake does not
// change that: the placement rule refuses the water, not merely the stranding.
SpawnVerdict climbingNonSwimmer = SpawnRules.Evaluate(
    new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false),
    new CreatureTraits(CanSwim: false, CanClimb: true, CanFly: false));
Check.That(!climbingNonSwimmer.Allowed && climbingNonSwimmer.Refusal == SpawnRefusal.SubmergedWithoutSwimming,
    "a creature that cannot swim must stay out of the water even when it could climb out");
Check.That(SpawnRules.Evaluate(
    new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false),
    new CreatureTraits(CanSwim: true, CanClimb: true, CanFly: false)).Allowed,
    "a swimmer that can climb out is not stranded");
Check.That(SpawnRules.Evaluate(
    new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: false),
    new CreatureTraits(CanSwim: false, CanClimb: false, CanFly: true)).Allowed,
    "a flying creature cannot be stranded by water");
Check.That(SpawnRules.Evaluate(new SpawnSite(GroundY: 0, WaterLevel: 2, HasGround: false, ShoreIsReachable: false), CreatureTraits.Walker).Refusal == SpawnRefusal.NoGround,
    "a site without ground must be refused outright");


// --- encounter policy: region and time, caps, and despawn ---------------------------
EncounterPolicy policy = EncounterPolicy.Default;
EncounterSite meadow = new(RegionKind.Wilderness, RegionId: 7, SurfaceY: 6, WaterLevel: 2, HasGround: true, ShoreIsReachable: true);
EncounterSite lakeBed = new(RegionKind.Wilderness, RegionId: 7, SurfaceY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true);
EncounterCandidate wolf = new(Id: 1, meadow, CreatureTraits.Walker, TimeWindow: 12);
EncounterCandidate drowned = new(Id: 2, lakeBed, CreatureTraits.Walker, TimeWindow: 12);

Check.That(EncounterRules.IsEligible(wolf, 12), "a candidate must be eligible in its own time window");
Check.That(!EncounterRules.IsEligible(wolf, 13), "a candidate must not be eligible outside its time window");
Check.That(!EncounterRules.CanActivate(policy, wolf, 13, 0, 0).Act,
    "activation must be refused outside the current time window even with room to spare");
Check.That(EncounterRules.CanActivate(policy, wolf, 12, 0, 0).Act, "an eligible candidate in a region with room must activate");
Check.That(!EncounterRules.CanActivate(policy, wolf, 12, policy.MaximumActive, 0).Act,
    "the world-wide cap must refuse activation");
Check.That(!EncounterRules.CanActivate(policy, wolf, 12, 1, policy.MaximumActivePerRegion).Act,
    "the per-region cap must refuse activation");
EncounterDecision submergedActivation = EncounterRules.CanActivate(policy, drowned, 12, 0, 0);
Check.That(!submergedActivation.Act && submergedActivation.Refusal == EncounterRefusal.PlacementRefused
    && submergedActivation.Placement == SpawnRefusal.SubmergedWithoutSwimming,
    $"a walker must not be activated under water: {submergedActivation.Reason}");

ActiveEncounter active = new(Id: 1, RegionKind.Wilderness, RegionId: 7, ActivatedAtTick: 0, AwaySinceTick: -1);
Check.That(!EncounterRules.ShouldDespawn(policy, 100, active, distanceToPlayer: 10, regionResident: true).Act,
    "an encounter the player is standing next to must stay");
Check.That(EncounterRules.ShouldDespawn(policy, 100, active, distanceToPlayer: 10, regionResident: false).Act,
    "an encounter whose region is no longer resident must go, wherever the player is");
Check.That(!EncounterRules.ShouldDespawn(policy, 100, active, policy.DespawnDistance + 1, regionResident: true).Act,
    "being out of reach is not on its own enough to despawn");
ActiveEncounter away = EncounterRules.MarkAway(active, tick: 100, policy.DespawnDistance + 1, policy);
Check.That(away.IsAway && away.AwaySinceTick == 100, "leaving reach must start the grace period once");
Check.That(!EncounterRules.ShouldDespawn(policy, 100 + policy.DespawnGraceTicks - 1, away, policy.DespawnDistance + 1, true).Act,
    "the grace period must be honoured to the tick");
Check.That(EncounterRules.ShouldDespawn(policy, 100 + policy.DespawnGraceTicks, away, policy.DespawnDistance + 1, true).Act,
    "the encounter must despawn once the grace period elapses");
Check.That(!EncounterRules.MarkPresent(away, policy.DespawnDistance - 1, policy).IsAway,
    "coming back into reach must clear the grace period");
ActiveEncounter settled = EncounterRules.MarkAway(active, tick: 100, distanceToPlayer: 5, policy);
Check.That(!settled.IsAway, "an encounter the player is near must not start a grace period");


// --- creature behaviour: the state machine over sensed facts ------------------------
BehaviorTuning hostile = BehaviorTuning.Hostile;
BehaviorTuning neutral = BehaviorTuning.Neutral;
CreatureBehaviorState spawned = CreatureBehaviorState.Spawned;

CreatureBehaviorState unaware = CreatureBehaviorRules.Step(
    hostile, spawned, new PerceptionFacts(PlayerVisible: false, DistanceToPlayer: 1.0, OwnHealth: 10), tick: 0);
Check.That(unaware.State == CreatureState.Idle, "a creature that cannot see the player must stay idle");

CreatureBehaviorState distant = CreatureBehaviorRules.Step(
    hostile, spawned, new PerceptionFacts(true, DistanceToPlayer: hostile.SightRange + 1, OwnHealth: 10), tick: 0);
Check.That(distant.State == CreatureState.Idle, "a creature must not notice the player beyond its sight range");

CreatureBehaviorState alerted = CreatureBehaviorRules.Step(
    hostile, spawned, new PerceptionFacts(true, DistanceToPlayer: hostile.SightRange - 1, OwnHealth: 10), tick: 0);
Check.That(alerted.State == CreatureState.Alert, "first sight within range must raise the alarm, not start a chase");

CreatureBehaviorState pursuing = CreatureBehaviorRules.Step(
    hostile, alerted, new PerceptionFacts(true, DistanceToPlayer: 10.0, OwnHealth: 10), tick: 1);
Check.That(pursuing.State == CreatureState.Pursuing, "an alerted creature must close on the player");

CreatureBehaviorState striking = CreatureBehaviorRules.Step(
    hostile, pursuing, new PerceptionFacts(true, DistanceToPlayer: hostile.AttackRange, OwnHealth: 10), tick: 2);
Check.That(striking.State == CreatureState.Attacking, "a creature in reach must attack");
Check.That(striking.CanAttack(tick: 2, hostile), "a creature that has not struck yet must be able to strike");
CreatureBehaviorState afterStrike = striking.AfterAttack(tick: 2);
Check.That(!afterStrike.CanAttack(tick: 2 + hostile.AttackCooldownTicks - 1, hostile),
    "the attack cooldown must hold the creature back");
Check.That(afterStrike.CanAttack(tick: 2 + hostile.AttackCooldownTicks, hostile),
    "the attack cooldown must expire on its tick");

CreatureBehaviorState backedOff = CreatureBehaviorRules.Step(
    hostile, striking, new PerceptionFacts(true, DistanceToPlayer: 10.0, OwnHealth: 10), tick: 3);
Check.That(backedOff.State == CreatureState.Pursuing, "a player who backs off must be chased again");

CreatureBehaviorState lost = CreatureBehaviorRules.Step(
    hostile, pursuing, new PerceptionFacts(false, DistanceToPlayer: 1.0, OwnHealth: 10), tick: 4);
Check.That(lost.State == CreatureState.Idle, "losing sight must return the creature to idle");

CreatureBehaviorState wounded = CreatureBehaviorRules.Step(
    hostile, striking, new PerceptionFacts(true, DistanceToPlayer: 1.0, OwnHealth: CreatureBehaviorRules.DeathHealth), tick: 5);
Check.That(wounded.State == CreatureState.Dead, "a creature at zero health must die");
CreatureBehaviorState remainsDead = CreatureBehaviorRules.Step(
    hostile, wounded, new PerceptionFacts(true, DistanceToPlayer: 0.5, OwnHealth: 5), tick: 6);
Check.That(remainsDead.State == CreatureState.Dead, "death must be terminal, even if health is restored");

CreatureBehaviorState calm = CreatureBehaviorRules.Step(
    neutral, spawned, new PerceptionFacts(true, DistanceToPlayer: 0.5, OwnHealth: 10), tick: 0);
Check.That(calm.State == CreatureState.Alert, "a neutral creature must notice the player");
Check.That(!calm.CanAttack(tick: 0, neutral), "a neutral creature must never attack, even in reach");
CreatureBehaviorState stillCalm = CreatureBehaviorRules.Step(
    neutral, calm, new PerceptionFacts(true, DistanceToPlayer: 0.5, OwnHealth: 10), tick: 10);
Check.That(stillCalm.State == CreatureState.Alert, "a neutral creature must not start pursuing");


// --- one encounter end to end, in policy: fight, die, loot, respawn, advance --------
// The fight uses the same creature kind and character sheet the live module loads, so a changed
// stat changes this check too.
CreatureKind hostileKind = CreatureKinds.Hostile;
CharacterSheet sheet = CharacterSheet.Starting;
CombatantState wolf2 = CombatantState.Fresh(hostileKind.MaximumHealth, hostileKind.Defence);
AttackProfile swing = sheet.Unarmed;
const long respawnDelay = 900;

int strikes = 0;
while (!wolf2.IsDown && strikes < 40)
{
    (wolf2, _) = EncounterResolutionRules.Strike(roll: 50, swing, wolf2, tick: strikes);
    strikes++;
}

Check.That(wolf2.IsDown, $"the encounter never resolved: {strikes} strikes left {wolf2.Health} health");
Check.That(strikes > 1, "an ordinary blow must not end a fight in one strike");
Check.That(EncounterResolutionRules.Strike(roll: 100, swing, CombatantState.Fresh(hostileKind.MaximumHealth, hostileKind.Defence), tick: 0).Target.IsDown,
    "a critical blow against a weak creature may end it outright");
Check.That(wolf2.Health == 0, "a downed creature must be at zero health");

(CombatantState _, AttackOutcome afterDown) = EncounterResolutionRules.Strike(roll: 100, swing, wolf2, tick: 50);
Check.That(!afterDown.Hit && afterDown.Result == AttackResult.TargetDown,
    "a downed creature must not be struck again, so it cannot be farmed");

long downTick = wolf2.DownAtTick;
Check.That(downTick >= 0 && downTick < respawnDelay, "a downed creature must record when it fell");
Check.That(EncounterResolutionRules.Respawn(downTick + respawnDelay - 1, wolf2, respawnDelay).IsDown,
    "a creature must stay down until its delay elapses");
CombatantState back = EncounterResolutionRules.Respawn(downTick + respawnDelay, wolf2, respawnDelay);
Check.That(!back.IsDown && back.Health == back.MaximumHealth, "a respawned creature must return at full health");

EncounterReward reward = EncounterResolutionRules.Reward(
    hostileKind.ExperienceAward,
    hostileKind.Loot,
    scope: "creature:1",
    SeededDraw(99));
Check.That(reward.Experience.Source == ExperienceSource.Combat && reward.Experience.Amount == hostileKind.ExperienceAward,
    "a defeat must be worth a combat award");
ProgressionOutcome advanced = ProgressionRules.Award(0, 1, reward.Experience);
Check.That(advanced.Advanced && advanced.Level == 2,
    "defeating one creature worth a threshold must advance the character");
foreach (LootDrop drop in reward.Drops)
{
    Check.That(hostileKind.Loot.Entries.Any(entry => entry.ItemId == drop.ItemId), $"loot {drop.ItemId} is not on the creature's table");
}
Check.Throws<ArgumentOutOfRangeException>(() => EncounterResolutionRules.Reward(experience: 0, table, "wolf:2", SeededDraw(1)),
    "a defeat worth nothing must be refused");


// A creature's claw against the starting character, through the same rules the live module uses:
// it misses a low roll, lands a middling one, doubles on a critical, and the player's own evasion
// is what it is measured against.
DefenceProfile player = sheet.Defence;
Check.That(player.Evasion == sheet.Derived.Evasion && player.Armour == ArmourProfile.None,
    "the player's defence is their derived evasion and no armour");
Check.That(sheet.Derived.MaximumHealth == CharacterRules.Derive(CharacterAttributes.Starting, 1).MaximumHealth,
    "the live player's health comes from the character rules");
AttackOutcome clawMiss = CombatRules.Resolve(1, hostileKind.Attack, player);
Check.That(!clawMiss.Hit, $"a roll of 1 must miss a starting character, total {clawMiss.Total} vs {clawMiss.Defence}");
int firstLandingRoll = Enumerable.Range(CombatRules.MinimumRoll, CombatRules.MaximumRoll).First(roll => CombatRules.Resolve(roll, hostileKind.Attack, player).Hit);
Check.That(firstLandingRoll == player.Evasion - hostileKind.Attack.Accuracy,
    $"a claw must first land at roll {player.Evasion - hostileKind.Attack.Accuracy}, landed at {firstLandingRoll}");
AttackOutcome clawHit = CombatRules.Resolve(60, hostileKind.Attack, player);
Check.That(clawHit.Hit && clawHit.Damage == hostileKind.Attack.Power + (60 / CombatRules.RollDamageDivisor),
    $"an unarmoured player takes the claw's full raw damage, took {clawHit.Damage}");
AttackOutcome clawCritical = CombatRules.Resolve(CombatRules.CriticalRoll, hostileKind.Attack, player);
Check.That(clawCritical.Critical && clawCritical.Damage == 2 * (hostileKind.Attack.Power + (CombatRules.CriticalRoll / CombatRules.RollDamageDivisor)),
    "a critical claw doubles its raw damage");
PlayerDefeatState clawed = PlayerDefeatRules.Strike(PlayerDefeatState.Full(sheet.Derived.MaximumHealth), clawHit.Damage, tick: 1);
Check.That(clawed.Health == sheet.Derived.MaximumHealth - clawHit.Damage, "a landed claw comes off the player's health");
Check.That(hostileKind.ReachMetres == hostileKind.Tuning.AttackRange && hostileKind.HaltDistanceMetres < hostileKind.ReachMetres,
    "a creature strikes at the range its behaviour attacks at, and halts inside it");

// --- the director owns what is in the world, and the caps hold ----------------------
EncounterPolicy tight = EncounterPolicy.Default with { MaximumActive = 3, MaximumActivePerRegion = 2 };
EncounterDirector director = new(tight);
EncounterSite meadow12 = new(RegionKind.Wilderness, RegionId: 12, SurfaceY: 6, WaterLevel: 2, HasGround: true, ShoreIsReachable: true);

EncounterCandidate Candidate(int id, long regionId, CreatureTraits traits, long window = 1) =>
    new(id, new EncounterSite(RegionKind.Wilderness, regionId, 6, 2, true, true) with { RegionId = regionId }, traits, window);

Check.That(director.ActiveCount == 0, "a fresh director must hold nothing");
Check.That(director.TryActivate(Candidate(1, 12, CreatureTraits.Walker), tick: 0, out _), "the first candidate must activate");
Check.That(!director.TryActivate(Candidate(1, 12, CreatureTraits.Walker), tick: 0, out EncounterDecision duplicate)
    && duplicate.Refusal == EncounterRefusal.AlreadyActive,
    "the same encounter must not be placed twice");
Check.That(director.TryActivate(Candidate(2, 12, CreatureTraits.Walker), tick: 0, out _), "a second candidate fits the per-region cap");
Check.That(!director.TryActivate(Candidate(3, 12, CreatureTraits.Walker), tick: 0, out EncounterDecision regionFull)
    && regionFull.Refusal == EncounterRefusal.RegionFull,
    $"the per-region cap must refuse a third: {regionFull.Reason}");
Check.That(director.TryActivate(Candidate(4, 13, CreatureTraits.Walker), tick: 0, out _), "another region has its own room");
Check.That(!director.TryActivate(Candidate(5, 13, CreatureTraits.Walker), tick: 0, out EncounterDecision worldFull)
    && worldFull.Refusal == EncounterRefusal.WorldFull,
    $"the world cap must refuse a fourth: {worldFull.Reason}");
Check.That(director.ActiveCount == 3 && director.ActiveCountIn(12) == 2, "the director's counts must agree with what it placed");

EncounterCandidate submerged = new(9, new EncounterSite(RegionKind.Wilderness, 14, SurfaceY: 0, WaterLevel: 2, HasGround: true, ShoreIsReachable: true), CreatureTraits.Walker, 1);
EncounterDirector roomy = new(EncounterPolicy.Default);
Check.That(!roomy.TryActivate(submerged, tick: 0, out EncounterDecision water)
    && water.Refusal == EncounterRefusal.PlacementRefused && water.Placement == SpawnRefusal.SubmergedWithoutSwimming,
    $"the director must refuse a candidate the placement rule refuses: {water.Reason}");
Check.That(roomy.ActiveCount == 0, "a refused candidate must not be recorded");

// Despawn: a region that stops being resident takes its encounters with it.
EncounterDirector leaving = new(EncounterPolicy.Default);
leaving.TryActivate(Candidate(21, 30, CreatureTraits.Walker), tick: 0, out _);
leaving.TryActivate(Candidate(22, 31, CreatureTraits.Walker), tick: 0, out _);
int removed = leaving.Tick(tick: 10, _ => 1.0, regionId => regionId == 30).Count;
Check.That(removed == 1 && leaving.ActiveCount == 1 && leaving.IsActive(21) && !leaving.IsActive(22),
    "a non-resident region's encounters must be removed and the resident region's kept");

// Despawn: out of reach long enough, with the grace period respected.
EncounterPolicy patient = EncounterPolicy.Default with { DespawnDistance = 10.0, DespawnGraceTicks = 5 };
EncounterDirector drifting = new(patient);
drifting.TryActivate(new EncounterCandidate(31, meadow12, CreatureTraits.Walker, 1), tick: 0, out _);
Check.That(drifting.Tick(tick: 1, _ => 50.0, _ => true).Count == 0, "leaving reach must not despawn immediately");
Check.That(drifting.Tick(tick: 5, _ => 50.0, _ => true).Count == 0, "the grace period must be honoured");
Check.That(drifting.Tick(tick: 6, _ => 50.0, _ => true).Count == 1 && drifting.ActiveCount == 0,
    "the encounter must despawn once the grace period elapses");

// Coming back into reach clears the grace period.
EncounterDirector returns = new(patient);
returns.TryActivate(new EncounterCandidate(41, meadow12, CreatureTraits.Walker, 1), tick: 0, out _);
returns.Tick(tick: 1, _ => 50.0, _ => true);
returns.Tick(tick: 3, _ => 1.0, _ => true);
Check.That(returns.Tick(tick: 20, _ => 50.0, _ => true).Count == 0,
    "an encounter the player returned to must start its grace period afresh");
returns.Tick(tick: 24, _ => 50.0, _ => true);
Check.That(returns.Tick(tick: 25, _ => 50.0, _ => true).Count == 1, "the restarted grace period must still expire");


// Death is the other half of "win or die": lethal damage, a delay, then respawn.
PlayerDefeatState alivePlayer = PlayerDefeatState.Full(40);
Check.That(PlayerDefeatRules.Outcome(alivePlayer, 0) == PlayerDefeatOutcome.Alive, "a healthy player is alive");
PlayerDefeatState hurtPlayer = PlayerDefeatRules.Strike(alivePlayer, 15, tick: 10);
Check.That(hurtPlayer.Health == 25 && hurtPlayer.Defeats == 0, "damage that does not kill must not count a defeat");
PlayerDefeatState deadPlayer = PlayerDefeatRules.Strike(hurtPlayer, 999, tick: 100);
Check.That(deadPlayer.Health == 0 && deadPlayer.Defeats == 1, "lethal damage must zero health and count one defeat");
Check.That(PlayerDefeatRules.Outcome(deadPlayer, 100) == PlayerDefeatOutcome.Defeated, "a fresh defeat is not respawnable");
Check.That(!PlayerDefeatRules.CanRespawn(deadPlayer, 299), "respawn must wait out its delay");
Check.That(PlayerDefeatRules.CanRespawn(deadPlayer, 300), "respawn must be available once the delay has passed");
Check.That(PlayerDefeatRules.Outcome(deadPlayer, 300) == PlayerDefeatOutcome.RespawnReady, "a matured defeat reports respawn ready");
PlayerDefeatState risenPlayer = PlayerDefeatRules.Respawn(deadPlayer);
Check.That(risenPlayer.Health == 20, "respawn must restore half of maximum health");
Check.That(risenPlayer.Defeats == 1, "respawning must not erase the defeat count");
Check.That(PlayerDefeatRules.Outcome(risenPlayer, 300) == PlayerDefeatOutcome.Alive, "a respawned player is alive again");

Check.That(PlayerDefeatRules.Strike(deadPlayer, 4, tick: 150) == deadPlayer,
    "a defeated player must absorb further damage without counting another defeat");
Check.That(PlayerDefeatRules.Strike(deadPlayer, 4, tick: 150).RespawnTick == deadPlayer.RespawnTick,
    "further damage must not move a defeated player's respawn schedule");
PlayerDefeatState stillRisen = PlayerDefeatRules.Respawn(PlayerDefeatRules.Strike(deadPlayer, 4, tick: 299));
Check.That(stillRisen.Health == 20 && stillRisen.Defeats == 1,
    "a defeated player must still respawn on schedule at half health with one defeat counted");

Check.That(PlayerDefeatRules.IsInvulnerable(PlayerDefeatRules.GraceUntil(100), 150),
    "a respawned player must be safe inside the grace window");
Check.That(!PlayerDefeatRules.IsInvulnerable(PlayerDefeatRules.GraceUntil(100), 200),
    "grace must expire on schedule");
Check.That(PlayerDefeatRules.GraceUntil(100) == 200, "grace must last the configured number of ticks");

// --- creatures in the world -------------------------------------------------------
// One owner of membership: whatever the director releases leaves the roster in the same update,
// so no creature table can outlive the encounter that put it there.
EncounterDirector membership = new(EncounterPolicy.Default with { DespawnDistance = 10.0, DespawnGraceTicks = 5 });
CreatureRoster roster = new();
for (int id = 1; id <= 3; id++)
{
    Check.That(membership.TryActivate(new EncounterCandidate(id, meadow12, CreatureTraits.Walker, 1), tick: 0, out _),
        $"encounter {id} must activate");
    roster.Add(new Creature(id, CreatureKinds.ForSpawn(id), new Vector2(id * 20, 0)));
}

Func<int, double> distanceToPlayer = id => roster.TryGet(id, out Creature c) ? Math.Abs(c.Position.X) : double.MaxValue;
Check.That(membership.Tick(1, distanceToPlayer, _ => true).Count == 0, "no creature may leave inside the grace");
IReadOnlyList<int> departed = membership.Tick(6, distanceToPlayer, _ => true);
Check.That(roster.Release(departed) == departed.Count && departed.Count == 3,
    $"all three creatures stood beyond the despawn distance, released {departed.Count}");
Check.That(roster.Count == 0 && membership.ActiveCount == 0 && departed.All(id => !roster.TryGet(id, out _)),
    "after the director releases an id, the roster must not hold it");
Check.That(CreatureKinds.ForSpawn(3) == CreatureKinds.Neutral && CreatureKinds.ForSpawn(1) == CreatureKinds.Hostile,
    "every third spawn is neutral and the rest hostile");

// Engine step time: two steps admitted in one update move a creature twice as far as one, and a
// cooldown counts steps, not updates.
const double FixedDelta = 1.0 / 60.0;
Vector3 playerAt = new(40, 0, 0);
Creature oneStep = new(1, CreatureKinds.Hostile, Vector2.Zero) { Behavior = CreatureBehaviorState.Spawned with { State = CreatureState.Pursuing } };
Creature twoSteps = new(2, CreatureKinds.Hostile, Vector2.Zero) { Behavior = CreatureBehaviorState.Spawned with { State = CreatureState.Pursuing } };
CreatureSimulation.Step(oneStep, new CreatureSense(true), playerAt, playerCanBeHit: true, new ProductStep(1, 1, FixedDelta), waypoint: new Vector2(playerAt.X, playerAt.Z));
CreatureSimulation.Step(twoSteps, new CreatureSense(true), playerAt, playerCanBeHit: true, new ProductStep(2, 2, FixedDelta), waypoint: new Vector2(playerAt.X, playerAt.Z));
double expectedOne = CreatureKinds.Hostile.PursueSpeedMetresPerSecond * FixedDelta;
Check.That(Math.Abs(oneStep.Position.X - expectedOne) < 1e-5 && Math.Abs(twoSteps.Position.X - (2 * expectedOne)) < 1e-5,
    $"one step must move {expectedOne:F4} m and two steps twice that, moved {oneStep.Position.X:F4} and {twoSteps.Position.X:F4}");

// A pursuer with a route walks to its waypoint, not at the player, and stops at the waypoint
// rather than passing it.
Creature router = new(7, CreatureKinds.Hostile, Vector2.Zero) { Behavior = CreatureBehaviorState.Spawned with { State = CreatureState.Pursuing } };
CreatureSimulation.Step(router, new CreatureSense(true), playerAt, playerCanBeHit: true, new ProductStep(1, 1, FixedDelta), waypoint: new Vector2(0, 5));
Check.That(Math.Abs(router.Position.X) < 1e-5 && Math.Abs(router.Position.Y - expectedOne) < 1e-5,
    $"a routed pursuer must walk toward its waypoint, moved to {router.Position}");
Creature nearWaypoint = new(8, CreatureKinds.Hostile, Vector2.Zero) { Behavior = CreatureBehaviorState.Spawned with { State = CreatureState.Pursuing } };
CreatureSimulation.Step(nearWaypoint, new CreatureSense(true), playerAt, playerCanBeHit: true, new ProductStep(60, 60, FixedDelta), waypoint: new Vector2(0, 0.25f));
Check.That(Vector2.Distance(nearWaypoint.Position, new Vector2(0, 0.25f)) < 1e-5,
    $"a pursuer must stop at its waypoint rather than overshoot it, reached {nearWaypoint.Position}");

// Only a routed waypoint moves a pursuer: with no route - outside the published grid, a refused
// start, or no way through - it waits where it is instead of walking at the player.
Creature unrouted = new(9, CreatureKinds.Hostile, new Vector2(3, 4)) { Behavior = CreatureBehaviorState.Spawned with { State = CreatureState.Pursuing } };
CreatureSimulation.Step(unrouted, new CreatureSense(true), playerAt, playerCanBeHit: true, new ProductStep(60, 60, FixedDelta),
    CreatureSimulation.WaypointOrWait(unrouted.Position, routed: null));
Check.That(unrouted.Position == new Vector2(3, 4), $"a pursuer without a route must wait, moved to {unrouted.Position}");
Check.Equal(new Vector2(7, 8), CreatureSimulation.WaypointOrWait(new Vector2(3, 4), new Vector2(7, 8)), "a routed waypoint is followed");

Creature striker = new(3, CreatureKinds.Hostile, new Vector2(38, 0));
Vector3 closePlayer = new(40, 0, 0);
Check.That(CreatureSimulation.Step(striker, new CreatureSense(true), closePlayer, true, new ProductStep(100, 1, FixedDelta), waypoint: new Vector2(closePlayer.X, closePlayer.Z)) is CreatureStrike,
    "a hostile creature within reach must strike");
long cooldown = CreatureKinds.Hostile.Tuning.AttackCooldownTicks;
Check.That(CreatureSimulation.Step(striker, new CreatureSense(true), closePlayer, true, new ProductStep(100 + cooldown - 1, 2, FixedDelta), waypoint: new Vector2(closePlayer.X, closePlayer.Z)) is null,
    "the cooldown must hold one step short, whatever the steps per update");
Check.That(CreatureSimulation.Step(striker, new CreatureSense(true), closePlayer, true, new ProductStep(100 + cooldown + 1, 2, FixedDelta), waypoint: new Vector2(closePlayer.X, closePlayer.Z)) is CreatureStrike,
    "the cooldown must release once its steps have passed, even when an update covers two");
Creature patientStriker = new(4, CreatureKinds.Hostile, new Vector2(38, 0));
Check.That(CreatureSimulation.Step(patientStriker, new CreatureSense(true), closePlayer, playerCanBeHit: false, new ProductStep(10, 1, FixedDelta), waypoint: new Vector2(closePlayer.X, closePlayer.Z)) is null
    && patientStriker.Behavior.CanAttack(11, CreatureKinds.Hostile.Tuning),
    "a player who cannot be hit draws no blow and costs no cooldown");

// Sight range is decided once, on planar distance: two players at the same planar distance but
// at different heights draw the same decision, and the Engine's query radius never rejects a
// target the planar rule would see.
const double HeightSpan = 40;
foreach (double planar in new[] { CreatureKinds.Hostile.Tuning.SightRange - 0.5, CreatureKinds.Hostile.Tuning.SightRange + 0.5 })
{
    Creature low = new(5, CreatureKinds.Hostile, Vector2.Zero);
    Creature high = new(6, CreatureKinds.Hostile, Vector2.Zero);
    CreatureSimulation.Step(low, new CreatureSense(true), new Vector3((float)planar, 0, 0), true, new ProductStep(1, 1, FixedDelta), waypoint: new Vector2((float)planar, 0));
    CreatureSimulation.Step(high, new CreatureSense(true), new Vector3((float)planar, (float)HeightSpan, 0), true, new ProductStep(1, 1, FixedDelta), waypoint: new Vector2((float)planar, 0));
    Check.That(low.Behavior.State == high.Behavior.State,
        $"at {planar} m planar, height must not change the sight decision: {low.Behavior.State} vs {high.Behavior.State}");
}

double radius = CreatureSimulation.EngineSightRadius(CreatureKinds.Hostile.Tuning.SightRange, HeightSpan);
Check.That(Math.Sqrt(Math.Pow(CreatureKinds.Hostile.Tuning.SightRange, 2) + Math.Pow(HeightSpan, 2)) <= radius + 1e-9,
    "the Engine's query radius must reach a target at the edge of planar sight at the greatest height difference");

// The player's vitals own respawn and grace.
PlayerVitals vitals = new(40);
vitals.TakeHit(999, 100);
Check.That(vitals.IsDown && !vitals.TryRespawn(100 + PlayerDefeatRules.RespawnDelayTicks - 1), "a defeated player waits out the delay");
Check.That(vitals.TryRespawn(100 + PlayerDefeatRules.RespawnDelayTicks) && !vitals.IsDown, "the respawn must be granted on schedule");
long risen = 100 + PlayerDefeatRules.RespawnDelayTicks;
Check.That(vitals.TakeHit(5, risen + 1).Health == vitals.State.MaximumHealth / 2, "a risen player must be untouchable in grace");

// The world frame: positions handed to the Engine are local to the origin, and a rebase announces
// how far local positions moved.
WorldFrame frame = new();
Vector3 moved = Vector3.Zero;
frame.Rebased += translation => moved = translation;
frame.Commit(2000, 0, -3000);
Check.That(frame.ToLocal(2010.5, 7, -2990) == new Vector3(10.5f, 7, 10), "world-to-local must subtract the origin cell");
Check.That(frame.ToWorld(new Vector3(10.5f, 7, 10)) == new Vector3(2010.5f, 7, -2990), "local-to-world must add it back");
Check.That(moved == new Vector3(-2000, 0, 3000), $"the rebase must announce the local translation, announced {moved}");

// Spawns spread over the ring instead of bunching in one corner.
List<(long X, long Z)> placedSpawns = [];
foreach ((long X, long Z) candidate in CreatureSpawnPlan.Candidates(0, 0))
{
    if (CreatureSpawnPlan.FarEnoughFrom(placedSpawns, candidate))
    {
        placedSpawns.Add(candidate);
    }

    if (placedSpawns.Count == 3)
    {
        break;
    }
}

Check.That(placedSpawns.All(spawn => Math.Sqrt((spawn.X * spawn.X) + (spawn.Z * spawn.Z)) >= CreatureSpawnPlan.MinimumDistanceMetres - 1),
    "spawns must start beyond a hostile creature's sight");
Check.That(placedSpawns.SelectMany(a => placedSpawns.Where(b => b != a), (a, b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2)))
    .All(gap => gap >= CreatureSpawnPlan.MinimumSeparationMetres), "spawned creatures must stand apart");

Console.WriteLine("RPG rules: damage, armour, attacks, progression, loot determinism, spawn placement, encounter policy, creature behaviour, end-to-end resolution, the encounter director, player defeat, creature membership, step time, vitals, the world frame and spawn spread passed.");

return Check.Finish("RpgCore");
