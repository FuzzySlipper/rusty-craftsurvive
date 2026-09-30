namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>
/// The player's character as the rules see it: attributes and level, and everything derived from
/// them. The live player and the rule checks read the same sheet.
/// </summary>
internal sealed record CharacterSheet(CharacterAttributes Attributes, int Level)
{
    /// <summary>How far an unarmed swing reaches.</summary>
    internal const double UnarmedReachMetres = 4.0;

    internal static CharacterSheet Starting { get; } = new(CharacterAttributes.Starting, CharacterRules.MinimumLevel);

    internal DerivedStatistics Derived => CharacterRules.Derive(Attributes, Level);

    internal AttackProfile Unarmed => AttackProfile.Unarmed(Attributes);

    /// <summary>The player wears no armour yet; their defence is the evasion their agility gives.</summary>
    internal DefenceProfile Defence => new(Derived.Evasion, ArmourProfile.None);
}
