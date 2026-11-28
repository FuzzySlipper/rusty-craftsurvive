using System.Globalization;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The terrain a site is placed on. Placement needs the ground height and the local
/// slope and nothing else, so this is the whole dependency rather than the recipe.
/// </summary>
internal interface ITerrainColumns
{
    TerrainColumn ColumnAt(long x, long z);

    /// <summary>
    /// A column's surface and the highest cell under water over it, as <see cref="ColumnAt"/> has them, for a caller that needs
    /// no more (#11126: the crossing scan): a recipe answers it without the slope's four neighbouring columns.
    /// </summary>
    (long Surface, long WaterTop) WaterAt(long x, long z)
    {
        TerrainColumn column = ColumnAt(x, z);
        return (column.Surface, column.WaterTop);
    }

    /// <summary>Whether a column lies on a town's pad or edge, where wild sites should not stand (#11179).</summary>
    bool InTown(long x, long z) => false;

    /// <summary>Macro biome, region, and settlement-distance context for continental POI distribution (#11185).</summary>
    PoiContext PoiContextAt(long x, long z) => new(MapBiome.Grassland, 0, double.PositiveInfinity, BeyondFrontier: false, InNeck: false);

    /// <summary>Fast macro region and settlement-distance context for continental crossing cadence (#11186).</summary>
    CrossingContext CrossingContextAt(long x, long z) => new(double.PositiveInfinity, BeyondFrontier: false, InNeck: false, OpenSea: false);

    /// <summary>Shaped river centreline candidates inside anchor cell (<paramref name="cellX"/>, <paramref name="cellZ"/>) (#11186).</summary>
    IReadOnlyList<RiverCrossingCandidate>? RiverCandidatesInCell(long cellX, long cellZ) => null;
}

/// <summary>
/// One place worth travelling to, resolved once and thereafter a value.
///
/// Everything a structure needs is here - including the drawn height and variant -
/// so the builder that turns a site into voxels is a pure function of this record and
/// never draws again. That matters because the voxel pass and the discovery pass ask
/// the same question from different directions: if either re-drew, they could
/// disagree about the same structure.
/// </summary>
internal readonly record struct PoiSite(
    long CellX,
    long CellZ,
    PoiKind Kind,
    long X,
    long Z,
    long Ground,
    long Height,
    long Variant,
    long Aspect)
{
    /// <summary>
    /// The stable identity of a site. It is the anchor cell, because a cell holds at
    /// most one site and the cell is a pure function of the contract - so the same
    /// world always names the same sites, which is what makes a persisted discovery
    /// record meaningful across sessions.
    /// </summary>
    internal string Id => IdFor(CellX, CellZ);

    /// <summary>The identity of the site an anchor cell owns, in the one place that formats it.</summary>
    internal static string IdFor(long cellX, long cellZ) =>
        string.Create(CultureInfo.InvariantCulture, $"poi:{cellX}:{cellZ}");

    /// <summary>Human-readable kind name, for the journal and the readout.</summary>
    internal string KindName => Kind.ToString();
}

/// <summary>
/// Where the world's places are.
///
/// Placement is a pure function of the contract and the anchor cell, in the same
/// shape as the surface-feature pass: a cell decides *for itself* whether it owns a
/// site and exactly where that site stands, using only its own coordinates, so two
/// chunks - or a chunk and the discovery query - agree about it without
/// communicating and without an order. Decisions are cached because every voxel in a
/// structure's neighbourhood asks the same nine questions, and clearing the cache is
/// always safe because a decision is pure.
/// </summary>
internal sealed class PoiPlacement
{
    private readonly TerrainGeneratorContract contract;
    private readonly ITerrainDraws draws;
    private readonly ITerrainColumns columns;
    private readonly long radius;
    /// <summary>Each anchor cell's decision, read by the chunk workers too (#10907).</summary>
    private readonly SharedCache<(long X, long Z), PoiSite?> cells = new(PoiConstants.SiteCacheLimit);

    internal PoiPlacement(TerrainGeneratorContract contract, ITerrainDraws draws, ITerrainColumns columns, long radius)
    {
        this.contract = contract;
        this.draws = draws ?? throw new ArgumentNullException(nameof(draws));
        this.columns = columns ?? throw new ArgumentNullException(nameof(columns));
        this.radius = radius;
    }

