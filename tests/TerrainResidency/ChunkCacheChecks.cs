using System.Buffers.Binary;
using System.Security.Cryptography;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>The chunk cache as pure policy: its bound and invalidation, and a lossless payload and key.</summary>
internal static class ChunkCacheChecks
{
    internal static void Run()
    {
        // The chunk cache's bound and invalidation, as pure policy: the oldest chunk leaves first, and
        // nothing a different generator wrote survives a start.
        {
            TerrainChunkCacheIndex index = new([]);
            TerrainGeneratorContract contract = TerrainConfiguration.Default.Contract;
            string prefix = TerrainChunkCacheKey.GeneratorPrefix(contract, 0xABCDUL);
            for (int i = 0; i < TerrainChunkCacheIndex.MaximumChunks; i++)
            {
                Check.That(index.Add(TerrainChunkCacheKey.For(contract, 0xABCDUL, new TerrainChunkAddress(i, 0, 0))).Count == 0,
                    "the cache must not evict below its bound");
            }

            IReadOnlyList<string> evicted = index.Add(TerrainChunkCacheKey.For(contract, 0xABCDUL, new TerrainChunkAddress(-1, 0, 0)));
            Check.That(evicted.Count == 1 && evicted[0] == TerrainChunkCacheKey.For(contract, 0xABCDUL, new TerrainChunkAddress(0, 0, 0))
                && index.Keys.Count == TerrainChunkCacheIndex.MaximumChunks, "the oldest chunk must leave first, keeping the bound");
            Check.That(index.Add(index.Keys[^1]).Count == 0 && index.Keys.Count == TerrainChunkCacheIndex.MaximumChunks,
                "rewriting a cached chunk must not grow the index");
            TerrainChunkCacheIndex roundTrip = TerrainChunkCacheIndex.Decode(index.Encode());
            Check.That(roundTrip.Keys.SequenceEqual(index.Keys), "the index must survive its own encoding");
            Check.That(TerrainChunkCacheIndex.Decode([1, 2, 3]).Keys.Count == 0, "a foreign index blob must read as empty");
            string otherGenerator = TerrainChunkCacheKey.For(contract, 0x1234UL, new TerrainChunkAddress(0, 0, 0));
            TerrainChunkCacheIndex mixed = new([otherGenerator, index.Keys[0]]);
            IReadOnlyList<string> stale = mixed.RetainOnly(prefix);
            Check.That(stale.SequenceEqual([otherGenerator]) && mixed.Keys.All(key => key.StartsWith(prefix, StringComparison.Ordinal)),
                "a start must drop every chunk another generator wrote");
            Check.That(TerrainChunkCacheKey.For(contract, 0xABCDUL, new TerrainChunkAddress(0, 0, 0))
                != TerrainChunkCacheKey.For(contract, 0x1234UL, new TerrainChunkAddress(0, 0, 0)),
                "two generators must never share a chunk key");
            Console.WriteLine($"Chunk cache: bounded at {TerrainChunkCacheIndex.MaximumChunks} chunks, oldest first; another generator's chunks are dropped.");
        }

        // The chunk cache's payload and key, proven lossless before anything is wired to a
        // store: a cache that silently corrupts a chunk is worse than no cache at all.
        {
            TerrainConfiguration config = new(TerrainConstants.DefaultSeed, TerrainConstants.DefaultSize);
            var generator = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
            TerrainOverlaySnapshot snapshot = new TerrainOverlayState(config.Seed).Snapshot();
            TerrainChunkAddress address = new(1, 0, -2);
            TerrainChunk chunk = generator.Generate(address, snapshot);
            byte[] encoded = TerrainChunkCachePayload.Encode(chunk.Materials.Span);
            Check.That(encoded.Length == TerrainChunkCachePayload.HeaderLength + (chunk.Materials.Length * sizeof(ushort)),
                "the cached payload is not the size of the chunk it carries");
            Check.That(TerrainChunkCachePayload.TryDecode(encoded, out ushort[] decoded), "a freshly encoded payload did not decode");
            Check.That(decoded.AsSpan().SequenceEqual(chunk.Materials.Span), "a chunk did not survive the cache payload round trip");

            // A payload that is truncated, extended or foreign must be refused rather than
            // reinterpreted: a partial chunk would be a silently wrong world.
            Check.That(!TerrainChunkCachePayload.TryDecode(encoded.AsSpan(0, encoded.Length - 2), out _), "a truncated payload was accepted");
            Check.That(!TerrainChunkCachePayload.TryDecode([1, 2, 3, 4, 5, 6, 7, 8], out _), "a foreign payload was accepted");
            Check.That(!TerrainChunkCachePayload.TryDecode(ReadOnlySpan<byte>.Empty, out _), "an empty payload was accepted");

            const ulong Fingerprint = 0x1UL;
            string key = TerrainChunkCacheKey.For(config.Contract, Fingerprint, address);
            Check.That(key != TerrainChunkCacheKey.For(config.Contract, Fingerprint, new TerrainChunkAddress(1, 0, -1)), "two chunks share a cache key");
            Check.That(key != TerrainChunkCacheKey.For(config.Contract with { Version = config.Contract.Version + 1 }, Fingerprint, address),
                "a generation version bump did not change the cache key");
            Check.That(key != TerrainChunkCacheKey.For(config.Contract with { Seed = config.Contract.Seed + 1 }, Fingerprint, address),
                "a different world seed did not change the cache key");
            Console.WriteLine($"Chunk cache payload and key verified: {encoded.Length} bytes, key {key}");
        }
    }
}
