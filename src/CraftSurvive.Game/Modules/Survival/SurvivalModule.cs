using System.Globalization;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Survival;

/// <summary>
/// The one owner of the player's survival tracks: hunger and air. Each update it advances them by
/// <see cref="SurvivalRules"/> at the world's difficulty, gives back or takes away health through
/// the player's vitals, publishes the tracks to the UI and saves them. It notices a hurt by the
/// health it did not take itself, and a respawn by the vitals coming back from defeat.
/// </summary>
internal sealed class SurvivalModule : IProductModule
{
    /// <summary>Five seconds at 60 Hz, as the player's continuation saves.</summary>
    internal const long SaveIntervalSteps = 300;

    private readonly PlayerController player;
    private readonly WorldConditionsModule conditions;
    private readonly ProductUiPublisher ui;
    private readonly ProductSaveSlot<SurvivalState> slot;
    private SurvivalState state = SurvivalState.Fresh;
    private int lastHealth = -1;
    private long lastSaveStep = long.MinValue;
    private SurvivalUiFacts? published;
    private SurvivalHarm lastHarm = SurvivalHarm.None;
    private long regained;
    private long lost;

    /// <summary>How far the nearest awake hostile creature stands from the player, for the rule against sleeping beside one.</summary>
    private readonly Func<double> nearestHostileMetres;

    internal SurvivalModule(IEngineContext engine, ProductStore store, SaveIdentity identity, PlayerController player,
        WorldConditionsModule conditions, ProductUiPublisher ui, Func<double> nearestHostileMetres)
    {
        this.nearestHostileMetres = nearestHostileMetres ?? throw new ArgumentNullException(nameof(nearestHostileMetres));
        ArgumentNullException.ThrowIfNull(engine);
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        slot = new ProductSaveSlot<SurvivalState>(engine, store, SaveManifest.PlayerSurvival, new SurvivalCodec(identity));
    }

    internal SurvivalState State => state;