    internal TerrainGeneratorContract Contract => contract;

    /// <summary>
    /// The site an anchor cell owns, or none. This is the documented placement rule:
    /// <list type="number">
    /// <item>a <c>poi.present</c> one-in-N draw decides whether the cell holds a site;</item>
    /// <item>a <c>poi.offset.x/z</c> draw in <c>[0, CellSize)</c> places it inside the cell,
    /// so sites do not sit on a visible lattice;</item>
    /// <item>a <c>poi.kind</c> draw asks for a kind, which is then reconciled with the
    /// ground - a cave mouth needs a hillside, a vantage point needs height, and a ruin
    /// needs ground flat enough to have held walls - so a site is always something the
    /// terrain can carry;</item>
    /// <item>a site is refused outright when it would be submerged, stand outside the
    /// world, or straddle the border wall's band.</item>
    /// </list>
    /// </summary>
    internal PoiSite? SiteAt(long cellX, long cellZ)
    {
        if (cells.TryGetValue((cellX, cellZ), out PoiSite? cached))
        {
            return cached;
        }

        PoiSite? site = DecideSite(cellX, cellZ);
        cells.Set((cellX, cellZ), site);
        return site;
    }

    /// <summary>
    /// Every site whose structure could reach within <paramref name="radius"/> blocks of
    /// a position, appended to <paramref name="into"/>. The bound is the notice radius
    /// plus a structure's own reach, so a caller that wants "sites I could be looking
    /// at" does not have to know how big a structure is.
    /// </summary>
    internal void CollectSitesNear(long x, long z, double radius, List<PoiSite> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "A site query radius cannot be negative.");
        }

        long cell = PoiConstants.CellSize;
        long reach = (long)Math.Ceiling(radius) + PoiConstants.MaximumStructureReach;
        long firstX = GridMath.FloorDivide(x - reach, cell);
        long lastX = GridMath.FloorDivide(x + reach, cell);
        long firstZ = GridMath.FloorDivide(z - reach, cell);
        long lastZ = GridMath.FloorDivide(z + reach, cell);
        for (long cellX = firstX; cellX <= lastX; cellX++)
        {
            for (long cellZ = firstZ; cellZ <= lastZ; cellZ++)
            {
                if (SiteAt(cellX, cellZ) is PoiSite site)
                {
                    into.Add(site);
                }
            }
        }
    }

    private PoiSite? DecideSite(long cellX, long cellZ)
    {
        const uint seedVersion = TerrainGeneratorContract.PoiSeedVersion;
        bool continental = MapScale.IsContinental(contract.Extent);
        if (!continental && !contract.DrawUnit(draws, "poi.present", cellX, cellZ, PoiConstants.OneIn, seedVersion))
        {
            return null;
        }

        long x = (cellX * PoiConstants.CellSize)
            + contract.DrawLong(draws, "poi.offset.x", cellX, cellZ, 0, PoiConstants.CellSize - 1, seedVersion);
        long z = (cellZ * PoiConstants.CellSize)
            + contract.DrawLong(draws, "poi.offset.z", cellX, cellZ, 0, PoiConstants.CellSize - 1, seedVersion);
        if (Math.Abs(x) > radius - PoiConstants.WorldMargin || Math.Abs(z) > radius - PoiConstants.WorldMargin)
        {
            return null;
        }

        // Towns own their own ground (#11179): wild landmarks stay off a town's pad.
        if (columns.InTown(x, z))
        {
            return null;
        }

        PoiContext context = default;
        if (continental)
        {
            // Continental density gates before full column/slope sampling (#11185): refuses forbidden
            // mainland, sea, and most empty wilderness cells with a single context lookup.
            context = columns.PoiContextAt(x, z);
            double density = PoiDistribution.Density(in context);
            if (density <= 0)
            {
                return null;
            }

            double presentDraw = contract.DrawLong(draws, "poi.present", cellX, cellZ, 0, PoiConstants.DistributionDrawScale - 1, seedVersion)
                / (double)PoiConstants.DistributionDrawScale;
            if (presentDraw >= density)
            {
                return null;
            }
        }

        TerrainColumn column = columns.ColumnAt(x, z);
        // Refuse sites below the sea floor threshold or submerged under an elevated river or lake (#11179).
        if (column.Surface < PoiConstants.MinimumGroundHeight || column.Submerged)
        {
            return null;
        }

        long aspect = AspectAt(x, z, out long relief);
        PoiKind kind;
        if (continental)
        {
            double kindDraw = contract.DrawLong(draws, "poi.kind", cellX, cellZ, 0, PoiConstants.DistributionDrawScale - 1, seedVersion)
                / (double)PoiConstants.DistributionDrawScale;
            kind = PoiDistribution.PickKind(in context, column, relief, kindDraw);
        }
        else
        {
            PoiKind preferred = (PoiKind)contract.DrawLong(draws, "poi.kind", cellX, cellZ,
                PoiConstants.FirstKind, PoiConstants.LastKind, seedVersion);
            kind = Reconcile(preferred, column, relief, PoiConstants.VantageMinimumHeight);
        }

        (long minimum, long range) = HeightRangeFor(kind);
        long height = contract.DrawLong(draws, "poi.height", cellX, cellZ, minimum, minimum + range - 1, seedVersion);
        long variant = contract.DrawLong(draws, "poi.variant", cellX, cellZ, 0, PoiConstants.LastVariant, seedVersion);
        return new PoiSite(cellX, cellZ, kind, x, z, column.Surface, height, variant, aspect);
    }

    /// <summary>
    /// Which way is uphill, as one of the four cardinals. A mouth has to open downhill or
    /// it carves nothing but air, so the direction is measured from the ground rather than
    /// drawn - it is a property of the place, not a choice. Reads only heights via
    /// <see cref="ITerrainColumns.WaterAt"/> so each probe avoids four redundant slope samples (#11179).
    /// </summary>
    private long AspectAt(long x, long z, out long relief)
    {
        Span<long> heights = stackalloc long[4];
        heights[0] = columns.WaterAt(x + PoiConstants.AspectSampleDistance, z).Surface;
        heights[1] = columns.WaterAt(x - PoiConstants.AspectSampleDistance, z).Surface;
        heights[2] = columns.WaterAt(x, z + PoiConstants.AspectSampleDistance).Surface;
        heights[3] = columns.WaterAt(x, z - PoiConstants.AspectSampleDistance).Surface;
        long best = 0;
        long lowest = heights[0];
        for (long index = 1; index < heights.Length; index++)
        {
            if (heights[(int)index] > heights[(int)best])
            {
                best = index;
            }

            if (heights[(int)index] < lowest)
            {
                lowest = heights[(int)index];
            }
        }

        relief = heights[(int)best] - lowest;
        return best;
    }

    /// <summary>
    /// Reconciles the drawn kind with the ground that has to carry it. This is why a
    /// site is placed in almost every cell that has dry ground: a preference that the
    /// terrain cannot support becomes the nearest kind it can, rather than nothing.
    /// </summary>
    private static PoiKind Reconcile(PoiKind preferred, TerrainColumn column, long relief, long vantageMinimum) => preferred switch
    {
        // A way in needs a hillside to be cut into; without relief, pillars still read.
        PoiKind.CaveMouth or PoiKind.DungeonEntrance when relief < PoiConstants.ReliefMinimum
            => PoiKind.StandingStones,

        // A lookout needs height to be worth building; on low ground, a ruin.
        PoiKind.VantagePoint when column.Surface < vantageMinimum
            => PoiKind.Ruin,

        // Walls need ground flat enough to have held them; pillars stand anywhere.
        PoiKind.Ruin when column.Slope > PoiConstants.FlatSlopeMaximum
            => PoiKind.StandingStones,

        _ => preferred,
    };

    internal static (long Minimum, long Range) HeightRangeFor(PoiKind kind) => kind switch
    {
        PoiKind.StandingStones => (PoiConstants.StoneMinimumHeight, PoiConstants.StoneHeightRange),
        PoiKind.Ruin => (PoiConstants.RuinMinimumHeight, PoiConstants.RuinHeightRange),
        PoiKind.VantagePoint => (PoiConstants.VantageMinimumSteps, PoiConstants.VantageStepRange),
        _ => (PoiConstants.RuinMinimumHeight, PoiConstants.RuinHeightRange),
    };

}
