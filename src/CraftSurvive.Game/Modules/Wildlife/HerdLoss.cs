namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// What a seeded herd has lost and not yet recovered, as the ledger saves it (#10567): which herd,
/// how many animals, and the world day (with its fraction) the next recovery is counted from.
/// </summary>
/// <param name="Herd">The herd, as <see cref="IdOf"/> gives it from the herd's key.</param>
internal readonly record struct HerdLoss(ulong Herd, int Lost, double RecoveringSince)
{
    /// <summary>The most hunted herds a save keeps; more than a continent should ever have short at once.</summary>
    internal const int MostHerds = 1 << 17;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// A herd's identity in a save: its key's hash. Keys come from the region, kind and count that
    /// seeded it, so a herd is the same one whenever its region is seeded again.
    /// </summary>
    internal static ulong IdOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ulong hash = FnvOffsetBasis;
        foreach (char character in key) hash = unchecked((hash ^ character) * FnvPrime);
        return hash;
    }
}
