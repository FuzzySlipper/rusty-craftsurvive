using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// The owner of the snow cover in play (#10068): restores it when the world opens, carries the cells around the
/// party on with the clock, saves it every <see cref="SaveEveryHours"/> game hours and when the world closes, and
/// tells what draws snow (the places' and stones' surfaces) the depth where the party stands.
/// </summary>
internal sealed class SnowModule : IProductModule
{
    /// <summary>The cover is saved when the clock has moved this many game hours since it last was.</summary>
    internal const double SaveEveryHours = 2;
    /// <summary>Snow this deep (metres) covers all of the open flat ground; less covers it in patches (the Engine's cover, 0 to 1).</summary>
    internal const double FullCoverMetres = 0.12;

    /// <summary>The scene's snow cover for a depth shown: none at 0, whole at <see cref="FullCoverMetres"/>.</summary>
    internal static float CoverOf(double metres) => (float)Math.Clamp(metres / FullCoverMetres, 0, 1);

    /// <summary>The depth is told again once it has moved this far, in metres.</summary>
    private const double TellStepMetres = 0.002;
    /// <summary>
    /// Frost: in freezing air with nothing falling it gathers over <see cref="FrostHours"/> and goes over
    /// <see cref="ThawHours"/> once the air is above freezing; at full it shows on surfaces as this thin a snow.
    /// </summary>
    private const double FrostHours = 3, ThawHours = 0.75, FrostShowsAsMetres = 0.035;
    private double frostHours = double.NaN;

    private readonly ProductSaveSlot<IReadOnlyList<SnowCell>>? slot;
    private readonly WeatherField field;
    private readonly Func<double> hours;
    private readonly Func<Vector3> playerWorld;
    private readonly Action<double> depthChanged;
    private readonly Func<EnvironmentSample> here;
    private double savedHours = double.NaN, toldDepth = double.NaN;

    /// <param name="depthChanged">Told the depth where the party stands, in metres, whenever it has moved.</param>
    /// <param name="here">The weather over the player now, as the weather owner keeps it (sampled again only as the clock or the player moves).</param>
    internal SnowModule(IEngineContext? engine, ProductStore? store, SaveIdentity identity, WeatherField field, Func<double> hours,
        Func<Vector3> playerWorld, Func<EnvironmentSample> here, Action<double> depthChanged)
    {
        this.here = here ?? throw new ArgumentNullException(nameof(here));
        this.field = field ?? throw new ArgumentNullException(nameof(field));
        Cover = new SnowCover(field);
        this.hours = hours ?? throw new ArgumentNullException(nameof(hours));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.depthChanged = depthChanged ?? throw new ArgumentNullException(nameof(depthChanged));
        if (engine is not null && store is not null) slot = new(engine, store, SaveManifest.WeatherSnow, new SnowCoverCodec(identity));
    }

    internal SnowCover Cover { get; }

    /// <summary>The depth where the party stands.</summary>
    internal double Depth { get; private set; }

    /// <summary>The air where the party stands (product temperature units; freezing at <see cref="Water.Seasons.Freezing"/>), and the snow falling there, 0..1.</summary>
    internal double Air { get; private set; } = double.NaN;
    internal double Falling { get; private set; }

    /// <summary>The frost where the party stands, 0..1.</summary>
    internal double Frost { get; private set; }

    /// <summary>What surfaces show: the depth, and frost as a thin snow where there is less.</summary>
    internal double Shown => Math.Max(Depth, Frost * FrostShowsAsMetres);

    public void Start()
    {
        if (slot?.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: IReadOnlyList<SnowCell> saved }) Cover.Restore(saved, hours());
        Follow();
    }

    public void Update(ProductStep step) => Follow();

    public void Restart() => Follow();

    public void Dispose() => Save();

    /// <summary>What following the cover cost on the update, for the readout: the last and the worst, in milliseconds.</summary>
    private double lastFollowMs, worstFollowMs;
    /// <summary>What telling the surfaces the depth cost (the places' materials, and the stones' regrowth at a step), last and worst.</summary>
    private double lastTellMs, worstTellMs;

    private void Follow()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        double now = hours();
        Vector3 at = playerWorld();
        Cover.FollowAround(at.X, at.Z, now);
        Depth = Cover.DepthAt(at.X, at.Z, now);
        GatherFrost(at, now);
        if (double.IsNaN(toldDepth) || Math.Abs(Shown - toldDepth) >= TellStepMetres)
        {
            toldDepth = Shown;
            long telling = System.Diagnostics.Stopwatch.GetTimestamp();
            depthChanged(Shown);
            lastTellMs = System.Diagnostics.Stopwatch.GetElapsedTime(telling).TotalMilliseconds;
            worstTellMs = Math.Max(worstTellMs, lastTellMs);
        }

        lastFollowMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        worstFollowMs = Math.Max(worstFollowMs, lastFollowMs);
        if (double.IsNaN(savedHours) || Math.Abs(now - savedHours) >= SaveEveryHours) Save();
    }

    /// <summary>Frost eases toward its target by the game time passed: full in still freezing air, none above freezing or while snow falls.</summary>
    private void GatherFrost(Vector3 at, double now)
    {
        EnvironmentSample sample = here();
        double air = Cover.Air(at.X, at.Z, now, sample);
        Air = air;
        Falling = SnowCover.Snowfall(sample, air);
        bool freezing = air <= Water.Seasons.Freezing && Falling <= 0;
        double target = freezing ? 1 : 0;
        if (double.IsNaN(frostHours)) Frost = target;
        else
        {
            double passed = Math.Max(0, now - frostHours);
            Frost += (target - Frost) * (1 - Math.Exp(-passed / (freezing ? FrostHours : ThawHours)));
        }

        frostHours = now;
    }

    private void Save()
    {
        if (slot is null) return;
        slot.Save(Cover.Capture());
        savedHours = hours();
    }

    /// <summary>For diagnosis: lays (or, negative, takes) snow around the party at once.</summary>
    internal string Lay(double metres)
    {
        Vector3 at = playerWorld();
        Cover.Lay(at.X, at.Z, hours(), metres);
        toldDepth = double.NaN;
        Follow();
        return Readout();
    }

    internal string Readout()
    {
        Vector3 at = playerWorld();
        double now = hours();
        EnvironmentSample sample = field.Sample(at.X, at.Z, now);
        double air = Cover.Air(at.X, at.Z, now, sample);
        return string.Create(CultureInfo.InvariantCulture,
            $"snow depth={Depth:F3}m frost={Frost:F2} shown={Shown:F3}m fallen={Cover.FallenAt(at.X, at.Z, now):F3}m air={air:F3} freezing={Water.Seasons.Freezing:F2} season={Water.Seasons.At(now)} falling={SnowCover.Snowfall(sample, air):F2} ")
            + string.Create(CultureInfo.InvariantCulture,
            $"followMs={lastFollowMs:F3} worstFollowMs={worstFollowMs:F2} tellMs={lastTellMs:F2} worstTellMs={worstTellMs:F2} cells={Cover.Count} settled={Cover.Settled} steps={Cover.Stepped} stepMs={Cover.StepMs:F3} saved={savedHours:F1} restore={slot?.RestoreOutcome ?? "none"} saves={slot?.Saves ?? 0} failure={slot?.LastFailure ?? "none"}");
    }
}
