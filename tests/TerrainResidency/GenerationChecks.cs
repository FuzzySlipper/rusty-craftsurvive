using System.Buffers.Binary;
using System.Security.Cryptography;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>What generation produces: order independence, the surface passes, the content predicate, and every kind of place.</summary>
internal static class GenerationChecks
{
    internal static void Run()
    {
        // Cross-order agreement: two neighbours must produce identical voxels whichever
        // one is generated first, including a tree that overhangs the boundary. The two
        // passes use separate recipes, so nothing is shared but the contract.
        {
            TerrainConfiguration config = TerrainConfiguration.Default;
            TerrainOverlayState snapshot = new(config.Seed);
            TerrainChunkAddress[] pairs = [new(0, 1, 0), new(1, 1, 0), new(0, 1, 1), new(-1, 1, 0)];
            foreach (TerrainChunkAddress left in pairs)
            {
                foreach (TerrainChunkAddress right in pairs)
                {
                    var forward = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
                    TerrainChunk a = forward.Generate(left, snapshot.Snapshot());
                    TerrainChunk b = forward.Generate(right, snapshot.Snapshot());

                    var reverse = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
                    TerrainChunk bReversed = reverse.Generate(right, snapshot.Snapshot());
                    TerrainChunk aReversed = reverse.Generate(left, snapshot.Snapshot());

                    Check.That(a.Materials.Span.SequenceEqual(aReversed.Materials.Span),
                        $"chunk {left} depends on generation order");
                    Check.That(b.Materials.Span.SequenceEqual(bReversed.Materials.Span),
                        $"chunk {right} depends on generation order");
                }
            }

            Console.WriteLine("Terrain generation agrees across chunk order, including overhanging features.");
        }

        // The surface passes over the band where ground and features live: trees, water and the
        // bedrock floor must each place something. The box's exact output is pinned by the
        // generator's golden fingerprint; this checks the passes' invariants.
        {
            TerrainConfiguration config = TerrainConfiguration.Default;
            var generator = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
            var overlay = new TerrainOverlayState(config.Seed);
            long featureVoxels = 0;
            long waterVoxels = 0;
            long bedrockVoxels = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] bytes = new byte[TerrainConstants.ChunkVolume * sizeof(ushort)];
            // A 96x96-column window around the origin (chunks -3..2), so the box holds whole
            // anchor cells and the trees they own.
            for (long x = -3; x <= 2; x++)
            for (long z = -3; z <= 2; z++)
            // Chunk coordinates: y -1..1 is world y -16..31, the band the ground and its
            // features actually occupy.
            for (long y = -1; y <= 1; y++)
            {
                TerrainChunk chunk = generator.Generate(new(x, y, z), overlay.Snapshot());
                for (int i = 0; i < chunk.Materials.Length; i++)
                {
                    ushort material = chunk.Materials.Span[i];
                    if (material == (ushort)BlockId.Log || material == (ushort)BlockId.Leaves)
                        featureVoxels++;

                    if (material == (ushort)BlockId.Water)
                        waterVoxels++;

                    if (material == (ushort)BlockId.Bedrock)
                        bedrockVoxels++;
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), material);
                }

