using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Water;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// The snow lying on the ground (#10068): a depth in metres per coarse cell, fake but stateful. It builds
/// while snow falls - from a snow front, or from any front's precipitation once the air is freezing - and
/// melts with warmth above freezing and, near freezing, in daylight. The air is the map's climate, the
/// season's swing (<see cref="Seasons"/>, the river stage's) and a front's own cold.
/// <para>
/// A cell is worked out the first time it is asked for by running the weather over the days before
/// (<see cref="SettleDays"/>), so snow lies where a front passed before the party arrived; after that it is
/// carried on with the clock and saved, so it is still there a day later and melts as it warms.
/// Only cells near the party are kept; the oldest are forgotten past <see cref="MostCells"/>.
/// </para>
/// </summary>
internal sealed class SnowCover
{
    /// <summary>Cells (metres a side): fine enough that a front's edge shows on the way, coarse enough to be cheap.</summary>
    internal const double CellMetres = 500;
    /// <summary>A new cell is settled from this many days of the weather before.</summary>
    internal const double SettleDays = 8;
    /// <summary>The steps a cell is carried on by, at most, in game hours (a settle runs coarser).</summary>
    private const double StepHours = 0.5, SettleStepHours = 2;
    /// <summary>A cell is carried on once the clock has moved this far past it (three game minutes).</summary>
    private const double FollowHours = 0.05;
    /// <summary>Snow builds at this many metres an hour under full precipitation: a long storm lays most of a metre.</summary>
    internal const double DepthPerHour = 0.05;
    /// <summary>Snow lies no deeper than this; above it, more falling is blown off.</summary>
    internal const double MostDepth = SnowCell.MostDepth;
    /// <summary>Melting, in metres an hour per unit of warmth above freezing (product temperature units).</summary>
    private const double MeltPerWarmth = 1.2;
    /// <summary>Full sun melts this many metres an hour while the air is within <see cref="SunMeltBelow"/> of freezing.</summary>
    private const double SunMeltPerHour = 0.004, SunMeltBelow = 0.06;
    /// <summary>A front's cold channel lowers the air by this much at full cover.</summary>
    internal const double FrontChill = 0.1;
    /// <summary>Kept cells, and how many cells around the party are followed.</summary>
    internal const int MostCells = SnowCell.MostCells, FollowedReach = 1;
    private const double HoursPerDay = 24;

    private readonly WeatherField field;
    private readonly Dictionary<(int X, int Z), Cell> cells = [];
    private long settled, stepped;

    internal SnowCover(WeatherField field) => this.field = field ?? throw new ArgumentNullException(nameof(field));

    private sealed class Cell(double depth, double hours)
    {
        internal double Depth = depth, Hours = hours, Fallen, Touched = hours;
    }

    internal int Count => cells.Count;

    /// <summary>Cells worked out from the weather before, and steps carried on, for the readout.</summary>
    internal long Settled => settled;
    internal long Stepped => stepped;

    internal static (int X, int Z) CellOf(double x, double z) => ((int)Math.Floor(x / CellMetres), (int)Math.Floor(z / CellMetres));

    /// <summary>The air at a place and time: its climate, the season and the fronts' cold (0 polar, 1 tropical; freezing at <see cref="Seasons.Freezing"/>).</summary>
    internal double Air(double x, double z, double hours, EnvironmentSample sample) =>
        field.Ground(x, z).Sample.Temperature + Seasons.Offset(hours) - (FrontChill * sample.Channels.Cold);

    /// <summary>How much snow falls at a place, 0..1 of full precipitation: a snow front's, or any front's once the air freezes.</summary>
    internal static double Snowfall(EnvironmentSample sample, double air)
    {
        double falling = 0;
        foreach (FrontPresence presence in sample.Fronts)
        {
            if (presence.Front.Kind.Fall == WeatherFall.Snow || (presence.Front.Kind.Fall == WeatherFall.Rain && air <= Seasons.Freezing))
                falling += presence.Front.Kind.Channels.Precipitation * presence.Cover;
        }

        return Math.Clamp(falling, 0, 1);
    }

    /// <summary>
    /// The depth at a world point now, between the cells' centres, carrying the four cells around it on to
    /// <paramref name="hours"/> (working any new one out first).
    /// </summary>
    internal double DepthAt(double x, double z, double hours)
    {
        double fx = (x / CellMetres) - 0.5, fz = (z / CellMetres) - 0.5;
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        double a = Follow(ix, iz, hours).Depth, b = Follow(ix + 1, iz, hours).Depth;
        double c = Follow(ix, iz + 1, hours).Depth, d = Follow(ix + 1, iz + 1, hours).Depth;
        return ((a * (1 - tx)) + (b * tx)) * (1 - tz) + (((c * (1 - tx)) + (d * tx)) * tz);
    }

