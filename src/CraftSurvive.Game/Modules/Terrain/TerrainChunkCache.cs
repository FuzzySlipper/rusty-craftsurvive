using System.Buffers.Binary;
using System.Globalization;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The identity of a cached chunk payload. A cached chunk is only valid for the world
/// that produced it, so the key carries the whole generation contract - seed, version
/// and extent - and the exact address. A version bump therefore orphans every cached
/// chunk by construction rather than by remembering to clear a cache.
/// </summary>
internal static class TerrainChunkCacheKey
{
    internal const string Scope = "craftsurvive.terrain.cache";
    internal const string Prefix = "chunk";

    internal static string For(Content.TerrainGeneratorContract contract, TerrainChunkAddress address) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}/{contract.Seed:x16}/{contract.Version}/{contract.Extent}/{address.X}.{address.Y}.{address.Z}");
}

/// <summary>
/// The cached payload format: the chunk's material slots, little-endian, behind a magic
/// number and a length. The Engine's persistence primitive carries bytes, so this is the
/// product's own chunk format - which is why it is written and read explicitly rather
/// than by copying memory, and why a payload that does not match is rejected instead of
/// being interpreted as whatever length it happens to be.
/// </summary>
internal static class TerrainChunkCachePayload
{
    /// <summary>"CRCH", little-endian, so a foreign blob fails loudly.</summary>
    private const uint Magic = 0x4843_5243;

    private const int MagicLength = sizeof(uint);
    private const int LengthLength = sizeof(int);
    internal const int HeaderLength = MagicLength + LengthLength;

    internal static byte[] Encode(ReadOnlySpan<ushort> materials)
    {
        byte[] bytes = new byte[HeaderLength + (materials.Length * sizeof(ushort))];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(MagicLength), materials.Length);
        for (int index = 0; index < materials.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(HeaderLength + (index * sizeof(ushort))), materials[index]);
        }

        return bytes;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out ushort[] materials)
    {
        materials = [];
        if (bytes.Length < HeaderLength)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(MagicLength));
        if (count < 0 || bytes.Length != HeaderLength + (count * sizeof(ushort)))
        {
            return false;
        }

        ushort[] decoded = new ushort[count];
        for (int index = 0; index < count; index++)
        {
            decoded[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.Slice(HeaderLength + (index * sizeof(ushort))));
        }

        materials = decoded;
        return true;
    }
}
