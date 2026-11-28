using System.Globalization;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A dry way over water: the two bank abutment coordinates a bridge spans between, its deck height,
/// its style, and the bank ground heights its ramps meet (#11186). On continents a crossing is
/// oriented perpendicularly across a mapped river reach (arbitrary angle); on regional worlds and
/// synthetic checks it spans an axis-aligned channel.
/// </summary>
internal readonly record struct CrossingSite(
    long CellX,
    long CellZ,
    long FromX,
    long FromZ,
    long ToX,
    long ToZ,
    long DeckY,
    bool AlongX,
    CrossingStyle Style = CrossingStyle.TimberFootbridge,
    double NearBankY = double.NaN,
    double FarBankY = double.NaN)
{
    internal string Id => string.Create(CultureInfo.InvariantCulture, $"crossing:{CellX}:{CellZ}");

    internal double SpanMetres => double.Hypot(ToX - FromX, ToZ - FromZ);

    internal double EffectiveNearBankY => double.IsNaN(NearBankY) ? DeckY : NearBankY;

    internal double EffectiveFarBankY => double.IsNaN(FarBankY) ? DeckY : FarBankY;
}

/// <summary>
/// Where the world's rivers can be walked over (#11186).
///
/// A crossing is not a destination, so it is not a point of interest and it does not join the
/// journal: it has its own rule and its own anchor-cell decision. Decisions remain pure functions
/// of the contract and the cell, so every streaming query, export worker, and fingerprint probe
/// agrees without communicating and without query-order dependence.
///
/// On continental worlds (320-450 km), crossings follow the mapped drainage network
/// (<see cref="MapDrainage"/> / <see cref="WaterNetwork"/>) with a civilization gradient from
/// settled river hinterlands out to rare wilderness footbridges, oriented perpendicularly across
/// the river channel. On regional worlds and synthetic terrain doubles without drainage, it falls
/// back to scanning a drawn row and column for narrow water gaps.
/// </summary>
internal sealed class CrossingPlacement
{
    private readonly TerrainGeneratorContract contract;
    private readonly ITerrainDraws draws;
    private readonly ITerrainColumns columns;
    private readonly long radius;
    /// <summary>Each anchor cell's decision, read by streaming and export workers too (#10907).</summary>
    private readonly SharedCache<(long X, long Z), CrossingSite?> cells = new(PoiConstants.SiteCacheLimit);

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
        cells.Set((cellX, cellZ), site);
        return site;
    }

    /// <summary>
    /// Every crossing whose anchor cell could reach within <paramref name="radius"/> metres of
    /// <paramref name="x"/>, <paramref name="z"/>, appended to <paramref name="into"/>.
    /// </summary>
    internal void CollectSitesNear(long x, long z, double radius, List<CrossingSite> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "A crossing query radius cannot be negative.");
        }

        long cell = PoiConstants.CellSize;
        long reach = (long)Math.Ceiling(radius) + (long)Math.Ceiling(CrossingDistribution.SettledMaximumSpanMetres + CrossingLayout.RampRun + 4);
        long firstX = GridMath.FloorDivide(x - reach, cell);
        long lastX = GridMath.FloorDivide(x + reach, cell);
        long firstZ = GridMath.FloorDivide(z - reach, cell);
        long lastZ = GridMath.FloorDivide(z + reach, cell);
        for (long cellX = firstX; cellX <= lastX; cellX++)
        {
            for (long cellZ = firstZ; cellZ <= lastZ; cellZ++)
            {
                if (SiteAt(cellX, cellZ) is CrossingSite site)
                {
                    into.Add(site);
                }
            }
        }
    }

    private CrossingSite? Decide(long cellX, long cellZ)
    {
        if (MapScale.IsContinental(contract.Extent))
        {
            return DecideContinental(cellX, cellZ);
        }

        return DecideLineScan(cellX, cellZ);
    }

    private CrossingSite? DecideContinental(long cellX, long cellZ)
    {
        const uint seedVersion = TerrainGeneratorContract.CrossingSeedVersion;
        long cell = PoiConstants.CellSize;
        long midX = (cellX * cell) + (cell / 2);
        long midZ = (cellZ * cell) + (cell / 2);
        if (!InWorld(midX, midZ))
        {
            return null;
        }

        // Fast continent-level gate before touching drainage or region tiles (#11186).
        CrossingContext context = columns.CrossingContextAt(midX, midZ);
        double cadence = CrossingDistribution.Cadence(in context);
        if (cadence <= 0)
        {
            return null;
        }

        double presentDraw = contract.DrawLong(draws, "crossing.present", cellX, cellZ, 0, PoiConstants.DistributionDrawScale - 1, seedVersion)
            / (double)PoiConstants.DistributionDrawScale;
        if (presentDraw >= cadence)
        {
            return null;
        }

        IReadOnlyList<RiverCrossingCandidate>? candidates = columns.RiverCandidatesInCell(cellX, cellZ);
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        List<RiverCrossingCandidate>? eligible = null;
        foreach (RiverCrossingCandidate candidate in candidates)
        {
            if (CrossingDistribution.IsBridgeable(in candidate, in context))
            {
                (eligible ??= []).Add(candidate);
            }
        }

        if (eligible is null || eligible.Count == 0)
        {
            return null;
        }

        long picked = contract.DrawLong(draws, "crossing.pick", cellX, cellZ, 0, eligible.Count - 1, seedVersion);
        return SeatAcrossRiver(cellX, cellZ, eligible[(int)picked], in context);
    }

    private CrossingSite? SeatAcrossRiver(long cellX, long cellZ, in RiverCrossingCandidate candidate, in CrossingContext cellContext)
    {
        double downLen = double.Hypot(candidate.DownX, candidate.DownZ);
        if (downLen < 1e-6)
        {
            return null;
        }

        // Unit vector perpendicular across the river channel.
        double nx = -candidate.DownZ / downLen;
        double nz = candidate.DownX / downLen;

        // Re-check macro context at the exact river point (frontier/town boundaries can bisect a 256 m cell).
        long probeX = (long)Math.Round(candidate.CentreX);
        long probeZ = (long)Math.Round(candidate.CentreZ);
        if (!InWorld(probeX, probeZ) || columns.InTown(probeX, probeZ))
        {
            return null;
        }

        CrossingContext pointContext = columns.CrossingContextAt(probeX, probeZ);
        if (pointContext.BeyondFrontier || pointContext.OpenSea)
        {
            return null;
        }

        // Confirm water under the centre (allowing a small snap perpendicular to the nominal centreline).
        double centreX = candidate.CentreX;
        double centreZ = candidate.CentreZ;
        if (!FindWetCentre(ref centreX, ref centreZ, nx, nz, out long peakWaterTop))
        {
            return null;
        }

        int maxSearch = (int)Math.Ceiling(candidate.HalfWidth) + CrossingDistribution.BankSearchMargin;
        if (!FindBank(centreX, centreZ, -nx, -nz, maxSearch, ref peakWaterTop, out long nearX, out long nearZ, out long nearSurface)
            || !FindBank(centreX, centreZ, nx, nz, maxSearch, ref peakWaterTop, out long farX, out long farZ, out long farSurface))
        {
            return null;
        }

        if (!InWorld(nearX, nearZ) || !InWorld(farX, farZ)
            || columns.InTown(nearX, nearZ) || columns.InTown(farX, farZ))
        {
            return null;
        }

        if (columns.CrossingContextAt(nearX, nearZ).BeyondFrontier
            || columns.CrossingContextAt(farX, farZ).BeyondFrontier)
        {
            return null;
        }

        double span = double.Hypot(farX - nearX, farZ - nearZ);
        double maxSpan = CrossingDistribution.MaximumSpanMetres(in pointContext);
        if (span < CrossingDistribution.MinimumSpanMetres || span > maxSpan)
        {
            return null;
        }

        if (Math.Abs(nearSurface - farSurface) > CrossingDistribution.MaximumBankHeightDifference)
        {
            return null;
        }

        long waterCeiling = Math.Max(peakWaterTop, (long)Math.Ceiling(candidate.WaterSurface));
        long deckY = Math.Max(waterCeiling + 1, Math.Max(nearSurface, farSurface));
        bool alongX = Math.Abs(farX - nearX) >= Math.Abs(farZ - nearZ);
        CrossingStyle style = CrossingDistribution.StyleFor(in candidate, in pointContext);
        return new CrossingSite(cellX, cellZ, nearX, nearZ, farX, farZ, deckY, alongX, style, nearSurface, farSurface);
    }

    private bool FindWetCentre(ref double cx, ref double cz, double nx, double nz, out long waterTop)
    {
        foreach (int shift in (ReadOnlySpan<int>)[0, 1, -1, 2, -2])
        {
            double px = cx + (nx * shift);
            double pz = cz + (nz * shift);
            long ix = (long)Math.Round(px);
            long iz = (long)Math.Round(pz);
            if (!InWorld(ix, iz)) continue;
            (long surface, long top) = columns.WaterAt(ix, iz);
            if (surface <= top)
            {
                cx = px;
                cz = pz;
                waterTop = top;
                return true;
            }
        }

        waterTop = GenerationConstants.WaterLevel;
        return false;
    }

    private bool FindBank(double cx, double cz, double dirX, double dirZ, int maxSearch, ref long peakWaterTop,
        out long bankX, out long bankZ, out long bankSurface)
    {
        int dryRun = 0;
        long firstDryX = 0, firstDryZ = 0, firstDrySurface = 0;
        for (int step = 1; step <= maxSearch; step++)
        {
            long ix = (long)Math.Round(cx + (dirX * step));
            long iz = (long)Math.Round(cz + (dirZ * step));
            if (!InWorld(ix, iz))
            {
                bankX = bankZ = bankSurface = 0;
                return false;
            }

            (long surface, long top) = columns.WaterAt(ix, iz);
            if (surface > top)
            {
                if (dryRun == 0)
                {
                    firstDryX = ix;
                    firstDryZ = iz;
                    firstDrySurface = surface;
                }

                dryRun++;
                if (dryRun >= PoiConstants.CrossingMinimumBank)
                {
                    bankX = firstDryX;
                    bankZ = firstDryZ;
                    bankSurface = firstDrySurface;
                    return true;
                }
            }
            else
            {
                dryRun = 0;
                peakWaterTop = Math.Max(peakWaterTop, top);
            }
        }

        bankX = bankZ = bankSurface = 0;
        return false;
    }

    private CrossingSite? DecideLineScan(long cellX, long cellZ)
    {
        long cell = PoiConstants.CellSize;
        long originX = cellX * cell;
        long originZ = cellZ * cell;
        long row = contract.DrawLong(draws, "crossing.row", cellX, cellZ, 0, cell - 1, TerrainGeneratorContract.CrossingSeedVersion);
        long column = contract.DrawLong(draws, "crossing.column", cellX, cellZ, 0, cell - 1, TerrainGeneratorContract.CrossingSeedVersion);
        return TryLine(originX, originZ, cellX, cellZ, row, alongX: true)
            ?? TryLine(originX, originZ, cellX, cellZ, column, alongX: false);
    }

    private CrossingSite? TryLine(long originX, long originZ, long cellX, long cellZ, long offset, bool alongX)
    {
        long cell = PoiConstants.CellSize;
        long bank = 0;
        long waterStart = -1;
        long waterTop = GenerationConstants.WaterLevel;
        for (long step = 0; step < cell; step++)
        {
            long x = alongX ? originX + step : originX + offset;
            long z = alongX ? originZ + offset : originZ + step;
            if (!InWorld(x, z))
            {
                bank = 0;
                waterStart = -1;
                continue;
            }

            (long surface, long columnWaterTop) = columns.WaterAt(x, z);
            if (surface >= columnWaterTop)
            {
                bank++;
                if (waterStart >= 0 && bank >= PoiConstants.CrossingMinimumBank)
                {
                    long lastWater = step - bank;
                    long length = lastWater - waterStart;
                    if (length <= PoiConstants.CrossingMaximumSpan)
                    {
                        return BuildLineCrossing(cellX, cellZ, originX, originZ, offset, alongX, waterStart, lastWater, surface, waterTop);
                    }

                    waterStart = -1;
                }

                continue;
            }

            if (waterStart < 0 && bank >= PoiConstants.CrossingMinimumBank)
            {
                waterStart = step;
                waterTop = columnWaterTop;
            }

            waterTop = Math.Max(waterTop, columnWaterTop);
            bank = 0;
            if (waterStart >= 0 && step - waterStart >= PoiConstants.CrossingMaximumSpan)
            {
                waterStart = -1;
            }
        }

        return null;
    }

    private CrossingSite BuildLineCrossing(long cellX, long cellZ, long originX, long originZ, long offset,
        bool alongX, long first, long last, long farBankSurface, long waterTop)
    {
        long nearX = alongX ? originX + first - 1 : originX + offset;
        long nearZ = alongX ? originZ + offset : originZ + first - 1;
        long nearBankSurface = columns.WaterAt(nearX, nearZ).Surface;
        long fromX = alongX ? originX + first : originX + offset;
        long fromZ = alongX ? originZ + offset : originZ + first;
        long toX = alongX ? originX + last : originX + offset;
        long toZ = alongX ? originZ + offset : originZ + last;
        long deck = Math.Max(waterTop + 1, Math.Max(nearBankSurface, farBankSurface));
        return new CrossingSite(cellX, cellZ, fromX, fromZ, toX, toZ, deck, alongX, CrossingStyle.TimberFootbridge, nearBankSurface, farBankSurface);
    }

    private bool InWorld(long x, long z) =>
        Math.Abs(x) <= radius - PoiConstants.WorldMargin && Math.Abs(z) <= radius - PoiConstants.WorldMargin;
}
