namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// Tuning for point-of-interest placement, structure geometry, and discovery.
///
/// The lattice is deliberately much coarser than the surface-feature cell: a site is
/// a *destination*, not decoration, so the unit of decision is a square measured in
/// hundreds of metres rather than in the eight-metre cell that owns a tree. Because
/// the cell is far larger than any structure, a voxel only ever has to ask the nine
/// cells that touch its own - the same neighbourhood the tree pass scans - and the
/// offset draw inside a cell keeps sites off a visible grid.
/// </summary>
internal static class PoiConstants
{
    /// <summary>
    /// The anchor lattice. On the 10,240-block world this is 40 cells per axis and so
    /// 1,600 candidate cells: at one site per cell that is a landmark roughly every
    /// 256 m, which is a few minutes of walking in any direction and the density a
    /// traversal session needs.
    /// </summary>
    internal const long CellSize = 256;

    /// <summary>One in N candidate cells holds a site at all. One places in every cell.</summary>
    internal const long OneIn = 1;

    /// <summary>
    /// The furthest any structure reaches from its site origin, in blocks. It bounds
    /// the voxel-side neighbourhood scan: a structure never spans more than this, and
    /// this is far smaller than <see cref="CellSize"/>, so nine cells always suffice.
    /// </summary>
    internal const long MaximumStructureReach = 9;

    /// <summary>How many per-cell decisions are cached before the cache is cleared.</summary>
    internal const int SiteCacheLimit = 4096;

    /// <summary>
    /// How far uphill is measured. Close in, because a mouth is cut into the slope it
    /// stands on: sampling a cell's width away would average over a hillside and could
    /// point a recess at flat ground.
    /// </summary>
    internal const long AspectSampleDistance = 4;

    /// <summary>Sites are kept this far inside the world edge, so none straddles the border wall.</summary>
    internal const long WorldMargin = 24;

    /// <summary>Ground at or below the water line is refused: no site stands in a lake or on a coast flat.</summary>
    internal const long MinimumGroundHeight = 3;

    /// <summary>A site on flat ground is refused if the slope exceeds this, and relief kinds need at least the other.</summary>
    internal const long FlatSlopeMaximum = 2;

    /// <summary>The slope at which a cave mouth or a descent reads as cut into a hillside.</summary>
    internal const long ReliefSlopeMinimum = 2;

    /// <summary>The ground height at which a vantage point is worth building.</summary>
    internal const long VantageMinimumHeight = 7;

    // --- structures -----------------------------------------------------------------
    // Largest first: the ruin's footprint sets MaximumStructureReach, and every
    // builder must stay inside it.

    /// <summary>A ruin's fortification is a square of this half-extent, so 13x13 blocks.</summary>
    internal const long RuinHalfExtent = 6;

    internal const long RuinMinimumHeight = 4;

    internal const long RuinHeightRange = 4;

    /// <summary>A standing-stone ring's radius, and the tallest pillar it may raise.</summary>
    internal const long StoneRingRadius = 7;

    internal const long StoneMinimumHeight = 6;

    internal const long StoneHeightRange = 6;

    /// <summary>A cave mouth's arch: half-extent, opening height, and how deep the recess is cut.</summary>
    internal const long CaveHalfExtent = 4;

    internal const long CaveArchHeight = 5;

    internal const long CaveRecessDepth = 3;

    /// <summary>A descent's framed shaft head.</summary>
    internal const long EntranceHalfExtent = 5;

    internal const long EntranceFrameHeight = 6;

    /// <summary>A vantage point's half-extent, and the number of courses it may raise.</summary>
    internal const long VantageHalfExtent = 3;

    internal const long VantageMinimumSteps = 3;

    internal const long VantageStepRange = 4;

    /// <summary>
    /// How deep a descent's shaft is cut. Combined with <see cref="MinimumGroundHeight"/>,
    /// the deepest voxel any structure removes is at y = 3 - 6 = -3, which is well above
    /// the bedrock floor that starts below -9 - so no structure can open the world's
    /// underside, and that is arithmetic rather than a guard.
    /// </summary>
    internal const long EntranceShaftDepth = 6;

    /// <summary>
    /// The tallest any structure rises above its own ground, so a scan bounded by it
    /// cannot miss a voxel. It is the standing-stone maximum, which is the tallest shape.
    /// </summary>
    internal const long MaximumStructureHeight = StoneMinimumHeight + StoneHeightRange;

    /// <summary>How deep any structure removes material, named so the floor bound is checkable.</summary>
    internal const long MaximumCarveDepth = EntranceShaftDepth;

    // --- discovery ------------------------------------------------------------------

    /// <summary>
    /// How close the player must come, in metres, for a site to be noticed at all.
    /// The Engine's perception decides whether it is actually visible from there; this
    /// is the cheap outer bound that keeps the query to the cells in range.
    /// </summary>
    internal const double NoticeRadiusMetres = 128.0;

    /// <summary>A traversal that has noticed this many sites has a journal worth reading.</summary>
    internal const int MaximumDiscoveryEntries = 4096;

    /// <summary>Named so a reader cannot mistake the draw range for an exclusive bound.</summary>
    internal const long FirstKind = (long)PoiKind.StandingStones;

    internal const long LastKind = (long)PoiKind.VantagePoint;
}
