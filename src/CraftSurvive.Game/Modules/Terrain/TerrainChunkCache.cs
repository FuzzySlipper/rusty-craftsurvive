using System.Buffers.Binary;
using Rusty.Engine;
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

/// <summary>
/// Generated chunks kept in the Engine's persistence store, keyed by the generation
/// contract and the address.
///
/// The measured costs decide its shape: one 8 KiB chunk payload saves in about 5.6 ms
/// and loads in about 0.03 ms, so a read is cheap enough to sit on the path of a chunk
/// becoming visible while a write is not. This type therefore offers both and leaves
/// the policy to its caller - nothing here writes on a read.
/// </summary>
internal sealed class TerrainChunkCache
{
    private readonly IEngineContext engine;
    private readonly Content.TerrainGeneratorContract contract;
    private readonly PersistenceStore store;

    internal TerrainChunkCache(IEngineContext engine, Content.TerrainGeneratorContract contract)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.contract = contract;
        store = engine.Persistence.OpenStore(new PersistenceOpenRequest(TerrainChunkCacheKey.Scope));
    }

    /// <summary>
    /// Reads a cached chunk, if this world ever wrote one. A payload that does not decode
    /// counts as a miss rather than as an error: a cache is an optimisation, and a wrong
    /// one must not be able to stop a world from generating.
    /// </summary>
    internal bool TryRead(TerrainChunkAddress address, out ushort[] materials)
    {
        materials = [];
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(
            store,
            TerrainChunkCacheKey.For(contract, address)));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        if (!info.Present)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = engine.Persistence.ReadBlobBytes(blob).Span;
        if (!TerrainChunkCachePayload.TryDecode(bytes, out ushort[] decoded)
            || decoded.Length != TerrainConstants.ChunkVolume)
        {
            return false;
        }

        materials = decoded;
        return true;
    }

    /// <summary>Writes one chunk payload under this world's key for it.</summary>
    internal void Write(TerrainChunkAddress address, ReadOnlySpan<ushort> materials)
    {
        engine.Persistence.Save(new PersistenceSaveRequest(
            store,
            TerrainChunkCacheKey.For(contract, address),
            PersistenceRevisionGuard.Any,
            0,
            TerrainChunkCachePayload.Encode(materials)));
    }
}
