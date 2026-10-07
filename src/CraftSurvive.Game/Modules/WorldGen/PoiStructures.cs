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
/// The shapes of the places. Every builder is a pure function of the site it was given
/// - including the height and variant drawn when the site was resolved - so a structure
/// is identical whether it is reached from a chunk, from a neighbour chunk, or from the
/// discovery query.
///
/// Structures are the only generation pass that may displace terrain, and only inside
/// their own bounds: a ruin sits on the ground rather than hovering over a slope, and a
/// way in is cut into the hillside because a hole cannot be built. Everything they
/// remove is bounded by <see cref="PoiConstants.MaximumStructureReach"/> horizontally
/// and by the shaft depth vertically, so no structure can breach the world floor.
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
            PoiKind.StandingStones => StandingStones(site, x, y, z),
            PoiKind.Ruin => Ruin(site, x, y, z),
            PoiKind.CaveMouth => CaveMouth(site, x, y, z),
            PoiKind.DungeonEntrance => DungeonEntrance(site, x, y, z),
            PoiKind.VantagePoint => VantagePoint(site, x, y, z),
            _ => PoiVoxel.None,
        };
    }

    /// <summary>
    /// A ring of tapered steles leaning a little outward, each a different height so the ring reads
    /// as raised rather than as a fence, the tallest pierced by a round opening, and a low cairn at
    /// the centre (PoiShapes). Stone, so it is reconstructed as worn faceted rock.
    /// </summary>
    private static PoiVoxel StandingStones(PoiSite site, long x, long y, long z) => Shaped(site, x, y, z);

    /// <summary>
    /// The broken curved wall of a round building, whole in places and gone in others, with ribs
    /// leaning inward from it toward a roof that is no longer there (PoiShapes).
    /// </summary>
    private static PoiVoxel Ruin(PoiSite site, long x, long y, long z) => Shaped(site, x, y, z);

    /// <summary>A shaped site's stone: filled where the voxel centre is inside its distance field.</summary>
    private static PoiVoxel Shaped(PoiSite site, long x, long y, long z) =>
        PoiShapes.DistanceAt(site, x, y, z) is double distance && distance < 0
            ? PoiVoxel.Fill(BlockId.Stone)
            : PoiVoxel.None;

    /// <summary>
    /// A mouth cut into a slope: a tall stone arch standing proud of the ground, with a
    /// recess carved behind it. The direction it opens is one of the four cardinals,
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

        // The arch: a tall ring of stone framing the opening, in the plane of the mouth (PoiShapes).
        return Shaped(site, x, y, z);
    }

    /// <summary>
    /// A framed descent: a stone platform around a hole, two arches crossing over it, and a
    /// shaft cut down into the ground beneath. The shaft stops on the
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

        // The platform around the shaft and the two arches crossing over it (PoiShapes).
        return Shaped(site, x, y, z);
    }

    /// <summary>
    /// A stepped look-out: four courses, each a square two blocks smaller than the last,
    /// rising from the ground. Every course is one block high, so the whole thing can be
    /// walked up without climbing - which is the point, because traversal must not
    /// depend on an Engine capability that is still being built.
    /// </summary>
    private static PoiVoxel VantagePoint(PoiSite site, long x, long y, long z)
    {
        // The top course carries a lamp, so a look-out is findable after dark as well as in
        // daylight - the one place in the world that generates light.
        if (y - site.Ground == site.Height && x == site.X && z == site.Z)
        {
            return PoiVoxel.Fill(BlockId.Lamp);
        }

        // The courses stay cobble cubes: the player's step-up climbs a one-metre cube riser but not
        // the same riser reconstructed as stone (rusty-engine #9681), and being walked up is the point.
        long dx = Math.Abs(x - site.X), dz = Math.Abs(z - site.Z), below = y - site.Ground;
        if (below <= 0 || below > site.Height)
        {
            return PoiVoxel.None;
        }

        long half = Math.Max(PoiConstants.VantageHalfExtent - (below - 1), 0);
        return dx > half || dz > half ? PoiVoxel.None : PoiVoxel.Fill(BlockId.Cobblestone);
    }

    internal static (long X, long Z) CardinalOf(long index) => Cardinal(index);

    private static (long X, long Z) Cardinal(long index) => index switch
    {
        0 => (1, 0),
        1 => (-1, 0),
        2 => (0, 1),
        _ => (0, -1),
    };
}
