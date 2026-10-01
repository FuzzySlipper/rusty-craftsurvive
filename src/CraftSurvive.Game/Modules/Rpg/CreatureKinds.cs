namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>
/// Everything a kind of creature is, as data: how it behaves, what it can take, how it moves and
/// strikes, and what defeating it is worth. The live creature module and the rule checks load the
/// same kinds, so a number changed here changes both.
/// </summary>
internal sealed record CreatureKind(
    string Name,
    BehaviorTuning Tuning,
    int MaximumHealth,
    DefenceProfile Defence,
    double PursueSpeedMetresPerSecond,
    double HaltDistanceMetres,
    AttackProfile Attack,
    int ExperienceAward,
    LootTable Loot)
{
    /// <summary>How close the creature must stand to strike: the range its behaviour attacks at.</summary>
    internal double ReachMetres => Tuning.AttackRange;
}

internal static class CreatureKinds
{
    private const int Health = 24;
    private const int Evasion = 60;
    private const int Armour = 4;
    private const double PursueSpeed = 2.0;

    /// <summary>A pursuer halts here, strictly inside its attack reach.</summary>
    private const double HaltDistance = 2.0;

    /// <summary>
    /// A weak claw: it lands on a bit over half its rolls against a starting character and deals
    /// about eight when it does.
    /// </summary>
    private const int ClawAccuracy = 15;

    private const int ClawPower = 1;

    private const int Experience = 120;

    /// <summary>
    /// What a walker drops: meat, which is food, and the hide and claws a player crafts with. The
    /// item ids are the inventory's catalogue ids. Tuning the table is content work (task #8700).
    /// </summary>
    private static readonly LootTable PlaceholderLoot = new(
        Id: "creature-placeholder",
        MinimumRolls: 1,
        MaximumRolls: 2,
        Entries:
        [
            new LootEntry("meat", 1, 2, 1),
            new LootEntry("hide", 1, 2, 2),
            new LootEntry("claw", 1, 1, 4),
        ]);

    internal static CreatureKind Hostile { get; } = new(
        "hostile-walker",
        BehaviorTuning.Hostile,
        Health,
        new DefenceProfile(Evasion, new ArmourProfile(Armour)),
        PursueSpeed,
        HaltDistance,
        new AttackProfile(ClawAccuracy, ClawPower, DamageType.Cutting),
        Experience,
        PlaceholderLoot);

    /// <summary>Watches and warns but never closes: sees half as far as a hostile walker.</summary>
    internal static CreatureKind Neutral { get; } = Hostile with
    {
        Name = "neutral-walker",
        Tuning = BehaviorTuning.Neutral,
    };

    /// <summary>
    /// How often a neutral creature appears among a spawn: one in this many, so a starting set
    /// holds both dispositions.
    /// </summary>
    internal const int NeutralOneIn = 3;

    /// <summary>The kind the n-th spawned creature (counting from 1) takes.</summary>
    internal static CreatureKind ForSpawn(int ordinal) => ordinal % NeutralOneIn == 0 ? Neutral : Hostile;
}
