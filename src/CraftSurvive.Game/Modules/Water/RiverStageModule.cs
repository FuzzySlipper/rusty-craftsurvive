using System.Globalization;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The owner of the river stage in play (#9878). On a continent it builds the stage off the update - the
/// calibration and warm-up take seconds - and adopts it on an update once ready; then, as the clock
/// passes each game hour, it steps the stage on off the update too and, once a step is done, publishes
/// the stage it reached and tells the water surfaces to redraw. Play reads only the published stage
/// (<see cref="RiverStageView"/>), so a step never stalls an update. Before it is ready, and on a world
/// without a water network, every reach stands at its mean.
/// <para>
/// The stage is carried, not rebuilt (#9878 review): its stores remember months - snow, frost, a lake - so
/// it is saved (<see cref="IRiverStageArchive"/>) every <see cref="SaveEveryHours"/> game hours and when the
/// world closes, restored when the world opens, and carried on to the clock hour by hour on the absolute
/// grid. A stage that was never saved, or does not fit, starts afresh at the clock.
/// </para>
/// </summary>
internal sealed class RiverStageModule : IProductModule, IWaterStage
{
    private readonly WorldMap map;
    private readonly Func<double> hours;
    private readonly Action<IReadOnlyList<WaterBounds>> advanced;
    private Task<(RiverStage Stage, FloodPlains Plains)>? building;
    /// <summary>The stage that steps; only the stepping task touches it once adopted.</summary>
    private RiverStage? stepper;
    private Task<(RiverStageView View, FloodLevels Floods, double Milliseconds, WaterBounds[] Changed, RiverStageState? Save)>? stepping;
    private readonly IRiverStageArchive? archive;
    private double savedHours = double.NaN;
    private string restoreOutcome = "none";

    /// <summary>The stage is saved when it has moved on this many game hours since it last was.</summary>
    internal const double SaveEveryHours = 6;
    /// <summary>Each reach's area, and the areas of the lakes it freezes, for what must redraw when it changes.</summary>
    private WaterBounds[] reachBounds = [];
    private Dictionary<int, List<WaterBounds>> reachLakes = [];
    /// <summary>The stage as last published: everything in play reads this.</summary>
    private RiverStageView? stage;
    /// <summary>The floodplains (#9880), and the floods as last published with the stage.</summary>
    private FloodPlains? plains;
    private FloodLevels? floods;

    /// <summary>A flood whose level moves by more than this (metres) since it was last drawn is drawn again.</summary>
    private const double FloodRedrawMetres = 0.05;
    private WaterNetwork? network;
    private Dictionary<int, int> lakeReach = [];
    /// <summary>For each node of the network's grid, the largest reach through it, or -1.</summary>
    private int[] nodeReach = [];

    /// <summary>A crossing looks for its reach within this many grid cells of the party.</summary>
    private const int CrossingCells = 2;
    private double lastAdvanceMs;
    private long steps, changedAreas;

    /// <param name="hours">The clock as absolute game hours.</param>
    /// <param name="advanced">Called once the stage has moved on, with where water froze, thawed, ran dry or came back, so what draws water can redraw.</param>
    /// <param name="archive">Where the stage is saved and restored from; null keeps it for the session only.</param>
    internal RiverStageModule(WorldMap map, Func<double> hours, Action<IReadOnlyList<WaterBounds>> advanced, IRiverStageArchive? archive = null)
    {
        this.archive = archive;
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        this.hours = hours ?? throw new ArgumentNullException(nameof(hours));
        this.advanced = advanced ?? throw new ArgumentNullException(nameof(advanced));
    }

    internal bool Ready => stage is not null;

    /// <summary>The hour the published stage stands at; NaN before it is ready.</summary>
    internal double PublishedHours => stage?.Hours ?? double.NaN;

    /// <summary>A reach's water now, or null before the stage is ready (it stands at its mean).</summary>
    public ReachStage? At(int reach) => stage is not null && reach >= 0 && reach < stage.Count ? stage.At(reach) : null;

    /// <summary>Whether a lake is frozen: as its outflow is, or for a closed lake its first inflow.</summary>
    public bool LakeIce(Lake lake) => stage is not null && lakeReach.TryGetValue(lake.Id, out int reach) && stage.At(reach).Ice;

    /// <summary>A flood's level at a world point and its reach (#9880), as last published; any thread may ask.</summary>
    public (double Level, int Reach)? FloodAt(double x, double z) => Volatile.Read(ref floods)?.At(x, z);

    /// <summary>Whether a world point lies by a floodplain (#9880).</summary>
    public bool Floodable(double x, double z) => Volatile.Read(ref floods)?.Floodable(x, z) ?? false;

    /// <summary>Whether the map's ground at a world point is under a flood now (#9880): what the party's routes and crossings read.</summary>
    internal bool Flooded(double x, double z) => Volatile.Read(ref floods)?.Flooded(x, z) ?? false;

    /// <summary>The floods now: nodes and area under water, for readouts.</summary>
    internal FloodLevels? Floods => Volatile.Read(ref floods);

    /// <summary>
    /// The crossing where the map shows the party at a river: the largest walked reach within a few cells,
    /// read through its stage. Before the stage is ready, or with no walked reach near, the river runs as usual.
    /// </summary>
    internal RiverState CrossingAt(double x, double z)
    {
        if (stage is null || network is null) return RiverState.Running;
        MapGrid grid = network.Grid;
        int centre = grid.Nearest(x, z), ci = centre % grid.Side, cj = centre / grid.Side, best = -1;
        for (int dj = -CrossingCells; dj <= CrossingCells; dj++)
        for (int di = -CrossingCells; di <= CrossingCells; di++)
        {
            int i = ci + di, j = cj + dj;
            if (i < 0 || j < 0 || i >= grid.Side || j >= grid.Side) continue;
            int reach = nodeReach[(j * grid.Side) + i];
            if (reach >= 0 && (best < 0 || network.Reaches[reach].Flow > network.Reaches[best].Flow)) best = reach;
        }

        return RiverStates.Of(best < 0 ? null : stage.At(best));
    }

    public void Start()
    {
        if (!map.Scale.Continental || map.RegionFrame is not null || building is not null) return;
        double now = hours();
        WorldMap continent = map;
        RiverStageState? saved = archive?.Restore();
        // Off the update: its own weather field (the live one is the update's), the drainage built with the world.
        building = Task.Run(() =>
        {
            MapDrainage drainage = MapRegions.For(continent).Drainage;
            WaterNetwork built = drainage.Network;
            FloodPlains floodPlains = new(built, drainage.ElevationAt);
            RiverStage fresh = new(built, continent, WeatherField.For(continent));
            // A saved stage no later than the clock is carried on to it; any other starts afresh.
            if (saved is not null && saved.Hours <= now && fresh.StartFrom(saved))
            {
                restoreOutcome = string.Create(CultureInfo.InvariantCulture, $"restored@{saved.Hours:F0}");
                fresh.AdvanceTo(now);
            }
            else
            {
                restoreOutcome = saved is null ? "fresh" : "fresh (save did not fit)";
                fresh.Start(now);
            }

            return (fresh, floodPlains);
        });
    }

    public void Update(ProductStep step) => Follow();

    /// <summary>
    /// Adopts the stage once built and brings it to the clock. The map's updates call it too, since the
    /// walking updates stop while the map is open (#9878 review); a long gap is crossed hour by hour.
    /// </summary>
    internal void Follow()
    {
        if (stepper is null)
        {
            if (building is not { IsCompleted: true } done) return;
            building = null;
            (stepper, plains) = done.GetAwaiter().GetResult();
            stage = stepper.View();
            RiverStageView first = stage;
            Volatile.Write(ref floods, plains.Levels(first.At));
            FloodLevels firstFloods = floods!;
            network = MapRegions.For(map).Drainage.Network;
            lakeReach = network.Lakes
                .Select(node => (node.Lake.Id, Reach: node.Outflow >= 0 ? node.Outflow : node.Inflows.FirstOrDefault(-1)))
                .Where(pair => pair.Reach >= 0)
                .GroupBy(pair => pair.Id)
                .ToDictionary(group => group.Key, group => group.First().Reach);
            reachBounds = [.. network.Reaches.Select(reach => Bounds(network.Grid, reach.Nodes))];
            reachLakes = network.Lakes
                .Select(node => (Reach: node.Outflow >= 0 ? node.Outflow : node.Inflows.FirstOrDefault(-1), Area: Bounds(map.Grid, node.Lake.Basin)))
                .Where(pair => pair.Reach >= 0)
                .GroupBy(pair => pair.Reach)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Area).ToList());
            nodeReach = new int[network.Grid.Count];
            Array.Fill(nodeReach, -1);
            foreach (Reach reach in network.Reaches)
                foreach (int node in reach.Nodes)
                    if (nodeReach[node] < 0 || network.Reaches[nodeReach[node]].Flow < reach.Flow) nodeReach[node] = reach.Id;
            // Everything already frozen, dry or in flood is news to what draws water.
            advanced(Changed(null, stage, plains, null, firstFloods));
            Save(stepper.Capture());
        }

        if (stepping is { IsCompleted: true } stepped)
        {
            stepping = null;
            WaterBounds[] changed;
            RiverStageState? due;
            FloodLevels nowFlooded;
            (stage, nowFlooded, lastAdvanceMs, changed, due) = stepped.GetAwaiter().GetResult();
            Volatile.Write(ref floods, nowFlooded);
            steps++;
            changedAreas += changed.Length;
            advanced(changed);
            if (due is not null) Save(due);
        }

        double now = hours();
        if (stepping is not null || stage is null || now - stage.Hours < RiverStage.StepHours) return;
        RiverStage moving = stepper;
        RiverStageView before = stage;
        FloodPlains floodPlains = plains!;
        FloodLevels? floodsBefore = Volatile.Read(ref floods);
        double lastSaved = savedHours;
        stepping = Task.Run(() =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            moving.AdvanceTo(now);
            RiverStageView after = moving.View();
            FloodLevels flooded = floodPlains.Levels(after.At, floodsBefore, after.Hours - before.Hours);
            // The stores are copied here, where the stage is still, when a save is due.
            RiverStageState? save = double.IsNaN(lastSaved) || moving.Hours - lastSaved >= SaveEveryHours ? moving.Capture() : null;
            return (after, flooded, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, Changed(before, after, floodPlains, floodsBefore, flooded), save);
        });
    }

    /// <summary>
    /// Brings the published stage within so many game hours of the clock before something decides by it
    /// (a ford's state on the map, #9878 review), waiting on the steps off the update if it must.
    /// </summary>
    internal void CatchUp(double withinHours)
    {
        Follow();
        while (stage is not null && hours() - stage.Hours > withinHours)
        {
            stepping?.Wait();
            Follow();
        }
    }

    private void Save(RiverStageState state)
    {
        if (archive is null) return;
        archive.Save(state);
        savedHours = state.Hours;
    }

    /// <summary>
    /// Where water froze, thawed, ran dry or came back between two stages (from none: where it is frozen or
    /// dry), and where a flood rose, fell or receded (#9880): a reach's whole floodplain.
    /// </summary>
    private WaterBounds[] Changed(RiverStageView? before, RiverStageView after, FloodPlains floodPlains, FloodLevels? floodsBefore, FloodLevels floodsAfter)
    {
        List<WaterBounds> changed = [];
        for (int r = 0; r < after.Count; r++)
        {
            ReachStage now = after.At(r);
            ReachStage? then = before?.At(r);
            bool was = then is { } prior && (prior.Ice != now.Ice || prior.Dry != now.Dry);
            if (before is null ? now.Ice || now.Dry : was)
            {
                changed.Add(reachBounds[r]);
                if (reachLakes.TryGetValue(r, out List<WaterBounds>? lakes)) changed.AddRange(lakes);
            }

        }

        // A flood that came, went, or rose or fell by more than a little since it was last drawn.
        foreach (int r in floodsAfter.Reaches.Keys.Union(floodsBefore?.Reaches.Keys ?? []))
        {
            bool had = floodsBefore is not null && floodsBefore.Reaches.TryGetValue(r, out double was);
            bool has = floodsAfter.Reaches.TryGetValue(r, out double now);
            if (had != has || (has && Math.Abs(now - floodsBefore!.Reaches[r]) > FloodRedrawMetres)) changed.Add(floodPlains.Bounds[r]);
        }

        return [.. changed];
    }

    /// <summary>The area lattice nodes span, a cell more each way.</summary>
    private static WaterBounds Bounds(MapGrid grid, int[] nodes)
    {
        double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
        foreach (int node in nodes)
        {
            minX = Math.Min(minX, grid.X(node));
            maxX = Math.Max(maxX, grid.X(node));
            minZ = Math.Min(minZ, grid.Z(node));
            maxZ = Math.Max(maxZ, grid.Z(node));
        }

        return new(minX - grid.Spacing, minZ - grid.Spacing, maxX + grid.Spacing, maxZ + grid.Spacing);
    }

    public void Restart()
    {
    }

    public void Dispose()
    {
        // The stage is saved as it stands when the world closes; a step still running is finished first.
        stepping?.Wait();
        if (stepper is not null) Save(stepper.Capture());
        // A step still running finishes on its own; nothing reads it after this.
        stage = null;
        stepper = null;
        stepping = null;
        building = null;
    }

    /// <summary>The floods now, and the flooded nodes nearest a point: where, the water's level and depth, and its reach.</summary>
    internal string FloodReadout(double x, double z, int most)
    {
        if (Volatile.Read(ref floods) is not FloodLevels now || plains is null) return "floods=none (no stage yet)";
        MapGrid grid = plains.Network.Grid;
        IEnumerable<string> nearest = plains.Nodes.Where(n => !double.IsNaN(now.LevelAt(n)))
            .OrderBy(n => Math.Pow(grid.X(n) - x, 2) + Math.Pow(grid.Z(n) - z, 2))
            .Take(most)
            .Select(n => string.Create(CultureInfo.InvariantCulture,
                $"at={grid.X(n):F0},{grid.Z(n):F0} level={now.LevelAt(n):F2} depth={now.LevelAt(n) - plains.Ground(n):F2} reach={plains.ReachOf(n)}"));
        return string.Create(CultureInfo.InvariantCulture, $"floods: {now.Count} nodes, {now.SquareKilometres:F1} km2 under water over {now.Reaches.Count} reaches at hour {PublishedHours:F0}") + " | " + string.Join(" | ", nearest);
    }

    /// <summary>The reaches with a mean flow in a band passing within a distance of a point, largest first: where each is nearest, and its water now.</summary>
    internal string NearbyReadout(double x, double z, double metres, double leastFlow, double mostFlow, int most)
    {
        if (stage is null || network is null) return Readout();
        MapGrid grid = network.Grid;
        IEnumerable<string> lines = network.Reaches
            .Where(reach => reach.Flow >= leastFlow && reach.Flow <= mostFlow)
            .Select(reach => (reach, node: reach.Nodes.MinBy(n => Math.Pow(grid.X(n) - x, 2) + Math.Pow(grid.Z(n) - z, 2))))
            .Select(pair => (pair.reach, pair.node, distance: Math.Sqrt(Math.Pow(grid.X(pair.node) - x, 2) + Math.Pow(grid.Z(pair.node) - z, 2))))
            .Where(pair => pair.distance <= metres)
            .OrderByDescending(pair => pair.reach.Flow).Take(most)
            .Select(pair =>
            {
                ReachStage s = stage.At(pair.reach.Id);
                return string.Create(CultureInfo.InvariantCulture,
                    $"reach={pair.reach.Id} at={grid.X(pair.node):F0},{grid.Z(pair.node):F0} mean={pair.reach.Flow:F2} bankfull={pair.reach.BankfullFlow:F1} flow={s.Flow:F2} fullness={s.Fullness:F2} stage={s.Stage:+0.00;-0.00;0.00} state={RiverStates.Of(s)} air={stage.AirTemperature(pair.reach.Id):F2}");
            });
        return string.Join(" | ", [Readout(), .. lines]);
    }

    internal string Readout(int reach = -1)
    {
        if (stage is null) return building is null ? "stage=none (no water network)" : "stage=building";
        string here = reach >= 0 && At(reach) is ReachStage s
            ? string.Create(CultureInfo.InvariantCulture, $" reach={reach} flow={s.Flow:F2} ratio={s.Ratio:F2} fullness={s.Fullness:F2} stage={s.Stage:+0.00;-0.00;0.00} overbank={s.Overbank} ice={s.Ice} dry={s.Dry} air={stage.AirTemperature(reach):F2}")
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"stage=ready restore={restoreOutcome} saved={savedHours:F0} hours={stage.Hours:F1} season={Seasons.At(stage.Hours)} cells={stepper?.CellCount ?? 0} steps={steps} stepping={stepping is not null} lastStepMs={lastAdvanceMs:F1} (off the update) changedAreas={changedAreas}{here}");
    }
}
