using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Testing;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// End-to-end chunk-cache provenance across cave admission, release-before-eviction, cache drain,
/// and subsequent un-admitted reads (#11187 R11187-1). Verifies stale cave interior materials held
/// by a resident chunk when a cave is released can never be persisted into the overworld disk cache.
/// </summary>
internal static class CaveCacheChecks
{
    internal static void Run()
    {
        string cacheRoot = Path.Combine(Path.GetTempPath(), "craft-cave-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using EngineTestHost host = EngineTestHost.Create(new() { PersistenceRoot = cacheRoot });
            host.Call(engine =>
            {
                TerrainConfiguration config = TerrainConfiguration.Default;
                TerrainRecipe recipe = config.CreateRecipe(EngineTerrainDraws.Shared);

                // Find a real cave and an interior box-only chunk outside its tendril.
                List<PoiSite> sites = [];
                recipe.Placement.CollectSitesNear(0, 0, 4000, sites);
                CavePlacement? cave = null;
                foreach (PoiSite site in sites.Where(s => s.Kind == PoiKind.CaveMouth).OrderBy(s => Math.Abs(s.X) + Math.Abs(s.Z)))
                {
                    if (recipe.Caves.For(site) is CavePlacement found)
                    {
                        cave = found;
                        break;
                    }
                }

                Check.That(cave is not null, "default world must have a cave within 4 km for cache provenance check");
                if (cave is null) return;

                long edge = TerrainConstants.ChunkEdgeLength;
                TerrainChunkAddress? foundChunk = null;
                for (long cx = GridMath.FloorDivide(cave.BoxMinimum.X, edge) + 1; cx < GridMath.FloorDivide(cave.BoxMaximum.X, edge) && foundChunk is null; cx++)
                for (long cz = GridMath.FloorDivide(cave.BoxMinimum.Z, edge) + 1; cz < GridMath.FloorDivide(cave.BoxMaximum.Z, edge) && foundChunk is null; cz++)
                {
                    TerrainChunkAddress candidate = new(cx, GridMath.FloorDivide(cave.BoxMinimum.Y, edge) + 1, cz);
                    if (cave.ChunkTouchesBox(candidate) && !cave.ChunkTouchesTendril(candidate))
                    {
                        foundChunk = candidate;
                    }
                }

                Check.That(foundChunk is not null, "cave must have an interior box-only chunk outside its tendril");
                if (foundChunk is not TerrainChunkAddress boxChunk) return;

                TerrainOverlaySnapshot pristine = new(config.Seed, []);
                TerrainChunk expectedOverworld = new TerrainChunkGenerator(recipe).Generate(boxChunk, pristine);

                // Build a synthetic cave interior with carved air strictly inside boxChunk.
                int volume = CavePlacement.SizeX * CavePlacement.SizeY * CavePlacement.SizeZ;
                ushort[] caveMats = new ushort[volume];
                float[] caveDens = new float[volume];
                Array.Fill(caveMats, (ushort)BlockId.CaveLimestone);
                Array.Fill(caveDens, 1.5f);
                Modules.Terrain.VoxelAddress boxOrigin = boxChunk.Origin;
                int carved = 0;
                for (int z = 2; z < 14; z++)
                for (int y = 2; y < 14; y++)
                for (int x = 2; x < 14; x++)
                {
                    if (cave.InBox(boxOrigin.X + x, boxOrigin.Y + y, boxOrigin.Z + z, out int ci, out int cj, out int ck))
                    {
                        int idx = (((ck * CavePlacement.SizeY) + cj) * CavePlacement.SizeX) + ci;
                        caveMats[idx] = TerrainConstants.EmptyMaterial;
                        caveDens[idx] = 0.25f;
                        carved++;
                    }
                }

                Check.That(carved > 0, "synthetic cave interior must carve air inside boxChunk");
                CaveInterior interior = new(CavePlacement.SizeX, CavePlacement.SizeY, CavePlacement.SizeZ, caveMats, caveDens);

                using SpatialSession session = engine.Spatial.CreateSession(new SpatialSessionConfig(
                    TerrainConstants.VoxelSize,
                    TerrainConstants.VoxelChunkSize,
                    VoxelSurfaceMode.DualContouring));
                VoxelMaterialRules.Apply(engine, session);

                using TerrainChunkCache cache = new(engine, recipe.Contract, 0xCAFE_0000_0001_1187UL);
                TerrainChunkGenerator generator = new(recipe, cache);
                TerrainResidencyPolicy policy = new(recipe, generator);
                TerrainOverlayState overlay = new(config.Seed);
                TerrainResidencyStreamer streamer = new(engine, policy, generator, cache, overlay, config.Seed)
                {
                    Workers = false,
                    PlayerStill = true,
                };
                try
                {
                    // 1. Admit cave and stream boxChunk (unedited) into residency with its carved cave interior.
                    recipe.Caves.Admit(cave, interior);
                    policy.SetUnderground(new TerrainResidencyPolicy.UndergroundResidency(1, -1, false, new HashSet<TerrainChunkAddress> { boxChunk }));
                    for (int step = 0; step < 4 && !streamer.IsResident(boxChunk); step++)
                    {
                        streamer.Synchronize(session, boxChunk);
                    }

                    Check.That(streamer.IsResident(boxChunk), "boxChunk must become resident while cave is admitted");
                    Check.That(!generator.Cacheable(boxChunk), "boxChunk must not be cacheable while cave is admitted");
                    ushort[] admittedSnapshot = new ushort[TerrainConstants.ChunkVolume];
                    Check.That(streamer.TryAdmittedMaterials(boxChunk, admittedSnapshot)
                        && admittedSnapshot.Contains(TerrainConstants.EmptyMaterial),
                        "resident boxChunk must hold the admitted cave interior's carved air");

                    // 2. Release cave while boxChunk is still resident (only marks stale; chunk is not replaced).
                    recipe.Caves.Release(cave);
                    policy.Forget(address => address == boxChunk);
                    streamer.MarkStale(address => address == boxChunk);
                    Check.That(streamer.IsResident(boxChunk) && generator.Cacheable(boxChunk),
                        "released boxChunk remains resident until evicted and becomes cacheable again");

                    // 3. Synchronize against a plan that no longer retains boxChunk -> evicts without replacing first.
                    policy.SetUnderground(new TerrainResidencyPolicy.UndergroundResidency(2, -1, false, new HashSet<TerrainChunkAddress>()));
                    streamer.Synchronize(session, boxChunk);
                    Check.That(!streamer.IsResident(boxChunk), "boxChunk must be evicted once dropped from the plan");

                    // 4. Drain the pending cache write on quiet updates.
                    for (int step = 0; step < TerrainConstants.ResidencyCacheWriteRestUpdates + 8 && cache.Count == 0; step++)
                    {
                        streamer.Synchronize(session, boxChunk);
                    }

                    Check.That(cache.Count == 1, $"evicted boxChunk must flush to the disk cache, count={cache.Count}");

                    // 5. Subsequent un-admitted read must hit the disk cache and return pure overworld terrain, never stale cave air.
                    TerrainChunkGenerator reader = new(recipe, cache);
                    TerrainChunk reloaded = reader.Generate(boxChunk, pristine);
                    Check.That(reader.CacheHits == 1, $"un-admitted read must hit the disk cache, hits={reader.CacheHits}");
                    Check.That(reloaded.Materials.Span.SequenceEqual(expectedOverworld.Materials.Span)
                        && reloaded.Densities.Span.SequenceEqual(expectedOverworld.Densities.Span),
                        "cached boxChunk after release-before-eviction must hold pure overworld terrain, not stale cave interior");

                    // 6. Re-admitting the cave must bypass the disk cache and serve live cave interior again.
                    recipe.Caves.Admit(cave, interior);
                    TerrainChunk readmitted = reader.Generate(boxChunk, pristine);
                    Check.That(reader.CacheHits == 1, "re-admitted cave chunk must bypass the overworld disk cache");
                    Check.That(readmitted.Materials.Span.Contains(TerrainConstants.EmptyMaterial),
                        "re-admitted cave chunk must serve live cave interior air");
                }
                finally
                {
                    streamer.Stop();
                    streamer.Clear();
                }
            });
        }
        finally
        {
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true);
        }
    }
}