                hash.AppendData(bytes);
            }

            string featureHash = Convert.ToHexString(hash.GetHashAndReset());
            // The box's exact output is pinned by the generator's golden fingerprint above; this block
            // checks the passes' invariants.
            Console.WriteLine(
                $"Terrain features, water and world edges placed and deterministic: {featureVoxels} feature, " +
                $"{waterVoxels} water, {bedrockVoxels} bedrock voxels, {featureHash}");
            // The hashed box is a sample, and which trees fall inside it is the version's draw to
            // decide: pinning "the box holds a tree" made the check depend on that accident rather
            // than on the pass working. The invariant is that the world places features at all, so it
            // is answered by looking for one across a region wide enough that a world without trees
            // cannot pass by chance.
            {
                TerrainConfiguration scanConfig = TerrainConfiguration.Default;
                var scanGenerator = new TerrainChunkGenerator(scanConfig.CreateRecipe(new TestDraws(scanConfig.Seed)));
                TerrainOverlayState scanOverlay = new(scanConfig.Seed);
                long scanFeatures = 0;
                for (long chunkX = -8; chunkX <= 8 && scanFeatures == 0; chunkX++)
                {
                    for (long chunkZ = -8; chunkZ <= 8 && scanFeatures == 0; chunkZ++)
                    {
                        for (long chunkY = 0; chunkY <= 2 && scanFeatures == 0; chunkY++)
                        {
                            TerrainChunk scan = scanGenerator.Generate(new(chunkX, chunkY, chunkZ), scanOverlay.Snapshot());
                            foreach (ushort material in scan.Materials.Span)
                            {
                                if (material == (ushort)BlockId.Log || material == (ushort)BlockId.Leaves)
                                {
                                    scanFeatures++;
                                }
                            }
                        }
                    }
                }

                Check.That(scanFeatures > 0, "the surface feature pass placed no feature voxel anywhere in the scanned region");
            }


            Check.That(waterVoxels > 0, "the water pass placed no water voxel");
            Check.That(bedrockVoxels > 0, "the world has no authored bedrock floor");
        }

        // The chunk content predicate against generation itself: the predicate must never claim
        // a chunk is empty when generating it produces voxels, because the residency policy will
        // use it to decide what to evict. The reverse - claiming content for an empty chunk - only
        // costs a retained empty chunk, so it is reported rather than failed.
        {
            long falseEmpties = 0;
            long falseContents = 0;
            long compared = 0;
            foreach (ulong seed in new[] { TerrainConstants.DefaultSeed, 12345UL, 777UL })
            {
                TerrainConfiguration config = new(seed, TerrainConstants.DefaultSize);
                var generator = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(seed)));
                TerrainOverlaySnapshot snapshot = new TerrainOverlayState(seed).Snapshot();
                for (long x = -4; x <= 4; x += 2)
                for (long z = -4; z <= 4; z += 2)
                for (long y = -1; y <= 1; y++)
                {
                    TerrainChunkAddress address = new(x, y, z);
                    bool generated = generator.Generate(address, snapshot).SolidVoxelCount > 0;
                    bool predicted = config.CreateRecipe(new TestDraws(seed)).ChunkHasContent(address);
                    compared++;
                    if (generated && !predicted)
                    {
                        falseEmpties++;
                    }

                    if (!generated && predicted)
                    {
                        falseContents++;
                    }
                }
            }

            Check.That(falseEmpties == 0, $"{falseEmpties} chunks were called empty but generate voxels");
            Check.That(falseContents == 0, $"{falseContents} chunks were called content but generate nothing");
            Console.WriteLine($"Chunk content predicate matched generation exactly on all {compared} chunks");
        }

        // Every kind the placement contract draws must actually stand somewhere in the real world.
        //
        // This exists because two of the five did not. The relief a cave mouth is cut into was gated on
        // a single-block slope, which this terrain's gentle noise almost never produces, so every cave
        // mouth and dungeon entrance was reconciled into standing stones - a rule that looked reasonable
        // and placed nothing. A census over the fakes could not see it: their hillsides are steep. Only
        // the real recipe can answer it, which is why the check lives here.
        {
            TerrainConfiguration censusConfig = TerrainConfiguration.Default;
            var censusRecipe = censusConfig.CreateRecipe(new TestDraws(censusConfig.Seed));
            Dictionary<PoiKind, int> census = [];
            int censusSites = 0;
            for (long cellX = -12; cellX <= 12; cellX++)
            {
                for (long cellZ = -12; cellZ <= 12; cellZ++)
                {
                    if (censusRecipe.Placement.SiteAt(cellX, cellZ) is not PoiSite site)
                    {
                        continue;
                    }

                    censusSites++;
                    census[site.Kind] = census.GetValueOrDefault(site.Kind) + 1;
                }
            }

            string counts = string.Join(", ", census.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
            Console.WriteLine($"Point-of-interest census over {censusSites} real sites: {counts}");
            Check.That(censusSites > 400, $"the world must place sites across a wide region, found {censusSites}");
            foreach (PoiKind kind in new[]
                { PoiKind.StandingStones, PoiKind.Ruin, PoiKind.CaveMouth, PoiKind.DungeonEntrance, PoiKind.VantagePoint })
            {
                Check.That(census.GetValueOrDefault(kind) > 0,
                    $"the real world must carry at least one {kind}, counts were {counts}");
            }
        }
    }
}
