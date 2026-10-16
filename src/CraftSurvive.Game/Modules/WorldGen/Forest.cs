namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Where the world's forests stand and what grows in them (#10032), from the flora catalog's ecology
/// and the map's climate. Pure and seeded, so chunk generation, the vegetation tiles' workers and the
/// tree decisions all agree.
/// <list type="bullet">
/// <item><b>Canopy:</b> how closed the forest is at a point. The canopy species' suitability there,
/// saturating, broken by glades and stand-scale variation, and thinned on rock. Forest edges feather
/// along the climate's gradients rather than stopping at a biome line.</item>
/// <item><b>Forest floor:</b> where the canopy is closed, the topsoil is bare litter, not meadow
/// (TerrainRecipe uses Dirt, which grows no grass).</item>
/// <item><b>Trees:</b> canopy trees with a chance from the canopy, open-country trees (wood pasture,
/// scrub, steppe) sparsely from their own suitability; species come mostly from the stand's own.</item>
/// <item><b>Understory and scrub:</b> shrubs and ferns under the canopy, scrub in the open, by their
/// suitability (presentation only: VegetationTiles places them as records).</item>
/// </list>
/// </summary>
internal sealed class Forest
{
    // How quickly summed canopy suitability closes the canopy: 1 - exp(-k · sum).
    private const double CanopyClosure = 1.6;

    // Glades: low noise over this many metres opens the canopy; the band is where it fades.
    private const int GladeScale = 96;
    private const double GladeFrom = 0.24, GladeTo = 0.36;
    // Stands: the canopy breathes over this scale, between these shares.
    private const int StandScale = 40;
    private const double StandLow = 0.7;
    // Rock thins the forest from this much exposed rock, and holds none at the second.
    private const double RockThinFrom = 0.35, RockBare = 0.6;

    /// <summary>The topsoil under a canopy this closed is forest floor, not meadow.</summary>
    internal const double FloorFromCanopy = 0.55;

    /// <summary>A canopy tree stands in a 4 m cell with this chance at a fully closed canopy: crowns touching.</summary>
    internal const double CanopyTreesPerCell = 0.5;

    /// <summary>An open-country tree stands in a cell with this chance at its best: a tree every few hundred square metres.</summary>
    internal const double OpenTreesPerCell = 0.012;

    /// <summary>One shrub or ground plant may grow in each cell this many metres square (PlantTiles).</summary>
    internal const int PlantCellMetres = 2;

    /// <summary>The chance a plant grows in a cell where its species suits the place best.</summary>
    internal const double PlantsPerCell = 0.14;

    /// <summary>A stand is one species over cells this size; most of its trees are that species.</summary>
    internal const long StandMetres = 48;
    internal const double StandShare = 0.7;

    private readonly ulong gladeSeed, standSeed, mixSeed;
    private readonly FloraSpecies[] canopy, open, edge, understory, scrub;

    internal Forest(FloraCatalog catalog, ulong seed)
    {
        Catalog = catalog;
        gladeSeed = seed ^ 0x9E37_79B9_7F4A_7C15UL;
        standSeed = seed ^ 0xC2B2_AE3D_27D4_EB4FUL;
        mixSeed = seed ^ 0x1656_67B1_9E37_79F9UL;
        FloraSpecies[] With(FloraRole role, params FloraHabit[] habits) =>
            [.. catalog.Species.Where(s => s.Role == role && s.Ecology is FloraEcology e && habits.Contains(e.Habit))];
        canopy = With(FloraRole.Tree, FloraHabit.Canopy);
        open = With(FloraRole.Tree, FloraHabit.Open, FloraHabit.Edge);
        edge = With(FloraRole.Tree, FloraHabit.Edge);
        understory = [.. catalog.Species.Where(s => s.Role != FloraRole.Tree && s.Ecology?.Habit is FloraHabit.Understory or FloraHabit.Edge)];
        scrub = [.. catalog.Species.Where(s => s.Role != FloraRole.Tree && s.Ecology?.Habit is FloraHabit.Open or FloraHabit.Edge)];
    }

