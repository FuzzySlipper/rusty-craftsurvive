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
/// <item><b>Understory and scrub (#10034):</b> each plant species grows on its own in every cell, at its
/// catalog density scaled by how well the climate suits it and by its habit's share of the canopy here, so
/// ferns, flowers, shrubs and deadwood overlap. Saplings of the stand's trees grow in its gaps and edges.
/// Presentation only: PlantTiles places them as records.</item>
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

    /// <summary>Plants are drawn per cell this many metres square (PlantTiles): each species on its own.</summary>
    internal const int PlantCellMetres = 2;
    private const double PlantCellArea = PlantCellMetres * PlantCellMetres;
    /// <summary>A species may grow more than once in a cell where its density asks for it, up to this many.</summary>
    private const int MostPerCell = 3;

    // An understory plant grows from this canopy, fully by the second; an open-country plant fades out
    // over the same band; an edge plant peaks between.
    private const double UnderFrom = 0.3, UnderFull = 0.8;
    private const double OpenFrom = 0.1, OpenGone = 0.5;

    /// <summary>Saplings per square metre where the canopy is half open, fewer under a closed one and none in the open.</summary>
    internal const double SaplingsPerSquareMetre = 0.012;
    private const double SaplingCanopyMinimum = 0.15, SaplingUnderClosed = 0.25;
    private const ulong SaplingStream = 0x5A91_1C0E_0000_0001UL;

    /// <summary>A stand is one species over cells this size; most of its trees are that species.</summary>
    internal const long StandMetres = 48;
    internal const double StandShare = 0.7;

    private readonly ulong gladeSeed, standSeed, mixSeed, treeStandSeed;
    private readonly FloraSpecies[] canopy, open, edge, plants;

    internal Forest(FloraCatalog catalog, ulong seed)
    {
        Catalog = catalog;
        gladeSeed = seed ^ 0x9E37_79B9_7F4A_7C15UL;
        standSeed = seed ^ 0xC2B2_AE3D_27D4_EB4FUL;
        mixSeed = seed ^ 0x1656_67B1_9E37_79F9UL;
        treeStandSeed = seed ^ GenerationConstants.TreeStandSeed;
        FloraSpecies[] With(FloraRole role, params FloraHabit[] habits) =>
            [.. catalog.Species.Where(s => s.Role == role && s.Ecology is FloraEcology e && habits.Contains(e.Habit))];
        canopy = With(FloraRole.Tree, FloraHabit.Canopy);
        open = With(FloraRole.Tree, FloraHabit.Open, FloraHabit.Edge);
        edge = With(FloraRole.Tree, FloraHabit.Edge);
        plants = [.. catalog.Species.Where(s => s.Role != FloraRole.Tree && s.Ecology is { Density: > 0 })];
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
    /// The shrubs, ground plants and deadwood growing in one cell (#10034): each species on its own, with a
    /// chance from its density, the climate's fit and its habit's share of the canopy here, so several may
    /// overlap. Each comes with a hash that fixes where in the cell it stands and how it looks. Pure, so the
    /// vegetation tiles' workers may ask.
    /// </summary>
    internal void Plants(long cellX, long cellZ, double canopyHere, MapSample geography, List<(FloraSpecies Species, ulong Hash)> into)
    {
        foreach (FloraSpecies species in plants)
        {
            FloraEcology ecology = species.Ecology!;
            double expected = ecology.Density * PlantCellArea * ecology.Fit(geography) * HabitShare(ecology.Habit, canopyHere);
            if (expected <= 0) continue;
            ulong hash = TerrainRecipe.CoordinateHash(mixSeed ^ ((ulong)species.Id * 0x9E37_79B9_7F4A_7C15UL), cellX, cellZ);
            for (int n = 0; n < MostPerCell && TerrainRecipe.HashUnit(hash) < expected - n; n++)
            {
                into.Add((species, hash * 0xBF58_476D_1CE4_E5B9UL + (ulong)n));
                hash = TerrainRecipe.CoordinateHash(hash, n + 1, 0);
            }
        }
    }

    /// <summary>
    /// A sapling of the stand's trees in one cell, if one grows (#10034): most where the canopy is half open
    /// (gaps and edges), fewer under a closed canopy, none in open country.
    /// </summary>
    internal FloraSpecies? Sapling(long cellX, long cellZ, long x, long z, double canopyHere, MapSample geography, out ulong hash)
    {
        hash = TerrainRecipe.CoordinateHash(mixSeed ^ SaplingStream, cellX, cellZ);
        if (canopyHere < SaplingCanopyMinimum) return null;
        double gaps = Math.Max(SaplingUnderClosed, 4 * canopyHere * (1 - canopyHere));
        if (TerrainRecipe.HashUnit(hash) >= SaplingsPerSquareMetre * PlantCellArea * gaps) return null;
        return TreeSpecies(geography, canopyHere >= FloorFromCanopy, StandDraw(x, z),
            TerrainRecipe.HashUnit(TerrainRecipe.CoordinateHash(hash, 1, 0)), TerrainRecipe.HashUnit(TerrainRecipe.CoordinateHash(hash, 2, 0)));
    }

    /// <summary>The stand's species draw for a trunk at a point: the same for every tree in its stand.</summary>
    internal double StandDraw(long x, long z)
    {
        (long standX, long standZ) = StandOf(x, z);
        return TerrainRecipe.HashUnit(TerrainRecipe.CoordinateHash(treeStandSeed, standX, standZ));
    }

    /// <summary>How much of a habit's growth the canopy here allows: understory in shade, open plants in the open, edge plants between.</summary>
    internal static double HabitShare(FloraHabit habit, double canopyHere) => habit switch
    {
        FloraHabit.Understory => Smooth((canopyHere - UnderFrom) / (UnderFull - UnderFrom)),
        FloraHabit.Open => 1 - Smooth((canopyHere - OpenFrom) / (OpenGone - OpenFrom)),
        FloraHabit.Edge => Math.Clamp(4 * canopyHere * (1 - canopyHere), 0, 1),
        _ => 0,
    };

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
