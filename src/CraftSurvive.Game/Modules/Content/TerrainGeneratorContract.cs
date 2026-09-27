using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// The versioned identity of a generated world, and the only way generation draws
/// a random value.
///
/// Two properties matter and both come from the Engine's keyed RNG rather than a
/// product hash:
/// <list type="bullet">
/// <item>A draw is a pure function of (seed, version, scope, key). It holds no
/// stream position, so a chunk produces the same voxels no matter which chunks
/// were generated before it.</item>
/// <item>Every draw names its purpose and its coordinates, so a feature that
/// overhangs a chunk boundary is decided once, by the cell that owns it, and every
/// chunk that overlaps it reads the same answer.</item>
/// </list>
/// A change to generation is a version bump, which changes every draw key and so
/// regenerates the world deliberately rather than silently.
/// </summary>
internal readonly record struct TerrainGeneratorContract(ulong Seed, uint Version, int Extent)
{
    /// <summary>
    /// The current generation version. Version 2 was the pre-registry height field
    /// with hand-placed landmarks; version 3 added surface features drawn from the
    /// Engine's keyed RNG; version 4 placed them; version 5 adds water bodies at a
    /// water level that is part of this contract; version 6 gives the finite world
    /// an authored bedrock floor and border wall; version 7 replaces the hand-placed
    /// landmark pillars with seed-drawn points of interest, which are also the first pass
    /// allowed to cut into the ground, because a way in is a hole rather than a building;
    /// version 8 adds crossings, which span narrow water so a route is never gated on swimming;
    /// version 9 bounds how deep a structure may cut, so a way in is never a pit a character
    /// with no climb reach cannot step out of; version 10 measures the relief a cave mouth or a
    /// descent is cut into across a distance rather than as a single step, because a one-block
    /// step is not a hillside and gating on it left two kinds unplaced in the real world;
    /// version 11 ramps each end of a crossing down to its bank, because a deck laid at the
    /// higher bank left the lower one unclimbable.
    /// </summary>
    internal const uint CurrentVersion = 11;

    private const string GenerationScope = "craftsurvive.terrain";

    /// <summary>Coordinate keys are stable text, so a draw names its own inputs.</summary>
    internal static string CoordinateKey(long x, long z) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x},{z}");

    /// <summary>
    /// A keyed draw in [minimum, maximum]. The version is mixed into the seed so a
    /// version bump redraws everything, and the key carries the coordinates so two
    /// chunks never disagree about the same feature.
    /// </summary>
    internal long DrawLong(ITerrainDraws draws, string purpose, string key, long minimum, long maximum)
    {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "A draw range must not be inverted.");
        }

        return draws.Draw($"{GenerationScope}.{purpose}", key, MixVersion(Seed), minimum, maximum);
    }

    /// <summary>
    /// A one-in-N draw, the shape most feature placement needs. It compares against
    /// a fraction of a wide range rather than testing a narrow draw for an exact
    /// value: a narrow modulo is only as uniform as the low bits of whatever
    /// implements the port, and a biased draw here reads as "this feature is never
    /// placed", which is exactly how it presented the first time.
    /// </summary>
    internal bool DrawUnit(ITerrainDraws draws, string purpose, string key, long oneIn)
    {
        if (oneIn < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(oneIn), oneIn, "A one-in-N draw needs N of at least one.");
        }

        const long Scale = 1_000_000;
        return DrawLong(draws, purpose, key, 0, Scale - 1) < Scale / oneIn;
    }

    private ulong MixVersion(ulong seed) => seed ^ (Version * 0x9e3779b97f4a7c15UL);
}

/// <summary>
/// The generation draw port. Generation needs one thing from a random source - a
/// stateless, keyed, reproducible value - and nothing else, so this is the whole
/// contract rather than the Engine's stream API.
/// </summary>
internal interface ITerrainDraws
{
    long Draw(string scope, string key, ulong seed, long minimum, long maximum);
}

/// <summary>
/// The Engine-backed implementation: every draw is an Engine keyed draw, so the
/// product owns placement policy and the Engine owns reproducibility. Nothing else
/// in generation touches randomness.
/// </summary>
internal sealed class EngineTerrainDraws(IRandomService random) : ITerrainDraws
{
    private readonly IRandomService random = random ?? throw new ArgumentNullException(nameof(random));

    public long Draw(string scope, string key, ulong seed, long minimum, long maximum)
    {
        KeyedRngReceipt receipt = random.DrawKeyed(new KeyedRngRequest(seed, scope, key, minimum, maximum));
        return receipt.Value;
    }
}
