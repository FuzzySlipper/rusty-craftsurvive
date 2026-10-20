namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>How dungeons are generated: one of the approaches #8604 is comparing.</summary>
internal enum DungeonApproach
{
    /// <summary>A: carve and stamp, all cubic voxels.</summary>
    CarveAndStamp,

    /// <summary>C: A's structure with its rock sculpted into a smooth mesh around the cubic building.</summary>
    SculptedCave,

    /// <summary>B: assembled from authored 3D modules joined at their sockets, rock sculpted as in C.</summary>
    Modules,

    /// <summary>V: the hand-placed vertical sketch of #7916, from the same modules as B.</summary>
    Vertical,

    /// <summary>S: the hand-placed shaft sketch of #7916: a deep open shaft with a ledge winding down it.</summary>
    Shaft,

    /// <summary>N: the rock route study's natural cavern (#10090), shaped freely with no regard for routes.</summary>
    Natural,

    /// <summary>K: the cave generator study (#10094), a cave made by water in bedded, jointed rock.</summary>
    Karst,
}

/// <summary>One generated dungeon: its layout, its plan, the generator's own verdict, and the voxel copy that was walked.</summary>
internal sealed record DungeonCandidate(int Index, ulong Seed, DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable)
{
    /// <summary>
    /// The candidate as voxels throughout, for a reconstructed surface: the walked copy (sculpted
    /// rock as stone where its density is solid) with the sculpted field as the voxels' densities.
    /// </summary>
    internal DungeonLayout AllVoxels => Layout with { Volume = Layout.KitDrawn ?? Walkable, Rock = null, Densities = Layout.Rock };
}

/// <summary>
/// The dungeons an entrance may have, in the order they are tried. Each generator already redraws
/// until its own walk check passes; a dungeon is accepted only when the Engine's navigation also
/// walks every route its flow promises (<see cref="DungeonRoutes"/>), and otherwise the next
/// candidate is tried. The sequence is a pure function of the entrance's seed, so an entrance always
/// has the same dungeon, and the bank tries exactly what the game tries.
/// </summary>
internal static class DungeonCandidates
{
    /// <summary>How many candidates an entrance tries before it is given up as having no dungeon.</summary>
    internal const int MaximumCandidates = 8;

    /// <summary>The seed of an entrance's candidate: the entrance's own seed first, then a fixed mix of it per index.</summary>
    internal static ulong Seed(ulong entranceSeed, int index) =>
        index == 0 ? entranceSeed : new DungeonRandom(entranceSeed ^ ((ulong)index * 0xD1B5_4A32_D192_ED03UL)).Next();

    /// <summary>Generates an entrance's candidate by an approach.</summary>
    /// <param name="voxelSize">The natural cavern study's voxel side in metres (#10094); other approaches are built in world blocks.</param>
    /// <param name="centrepiece">For the centrepiece studies (#10094): what a karst cave is built round - a great hall, a chasm or neither - or null for chance.</param>
    internal static DungeonCandidate Generate(DungeonApproach approach, ulong entranceSeed, int index, double voxelSize = 1d, KarstCave.Centrepiece? centrepiece = null)
    {
        ulong seed = Seed(entranceSeed, index);
        switch (approach)
        {
            case DungeonApproach.Modules:
                var modular = ModularDungeon.Generate(seed);
                return new DungeonCandidate(index, seed, modular.Layout, modular.Plan, modular.Verdict, modular.Walkable);
            case DungeonApproach.Vertical:
                var vertical = VerticalSampler.Generate(seed);
                return new DungeonCandidate(index, seed, vertical.Layout, vertical.Plan, vertical.Verdict, vertical.Walkable);
            case DungeonApproach.Shaft:
                var shaft = VerticalSampler.Shaft(seed);
                return new DungeonCandidate(index, seed, shaft.Layout, shaft.Plan, shaft.Verdict, shaft.Walkable);
            case DungeonApproach.Karst:
                var karst = KarstCave.Generate(seed, voxelSize, centrepiece);
                return new DungeonCandidate(index, seed, karst.Layout, karst.Plan, karst.Verdict, karst.Walkable);
            case DungeonApproach.Natural:
                var natural = NaturalCavern.Generate(seed, voxelSize);
                return new DungeonCandidate(index, seed, natural.Layout, natural.Plan, natural.Verdict, natural.Walkable);
            case DungeonApproach.SculptedCave:
                var sculpted = SculptedCave.Generate(seed);
                return new DungeonCandidate(index, seed, sculpted.Layout, sculpted.Plan, sculpted.Verdict, sculpted.Walkable);
            default:
                var carved = CarveAndStamp.Generate(seed);
                return new DungeonCandidate(index, seed, carved.Layout, carved.Plan, carved.Verdict, carved.Layout.Volume);
        }
    }
}
