namespace CraftSurvive.Game.Modules.Rpg;

internal enum DamageType
{
    Cutting = 0,
    Piercing = 1,
    Blunt = 2,
    Fire = 3,
    Frost = 4,
}

/// <summary>Armour as the character sheet states it, plus any named exceptions.</summary>
internal readonly record struct ArmourProfile(int Points)
{
    internal static ArmourProfile None => new(0);
}

internal static class ArmourRules
{
    /// <summary>Fraction of the incoming blow a single armour point removes.</summary>
    internal const float ReductionPerPoint = 0.02f;

    /// <summary>Armour never removes more than this share of a blow, however heavy it is.</summary>
    internal const float MaximumReductionFraction = 0.75f;

    /// <summary>
    /// How much of the armour applies to each damage type. Blunt finds the gaps,
    /// fire does not care about plate at all, and frost is slowed by padding.
    /// </summary>
    internal static float Effectiveness(DamageType type) => type switch
    {
        DamageType.Cutting => 1.0f,
        DamageType.Piercing => 0.75f,
        DamageType.Blunt => 0.5f,
        DamageType.Fire => 0.0f,
        DamageType.Frost => 0.5f,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown damage type."),
    };

    /// <summary>Armour points that actually apply to this damage type.</summary>
    internal static float EffectivePoints(ArmourProfile armour, DamageType type)
    {
        if (armour.Points < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(armour), armour.Points, "Armour points cannot be negative.");
        }

        return armour.Points * Effectiveness(type);
    }

    /// <summary>Damage that gets through. A blow that lands always does at least one point.</summary>
    internal static int Reduce(int incoming, DamageType type, ArmourProfile armour)
    {
        if (incoming <= 0)
        {
            return 0;
        }

        float reduction = Math.Min(EffectivePoints(armour, type) * ReductionPerPoint, MaximumReductionFraction);
        int remaining = (int)MathF.Round(incoming * (1f - reduction), MidpointRounding.AwayFromZero);
        return Math.Max(remaining, MinimumDamage);
    }

    internal const int MinimumDamage = 1;
}

internal readonly record struct AttackProfile(int Accuracy, int Power, DamageType Type)
{
    internal static AttackProfile Unarmed(CharacterAttributes attributes) =>
        new(
            Accuracy: UnarmedAccuracyBase + (attributes.Agility * UnarmedAccuracyPerAgility),
            Power: CharacterRules.Derive(attributes, 1).MeleePower,
            Type: DamageType.Blunt);

    /// <summary>An unarmed attack is weak enough that a starting evasion still matters.</summary>
    internal const int UnarmedAccuracyBase = 20;

    internal const int UnarmedAccuracyPerAgility = 2;
}

internal readonly record struct DefenceProfile(int Evasion, ArmourProfile Armour);

/// <summary>What became of one blow.</summary>
internal enum AttackResult
{
    Landed = 0,
    Critical = 1,
    Missed = 2,
    TargetDown = 3,
}

internal readonly record struct AttackOutcome(
    bool Hit,
    bool Critical,
    int Roll,
    int Total,
    int Defence,
    int RawDamage,
    int Damage,
    AttackResult Result)
{
    internal string Reason => Result switch
    {
        AttackResult.Critical => "a critical blow landed",
        AttackResult.Missed => "the blow missed",
        AttackResult.TargetDown => "the target is already down",
        _ => "the blow landed",
    };
}

internal static class CombatRules
{
    internal const int MinimumRoll = 1;
    internal const int MaximumRoll = 100;

    /// <summary>A roll at or above this is a critical blow: raw damage doubles.</summary>
    internal const int CriticalRoll = 95;

    /// <summary>Each full ten points of the roll adds one point of raw damage.</summary>
    internal const int RollDamageDivisor = 10;

    /// <summary>
    /// Resolves one blow from an already-drawn roll. The roll is a parameter rather
    /// than a draw so the arithmetic is testable without a random source, and the
    /// caller supplies it from the product's keyed draws in play.
    /// </summary>
    internal static AttackOutcome Resolve(int roll, AttackProfile attack, DefenceProfile defence)
    {
        if (roll is < MinimumRoll or > MaximumRoll)
        {
            throw new ArgumentOutOfRangeException(nameof(roll), roll, $"An attack roll is {MinimumRoll} to {MaximumRoll}.");
        }

        int total = roll + attack.Accuracy;
        if (total < defence.Evasion)
        {
            return new AttackOutcome(false, false, roll, total, defence.Evasion, 0, 0, AttackResult.Missed);
        }

        bool critical = roll >= CriticalRoll;
        int raw = attack.Power + (roll / RollDamageDivisor);
        if (critical)
        {
            raw *= 2;
        }

        int damage = ArmourRules.Reduce(raw, attack.Type, defence.Armour);
        return new AttackOutcome(
            true,
            critical,
            roll,
            total,
            defence.Evasion,
            raw,
            damage,
            critical ? AttackResult.Critical : AttackResult.Landed);
    }
}
