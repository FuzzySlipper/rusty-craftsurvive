using System.Buffers.Binary;
using Rusty.Engine;
using System.Globalization;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The identity of a cached chunk payload. A cached chunk is only valid for the generator that
/// produced it, so the key carries the whole generation contract - seed, version and extent -
/// the generator's cache identity (its output fingerprint and the stamp of its sources, see
/// <see cref="TerrainGenerationFingerprint.CacheIdentity"/>), and the exact address. A chunk
/// written by one generator build therefore can never be read under another's tuning, whether
/// or not its version was bumped.
/// </summary>
internal static class TerrainChunkCacheKey
{
    internal const string Scope = "craftsurvive.terrain.cache";
    internal const string Prefix = "chunk";

    /// <summary>Where the list of written chunks is kept, oldest first.</summary>
    internal const string IndexKey = "index";

    /// <summary>The part of every key this generator writes: all of it but the address.</summary>
    internal static string GeneratorPrefix(TerrainGeneratorContract contract, ulong identity) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}/{contract.Seed:x16}/{contract.Version}/{contract.Extent}/{identity:x16}/");

    internal static string For(TerrainGeneratorContract contract, ulong identity, TerrainChunkAddress address) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{GeneratorPrefix(contract, identity)}{address.X}.{address.Y}.{address.Z}");
}

/// <summary>
/// Which chunks the cache holds, oldest first, and the policy that bounds it: at most
/// <see cref="MaximumChunks"/> entries, the oldest evicted first, and anything a different
/// generator wrote dropped. Pure, so the policy is checked without a store.
/// </summary>
internal sealed class TerrainChunkCacheIndex
{
    /// <summary>At 8 KiB a chunk, the cache holds at most 16 MiB.</summary>
    internal const int MaximumChunks = 2_048;

    private const uint Magic = 0x5849_5243;

    private readonly List<string> keys;

    internal TerrainChunkCacheIndex(IEnumerable<string> keys) => this.keys = [.. keys];

    internal IReadOnlyList<string> Keys => keys;

    /// <summary>Drops every key that does not start with the current generator's prefix and returns them.</summary>
    internal IReadOnlyList<string> RetainOnly(string generatorPrefix)
    {
        List<string> stale = keys.Where(key => !key.StartsWith(generatorPrefix, StringComparison.Ordinal)).ToList();
        keys.RemoveAll(key => !key.StartsWith(generatorPrefix, StringComparison.Ordinal));
        return stale;
    }

    /// <summary>Records a write and returns the keys evicted to keep within the bound.</summary>
    internal IReadOnlyList<string> Add(string key)
    {
        if (keys.Contains(key))
        {
            return [];
        }

        List<string> evicted = [];
        while (keys.Count >= MaximumChunks)
        {
            evicted.Add(keys[0]);
            keys.RemoveAt(0);
        }

        keys.Add(key);
        return evicted;
    }

    internal byte[] Encode()
    {
        byte[][] encoded = keys.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();
        byte[] bytes = new byte[sizeof(uint) + sizeof(int) + encoded.Sum(key => sizeof(int) + key.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(uint)), encoded.Length);
        int offset = sizeof(uint) + sizeof(int);
        foreach (byte[] key in encoded)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), key.Length);
            key.CopyTo(bytes, offset + sizeof(int));
            offset += sizeof(int) + key.Length;
        }

        return bytes;
    }

    /// <summary>Reads a stored index; a blob that does not parse is an empty index.</summary>
    internal static TerrainChunkCacheIndex Decode(ReadOnlySpan<byte> bytes)
    {
        List<string> keys = [];
        if (bytes.Length < sizeof(uint) + sizeof(int) || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            return new TerrainChunkCacheIndex(keys);
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(uint)..]);
        int offset = sizeof(uint) + sizeof(int);
        for (int index = 0; index < count; index++)
        {
            if (offset + sizeof(int) > bytes.Length)
            {
                return new TerrainChunkCacheIndex([]);
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            if (length < 0 || offset + sizeof(int) + length > bytes.Length)
            {
                return new TerrainChunkCacheIndex([]);
            }

            keys.Add(System.Text.Encoding.UTF8.GetString(bytes.Slice(offset + sizeof(int), length)));
            offset += sizeof(int) + length;
        }

        return new TerrainChunkCacheIndex(keys);
    }
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
/// Generated chunks kept in the Engine's persistence store, keyed by the generator that wrote
/// them and bounded by <see cref="TerrainChunkCacheIndex"/>.
///
/// The measured costs decide its shape: one 8 KiB chunk payload saves in about 5.6 ms and loads
/// in about 0.03 ms, so a read is cheap enough to sit on the path of a chunk becoming visible
/// while a write is not. This type offers both and leaves the policy to its caller - nothing
/// here writes on a read. At start it deletes whatever another generator left behind.
/// </summary>
internal sealed class TerrainChunkCache : IDisposable
{
    /// <summary>The index is saved every this many writes, and on dispose.</summary>
    private const int IndexSaveInterval = 16;

