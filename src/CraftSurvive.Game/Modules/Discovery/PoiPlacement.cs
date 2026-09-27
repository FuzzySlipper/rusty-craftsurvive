using System.Globalization;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// The terrain a site is placed on. Placement needs the ground height and the local
/// slope and nothing else, so this is the whole dependency rather than the recipe.
/// </summary>
internal interface ITerrainColumns
{
    TerrainColumn ColumnAt(long x, long z);
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
    internal string Id => string.Create(CultureInfo.InvariantCulture, $"poi:{CellX}:{CellZ}");

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
    private readonly Dictionary<(long X, long Z), PoiSite?> cells = [];

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
        if (cells.Count >= PoiConstants.SiteCacheLimit)
        {
            cells.Clear();
        }

        cells[(cellX, cellZ)] = site;
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
        long firstX = FloorDivide(x - reach, cell);
        long lastX = FloorDivide(x + reach, cell);
        long firstZ = FloorDivide(z - reach, cell);
        long lastZ = FloorDivide(z + reach, cell);
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
        string key = TerrainGeneratorContract.CoordinateKey(cellX, cellZ);
        if (!contract.DrawUnit(draws, "poi.present", key, PoiConstants.OneIn))
        {
            return null;
        }

        long x = (cellX * PoiConstants.CellSize)
            + contract.DrawLong(draws, "poi.offset.x", key, 0, PoiConstants.CellSize - 1);
        long z = (cellZ * PoiConstants.CellSize)
            + contract.DrawLong(draws, "poi.offset.z", key, 0, PoiConstants.CellSize - 1);
        if (Math.Abs(x) > radius - PoiConstants.WorldMargin || Math.Abs(z) > radius - PoiConstants.WorldMargin)
        {
            return null;
        }

        TerrainColumn column = columns.ColumnAt(x, z);
        if (column.Surface < PoiConstants.MinimumGroundHeight)
        {
            return null;
        }

        PoiKind preferred = (PoiKind)contract.DrawLong(draws, "poi.kind", key,
            PoiConstants.FirstKind, PoiConstants.LastKind);
        PoiKind kind = Reconcile(preferred, column);
        (long minimum, long range) = HeightRangeFor(kind);
        long height = contract.DrawLong(draws, "poi.height", key, minimum, minimum + range - 1);
        long variant = contract.DrawLong(draws, "poi.variant", key, 0, 7);
        long aspect = AspectAt(x, z);
        return new PoiSite(cellX, cellZ, kind, x, z, column.Surface, height, variant, aspect);
    }

    /// <summary>
    /// Which way is uphill, as one of the four cardinals. A mouth has to open downhill or
    /// it carves nothing but air, so the direction is measured from the ground rather than
    /// drawn - it is a property of the place, not a choice.
    /// </summary>
    private long AspectAt(long x, long z)
    {
        Span<long> heights = stackalloc long[4];
        heights[0] = columns.ColumnAt(x + PoiConstants.AspectSampleDistance, z).Surface;
        heights[1] = columns.ColumnAt(x - PoiConstants.AspectSampleDistance, z).Surface;
        heights[2] = columns.ColumnAt(x, z + PoiConstants.AspectSampleDistance).Surface;
        heights[3] = columns.ColumnAt(x, z - PoiConstants.AspectSampleDistance).Surface;
        long best = 0;
        for (long index = 1; index < heights.Length; index++)
        {
            if (heights[(int)index] > heights[(int)best])
            {
                best = index;
            }
        }

        return best;
    }

    /// <summary>
    /// Reconciles the drawn kind with the ground that has to carry it. This is why a
    /// site is placed in almost every cell that has dry ground: a preference that the
    /// terrain cannot support becomes the nearest kind it can, rather than nothing.
    /// </summary>
    private static PoiKind Reconcile(PoiKind preferred, TerrainColumn column) => preferred switch
    {
        // A way in needs a hillside to be cut into; without relief, pillars still read.
        PoiKind.CaveMouth or PoiKind.DungeonEntrance when column.Slope < PoiConstants.ReliefSlopeMinimum
            => PoiKind.StandingStones,

        // A lookout needs height to be worth building; on low ground, a ruin.
        PoiKind.VantagePoint when column.Surface < PoiConstants.VantageMinimumHeight
            => PoiKind.Ruin,

        // Walls need ground flat enough to have held them; pillars stand anywhere.
        PoiKind.Ruin when column.Slope > PoiConstants.FlatSlopeMaximum
            => PoiKind.StandingStones,

        _ => preferred,
    };

    private static (long Minimum, long Range) HeightRangeFor(PoiKind kind) => kind switch
    {
        PoiKind.StandingStones => (PoiConstants.StoneMinimumHeight, PoiConstants.StoneHeightRange),
        PoiKind.Ruin => (PoiConstants.RuinMinimumHeight, PoiConstants.RuinHeightRange),
        PoiKind.VantagePoint => (PoiConstants.VantageMinimumSteps, PoiConstants.VantageStepRange),
        _ => (PoiConstants.RuinMinimumHeight, PoiConstants.RuinHeightRange),
    };

    private static long FloorDivide(long value, long divisor) =>
        value >= 0 ? value / divisor : ((value - divisor + 1) / divisor);
}
