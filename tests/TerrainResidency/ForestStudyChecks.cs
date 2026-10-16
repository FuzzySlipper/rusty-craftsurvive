using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

internal static class ForestStudyChecks
{
    private const long Reach = 256;
    private const int CoreSamples = 48;

    internal static void Run()
    {
        TerrainConfiguration config = TerrainConfiguration.Default with { Seed = 12345 };
        TerrainRecipe baseline = config.CreateRecipe(new TestDraws(config.Seed));
        TerrainRecipe dense = new(config, new TestDraws(config.Seed), baseline.Map, ForestPattern.Dense);
        int node = Enumerable.Range(0, baseline.Map.Grid.Count)
            .Where(i => ForestPatterns.IsForest(WorldMap.Biome(baseline.Map.Sample(baseline.Map.Grid.X(i), baseline.Map.Grid.Z(i)))))
            .MinBy(i => Math.Abs(baseline.Map.Grid.X(i)) + Math.Abs(baseline.Map.Grid.Z(i)));
        long x = (long)baseline.Map.Grid.X(node), z = (long)baseline.Map.Grid.Z(node);
        List<TerrainTree> before = [], after = [], again = [];
        baseline.TreesIn(x - Reach, z - Reach, x + Reach, z + Reach, before);
        dense.TreesIn(x - Reach, z - Reach, x + Reach, z + Reach, after);
        dense.TreesIn(x - Reach, z - Reach, x + Reach, z + Reach, again);
        int forestBefore = before.Count(t => ForestPatterns.IsForest(WorldMap.Biome(baseline.Geography(t.X, t.Z))));
        int forestAfter = after.Count(t => ForestPatterns.IsForest(WorldMap.Biome(dense.Geography(t.X, t.Z))));
        Check.That(forestBefore > 0 && forestAfter > forestBefore * 4,
            $"dense recipe must materially increase forest cover: {forestBefore} -> {forestAfter}");
        Check.That(after.SequenceEqual(again), "forest enumeration must be deterministic after cache reuse");
        long nearReach = TerrainConstants.RetainedChunkRadius * TerrainConstants.ChunkEdgeLength;
        List<TerrainTree> near = [];
        dense.TreesIn(x - nearReach, z - nearReach, x + nearReach, z + nearReach, near);
        int clears = dense.TreeCacheClears;
        near.Clear();
        dense.TreesIn(x - nearReach, z - nearReach, x + nearReach, z + nearReach, near);
        Check.That(dense.TreeCacheClears == clears,
            "a repeated dense near-tree query must fit the cache instead of regenerating it every residency update");
        Check.That(after.Select(t => (t.X, t.Z)).Distinct().Count() == after.Count, "dense tree anchors must be unique");
        foreach (TerrainTree tree in after.Take(CoreSamples))
        {
            Check.That(dense.TreeAtColumn(tree.X, tree.Z) == tree, "dense mesh and column decisions must agree");
            Check.That(dense.MaterialAt(new(tree.X, tree.GroundY, tree.Z)) == (ushort)BlockId.TreeCore,
                "dense mesh must keep a real colliding trunk core");
            Check.That(dense.TreeInCell(GridMath.FloorDivide(tree.X, dense.TreeCellMetres),
                GridMath.FloorDivide(tree.Z, dense.TreeCellMetres)) == tree, "near/far dense anchors must agree");
        }
        Check.That(ForestPatterns.OneIn(ForestPattern.Dense, MapBiome.Grassland, 1)
            == MapBiomes.TreeOneIn(MapBiome.Grassland) * 4, "subdivision must preserve expected prairie density");
        Console.WriteLine($"Forest study at {x},{z}: baseline={forestBefore}, dense={forestAfter}; existing tree meshes, 512 m square.");
    }
}
