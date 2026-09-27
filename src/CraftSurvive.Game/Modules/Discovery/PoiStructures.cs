using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Discovery;

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
    /// A ring of squared pillars, each a different height so the ring reads as built
    /// rather than as a fence. Two blocks thick: at a metre per block a single voxel is
    /// a post, and these are meant to be visible from across a valley.
    /// </summary>
    private static PoiVoxel StandingStones(PoiSite site, long x, long y, long z)
    {
        long dx = x - site.X;
        long dz = z - site.Z;
        if ((dx * dx) + (dz * dz) > (PoiConstants.StoneRingRadius + 2) * (PoiConstants.StoneRingRadius + 2))
        {
            return PoiVoxel.None;
        }

        int count = 5 + (int)(site.Variant % 4);
        long tallest = site.Height;
        for (int index = 0; index < count; index++)
        {
            (long pillarX, long pillarZ) = RingPoint(index, count);
            if (dx != pillarX && dx != pillarX + 1)
            {
                continue;
            }

            if (dz != pillarZ && dz != pillarZ + 1)
            {
                continue;
            }

            // Height falls away around the ring so the tallest stone is a landmark in
            // its own right, and the ring never reads as a wall.
            long height = tallest - (((index + site.Variant) % 3) * 2);
            if (height < PoiConstants.StoneMinimumHeight - 3)
            {
                height = PoiConstants.StoneMinimumHeight - 3;
            }

            if (y > site.Ground && y <= site.Ground + height)
            {
                return PoiVoxel.Fill(BlockId.Stone);
            }

            return PoiVoxel.None;
        }

        // A low cairn at the centre marks the middle of the ring.
        return x == site.X && z == site.Z && y > site.Ground && y <= site.Ground + 2
            ? PoiVoxel.Fill(BlockId.Cobblestone)
            : PoiVoxel.None;
    }

    /// <summary>
    /// The fallen walls of a square building: a ring of wall at the footprint edge, its
    /// height tapering by quadrant so one corner stands and another is gone, a brick
    /// pier at each standing corner, and a gravel floor inside. This is the shape that
    /// says "someone built here" from a distance.
    /// </summary>
    private static PoiVoxel Ruin(PoiSite site, long x, long y, long z)
    {
        long dx = Math.Abs(x - site.X);
        long dz = Math.Abs(z - site.Z);
        long extent = PoiConstants.RuinHalfExtent;
        if (dx > extent || dz > extent)
        {
            return PoiVoxel.None;
        }

        long below = y - site.Ground;
        if (below < 0 || below > PoiConstants.RuinMinimumHeight + PoiConstants.RuinHeightRange)
        {
            return PoiVoxel.None;
        }

        bool onWall = dx == extent || dz == extent;
        if (!onWall)
        {
            // The floor: one course of gravel, only where the ground is level enough
            // that a floor reads as a floor rather than as a step.
            return below == 1 && (dx + dz) % 3 != 0 ? PoiVoxel.Fill(BlockId.Gravel) : PoiVoxel.None;
        }

        long quadrant = (x >= site.X ? 1 : 0) + (z >= site.Z ? 2 : 0);
        long taper = (quadrant + site.Variant) % 3;
        long wallHeight = site.Height - taper;
        if (below <= 0 || below > wallHeight)
        {
            return PoiVoxel.None;
        }

        // A pier two blocks wide at each standing corner, brick against cobble, so a
        // ruin has structure rather than a uniform outline.
        long cornerReach = extent - dx <= 1 && extent - dz <= 1 ? 1 : 0;
        return PoiVoxel.Fill(cornerReach == 1 ? BlockId.Brick : BlockId.Cobblestone);
    }

    /// <summary>
    /// A mouth cut into a slope: an arch of stone standing proud of the ground, with a
    /// recess carved behind it. The direction it opens is one of the four cardinals,
    /// chosen by the site's variant, so neighbouring mouths do not all face the same way.
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

        if (below < 0 || below > PoiConstants.CaveArchHeight)
        {
            return PoiVoxel.None;
        }

        // The arch itself: a frame one block thick around the opening, in the plane of
        // the mouth, plus jambs down to the ground beside it.
        bool onFrameAcross = Math.Abs(across) == 2 || Math.Abs(across) == 1;
        if (along >= -1 && along <= 0 && onFrameAcross && below <= openingHeight)
        {
            return PoiVoxel.Fill(BlockId.Cobblestone);
        }

        bool lintel = along >= -1 && along <= 0 && Math.Abs(across) <= 2
            && below > openingHeight && below <= PoiConstants.CaveArchHeight;
        if (lintel)
        {
            return PoiVoxel.Fill(BlockId.Stone);
        }

        // Buttresses at the mouth's outer corners, so it reads as built, not eroded.
        bool buttress = along >= -1 && along <= 1 && Math.Abs(across) == extent && below <= PoiConstants.CaveArchHeight - 2;
        return buttress ? PoiVoxel.Fill(BlockId.Cobblestone) : PoiVoxel.None;
    }

    /// <summary>
    /// A framed descent: a squared stone rim around a hole, four corner posts with a
    /// lintel, and a shaft cut down into the ground beneath. The shaft stops on the
    /// world floor - carving refuses bedrock - so the structure can never open the
    /// world's underside. Nothing here travels; the entrance is a place, and the load
    /// transition into an interior belongs to the dimension slice.
    /// </summary>
    private static PoiVoxel DungeonEntrance(PoiSite site, long x, long y, long z)
    {
        long extent = PoiConstants.EntranceHalfExtent;
        long rim = 3;
        long dx = x - site.X;
        long dz = z - site.Z;
        long below = y - site.Ground;

        if (Math.Abs(dx) <= rim && Math.Abs(dz) <= rim)
        {
            bool inShaft = Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1;
            if (inShaft)
            {
                // The shaft. It stops one course short of the deepest cut, and that
                // uncarved course is the floor: a fill could not make one, because
                // filling never replaces solid ground.
                return below > -PoiConstants.EntranceShaftDepth && below < 0
                    ? PoiVoxel.Carve
                    : PoiVoxel.None;
            }

            // The rim: a walkable course at ground level, chamfered at the corners so the
            // frame reads as a square with cut corners.
            bool corner = Math.Abs(dx) == rim && Math.Abs(dz) == rim;
            if (!corner && below == 0)
            {
                return PoiVoxel.Fill(BlockId.Cobblestone);
            }
        }

        if (Math.Abs(dx) > extent || Math.Abs(dz) > extent)
        {
            return PoiVoxel.None;
        }

        if (below < 0 || below > PoiConstants.EntranceFrameHeight)
        {
            return PoiVoxel.None;
        }

        // Four corner posts and the lintel spanning the two northern ones.
        bool cornerPost = Math.Abs(dx) == extent && Math.Abs(dz) == extent;
        if (cornerPost)
        {
            return PoiVoxel.Fill(BlockId.Brick);
        }

        bool lintelSpan = dz == -extent && Math.Abs(dx) <= extent && below > PoiConstants.EntranceFrameHeight - 1;
        return lintelSpan ? PoiVoxel.Fill(BlockId.Brick) : PoiVoxel.None;
    }

    /// <summary>
    /// A stepped look-out: four courses, each a square two blocks smaller than the last,
    /// rising from the ground. Every course is one block high, so the whole thing can be
    /// walked up without climbing - which is the point, because traversal must not
    /// depend on an Engine capability that is still being built.
    /// </summary>
    private static PoiVoxel VantagePoint(PoiSite site, long x, long y, long z)
    {
        long dx = Math.Abs(x - site.X);
        long dz = Math.Abs(z - site.Z);
        long below = y - site.Ground;
        long steps = site.Height;
        if (below <= 0 || below > steps)
        {
            return PoiVoxel.None;
        }

        long halfExtent = PoiConstants.VantageHalfExtent - (below - 1);
        if (halfExtent < 0)
        {
            halfExtent = 0;
        }

        if (dx > halfExtent || dz > halfExtent)
        {
            return PoiVoxel.None;
        }

        // The top course is a lamp, so a look-out is findable after dark as well as in
        // daylight - the one place in the world that generates light.
        if (below == steps && dx == 0 && dz == 0)
        {
            return PoiVoxel.Fill(BlockId.Lamp);
        }

        return PoiVoxel.Fill(below == steps ? BlockId.Brick : BlockId.Cobblestone);
    }

    private static (long X, long Z) RingPoint(int index, int count)
    {
        // Even spacing on a squared ring: the ring is a square, so a point is placed on
        // whichever side its angle falls, which keeps pillars on the footprint's edge.
        double angle = (2.0 * Math.PI * index) / count;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double scale = PoiConstants.StoneRingRadius / Math.Max(Math.Abs(cos), Math.Abs(sin));
        return ((long)Math.Round(cos * scale, MidpointRounding.AwayFromZero),
            (long)Math.Round(sin * scale, MidpointRounding.AwayFromZero));
    }

    private static (long X, long Z) Cardinal(long index) => index switch
    {
        0 => (1, 0),
        1 => (-1, 0),
        2 => (0, 1),
        _ => (0, -1),
    };
}