    internal FloraCatalog Catalog { get; }

    /// <summary>How closed the canopy is at a point (0 open, 1 closed), from its climate and place.</summary>
    internal double Canopy(double x, double z, MapSample geography)
    {
        if (geography.Rock >= RockBare) return 0;
        double sum = 0;
        foreach (FloraSpecies species in canopy) sum += species.Ecology!.Suitability(geography);
        if (sum <= 0) return 0;
        double closed = 1 - Math.Exp(-CanopyClosure * sum);
        double glade = Smooth((TerrainRecipe.ValueNoise(gladeSeed, x, z, GladeScale) - GladeFrom) / (GladeTo - GladeFrom));
        double stand = StandLow + ((1 - StandLow) * TerrainRecipe.ValueNoise(standSeed, x, z, StandScale));
        double rock = 1 - Smooth((geography.Rock - RockThinFrom) / (RockBare - RockThinFrom));
        return closed * glade * stand * rock;
    }

    /// <summary>The chance an open-country tree stands in a cell here: wood pasture, scrub woodland, steppe.</summary>
    internal double OpenTrees(MapSample geography)
    {
        double best = 0;
        foreach (FloraSpecies species in open) best = Math.Max(best, species.Ecology!.Suitability(geography));
        return OpenTreesPerCell * best;
    }

    /// <summary>
    /// The species of a tree: the stand's own (chosen once per stand from the place's suitable species)
    /// with <see cref="StandShare"/>, otherwise a suitable species drawn afresh. Open-country trees draw
    /// from the open species. Null where nothing suits.
    /// </summary>
    /// <param name="standDraw">A draw in [0, 1) for the tree's stand (the same for every tree in it).</param>
    /// <param name="mixDraw">A draw in [0, 1): whether this tree is the stand's species.</param>
    /// <param name="pickDraw">A draw in [0, 1): which species, when it is not.</param>
    internal FloraSpecies? TreeSpecies(MapSample geography, bool inCanopy, double standDraw, double mixDraw, double pickDraw)
    {
        FloraSpecies[] pool = inCanopy ? canopy : open;
        return Pick(pool, geography, mixDraw < StandShare ? standDraw : pickDraw);
    }

    /// <summary>
    /// A shrub or ground plant at a point, if one grows there: understory under the canopy, scrub in the
    /// open, each by its suitability, and a few edge plants either side. Pure (hashed from the point), so
    /// the vegetation tiles' workers may ask.
    /// </summary>
    /// <param name="perCell">The chance a plant grows in one sampled cell at full suitability.</param>
    internal FloraSpecies? Plant(long cellX, long cellZ, double canopyHere, MapSample geography, double perCell)
    {
        ulong hash = TerrainRecipe.CoordinateHash(mixSeed, cellX, cellZ);
        double present = TerrainRecipe.HashUnit(hash);
        double pick = TerrainRecipe.HashUnit(hash * 0x9E37_79B9_7F4A_7C15UL + 1);
        bool under = canopyHere >= FloorFromCanopy;
        FloraSpecies? species = Pick(under ? understory : scrub, geography, pick);
        if (species is null) return null;
        double suits = species.Ecology!.Suitability(geography) / Math.Max(species.Ecology.Abundance, 1e-6);
        return present < perCell * suits ? species : null;
    }

    /// <summary>A stand's own draw: the same for every tree whose trunk stands in it.</summary>
    internal static (long X, long Z) StandOf(long x, long z) => (GridMath.FloorDivide(x, StandMetres), GridMath.FloorDivide(z, StandMetres));

    private static FloraSpecies? Pick(FloraSpecies[] pool, MapSample geography, double draw)
    {
        double total = 0;
        foreach (FloraSpecies species in pool) total += species.Ecology!.Suitability(geography);
        if (total <= 0) return null;
        double target = draw * total;
        foreach (FloraSpecies species in pool)
        {
            target -= species.Ecology!.Suitability(geography);
            if (target < 0) return species;
        }

        return pool[^1];
    }

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
