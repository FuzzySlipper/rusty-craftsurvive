using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The managed lanes' one draw port. The Engine's keyed RNG is the production source and the live
/// golden pins it; this one only has to be a pure function of (world seed, scope, key, draw seed),
/// which is the property the generation contract promises, so its properties - order
/// independence, neighbour agreement, determinism - can be checked without an Engine context.
/// The managed goldens are recorded against it, so changing it re-records them.
/// </summary>
internal sealed class TestDraws(ulong seed = 0) : ITerrainDraws
{
    private const ulong Prime = 0x100000001b3UL;
    private const int FoldShift = 29;

    public long Draw(string scope, string key, ulong drawSeed, long minimum, long maximum)
    {
        ulong value = seed ^ drawSeed;
        foreach (char character in scope)
        {
            value = Mix(value, character);
        }

        foreach (char character in key)
        {
            value = Mix(value, character);
        }

        ulong span = (ulong)(maximum - minimum + 1);
        return minimum + (long)(value % span);
    }

    private static ulong Mix(ulong value, int next)
    {
        unchecked
        {
            value ^= (ulong)next;
            value *= Prime;
            value ^= value >> FoldShift;
            return value;
        }
    }
}
