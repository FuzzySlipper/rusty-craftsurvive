using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// A hash of a fixed box of generated chunks, including the height band that
/// carries surface features. It exists so generation determinism can be checked
/// where generation actually runs - in the live product, through the Engine's keyed
/// RNG - rather than only against a test draw port.
/// </summary>
internal static class TerrainGenerationSnapshot
{
    private const long MinimumX = -2;
    private const long MaximumX = 2;
    private const long MinimumZ = -2;
    private const long MaximumZ = 2;
    private const long MinimumY = 2;
    private const long MaximumY = 6;

    internal static string Hash(TerrainRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var generator = new TerrainChunkGenerator(recipe);
        var overlay = new TerrainOverlayState(recipe.Contract.Seed);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] bytes = new byte[TerrainConstants.ChunkVolume * sizeof(ushort)];
        for (long x = MinimumX; x <= MaximumX; x++)
        {
            for (long z = MinimumZ; z <= MaximumZ; z++)
            {
                for (long y = MinimumY; y <= MaximumY; y++)
                {
                    TerrainChunk chunk = generator.Generate(new TerrainChunkAddress(x, y, z), overlay.Snapshot());
                    for (int index = 0; index < chunk.Materials.Length; index++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            bytes.AsSpan(index * sizeof(ushort)), chunk.Materials.Span[index]);
                    }

                    hash.AppendData(bytes);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
