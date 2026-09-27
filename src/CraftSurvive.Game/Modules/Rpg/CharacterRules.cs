namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>The adventurer's four attributes. Every derived statistic comes from these.</summary>
internal readonly record struct CharacterAttributes(int Might, int Agility, int Vitality, int Will)
{
    internal static CharacterAttributes Starting => new(5, 5, 5, 5);
}

/// <summary>What an attribute set and a level add up to. Derived, never stored.</summary>
internal readonly record struct DerivedStatistics(
    int MaximumHealth,
    int MaximumStamina,
    int Evasion,
    int MeleePower,
    int CarryCapacity);

internal static class CharacterRules
{
    internal const int HealthPerVitality = 4;
    internal const int HealthPerLevel = 6;
    internal const int StaminaPerVitality = 2;
    internal const int StaminaPerWill = 3;
    internal const int EvasionPerAgility = 4;
    internal const int MeleePowerPerMight = 2;
    internal const int CarryCapacityPerMight = 5;

    internal static DerivedStatistics Derive(CharacterAttributes attributes, int level)
    {
        if (level < MinimumLevel || level > MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"Level must be {MinimumLevel} to {MaximumLevel}.");
        }

        return new DerivedStatistics(
            MaximumHealth: (attributes.Vitality * HealthPerVitality) + ((level - 1) * HealthPerLevel) + HealthBase,
            MaximumStamina: (attributes.Vitality * StaminaPerVitality) + (attributes.Will * StaminaPerWill) + StaminaBase,
            Evasion: EvasionBase + (attributes.Agility * EvasionPerAgility),
            MeleePower: MeleePowerBase + (attributes.Might * MeleePowerPerMight),
            CarryCapacity: CarryBase + (attributes.Might * CarryCapacityPerMight));
    }

    internal const int MinimumLevel = 1;
    internal const int MaximumLevel = 30;
    internal const int HealthBase = 10;
    internal const int StaminaBase = 10;
    internal const int EvasionBase = 40;
    internal const int MeleePowerBase = 2;
    internal const int CarryBase = 20;
}

/// <summary>
/// Where experience may come from. Only the awarding sources advance a character,
/// and the list is closed on purpose: enchanting is out of this campaign's scope,
/// so a source this product does not intend can never leak experience in.
/// </summary>
internal enum ExperienceSource
{
    Combat = 0,
    Discovery = 1,
    Enchanting = 2,
}

internal readonly record struct ExperienceAward(ExperienceSource Source, int Amount);

internal readonly record struct ProgressionOutcome(bool Advanced, int Experience, int PreviousLevel, int Level, string Reason);

internal static class ProgressionRules
{
    private static readonly ExperienceSource[] AwardingSources = [ExperienceSource.Combat, ExperienceSource.Discovery];

    /// <summary>Experience needed to reach a level from the one below it.</summary>
    internal static int ExperienceForLevel(int level) =>
        level <= CharacterRules.MinimumLevel ? 0 : (level - 1) * ExperiencePerLevelStep * level / 2;

    internal const int ExperiencePerLevelStep = 100;

    internal static bool AwardsExperience(ExperienceSource source) => Array.IndexOf(AwardingSources, source) >= 0;

    internal static int LevelFor(int totalExperience)
    {
        if (totalExperience < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalExperience), totalExperience, "Total experience cannot be negative.");
        }

        int level = CharacterRules.MinimumLevel;
        while (level < CharacterRules.MaximumLevel && totalExperience >= ExperienceForLevel(level + 1))
        {
            level++;
        }

        return level;
    }

    internal static ProgressionOutcome Award(int totalExperience, int currentLevel, ExperienceAward award)
    {
        if (!AwardsExperience(award.Source))
        {
            return new ProgressionOutcome(false, totalExperience, currentLevel, currentLevel,
                $"{award.Source} is not an awarding experience source, so the award is refused.");
        }

        if (award.Amount <= 0)
        {
            return new ProgressionOutcome(false, totalExperience, currentLevel, currentLevel,
                $"An award of {award.Amount} advances nothing, so it is refused.");
        }

        int updated = checked(totalExperience + award.Amount);
        int level = LevelFor(updated);
        return new ProgressionOutcome(level > currentLevel, updated, currentLevel, level, "applied");
    }
}
