namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The live generator fingerprint recorded for each generator version, taken through the
/// Engine's keyed draws over <see cref="TerrainGenerationFingerprint.Startup"/>. A run whose
/// fingerprint differs from its version's entry is generating a different world under the same
/// version - a tuning change without a bump, or an Engine draw change - and CI's serve check
/// refuses it. Record a new entry only with a version bump.
/// </summary>
internal static class TerrainGenerationGoldens
{
    internal static IReadOnlyDictionary<uint, ulong> Live { get; } = new Dictionary<uint, ulong>
    {
        [12] = 0x5be1df75e73f58b7UL,
    };
}
