using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

internal static class WorldMapChecks
{
    internal static void Run()
    {
        foreach (ulong seed in new ulong[] { TerrainConstants.DefaultSeed, 0, 1, 12345, ulong.MaxValue })
        {
            WorldMap candidate = WorldMapGenerator.Generate(new(seed, 8192));
            Check.That(candidate.Sites.Select(s => WorldMap.Region(s.Geography)).Distinct().Count() == 3,
                "representative sites retain distinct regional meanings across seeds");
        }
        foreach (int size in new[] { 32, 4096, 16384, TerrainConstants.MaximumSize })
        {
            TerrainConfiguration config = new(12345, size);
            WorldMap map = WorldMapGenerator.Generate(config);
            Check.That(map.Nodes.Length <= WorldMap.MaximumNodes, "coarse map memory is bounded independently of voxel extent");
            Check.That(map.Fingerprint == WorldMapGenerator.Generate(config).Fingerprint, "map generation is seeded and repeatable");
            Check.That(map.Fingerprint != WorldMapGenerator.Generate(config with { Seed = config.Seed + 1 }).Fingerprint, "seed changes the map");
            Check.That(map.Sample(-map.Radius - 1, 0) == map.Sample(-map.Radius, 0), "outside geographic sampling clamps to the finite edge");
            Check.That(!map.Contains(map.Radius + 1, 0), "clamped sampling does not extend the playable map");
            TerrainRecipe recipe = config.CreateRecipe(new TestDraws(config.Seed), map);
            foreach (MapSite site in map.Sites)
            {
                Check.That(map.Contains(site.X, site.Z), "selected site lies inside the finite map");
                double actual = recipe.ContinuousHeightAt((long)site.X, (long)site.Z);
                Check.That(Math.Abs(actual - map.Sample(site.X, site.Z).Elevation) <= WorldMap.LocalReliefLimit,
                    "local density relief stays anchored to map geography");
            }
            long edge = config.Size / 2;
            long guardHeight = (long)(WorldMap.MaximumElevation + WorldMap.LocalReliefLimit) + 1;
            Check.That(recipe.MaterialAt(new(edge, guardHeight, 0)) == (ushort)BlockId.Bedrock
                && recipe.MaterialAt(new(0, guardHeight, -edge)) == (ushort)BlockId.Bedrock,
                "the authored border rises above the map and local-detail elevation limit");
            Check.That(recipe.MaterialAt(new(edge + 1, guardHeight, 0)) == TerrainConstants.EmptyMaterial,
                "the boundary does not generate an infinite outer region");
            for (int i = 1; i < map.Segments; i++)
            {
                double x = map.Coordinate(i);
                Check.That(Math.Abs(map.Sample(x - 0.0001, 0).Elevation - map.Sample(x + 0.0001, 0).Elevation) < 0.001,
                    "geography is continuous across coarse cell boundaries");
            }
            if (size < 4096) continue;
            Check.That(map.Nodes.ToArray().Max(n => n.Elevation) - map.Nodes.ToArray().Min(n => n.Elevation) > 20,
                "map has meaningful regional relief before local noise");
            Check.That(map.Sites.Select(s => WorldMap.Region(s.Geography)).Distinct().Count() == 3,
                "representative sites cover separated climate/geology families");
            TerrainChunkGenerator forward = new(recipe), backward = new(config.CreateRecipe(new TestDraws(config.Seed), map));
            TerrainOverlaySnapshot overlay = new(config.Seed, []);
            TerrainChunkAddress[] addresses = map.Sites.Select(s => new VoxelAddress((long)s.X, (long)s.Geography.Elevation, (long)s.Z).Chunk).ToArray();
            var expected = addresses.Select(a => forward.Generate(a, overlay)).ToArray();
            for (int i = addresses.Length - 1; i >= 0; i--)
            {
                var actual = backward.Generate(addresses[i], overlay);
                Check.That(actual.Materials.Span.SequenceEqual(expected[i].Materials.Span), "map-local chunks are independent of request order");
                Check.That(actual.Densities.Span.SequenceEqual(expected[i].Densities.Span), "density is independent of request order");
            }
        }
    }
}