    /// <summary>The snow that has fallen on a point's cell this session, in metres: a track fades as more falls on it.</summary>
    internal double FallenAt(double x, double z, double hours)
    {
        (int cx, int cz) = CellOf(x, z);
        return Follow(cx, cz, hours).Fallen;
    }

    /// <summary>Carries every cell around a point on to the clock: the party's, so their snow is current.</summary>
    internal void FollowAround(double x, double z, double hours)
    {
        (int cx, int cz) = CellOf(x, z);
        for (int dz = -FollowedReach; dz <= FollowedReach; dz++)
            for (int dx = -FollowedReach; dx <= FollowedReach; dx++)
                Follow(cx + dx, cz + dz, hours);
    }

    private Cell Follow(int cx, int cz, double hours)
    {
        if (!cells.TryGetValue((cx, cz), out Cell? cell))
        {
            cell = new Cell(0, hours - (SettleDays * HoursPerDay));
            Advance(cx, cz, cell, hours, SettleStepHours);
            settled++;
            cell.Fallen = 0;
            cells[(cx, cz)] = cell;
            Forget();
        }
        else if (hours - cell.Hours >= FollowHours)
        {
            // A long absence is settled again rather than stepped through.
            if (hours - cell.Hours > SettleDays * HoursPerDay)
            {
                cell.Depth = 0;
                cell.Hours = hours - (SettleDays * HoursPerDay);
                Advance(cx, cz, cell, hours, SettleStepHours);
            }
            else
            {
                Advance(cx, cz, cell, hours, StepHours);
            }
        }

        cell.Touched = hours;
        return cell;
    }

    /// <summary>A cell's snow from its hour to <paramref name="hours"/>, in steps no longer than <paramref name="most"/>, each read at its middle.</summary>
    private void Advance(int cx, int cz, Cell cell, double hours, double most)
    {
        double x = (cx + 0.5) * CellMetres, z = (cz + 0.5) * CellMetres;
        while (cell.Hours < hours)
        {
            double dt = Math.Min(most, hours - cell.Hours), mid = cell.Hours + (dt / 2);
            EnvironmentSample sample = field.Sample(x, z, mid);
            double air = Air(x, z, mid, sample);
            double falling = Snowfall(sample, air) * DepthPerHour * dt;
            double laid = Math.Min(falling, Math.Max(0, MostDepth - cell.Depth));
            double warmth = Math.Max(0, air - Seasons.Freezing);
            double sun = air > Seasons.Freezing - SunMeltBelow ? SunMeltPerHour * WorldClock.Daylight(Fraction(mid)) * (1 - sample.Channels.Cloud) : 0;
            double melt = ((MeltPerWarmth * warmth) + sun) * dt;
            cell.Depth = Math.Max(0, cell.Depth + laid - melt);
            cell.Fallen += laid;
            cell.Hours += dt;
            stepped++;
        }
    }

    private static double Fraction(double hours) => ((hours / HoursPerDay) % 1 + 1) % 1;

    private void Forget()
    {
        if (cells.Count <= MostCells) return;
        foreach ((int, int) oldest in cells.OrderBy(pair => pair.Value.Touched).Take(cells.Count - MostCells).Select(pair => pair.Key).ToList())
            cells.Remove(oldest);
    }

    /// <summary>The kept cells as saved.</summary>
    internal IReadOnlyList<SnowCell> Capture() =>
        [.. cells.OrderBy(pair => pair.Key.Z).ThenBy(pair => pair.Key.X).Select(pair => new SnowCell(pair.Key.X, pair.Key.Z, pair.Value.Depth, pair.Value.Hours))];

    /// <summary>Takes saved cells up; a cell saved later than the clock is not (it starts again).</summary>
    internal void Restore(IReadOnlyList<SnowCell> saved, double hours)
    {
        cells.Clear();
        foreach (SnowCell cell in saved)
        {
            if (cell.Hours <= hours) cells[(cell.CellX, cell.CellZ)] = new Cell(cell.Depth, cell.Hours);
        }
    }

    /// <summary>For diagnosis: lays (or, negative, takes) snow on the cells around a point at once.</summary>
    internal void Lay(double x, double z, double hours, double metres)
    {
        (int cx, int cz) = CellOf(x, z);
        for (int dz = -FollowedReach - 1; dz <= FollowedReach + 1; dz++)
            for (int dx = -FollowedReach - 1; dx <= FollowedReach + 1; dx++)
            {
                Cell cell = Follow(cx + dx, cz + dz, hours);
                double laid = Math.Clamp(cell.Depth + metres, 0, MostDepth) - cell.Depth;
                cell.Depth += laid;
                if (laid > 0) cell.Fallen += laid;
            }
    }
}
