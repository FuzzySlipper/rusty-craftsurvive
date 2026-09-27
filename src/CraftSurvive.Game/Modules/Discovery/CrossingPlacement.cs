using System.Globalization;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// A dry way over water: the run of water columns a span crosses, and the height its deck is
/// laid at. The deck sits at the higher of the two banks, so a crossing meets the ground on
/// both sides instead of cutting a trench into one of them.
/// </summary>
internal readonly record struct CrossingSite(
    long CellX,
    long CellZ,
    long FromX,
    long FromZ,
    long ToX,
    long ToZ,
    long DeckY,
    bool AlongX)
{
    internal string Id => string.Create(CultureInfo.InvariantCulture, $"crossing:{CellX}:{CellZ}");
}

/// <summary>
/// Where the world's water can be walked over.
///
/// A crossing is not a destination, so it is not a point of interest and it does not join the
/// journal: it has its own rule and its own lattice decision. What it shares with the site
/// pass is the property that matters - it is a pure function of the contract and its cell, so
/// every chunk that touches it agrees about it and no route depends on which chunk was
/// generated first.
///
/// The rule, per cell, on a drawn row and then a drawn column (the first that works wins):
/// walk the line, find a run of water columns that is preceded and followed by enough dry
/// land, and accept it when the run is short enough to span. Water too wide to bridge is left
/// to be walked around, which is the honest answer for a lake rather than a river.
/// </summary>
internal sealed class CrossingPlacement
{
    private readonly TerrainGeneratorContract contract;
    private readonly ITerrainDraws draws;
    private readonly ITerrainColumns columns;
    private readonly long radius;
    private readonly Dictionary<(long X, long Z), CrossingSite?> cells = [];

    internal CrossingPlacement(TerrainGeneratorContract contract, ITerrainDraws draws, ITerrainColumns columns, long radius)
    {
        this.contract = contract;
        this.draws = draws ?? throw new ArgumentNullException(nameof(draws));
        this.columns = columns ?? throw new ArgumentNullException(nameof(columns));
        this.radius = radius;
    }

    internal CrossingSite? SiteAt(long cellX, long cellZ)
    {
        if (cells.TryGetValue((cellX, cellZ), out CrossingSite? cached))
        {
            return cached;
        }

        CrossingSite? site = Decide(cellX, cellZ);
        if (cells.Count >= PoiConstants.SiteCacheLimit)
        {
            cells.Clear();
        }

        cells[(cellX, cellZ)] = site;
        return site;
    }

    private CrossingSite? Decide(long cellX, long cellZ)
    {
        long cell = PoiConstants.CellSize;
        long originX = cellX * cell;
        long originZ = cellZ * cell;
        string key = TerrainGeneratorContract.CoordinateKey(cellX, cellZ);
        return TryLine(originX, originZ, cellX, cellZ, key, alongX: true)
            ?? TryLine(originX, originZ, cellX, cellZ, key, alongX: false);
    }

    private CrossingSite? TryLine(long originX, long originZ, long cellX, long cellZ, string key, bool alongX)
    {
        long cell = PoiConstants.CellSize;
        long offset = contract.DrawLong(draws, alongX ? "crossing.row" : "crossing.column", key, 0, cell - 1);
        long bank = 0;
        long waterStart = -1;
        for (long step = 0; step < cell; step++)
        {
            long x = alongX ? originX + step : originX + offset;
            long z = alongX ? originZ + offset : originZ + step;
            if (!InWorld(x, z))
            {
                // The world's edge is not a shore. A column outside it reports the default
                // surface, which would otherwise read as deep water.
                bank = 0;
                waterStart = -1;
                continue;
            }

            long surface = columns.ColumnAt(x, z).Surface;
            if (surface >= TerrainConstants.WaterLevel)
            {
                bank++;
                if (waterStart < 0)
                {
                    continue;
                }

                long length = step - waterStart;
                if (length <= PoiConstants.CrossingMaximumSpan && bank >= PoiConstants.CrossingMinimumBank)
                {
                    return Build(cellX, cellZ, originX, originZ, offset, alongX, waterStart, step - 1, surface);
                }

                waterStart = -1;
                continue;
            }

            // Water. A span has to start from a real bank, and a run that grows past the
            // span limit is abandoned rather than carried along the whole lake.
            if (waterStart < 0 && bank >= PoiConstants.CrossingMinimumBank)
            {
                waterStart = step;
            }

            bank = 0;
            if (waterStart >= 0 && step - waterStart >= PoiConstants.CrossingMaximumSpan)
            {
                waterStart = -1;
            }
        }

        return null;
    }

    private CrossingSite Build(long cellX, long cellZ, long originX, long originZ, long offset,
        bool alongX, long first, long last, long farBankSurface)
    {
        long nearX = alongX ? originX + first - 1 : originX + offset;
        long nearZ = alongX ? originZ + offset : originZ + first - 1;
        long nearBankSurface = columns.ColumnAt(nearX, nearZ).Surface;
        long fromX = alongX ? originX + first : originX + offset;
        long fromZ = alongX ? originZ + offset : originZ + first;
        long toX = alongX ? originX + last : originX + offset;
        long toZ = alongX ? originZ + offset : originZ + last;
        long deck = Math.Max(TerrainConstants.WaterLevel + 1, Math.Max(nearBankSurface, farBankSurface));
        return new CrossingSite(cellX, cellZ, fromX, fromZ, toX, toZ, deck, alongX);
    }

    private bool InWorld(long x, long z) =>
        Math.Abs(x) <= radius - PoiConstants.WorldMargin && Math.Abs(z) <= radius - PoiConstants.WorldMargin;
}

/// <summary>
/// The bridge itself: a plank deck one course thick and three wide along the span, a
/// cobblestone pier every few blocks standing down into the water, and stone abutments where
/// the deck meets each bank.
///
/// Everything it places is a fill, never a cut. That is the whole point of a crossing rather
/// than a dam: the water underneath stays water, and the ground on either side is walked over
/// rather than dug through.
/// </summary>
internal static class CrossingStructure
{
    internal static PoiVoxel MaterialAt(CrossingSite site, long x, long y, long z)
    {
        long along = site.AlongX ? x - site.FromX : z - site.FromZ;
        long across = site.AlongX ? z - site.FromZ : x - site.FromX;
        long length = site.AlongX ? site.ToX - site.FromX : site.ToZ - site.FromZ;
        long reach = PoiConstants.CrossingMaximumSpan;
        if (along < -1 || along > length + 1 || Math.Abs(across) > PoiConstants.CrossingHalfWidth
            || along < -reach || along > reach + length)
        {
            return PoiVoxel.None;
        }

        if (y == site.DeckY && along >= 0 && along <= length)
        {
            return PoiVoxel.Fill(BlockId.Planks);
        }

        if (y > site.DeckY || y < site.DeckY - PoiConstants.CrossingPierDepth)
        {
            return PoiVoxel.None;
        }

        // A pier every few blocks, in the middle of the deck's width.
        bool onPier = along >= 0 && along <= length && along % PoiConstants.CrossingPierSpacing == 0 && across == 0;
        if (onPier)
        {
            return PoiVoxel.Fill(BlockId.Cobblestone);
        }

        // Abutments: the bank columns at each end of the span.
        bool onAbutment = (along == -1 || along == length + 1) && Math.Abs(across) <= PoiConstants.CrossingHalfWidth;
        return onAbutment ? PoiVoxel.Fill(BlockId.Cobblestone) : PoiVoxel.None;
    }
}