    public void Start()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: SurvivalState saved })
        {
            state = saved;
        }

        lastHealth = player.Vitals.State.Health;
        Publish();
    }

    public void Update(ProductStep step)
    {
        int health = player.Vitals.State.Health;
        if (lastHealth <= 0 && health > 0)
        {
            state = SurvivalRules.Respawned(state);
        }

        SurvivalStep next = SurvivalRules.Advance(
            state,
            new SurvivalFacts(health, player.Vitals.MaximumHealth, player.HeadSubmerged, player.Sprinting, Hurt: health < lastHealth),
            conditions.Difficulty,
            step.ElapsedSeconds);
        state = next.State;
        if (next.Regained > 0)
        {
            player.Vitals.Heal(next.Regained);
            regained += next.Regained;
        }

        if (next.Lost > 0)
        {
            player.Vitals.TakeHit(next.Lost, step.Step);
            lost += next.Lost;
            lastHarm = next.Cause;
        }

        lastHealth = player.Vitals.State.Health;
        Publish();
        if (lastSaveStep == long.MinValue)
        {
            lastSaveStep = step.Step;
        }
        else if (step.Step - lastSaveStep >= SaveIntervalSteps)
        {
            slot.Save(state);
            lastSaveStep = step.Step;
        }
    }

    /// <summary>A fresh session starts fed and breathing, as a fresh player does.</summary>
    public void Restart()
    {
        state = SurvivalState.Fresh;
        lastHealth = player.Vitals.State.Health;
        Publish();
    }

    public void Dispose() => SaveNow();

    /// <summary>Saves the tracks at once, for a caller that advanced them outside an update (map travel).</summary>
    internal void SaveNow() => slot.Save(state);

    /// <summary>How many rests have been refused, so a caller can tell a refusal from its answer.</summary>
    internal long RestsRefused { get; private set; }

    /// <summary>
    /// Sleeps until morning: only at night, only when no awake hostile creature is near, and never
    /// while defeated. The night passes at once; health comes back while there is food to pay for it.
    /// </summary>
    /// <param name="meal">Food eaten from what is carried as hunger comes during the night, if any.</param>
    internal string Rest(Func<SurvivalState, SurvivalState>? meal = null)
    {
        if (player.Vitals.IsDown)
        {
            return RefuseRest("rest refused: the player is down");
        }

        if (!conditions.IsNight)
        {
            return RefuseRest("rest refused: it is not night");
        }

        double nearest = nearestHostileMetres();
        if (nearest < SurvivalRules.RestSafetyMetres)
        {
            return RefuseRest(string.Create(CultureInfo.InvariantCulture, $"rest refused: a hostile creature is {nearest:F0} m away"));
        }

        double seconds = Sky.WorldClock.SecondsUntil(conditions.Time.DayFraction, Sky.WorldClock.WakingFraction);
        return Sleep(seconds, meal);
    }

    /// <summary>A rest of a set length at any hour (a daytime halt on a journey), with the same safety rule as sleeping.</summary>
    internal string RestFor(double seconds, Func<SurvivalState, SurvivalState>? meal = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        if (player.Vitals.IsDown) return RefuseRest("rest refused: the player is down");
        double nearest = nearestHostileMetres();
        if (nearest < SurvivalRules.RestSafetyMetres)
            return RefuseRest(string.Create(CultureInfo.InvariantCulture, $"rest refused: a hostile creature is {nearest:F0} m away"));
        return Sleep(seconds, meal);
    }

    /// <summary>
    /// Time spent marching on the map: the ordinary rules applied over those seconds, so the journey
    /// costs food, carried food is eaten as hunger comes, and an empty stomach hurts.
    /// </summary>
    internal void Journey(double seconds, Func<SurvivalState, SurvivalState>? meal = null)
    {
        if (seconds <= 0) return;
        Apply(SurvivalRules.March(state, player.Vitals.State.Health, player.Vitals.MaximumHealth, conditions.Difficulty, seconds, meal));
        Publish();
    }

    /// <summary>Game hours of every rest or sleep as it happens, so the expedition's fatigue recovers whichever way it rested (#9553).</summary>
    internal event Action<double>? Slept;

    private string Sleep(double seconds, Func<SurvivalState, SurvivalState>? meal)
    {
        SurvivalStep rested = SurvivalRules.Rest(state, player.Vitals.State.Health, player.Vitals.MaximumHealth, conditions.Difficulty, seconds, meal);
        Apply(rested);
        conditions.Pass(seconds);
        slot.Save(state);
        Publish();
        Slept?.Invoke(seconds / Sky.WorldClock.DaySeconds * 24d);
        return string.Create(CultureInfo.InvariantCulture,
            $"slept {seconds / Sky.WorldClock.DaySeconds * 24d:F1} hours: regained {rested.Regained}, food now {state.Satiety:F0}%");
    }

    private void Apply(SurvivalStep step)
    {
        state = step.State;
        if (step.Regained > 0)
        {
            player.Vitals.Heal(step.Regained);
            regained += step.Regained;
        }
        if (step.Lost > 0)
        {
            player.Vitals.TakeHit(step.Lost, 0);
            lost += step.Lost;
            lastHarm = step.Cause;
        }
        lastHealth = player.Vitals.State.Health;
    }

    private string RefuseRest(string reason)
    {
        RestsRefused++;
        return reason;
    }

    /// <summary>Eats something worth some nourishment.</summary>
    internal void Eat(double nourishment)
    {
        state = SurvivalRules.Eat(state, nourishment);
        Publish();
    }

    /// <summary>Sets the tracks directly, for a live check of hunger and drowning.</summary>
    internal string Set(double satiety, double breath)
    {
        if (!double.IsFinite(satiety) || satiety is < 0d or > SurvivalRules.MaximumSatiety
            || !double.IsFinite(breath) || breath is < 0d or > SurvivalRules.MaximumBreathSeconds)
        {
            return $"refused: satiety must be 0 to {SurvivalRules.MaximumSatiety} and breath 0 to {SurvivalRules.MaximumBreathSeconds}";
        }

        state = state with { Satiety = satiety, Breath = breath };
        Publish();
        return Readout();
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"survival satiety={state.Satiety:F1} breath={state.Breath:F1} calm={state.SinceHurtSeconds:F1}s regained={regained} lost={lost} lastHarm={lastHarm} difficulty={conditions.Difficulty} health={player.Vitals.State.Health}/{player.Vitals.MaximumHealth} submerged={player.HeadSubmerged} restore={slot.RestoreOutcome} saves={slot.Saves} failure={slot.LastFailure ?? "none"}");

    /// <summary>Publishes the tracks when what a player would read of them has changed.</summary>
    private void Publish()
    {
        SurvivalUiFacts facts = new(
            Math.Ceiling(state.Satiety),
            Math.Ceiling(state.Breath),
            SurvivalRules.MaximumBreathSeconds,
            lastHarm == SurvivalHarm.None ? string.Empty : lastHarm.ToString().ToLowerInvariant());
        if (published != facts)
        {
            published = facts;
            ui.PublishSurvival(facts);
        }
    }
}
