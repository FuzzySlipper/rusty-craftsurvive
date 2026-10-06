using System.Buffers.Binary;
using System.Security.Cryptography;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>The generator's identity: golden fingerprints, the structure catalogue, the cache identity, and a world's own border.</summary>
internal static class GeneratorChecks
{
    internal static void Run()
    {
        // The generator's golden fingerprints, one per version, over two seeds. The fingerprint covers
        // heights edge to edge, every site and crossing decision in a 41-cell square, 68 whole chunks
        // including a structure and the border wall, and every structure and crossing shape on synthetic
        // sites, so changing any generation constant or rule moves it. A change that moves it must bump TerrainGeneratorContract.CurrentVersion and add a
        // row here; editing an existing row instead would let two different worlds share a version.
        {
            Dictionary<(uint Version, ulong Seed), ulong> golden = new()
            {
                [(20, TerrainConstants.DefaultSeed)] = 0x5b80fee723f058b0UL,
                [(20, 12345UL)] = 0x3f158bee0cb43999UL,
                [(21, TerrainConstants.DefaultSeed)] = 0x429063959670133aUL,
                [(21, 12345UL)] = 0x6ed561d5a5b32a44UL,
                [(22, TerrainConstants.DefaultSeed)] = 0x42861b90686b4c43UL,
                [(22, 12345UL)] = 0x31bb2a69eaac7036UL,
                [(23, TerrainConstants.DefaultSeed)] = 0xf90b5699e4db2423UL,
                [(23, 12345UL)] = 0x3ab08a04d9294e55UL,
                [(19, TerrainConstants.DefaultSeed)] = 0xe808453fe4c8386cUL,
                [(19, 12345UL)] = 0xf439dc53c78a57d1UL,
                [(18, TerrainConstants.DefaultSeed)] = 0x8780952811978cd6UL,
                [(18, 12345UL)] = 0x2a0d018c5f4a8184UL,
                [(17, TerrainConstants.DefaultSeed)] = 0xd2278bad4e00b244UL,
                [(17, 12345UL)] = 0x75a45035a5557fc5UL,
                [(16, TerrainConstants.DefaultSeed)] = 0x4c024e23973f0a81UL,
                [(16, 12345UL)] = 0x91f6ecedc0505ef4UL,
                [(15, TerrainConstants.DefaultSeed)] = 0x6691638c856c2cc5UL,
                [(15, 12345UL)] = 0xf15c8a71097f20b0UL,
                [(12, TerrainConstants.DefaultSeed)] = 0x04ce381c4bc1ec87UL,
                [(12, 12345UL)] = 0x8a9c21d06e49334bUL,
                [(13, TerrainConstants.DefaultSeed)] = 0x5bd56a7343bb75d1UL,
                [(13, 12345UL)] = 0x7295b23670915ba5UL,
                [(14, TerrainConstants.DefaultSeed)] = 0xe41634f42355d93cUL,
                [(14, 12345UL)] = 0x413df3e94ad58c97UL,
            };
            List<string> mismatches = [];
            foreach (ulong seed in new[] { TerrainConstants.DefaultSeed, 12345UL })
            {
                TerrainConfiguration config = new(seed, TerrainConstants.DefaultSize);
                ulong actual = TerrainGenerationFingerprint.Compute(config.CreateRecipe(new TestDraws(seed)), TerrainGenerationFingerprint.Golden);
                if (!golden.TryGetValue((TerrainGeneratorContract.CurrentVersion, seed), out ulong expected) || expected != actual)
                {
                    mismatches.Add($"version {TerrainGeneratorContract.CurrentVersion} seed {seed:x16}: 0x{actual:x16}");
                }
            }

            Check.That(mismatches.Count == 0,
                $"the generator's output changed without a version bump (or a new version has no golden row): {string.Join("; ", mismatches)}");
            Check.That(golden.Values.Distinct().Count() == golden.Count, "two golden rows share a fingerprint");

            // The fingerprint answers to the world's identity, not only to its tuning.
            TerrainConfiguration baseline = new(TerrainConstants.DefaultSeed, TerrainConstants.DefaultSize);
            ulong startup = TerrainGenerationFingerprint.Compute(baseline.CreateRecipe(new TestDraws(baseline.Seed)), TerrainGenerationFingerprint.Startup);
            Check.That(startup == TerrainGenerationFingerprint.Compute(baseline.CreateRecipe(new TestDraws(baseline.Seed)), TerrainGenerationFingerprint.Startup),
                "a fingerprint must repeat for the same world");
            Check.That(startup != TerrainGenerationFingerprint.Compute((baseline with { Seed = baseline.Seed + 1 }).CreateRecipe(new TestDraws(baseline.Seed + 1)), TerrainGenerationFingerprint.Startup),
                "a different seed must move the fingerprint");
            Check.That(startup != TerrainGenerationFingerprint.Compute((baseline with { GeneratorVersion = baseline.GeneratorVersion + 1 }).CreateRecipe(new TestDraws(baseline.Seed)), TerrainGenerationFingerprint.Startup),
                "a different recipe version must move the complete terrain fingerprint");

            // A structure's geometry is part of the identity even when this seed's probe holds no site of
            // its kind: each kind, and the crossing, changed on its own moves the catalogue, and a
            // changed catalogue moves the fingerprint the chunk cache keys on.
            ulong catalogue = TerrainGenerationFingerprint.StructureCatalogue(PoiStructures.MaterialAt, CrossingStructure.MaterialAt);
            for (long kind = PoiConstants.FirstKind; kind <= PoiConstants.LastKind; kind++)
            {
                PoiKind changed = (PoiKind)kind;
                ulong mutated = TerrainGenerationFingerprint.StructureCatalogue(
                    (site, x, y, z) => PoiStructures.MaterialAt(site, site.Kind == changed ? x + 1 : x, y, z),
                    CrossingStructure.MaterialAt);
                Check.That(mutated != catalogue, $"a change to the {changed} builder must move the structure catalogue");
            }

            Check.That(catalogue != TerrainGenerationFingerprint.StructureCatalogue(
                    PoiStructures.MaterialAt, (site, x, y, z) => CrossingStructure.MaterialAt(site, x, y + 1, z)),
                "a change to the crossing builder must move the structure catalogue");
            Check.That(startup == TerrainGenerationFingerprint.Compute(baseline.CreateRecipe(new TestDraws(baseline.Seed)), TerrainGenerationFingerprint.Startup, catalogue),
                "the fingerprint must be taken over the shipped builders");
            Check.That(startup != TerrainGenerationFingerprint.Compute(baseline.CreateRecipe(new TestDraws(baseline.Seed)), TerrainGenerationFingerprint.Startup, catalogue + 1),
                "a changed structure catalogue must move the fingerprint");

            // The cache keys on more than the sampled output: the stamp of the generator's sources moves
            // the identity for a change that shows only far from the probe, and so does the output.
            ulong identity = TerrainGenerationFingerprint.CacheIdentity(startup, 0x1111UL);
            Check.That(identity != TerrainGenerationFingerprint.CacheIdentity(startup, 0x1112UL), "a changed generator source must move the cache identity");
            Check.That(identity != TerrainGenerationFingerprint.CacheIdentity(startup + 1, 0x1111UL), "a changed output must move the cache identity");
            Check.That(TerrainChunkCacheKey.GeneratorPrefix(baseline.Contract, identity)
                    != TerrainChunkCacheKey.GeneratorPrefix(baseline.Contract, TerrainGenerationFingerprint.CacheIdentity(startup, 0x1112UL)),
                "chunks cached by one generator source must not be readable under another");
            Console.WriteLine($"Generator golden fingerprints hold for version {TerrainGeneratorContract.CurrentVersion}; seed, version and every structure builder each move the fingerprint; source and output each move the cache identity.");
        }

        // A world of any size has its border wall at its own edge, not at the default world's.
        {
            const int SmallSize = 256;
            TerrainRecipe small = new TerrainConfiguration(TerrainConstants.DefaultSeed, SmallSize).CreateRecipe(new TestDraws(TerrainConstants.DefaultSeed));
            long edge = SmallSize / 2;
            ushort bedrock = (ushort)BlockId.Bedrock;
            Check.That(small.MaterialAt(new VoxelAddress(edge, GenerationConstants.WaterLevel, 0)) == bedrock,
                "a small world's wall must stand at its own edge");
            long wallTop = Math.Max(small.SurfaceAt(edge, 0), GenerationConstants.WaterLevel) + GenerationConstants.WorldWallRise;
            Check.That(small.MaterialAt(new VoxelAddress(edge, wallTop, 0)) == bedrock
                && small.MaterialAt(new VoxelAddress(edge, wallTop + 1, 0)) != bedrock,
                "a small world's wall rises its stated height above its own ground");
            long inside = edge - GenerationConstants.WorldWallThickness - 1;
            Check.That(small.MaterialAt(new VoxelAddress(inside, wallTop, 0)) != bedrock || small.SurfaceAt(inside, 0) >= wallTop,
                "a small world's wall must not extend inward past its thickness");
            Check.That(small.ChunkHasContent(new VoxelAddress(edge, wallTop, 0).Chunk),
                "the content predicate must see a small world's wall");
            TerrainRecipe large = TerrainConfiguration.Default.CreateRecipe(new TestDraws(TerrainConstants.DefaultSeed));
            Check.That(large.MaterialAt(new VoxelAddress(edge, large.SurfaceAt(edge, 0) + 1, 0)) != bedrock,
                "the default world must have no wall where a small world's edge would be");
            Console.WriteLine($"A {SmallSize}-voxel world has its border at its own edge.");
        }
    }
}
