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
    /// <summary>Following around the party takes at most this many steps an update (each mostly a weather reading, about 0.13 ms).</summary>
    private const int StepsPerFollow = 6;
    /// <summary>A reader carries a cell on itself only once it is this far behind (game hours); the following keeps it nearer.</summary>
    private const double ReadLagHours = 0.5;
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
    private long settled, stepped, steppedTicks;

    internal SnowCover(WeatherField field) => this.field = field ?? throw new ArgumentNullException(nameof(field));

    private sealed class Cell(double depth, double hours, double touched)
    {
        /// <summary>The snow lying, the hour it stands at, the snow that has fallen on it this session (all of it, even what the
        /// cap blew off: tracks fill with it) and when the party last needed it.</summary>
        internal double Depth = depth, Hours = hours, Fallen, Touched = touched;
        /// <summary>Still being worked out from the days before it came into reach.</summary>
        internal bool Settling;
    }

    internal int Count => cells.Count;

    /// <summary>Cells worked out from the weather before, and steps carried on, for the readout.</summary>
    internal long Settled => settled;
    internal long Stepped => stepped;

    /// <summary>What a step costs on average (most of it reading the weather), in milliseconds, for the readout.</summary>
    internal double StepMs => stepped == 0 ? 0 : steppedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / stepped;

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

    /// <summary>
    /// Keeps the cells around a point (the party's) on the clock within a budget of <see cref="StepsPerFollow"/> steps, the
    /// cell furthest behind first: a cell newly in reach is settled a little each update instead of all at once, and the
    /// cells' regular steps are spread over updates. The readers then find them current.
    /// </summary>
    internal void FollowAround(double x, double z, double hours)
    {
        (int cx, int cz) = CellOf(x, z);
        List<(int X, int Z)> ring = new((2 * FollowedReach + 1) * (2 * FollowedReach + 1));
        for (int dz = -FollowedReach; dz <= FollowedReach; dz++)
            for (int dx = -FollowedReach; dx <= FollowedReach; dx++)
            {
                ring.Add((cx + dx, cz + dz));
                if (cells.TryGetValue((cx + dx, cz + dz), out Cell? near)) near.Touched = hours;
                else Begin(cx + dx, cz + dz, hours, forget: false);
            }

        Forget(ring);
        for (int budget = StepsPerFollow; budget > 0;)
        {
            (int X, int Z)? stalest = null;
            double behind = FollowHours;
            foreach ((int X, int Z) near in ring)
            {
                Cell cell = cells[near];
                if (hours - cell.Hours >= behind) (stalest, behind) = (near, hours - cell.Hours);
            }

            if (stalest is not { } key) break;
            Cell due = cells[key];
            budget -= Advance(key.X, key.Z, due, hours, behind > SettleStepHours * 3 ? SettleStepHours : StepHours, budget);
            due.Touched = hours;
            if (due.Settling && hours - due.Hours < FollowHours) Settle(due);
        }
    }

    /// <summary>A cell newly in reach, to be settled from the weather of the days before.</summary>
    private Cell Begin(int cx, int cz, double hours, bool forget = true)
    {
        Cell cell = new(0, hours - (SettleDays * HoursPerDay), hours) { Settling = true };
        cells[(cx, cz)] = cell;
        if (forget) Forget([(cx, cz)]);
        return cell;
    }

    private void Settle(Cell cell)
    {
        cell.Settling = false;
        cell.Fallen = 0;
        settled++;
    }

    /// <summary>
    /// A cell as a reader needs it: settled at once if it is new or still settling (after a teleport), and carried on if it
    /// has fallen more than <see cref="ReadLagHours"/> behind; otherwise as it stands, as the budgeted following keeps it.
    /// </summary>
    private Cell Follow(int cx, int cz, double hours)
    {
        Cell cell = cells.TryGetValue((cx, cz), out Cell? known) ? known : Begin(cx, cz, hours);
        if (cell.Settling)
        {
            Advance(cx, cz, cell, hours, SettleStepHours, int.MaxValue);
            Settle(cell);
        }
        else if (hours - cell.Hours > SettleDays * HoursPerDay)
        {
            // A long absence is settled again rather than stepped through.
            cell.Depth = 0;
            cell.Hours = hours - (SettleDays * HoursPerDay);
            Advance(cx, cz, cell, hours, SettleStepHours, int.MaxValue);
        }
        else if (hours - cell.Hours > ReadLagHours)
        {
            Advance(cx, cz, cell, hours, StepHours, int.MaxValue);
        }

        cell.Touched = hours;
        return cell;
    }

    /// <summary>
    /// A cell's snow from its hour toward <paramref name="hours"/>, in steps no longer than <paramref name="most"/>, each read
    /// at its middle, and at most <paramref name="steps"/> of them. Returns the steps taken.
    /// </summary>
    private int Advance(int cx, int cz, Cell cell, double hours, double most, int steps)
    {
        double x = (cx + 0.5) * CellMetres, z = (cz + 0.5) * CellMetres;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int taken = 0;
        while (cell.Hours < hours && taken < steps)
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
            cell.Fallen += falling;
            cell.Hours += dt;
            stepped++;
            taken++;
        }

        steppedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        return taken;
    }

    private static double Fraction(double hours) => ((hours / HoursPerDay) % 1 + 1) % 1;

    /// <summary>Forgets the cells needed longest ago past <see cref="MostCells"/>, never one of <paramref name="kept"/> (the party's).</summary>
    private void Forget(IReadOnlyCollection<(int X, int Z)> kept)
    {
        if (cells.Count <= MostCells) return;
        foreach ((int, int) oldest in cells.Where(pair => !kept.Contains(pair.Key)).OrderBy(pair => pair.Value.Touched)
            .Take(cells.Count - MostCells).Select(pair => pair.Key).ToList())
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
            if (cell.Hours <= hours) cells[(cell.CellX, cell.CellZ)] = new Cell(cell.Depth, cell.Hours, cell.Hours);
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
                if (metres > 0) cell.Fallen += metres;
            }
    }
}
