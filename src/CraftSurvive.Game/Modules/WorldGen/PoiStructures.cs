using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>What a structure wants done to one voxel.</summary>
internal enum PoiVoxelKind
{
    /// <summary>The structure says nothing about this voxel.</summary>
    None = 0,

    /// <summary>Fill it if it is air. Never displaces terrain, so a wall meeting a slope loses to the slope.</summary>
    Fill = 1,

    /// <summary>
    /// Remove whatever is here. This is the one way generation takes material away, and
    /// only structures use it: a way in is a hole, and no amount of building makes one.
    /// </summary>
    Carve = 2,
}

internal readonly record struct PoiVoxel(PoiVoxelKind Kind, ushort Material)
{
    internal static PoiVoxel None => new(PoiVoxelKind.None, Terrain.TerrainConstants.EmptyMaterial);

    internal static PoiVoxel Fill(BlockId id) => new(PoiVoxelKind.Fill, BlockRegistry.IsBound(id)
        ? (ushort)id
        : Terrain.TerrainConstants.EmptyMaterial);

    internal static PoiVoxel Carve => new(PoiVoxelKind.Carve, Terrain.TerrainConstants.EmptyMaterial);

    internal bool IsNone => Kind == PoiVoxelKind.None;
}

/// <summary>
/// What the places do to the terrain voxels. The places themselves are textured meshes placed at their
/// sites (PlaceLayouts, #9992); the voxels keep only what a mesh cannot be: the holes that are a way in
/// (a cave mouth's recess, a descent's shaft) and the vantage point's lamp, the one place in the world
/// that generates light. Every builder is a pure function of the site it was given - including the
/// height and variant drawn when the site was resolved - so a structure is identical whether it is
/// reached from a chunk, from a neighbour chunk, or from the discovery query.
///
/// Everything a structure removes is bounded by <see cref="PoiConstants.MaximumStructureReach"/>
/// horizontally and by the shaft depth vertically, so no structure can breach the world floor.
/// </summary>
internal static class PoiStructures
{
    /// <summary>
    /// What the site wants at one voxel. Callers test the bound first: everything
    /// outside the structure's footprint answers <see cref="PoiVoxelKind.None"/>, which
    /// is what lets a voxel ask all nine cells that touch it without asking about a
    /// structure that is nowhere near.
    /// </summary>
    internal static PoiVoxel MaterialAt(PoiSite site, long x, long y, long z)
    {
        if (Math.Abs(x - site.X) > PoiConstants.MaximumStructureReach
            || Math.Abs(z - site.Z) > PoiConstants.MaximumStructureReach)
        {
            return PoiVoxel.None;
        }

        return site.Kind switch
        {
            PoiKind.CaveMouth => CaveMouth(site, x, y, z),
            PoiKind.DungeonEntrance => DungeonEntrance(site, x, y, z),
            PoiKind.VantagePoint => VantagePoint(site, x, y, z),
            _ => PoiVoxel.None,
        };
    }

    /// <summary>
    /// A mouth cut into a slope: the recess carved behind the mouth's arch (the arch is a mesh). The direction it opens is one of the four cardinals,
    /// chosen by the uphill direction measured from the ground (the site's aspect), not the variant, so neighbouring mouths do not all face the same way.
    /// </summary>
    private static PoiVoxel CaveMouth(PoiSite site, long x, long y, long z)
    {
        long extent = PoiConstants.CaveHalfExtent;
        long depth = PoiConstants.CaveRecessDepth;
        (long forwardX, long forwardZ) = Cardinal(site.Aspect);
        long dx = x - site.X;
        long dz = z - site.Z;

        // Coordinates in the mouth's own frame: `along` runs into the hill, `across`
        // spans the opening.
        long along = (dx * forwardX) + (dz * forwardZ);
        long across = (dx * forwardZ) - (dz * forwardX);
        if (Math.Abs(across) > extent || along < -extent || along > extent + depth)
        {
            return PoiVoxel.None;
        }

        long below = y - site.Ground;
        long openingHeight = 3;
        bool insideOpening = Math.Abs(across) <= 1 && below >= 0 && below < openingHeight;

        // The recess: the arch's shadow, cut into the slope behind the mouth.
        if (along > 0 && along <= depth && insideOpening)
        {
            return PoiVoxel.Carve;
        }

        return PoiVoxel.None;
    }

    /// <summary>
    /// A framed descent: the shaft cut down into the ground beneath the platform and its crossing
    /// arches (both meshes). The shaft stops on the
    /// world floor - carving refuses bedrock - so the structure can never open the
    /// world's underside. Nothing here travels; the entrance is a place, and the load
    /// transition into an interior belongs to the dimension slice.
    /// </summary>
    private static PoiVoxel DungeonEntrance(PoiSite site, long x, long y, long z)
    {
        long dx = x - site.X;
        long dz = z - site.Z;
        long below = y - site.Ground;
        if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1 && below < 0)
        {
            // The shaft. It stops one course short of the deepest cut, and that uncarved course is
            // the floor: a fill could not make one, because filling never replaces solid ground.
            return below > -PoiConstants.EntranceShaftDepth ? PoiVoxel.Carve : PoiVoxel.None;
        }

        return PoiVoxel.None;
    }

    /// <summary>
    /// A stepped look-out's lamp, on its top course, so a look-out is findable after dark as well as
    /// in daylight: the one place in the world that generates light. The courses are a mesh.
    /// </summary>
    private static PoiVoxel VantagePoint(PoiSite site, long x, long y, long z) =>
        y - site.Ground == site.Height && x == site.X && z == site.Z ? PoiVoxel.Fill(BlockId.Lamp) : PoiVoxel.None;

    internal static (long X, long Z) CardinalOf(long index) => Cardinal(index);

    private static (long X, long Z) Cardinal(long index) => index switch
    {
        0 => (1, 0),
        1 => (-1, 0),
        2 => (0, 1),
        _ => (0, -1),
    };
}