    private readonly IEngineContext engine;
    private readonly TerrainGeneratorContract contract;
    private readonly ulong identity;
    private readonly PersistenceStore store;
    private readonly TerrainChunkCacheIndex index;
    private int unsavedWrites;

    internal TerrainChunkCache(IEngineContext engine, TerrainGeneratorContract contract, ulong identity)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.contract = contract;
        this.identity = identity;
        store = engine.Persistence.OpenStore(new PersistenceOpenRequest(TerrainChunkCacheKey.Scope));
        using (PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store, TerrainChunkCacheKey.IndexKey)))
        {
            index = engine.Persistence.DescribeBlob(blob).Present
                ? TerrainChunkCacheIndex.Decode(engine.Persistence.ReadBlobBytes(blob).Span)
                : new TerrainChunkCacheIndex([]);
        }

        IReadOnlyList<string> stale = index.RetainOnly(TerrainChunkCacheKey.GeneratorPrefix(contract, identity));
        foreach (string key in stale)
        {
            engine.Persistence.Delete(new PersistenceDeleteRequest(store, key, PersistenceRevisionGuard.Any, 0));
        }

        StaleDropped = stale.Count;
        if (stale.Count > 0)
        {
            SaveIndex();
        }
    }

    /// <summary>How many chunks another generator had left, deleted at start.</summary>
    internal int StaleDropped { get; }

    internal int Count => index.Keys.Count;

    /// <summary>
    /// Reads a cached chunk, if this generator ever wrote one. A payload that does not decode
    /// counts as a miss rather than as an error: a cache is an optimisation, and a wrong one must
    /// not be able to stop a world from generating.
    /// </summary>
    internal bool TryRead(TerrainChunkAddress address, out ushort[] materials)
    {
        materials = [];
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(
            store,
            TerrainChunkCacheKey.For(contract, identity, address)));
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

    /// <summary>Writes one generated chunk, evicting the oldest when the cache is full.</summary>
    internal void Write(TerrainChunkAddress address, ReadOnlySpan<ushort> materials)
    {
        string key = TerrainChunkCacheKey.For(contract, identity, address);
        foreach (string evicted in index.Add(key))
        {
            engine.Persistence.Delete(new PersistenceDeleteRequest(store, evicted, PersistenceRevisionGuard.Any, 0));
        }

        engine.Persistence.Save(new PersistenceSaveRequest(
            store,
            key,
            PersistenceRevisionGuard.Any,
            0,
            TerrainChunkCachePayload.Encode(materials)));
        if (++unsavedWrites >= IndexSaveInterval)
        {
            SaveIndex();
        }
    }

    public void Dispose()
    {
        if (unsavedWrites > 0)
        {
            SaveIndex();
        }

        store.Dispose();
    }

    private void SaveIndex()
    {
        engine.Persistence.Save(new PersistenceSaveRequest(
            store, TerrainChunkCacheKey.IndexKey, PersistenceRevisionGuard.Any, 0, index.Encode()));
        unsavedWrites = 0;
    }
}
