using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The river stage (#9878): one number per reach that rises and falls with the weather and the season,
/// routed rather than scaled. Fronts' precipitation falls on a coarse grid of cells; there it lies as
/// snow while the air is freezing and melts when it warms, runs off quickly in part and soaks into a
/// slow ground store in part, and evaporation takes from that store in warmth. Each reach then holds
/// water by its storage time (a linear reservoir; a lake on it adds its own storage) and passes it on
/// downstream, so a storm's water arrives later and lower downstream and recedes. A reach's ice follows
/// the cold it has had (freezing degree-hours); a small stream in a dry spell runs dry.
/// Nothing is saved: the fronts follow from the seed, the map and the clock, so the stage is rebuilt by
/// running from a warm-up before now.
/// </summary>
internal sealed class RiverStage
{
    /// <summary>Precipitation cells (metres a side): coarse enough to be cheap, fine enough for a front.</summary>
    internal const double CellMetres = 4000;

    /// <summary>The routing step (game hours): the stage steps on this grid of absolute hours and on no other.</summary>
    internal const double StepHours = 1;

    /// <summary>The climate run that calibrates the cells takes steps this long (game hours).</summary>
    private const double CalibrationStepHours = 12;

    /// <summary>A cell is calibrated on the mean of the cells within this many of it: one year of one cell's storms is too few.</summary>
    private const int CalibrationReach = 2;

    /// <summary>
    /// The bucket, linear in the rain so each cell can be calibrated exactly: the share of liquid water that
    /// runs off at once, the ground store's time, snow's melt rate per hour per degree above freezing, the
    /// share of liquid water the warmth evaporates per degree (at most a cap), and a glacier's slow release
    /// of snow that never melts (about a year).
    /// </summary>
    private const double GroundHours = 24 * 25, MeltRate = 0.6, EvaporationPerDegree = 1.5, MostEvaporation = 0.7, GlacierHours = 24 * 30;

    /// <summary>
    /// The share that runs off at once: wet ground holds more and seeps it out (a river that keeps running
    /// between storms), dry ground sheds rain at once and holds little (a stream that runs only after rain).
    /// </summary>
    private const double WetQuickShare = 0.3, DryQuickShare = 0.9;

    /// <summary>A lake holds this depth (m) over its area per unit of mean flow, as storage time; at least a day, at most two months.</summary>
    private const double LakeStorageMetres = 1, LeastLakeHours = 24, MostLakeHours = 24 * 60;

    /// <summary>
    /// Ice forms after this much frost (degree-hours, in hundredths of the climate's temperature scale: about
    /// three days a tenth below freezing; more on a larger river), and goes twice as fast as warmth melts it.
    /// </summary>
    private const double IceDegreeHours = 700, IceThaw = 2, DegreeScale = 100;

    /// <summary>A stream with a mean under this flow (m³/s) runs dry when its flow falls under this share of its mean.</summary>
    internal const double DryStreamFlow = 1;
    private const double DryShare = 0.15;

    private readonly WaterNetwork network;
    private readonly WeatherField weather;
    private readonly int[] order;
    private readonly int[] cellOf;
    private readonly double[] localMean, storageHours, mean;
    private readonly (double X, double Z, double Temperature, double Quick)[] cells;
    private readonly double[] snow, ground, runoff, cellScale;
    private readonly double[] storage, flow, cold;
    private readonly List<int>[] upstream;

    internal RiverStage(WaterNetwork network, WorldMap continent, WeatherField weather)
    {
        this.network = network ?? throw new ArgumentNullException(nameof(network));
        this.weather = weather ?? throw new ArgumentNullException(nameof(weather));
        ArgumentNullException.ThrowIfNull(continent);
        int count = network.Reaches.Count;
        MapGrid grid = network.Grid;
        upstream = new List<int>[count];
        foreach (Reach reach in network.Reaches)
            if (reach.Downstream >= 0) (upstream[reach.Downstream] ??= []).Add(reach.Id);

        // Upstream first (Kahn's order over the downstream links).
        int[] waiting = new int[count];
        foreach (Reach reach in network.Reaches) waiting[reach.Id] = upstream[reach.Id]?.Count ?? 0;
        Queue<int> ready = new(network.Reaches.Where(r => waiting[r.Id] == 0).Select(r => r.Id));
        List<int> sorted = new(count);
        while (ready.Count > 0)
        {
            int r = ready.Dequeue();
            sorted.Add(r);
            int down = network.Reaches[r].Downstream;
            if (down >= 0 && --waiting[down] == 0) ready.Enqueue(down);
        }

        order = [.. sorted];

        mean = [.. network.Reaches.Select(r => r.Flow)];
        localMean = new double[count];
        storageHours = new double[count];
        foreach (Reach reach in network.Reaches)
        {
            double above = upstream[reach.Id]?.Sum(u => network.Reaches[u].CatchmentSquareKilometres) ?? 0;
            localMean[reach.Id] = WaterBudget.Flow(Math.Max(0, reach.CatchmentSquareKilometres - above));
            storageHours[reach.Id] = Math.Max(reach.StorageHours, 1e-3);
        }

        // A lake holds a metre over its area per the mean flowing through it, on the reaches under it - between
        // its inflows and its outflow (#9876 review) - shared among them; a lake no reach runs under holds it on its outflow.
        foreach (LakeNode lake in network.Lakes)
        {
            int[] holders = lake.Interior.Length > 0 ? lake.Interior : lake.Outflow >= 0 ? [lake.Outflow] : [];
            double through = lake.Outflow >= 0 ? mean[lake.Outflow] : holders.Select(r => mean[r]).DefaultIfEmpty(0).Max();
            if (holders.Length == 0 || through <= 0) continue;
            double hours = Math.Clamp(lake.Lake.AreaSquareKilometres * 1e6 * LakeStorageMetres / through / 3600, LeastLakeHours, MostLakeHours);
            foreach (int r in holders) storageHours[r] += hours / holders.Length;
        }

        Dictionary<(long, long), int> cellIndex = [];
        List<(double, double, double, double)> cellList = [];
        cellOf = new int[count];
        foreach (Reach reach in network.Reaches)
        {
            int middle = reach.Nodes[reach.Nodes.Length / 2];
            (long cx, long cz) = ((long)Math.Floor(grid.X(middle) / CellMetres), (long)Math.Floor(grid.Z(middle) / CellMetres));
            if (!cellIndex.TryGetValue((cx, cz), out int cell))
            {
                double x = (cx + 0.5) * CellMetres, z = (cz + 0.5) * CellMetres;
                cell = cellList.Count;
                cellIndex[(cx, cz)] = cell;
                MapSample climate = continent.Sample(x, z);
                cellList.Add((x, z, climate.Temperature, WetQuickShare + ((DryQuickShare - WetQuickShare) * (1 - Math.Clamp(climate.Moisture, 0, 1)))));
            }

            cellOf[reach.Id] = cell;
        }

        cells = [.. cellList];
        snow = new double[cells.Length];
        cellScale = new double[cells.Length];
        ground = new double[cells.Length];
        runoff = new double[cells.Length];
        storage = new double[count];
        flow = new double[count];
        cold = new double[count];
    }

    /// <summary>The game hour the stage stands at; NaN until it has been started.</summary>
    internal double Hours { get; private set; } = double.NaN;

    internal int CellCount => cells.Length;

    /// <summary>The first hour of the climate year every stage calibrates against: the world's own, whenever it starts.</summary>
    private const double CalibrationFromHours = 0;

    /// <summary>
    /// A new stage at a time (a world's first): calibrates the precipitation against the world's first year
    /// so the long-run runoff is the water budget's mean, starts every store at its mean, and runs from a
    /// warm-up before the time so the stage carries the recent weather. It stands on the hour at or before
    /// the time. Afterwards the stage is carried on (<see cref="AdvanceTo"/>) and saved (<see cref="Capture"/>),
    /// not made again: a stage's stores remember months (snow, frost, a lake), which no warm-up recovers.
    /// </summary>
    internal void Start(double hours, double warmupDays = 30, double climateDays = Seasons.YearDays, double coarseHours = 6)
    {
        Calibrate(CalibrationFromHours, climateDays * 24, CalibrationStepHours);
        double target = Grid(hours);
        // The cells keep the snow and ground water the calibration year ended with, scaled to their calibrated rain.
        for (int c = 0; c < cells.Length; c++) { snow[c] *= cellScale[c]; ground[c] *= cellScale[c]; }
        for (int r = 0; r < storage.Length; r++) { storage[r] = mean[r] * storageHours[r] * 3600; flow[r] = mean[r]; cold[r] = 0; }
        Hours = target - (warmupDays * 24);
        while (Hours < target) Step(Math.Min(coarseHours, target - Hours));
        Hours = target;
    }

    /// <summary>
    /// A stage carried on from a saved one (#9878 review): calibrated as every stage is, its stores as they
    /// were saved. False, and nothing changed, when the save does not fit this network (another world or generator).
    /// </summary>
    internal bool StartFrom(RiverStageState saved, double climateDays = Seasons.YearDays)
    {
        if (!Fits(saved)) return false;
        Calibrate(CalibrationFromHours, climateDays * 24, CalibrationStepHours);
        Array.Copy(saved.Storage, storage, storage.Length);
        Array.Copy(saved.Frost, cold, cold.Length);
        Array.Copy(saved.Snow, snow, snow.Length);
        Array.Copy(saved.Ground, ground, ground.Length);
        for (int r = 0; r < storage.Length; r++) flow[r] = storage[r] / (storageHours[r] * 3600);
        Hours = saved.Hours;
        return true;
    }

    /// <summary>Whether a saved stage is this network's: as many reaches and cells, on the hour, every store sane.</summary>
    internal bool Fits(RiverStageState saved) =>
        saved.Storage.Length == storage.Length && saved.Frost.Length == cold.Length && saved.Snow.Length == snow.Length && saved.Ground.Length == ground.Length
        && double.IsFinite(saved.Hours) && saved.Hours == Grid(saved.Hours);

    /// <summary>The stage's stores now, copied, for saving.</summary>
    internal RiverStageState Capture() => new(Hours, (double[])storage.Clone(), (double[])cold.Clone(), (double[])snow.Clone(), (double[])ground.Clone());

    /// <summary>
    /// Advances to a time on the hour grid, one hour at a time, however far behind it is (#9878 review): the
    /// stage at an hour is the same whatever the calls that brought it there. It stands on the last whole
    /// hour at or before the time.
    /// </summary>
    internal void AdvanceTo(double hours)
    {
        if (double.IsNaN(Hours)) return;
        double target = Grid(hours);
        while (Hours < target) Step(StepHours);
    }

    /// <summary>The whole step at or before a time.</summary>
    private static double Grid(double hours) => Math.Floor(hours / StepHours) * StepHours;

    private static (long X, long Z) Cell(double x, double z) => ((long)Math.Floor(x / CellMetres), (long)Math.Floor(z / CellMetres));

    /// <summary>A reach's water now.</summary>
    internal ReachStage At(int reach) => Read(network.Reaches[reach], flow[reach], cold[reach]);

    /// <summary>A reach's water at a flow and a frost (degree-hours).</summary>
    internal static ReachStage Read(Reach r, double q, double frost)
    {
        double stage = RiverGeometry.Stage(q, r.Width, r.Gradient) - RiverGeometry.Stage(r.Flow, r.Width, r.Gradient);
        bool ice = frost > IceDegreeHours * (1 + Math.Log10(1 + r.Flow));
        bool dry = r.Flow < DryStreamFlow && q < DryShare * r.Flow;
        return new(q, r.Flow > 0 ? q / r.Flow : 1, r.BankfullFlow > 0 ? q / r.BankfullFlow : 0, stage, ice, dry);
    }

    /// <summary>The air temperature over a reach now (product units).</summary>
    internal double AirTemperature(int reach) => cells[cellOf[reach]].Temperature + Seasons.Offset(Hours);

    /// <summary>The stage as it stands now, apart from this one, which may go on stepping elsewhere.</summary>
    internal RiverStageView View()
    {
        double[] air = new double[cellOf.Length];
        for (int r = 0; r < air.Length; r++) air[r] = AirTemperature(r);
        return new(network, Hours, (double[])flow.Clone(), (double[])cold.Clone(), air);
    }

    private void Step(double dt)
    {
        double mid = Hours + (dt / 2), season = Seasons.Offset(mid);
        for (int c = 0; c < cells.Length; c++)
        {
            double t = cells[c].Temperature + season;
            double fallen = weather.Sample(cells[c].X, cells[c].Z, mid).Channels.Precipitation * cellScale[c] * dt;
            runoff[c] = Bucket(c, t, fallen, dt);
        }

        foreach (int r in order)
        {
            double inflow = localMean[r] * runoff[cellOf[r]];
            if (upstream[r] is { } ups) foreach (int u in ups) inflow += flow[u];
            double k = storageHours[r] * 3600, s = storage[r];
            double settled = inflow * k;
            s = settled + ((s - settled) * Math.Exp(-dt * 3600 / k));
            storage[r] = s;
            flow[r] = s / k;
            double t = cells[cellOf[r]].Temperature + season;
            cold[r] = t < Seasons.Freezing ? cold[r] + ((Seasons.Freezing - t) * DegreeScale * dt) : Math.Max(0, cold[r] - ((t - Seasons.Freezing) * DegreeScale * IceThaw * dt));
        }

        Hours += dt;
    }

    /// <summary>
    /// One cell's water this step, linear in what fell: snow lies while it freezes and melts as it warms (a
    /// glacier's never-melting snow creeps out over about a year), warmth evaporates a share of the liquid,
    /// part of the rest runs off at once and part soaks into a ground store that seeps out slowly. Returns
    /// the runoff rate (1 is the cell's mean once calibrated).
    /// </summary>
    private double Bucket(int c, double temperature, double fallen, double dt)
    {
        double warmth = Math.Max(0, temperature - Seasons.Freezing);
        double liquid = 0;
        if (temperature <= Seasons.Freezing) snow[c] += fallen;
        else liquid = fallen;
        double melt = snow[c] * (1 - Math.Exp(-((MeltRate * warmth) + (1 / GlacierHours)) * dt));
        snow[c] -= melt;
        liquid += melt;
        liquid *= 1 - Math.Min(MostEvaporation, EvaporationPerDegree * warmth);
        double quick = cells[c].Quick * liquid;
        double g = ground[c] + ((1 - cells[c].Quick) * liquid);
        double seep = g * (1 - Math.Exp(-dt / GroundHours));
        ground[c] = g - seep;
        return (quick + seep) / dt;
    }

    /// <summary>
    /// Scales each cell's precipitation so its runoff averages one over a run of the climate, so every reach's
    /// long-run flow is its network mean: the bucket is linear in the rain, so one run finds the scale. A cell
    /// that saw no rain in the run keeps a scale of one (it stays dry).
    /// </summary>
    private void Calibrate(double from, double span, double step)
    {
        double[] total = new double[cells.Length];
        int samples = 0;
        Array.Clear(snow);
        Array.Clear(ground);
        for (double h = from; h < from + span; h += step)
        {
            double season = Seasons.Offset(h + (step / 2));
            bool counted = h >= from + (span / 4);
            for (int c = 0; c < cells.Length; c++)
            {
                double fallen = weather.Sample(cells[c].X, cells[c].Z, h + (step / 2)).Channels.Precipitation * step;
                double flowing = Bucket(c, cells[c].Temperature + season, fallen, step);
                if (counted) total[c] += flowing;
            }

            if (counted) samples++;
        }

        // Each cell against its neighbourhood's mean runoff: its own year of storms is too few to judge by.
        Dictionary<(long, long), int> at = [];
        for (int c = 0; c < cells.Length; c++) at[Cell(cells[c].X, cells[c].Z)] = c;
        for (int c = 0; c < cells.Length; c++)
        {
            (long cx, long cz) = Cell(cells[c].X, cells[c].Z);
            double sum = 0;
            int n = 0;
            for (long dz = -CalibrationReach; dz <= CalibrationReach; dz++)
            for (long dx = -CalibrationReach; dx <= CalibrationReach; dx++)
                if (at.TryGetValue((cx + dx, cz + dz), out int near)) { sum += total[near]; n++; }
            double mean = samples == 0 || n == 0 ? 0 : sum / n / samples;
            cellScale[c] = mean > 1e-9 ? 1 / mean : 1;
        }
    }
}

/// <summary>
/// The river stage as it stood at one time (#9878): what play reads while the stage steps on off the
/// update. Read-only, so any thread may read it.
/// </summary>
internal sealed class RiverStageView(WaterNetwork network, double hours, double[] flow, double[] frost, double[] air)
{
    internal double Hours => hours;

    internal int Count => flow.Length;

    internal ReachStage At(int reach) => RiverStage.Read(network.Reaches[reach], flow[reach], frost[reach]);

    internal double AirTemperature(int reach) => air[reach];
}
