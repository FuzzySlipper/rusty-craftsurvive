using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The forests (#10032): dense where the climate grows them, with forest floor under a closed canopy,
/// several species in stands of mostly one, sparse open-country trees, understory and scrub plants, and
/// a mesh for every variant the flora catalog names.
/// </summary>
internal static class ForestChecks
{
    private const long Square = 128;
    private const int Probes = 400;

    // A closed forest's 128 m square holds at least this many trees (one per 4 m cell at most).
    private const int DenseTrees = 250;
    // An open country's square holds at most this share of the densest forest square's trees.
    private const double OpenShare = 0.25;
    // A stand's commonest species is at least this share of its trees.
    private const double StandDominance = 0.5;
    private const int MinimumForestSpecies = 3;
    private const int PlantProbes = 4000;
    private const string MeshSuffix = ".prop-mesh.json";

    internal static void Run()
    {
        TerrainConfiguration config = TerrainConfiguration.Default with { Seed = 12345 };
        TerrainRecipe recipe = config.CreateRecipe(new TestDraws(config.Seed));
        Forest forest = recipe.Forest;
        WorldMap map = recipe.Map;
        (long x, long z, double canopy)[] probes = [.. Enumerable.Range(0, map.Grid.Count)
            .Where(i => i % Math.Max(1, map.Grid.Count / Probes) == 0)
            .Select(i => ((long)map.Grid.X(i), (long)map.Grid.Z(i)))
            .Where(p => Math.Abs(p.Item1) < recipe.Radius - Square && Math.Abs(p.Item2) < recipe.Radius - Square)
            .Select(p => (p.Item1, p.Item2, forest.Canopy(p.Item1, p.Item2, recipe.Geography(p.Item1, p.Item2))))];
        var (fx, fz, closed) = probes.MaxBy(p => p.canopy);
        Check.That(closed >= Forest.FloorFromCanopy, $"the world must have closed forest somewhere: best canopy {closed:F2}");

        List<TerrainTree> trees = [], again = [];
        recipe.TreesIn(fx - Square / 2, fz - Square / 2, fx + Square / 2, fz + Square / 2, trees);
        recipe.TreesIn(fx - Square / 2, fz - Square / 2, fx + Square / 2, fz + Square / 2, again);
        Check.That(trees.Count >= DenseTrees, $"a closed forest's {Square} m square must be dense: {trees.Count} trees at {fx},{fz}");
        Check.That(trees.SequenceEqual(again), "forest enumeration must be deterministic");
        Check.That(trees.Select(t => (t.X, t.Z)).Distinct().Count() == trees.Count, "tree trunks must not share a column");

        // Forest floor under the canopy, meadow outside it.
        TerrainColumn under = recipe.ColumnAt(fx, fz);
        ushort floor = recipe.MaterialAt(new VoxelAddress(fx, under.Surface, fz), under);
        Check.That(floor is TerrainConstants.DirtMaterial or (ushort)BlockId.TreeCore || under.Slope > GenerationConstants.TopsoilSlopeMaximum,
            $"the ground under a closed canopy at {fx},{fz} must be forest floor, not {(BlockId)floor}");

        // Stands: species vary across the forest, but each stand is mostly one.
        List<TerrainTree> wide = [];
        recipe.TreesIn(fx - Square * 2, fz - Square * 2, fx + Square * 2, fz + Square * 2, wide);
        int species = wide.Select(t => t.Species).Distinct().Count();
        Check.That(species >= MinimumForestSpecies, $"a forest {Square * 4} m across must hold several species: {species}");
        var stands = wide.GroupBy(t => Forest.StandOf(t.X, t.Z)).Where(g => g.Count() >= 20).ToList();
        double dominance = stands.Count == 0 ? 1 : stands.Average(g => g.GroupBy(t => t.Species).Max(s => s.Count()) / (double)g.Count());
        Check.That(dominance >= StandDominance, $"a stand's commonest species must be most of its trees: {dominance:P0} on average");

        // Open country: trees, but far fewer than a forest.
        var (ox, oz, _) = probes.Where(p => p.canopy == 0 && forest.OpenTrees(recipe.Geography(p.x, p.z)) > 0).DefaultIfEmpty((0, 0, 1)).First();
        List<TerrainTree> open = [];
        recipe.TreesIn(ox - Square / 2, oz - Square / 2, ox + Square / 2, oz + Square / 2, open);
        Check.That(open.Count <= trees.Count * OpenShare, $"open country must be open: {open.Count} trees at {ox},{oz} beside {trees.Count} in forest");

        // A repeated dense near query must fit the feature cache, not regenerate it every residency update.
        long nearReach = TerrainConstants.RetainedChunkRadius * TerrainConstants.ChunkEdgeLength;
        List<TerrainTree> near = [];
        recipe.TreesIn(fx - nearReach, fz - nearReach, fx + nearReach, fz + nearReach, near);
        int clears = recipe.TreeCacheClears;
        near.Clear();
        recipe.TreesIn(fx - nearReach, fz - nearReach, fx + nearReach, fz + nearReach, near);
        Check.That(recipe.TreeCacheClears == clears, "a repeated dense near-tree query must fit the feature cache");

        // Every tree's core is real and its column and cell decisions agree.
        foreach (TerrainTree tree in trees.Take(48))
        {
            Check.That(recipe.TreeAtColumn(tree.X, tree.Z) == tree, "column and mesh decisions must agree");
            Check.That(recipe.MaterialAt(new(tree.X, tree.GroundY, tree.Z)) == (ushort)BlockId.TreeCore, "a tree must keep its colliding core");
        }

        // Understory under the canopy and scrub in the open.
        int understory = 0, scrub = 0;
        for (int i = 0; i < PlantProbes; i++)
        {
            long cx = GridMath.FloorDivide(fx, Forest.PlantCellMetres) + (i % 64), cz = GridMath.FloorDivide(fz, Forest.PlantCellMetres) + (i / 64);
            long px = cx * Forest.PlantCellMetres, pz = cz * Forest.PlantCellMetres;
            MapSample here = recipe.Geography(px, pz);
            if (forest.Plant(cx, cz, forest.Canopy(px, pz, here), here, Forest.PlantsPerCell) is FloraSpecies plant)
            {
                if (plant.Ecology!.Habit is FloraHabit.Understory) understory++;
                else scrub++;
            }
        }

        Check.That(understory > 0, "a closed forest must grow understory plants");

        // Every species variant the catalog names has its meshes (each tree also its far variant).
        string models = Path.Combine(ContentChecks.RepositoryRoot(), "content", "game", "models");
        foreach (FloraSpecies flora in FloraCatalog.Shared.Species)
        {
            foreach (string variant in flora.Variants)
            {
                string mesh = Path.Combine(models, flora.Folder, variant + MeshSuffix);
                Check.That(File.Exists(mesh), $"the catalog's {flora.Name} names {variant}, which has no mesh");
                if (flora.Role == FloraRole.Tree)
                    Check.That(File.Exists(Path.Combine(models, flora.Folder, variant + "-far" + MeshSuffix)), $"tree {variant} has no far variant");
            }
        }

        Console.WriteLine(FormattableString.Invariant(
            $"Forests: canopy {closed:F2} at {fx},{fz} holds {trees.Count} trees per {Square} m square, {species} species within {Square * 4} m, stands {dominance:P0} one species; open country {open.Count}; plants {understory} understory, {scrub} scrub in {PlantProbes} cells."));
    }
}
