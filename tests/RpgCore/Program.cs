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

Console.WriteLine("RPG rules: damage, armour, attacks, progression, loot determinism and spawn placement passed.");
