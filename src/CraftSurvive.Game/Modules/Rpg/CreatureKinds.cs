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
    int AttackDamage,
    double AttackReachMetres,
    int ExperienceAward,
    LootTable Loot);

internal static class CreatureKinds
{
    private const int Health = 24;
    private const int Evasion = 60;
    private const int Armour = 4;
    private const double PursueSpeed = 2.0;

    /// <summary>A pursuer halts here, strictly inside its attack reach.</summary>
    private const double HaltDistance = 2.0;

    private const int AttackDamage = 4;
    private const double AttackReach = 3.0;
    private const int Experience = 120;

    /// <summary>
    /// Placeholder drops: the shape of a table, not designed content. What a creature should drop
    /// is content work (task #8700).
    /// </summary>
    private static readonly LootTable PlaceholderLoot = new(
        Id: "creature-placeholder",
        MinimumRolls: 1,
        MaximumRolls: 2,
        Entries:
        [
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
        AttackDamage,
        AttackReach,
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
