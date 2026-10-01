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

    internal SurvivalModule(IEngineContext engine, ProductStore store, SaveIdentity identity, PlayerController player,
        WorldConditionsModule conditions, ProductUiPublisher ui)
    {
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

    public void Dispose() => slot.Save(state);

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
