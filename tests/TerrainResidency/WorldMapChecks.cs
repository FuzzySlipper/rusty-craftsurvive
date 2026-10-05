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
            Check.That(candidate.Sites.Select(s => WorldMap.Region(s.Geography)).Distinct().Count() >= 4,
                "every seed's map holds several distinct environments, each with a representative site");
        }
        foreach (int size in new[] { 32, 4096, 16384, TerrainConstants.MaximumSize })
        {
            TerrainConfiguration config = new(12345, size);
            WorldMap map = WorldMapGenerator.Generate(config);
            Check.That(map.Grid.Count <= MapGrid.MaximumNodes, "map memory is bounded independently of voxel extent");
            Check.That(size < MapGrid.TargetSpacing * MapGrid.MinimumSegments || size > MapGrid.TargetSpacing * MapGrid.MaximumSegments
                || Math.Abs(map.Spacing - MapGrid.TargetSpacing) < 1, "geography is resolved at a fixed metre spacing, not stretched with extent");
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
            foreach ((long wx, long wz) in new[] { (edge, 0L), (0L, -edge) })
            {
                long guard = Math.Max(recipe.SurfaceAt(wx, wz), GenerationConstants.WaterLevel) + GenerationConstants.WorldWallRise;
                Check.That(recipe.MaterialAt(new(wx, guard, wz)) == (ushort)BlockId.Bedrock,
                    "the authored border rises its stated height above the ground it stands on");
                Check.That(guard <= recipe.MaximumMaterialY, "the border wall fits the terrain's vertical bounds");
            }
            Check.That(recipe.MaterialAt(new(edge + 1, GenerationConstants.WaterLevel, 0)) == TerrainConstants.EmptyMaterial,
                "the boundary does not generate an infinite outer region");
            for (int i = 1; i < map.Segments; i++)
            {
                double x = map.Coordinate(i);
                Check.That(Math.Abs(map.Sample(x - 0.0001, 0).Elevation - map.Sample(x + 0.0001, 0).Elevation) < 0.001,
                    "geography is continuous across coarse cell boundaries");
            }
            if (size < 4096) continue;
            Check.That(map.Fields.Elevation.Max() - map.Fields.Elevation.Min() > 150,
                "map has mountain-scale relief before local noise");
            Check.That(map.Sites.Select(s => WorldMap.Region(s.Geography)).Distinct().Count() == map.Sites.Count,
                "each representative site stands for a different environment");
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
