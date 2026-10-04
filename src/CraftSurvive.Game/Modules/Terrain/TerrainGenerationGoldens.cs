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
        [13] = 0x8f58f123a8572be0UL,
        [14] = 0xf3b43051f222d0d6UL,
        [15] = 0x6c0b940b85d1f8beUL,
        [16] = 0xd5e257893c70a6cbUL,
        [18] = 0x78e4e675451e0a55UL,
        [17] = 0x89a5cc63c5e6f43cUL,
    };
}
