namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>
/// The product's one keyed draw for play: a number in a range, determined by the world seed and a
/// named scope. The same seed and scope always give the same number, so a fight or a drop can be
/// replayed; different scopes are independent. Combat rolls and loot tables both draw through it.
/// </summary>
internal static class KeyedDraws
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    internal static int Draw(ulong seed, string scope, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "A draw's maximum cannot be below its minimum.");
        }

        ulong hash = FnvOffsetBasis ^ seed;
        foreach (char character in scope)
        {
            hash = unchecked((hash ^ character) * FnvPrime);
        }

        ulong span = (ulong)((long)maximum - minimum + 1);
        return (int)(minimum + (long)(hash % span));
    }

    /// <summary>The draw a loot table takes, bound to one world seed.</summary>
    internal static LootRules.Draw For(ulong seed) => (scope, minimum, maximum) => Draw(seed, scope, minimum, maximum);

    /// <summary>An attack roll for a named blow.</summary>
    internal static int AttackRoll(ulong seed, string scope) =>
        Draw(seed, scope, CombatRules.MinimumRoll, CombatRules.MaximumRoll);
}
