using System.Globalization;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Survival;

/// <summary>
/// The one owner of the world's conditions: it advances the clock on Engine step time, shows the
/// sky and light for it (closing the view into water's murk while the player's eyes are under),
/// keeps the difficulty, publishes both to the UI and saves them. Survival and encounters read the
/// time and difficulty from here.
/// </summary>
internal sealed class WorldConditionsModule : IProductModule
{
    /// <summary>Five seconds at 60 Hz, as the player's continuation saves.</summary>
    internal const long SaveIntervalSteps = 300;

    private const int MinutesPerDay = 24 * 60;

    /// <summary>The difficulties a player can choose, by name, in order.</summary>
    private static readonly string Difficulties = string.Join(',', Enum.GetNames<Difficulty>().Select(name => name.ToLowerInvariant()));

    private readonly DayNightSky sky;
    private readonly Func<bool> eyesSubmerged;
    private readonly ProductUiPublisher ui;
    private readonly ProductSaveSlot<WorldConditionsState> slot;
    private WorldConditionsState state = WorldConditionsState.Fresh;
    private long lastSaveStep = long.MinValue;
    private long publishedMinute = long.MinValue;

    internal WorldConditionsModule(IEngineContext engine, ProductStore store, SaveIdentity identity, DayNightSky sky, Func<bool> eyesSubmerged, ProductUiPublisher ui)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.sky = sky ?? throw new ArgumentNullException(nameof(sky));
        this.eyesSubmerged = eyesSubmerged ?? throw new ArgumentNullException(nameof(eyesSubmerged));
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        slot = new ProductSaveSlot<WorldConditionsState>(engine, store, SaveManifest.WorldConditions, new WorldConditionsCodec(identity));
    }

    internal WorldTime Time => state.Time;

    internal Difficulty Difficulty => state.Difficulty;

    internal bool IsNight => WorldClock.IsNight(state.DayFraction);

    public void Start()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: WorldConditionsState saved })
        {
            state = saved;
        }

        Show();
    }

    public void Update(ProductStep step)
    {
        WorldTime time = WorldClock.Advance(state.Time, step.ElapsedSeconds);
        state = state with { Day = time.Day, DayFraction = time.DayFraction };
        sky.Submerged(eyesSubmerged());
        Show();
        if (lastSaveStep == long.MinValue)
        {
            lastSaveStep = step.Step;
        }
        else if (step.Step - lastSaveStep >= SaveIntervalSteps)
        {
            Save(step.Step);
        }
    }

    internal string SetWindStrength(float strength) => sky.SetWindStrength(strength);

    internal string SetGrade(float temperature, float tint, float contrast, float saturation, float exposure) =>
        sky.SetGrade(temperature, tint, contrast, saturation, exposure);

    /// <summary>A fresh play session keeps the world's time and difficulty; only the sky is shown again.</summary>
    public void Restart() => Show(force: true);

    public void Dispose() => slot.Save(state);

    /// <summary>Lets some seconds of play pass at once, as a rest does.</summary>
    internal void Pass(double seconds) => Pass(seconds, save: true);

    /// <summary>
    /// Lets seconds of play pass; map travel advances the clock every update and saves only when
    /// the journey stops.
    /// </summary>
    internal void Pass(double seconds, bool save)
    {
        WorldTime time = WorldClock.Advance(state.Time, seconds);
        state = state with { Day = time.Day, DayFraction = time.DayFraction };
        Show(force: true);
        if (save) slot.Save(state);
    }

    /// <summary>Sets the hour of the current day, for a live check of night and day.</summary>
    internal string SetHour(double hour)
    {
        if (!double.IsFinite(hour) || hour < 0d || hour >= 24d)
        {
            return $"refused: the hour must be 0 to below 24, not {hour}";
        }

        state = state with { DayFraction = hour / 24d };
        Show(force: true);
        slot.Save(state);
        return Readout();
    }

    /// <summary>How many difficulty changes were refused, so a caller can tell a refusal from its answer.</summary>
    internal long DifficultyRefused { get; private set; }

    /// <summary>Sets the difficulty by name: gentle, normal or harsh.</summary>
    internal string SetDifficulty(string name)
    {
        if (!Enum.TryParse(name, ignoreCase: true, out Difficulty chosen) || !Enum.IsDefined(chosen) || int.TryParse(name, out _))
        {
            DifficultyRefused++;
            return $"refused: \"{name}\" is not a difficulty ({string.Join(", ", Enum.GetNames<Difficulty>()).ToLowerInvariant()})";
        }

        state = state with { Difficulty = chosen };
        Show(force: true);
        slot.Save(state);
        return $"difficulty is {chosen.ToString().ToLowerInvariant()}";
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"world time={WorldClock.Describe(state.Time)} fraction={state.DayFraction:F4} daylight={WorldClock.Daylight(state.DayFraction):F3} night={IsNight} underwater={sky.ViewSubmerged} difficulty={state.Difficulty} restore={slot.RestoreOutcome} saves={slot.Saves} failure={slot.LastFailure ?? "none"}");

    private void Save(long step)
    {
        slot.Save(state);
        lastSaveStep = step;
    }

    /// <summary>Shows the sky and, when the clock's minute has turned, publishes the time.</summary>
    private void Show(bool force = false)
    {
        sky.Show(state.Time);
        long minute = (state.Day * MinutesPerDay) + (long)Math.Floor(state.DayFraction * MinutesPerDay);
        if (force || minute != publishedMinute)
        {
            publishedMinute = minute;
            ui.PublishConditions(new ConditionsUiFacts(
                WorldClock.Describe(state.Time),
                WorldClock.Daylight(state.DayFraction),
                IsNight,
                state.Difficulty.ToString().ToLowerInvariant(),
                Difficulties));
        }
    }
}
