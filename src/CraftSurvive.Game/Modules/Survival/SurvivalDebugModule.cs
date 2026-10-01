using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Survival;

/// <summary>Live-debug adapter over the survival tracks: read them, or set them to check hunger and drowning.</summary>
public sealed class SurvivalDebugModule : IDebugCommandModule
{
    private readonly SurvivalModule survival;

    internal SurvivalDebugModule(SurvivalModule survival) =>
        this.survival = survival ?? throw new ArgumentNullException(nameof(survival));

    [DebugCommand("craft.survival.readout", Description = "Reads hunger, breath, health regained and lost, and how the survival save is going.")]
    public string Readout() => survival.Readout();

    [DebugCommand("craft.survival.set", Description = "Sets satiety (0-100) and breath (0-20 seconds), to check hunger and drowning live.")]
    public string Set(double satiety, double breath) => survival.Set(satiety, breath);
}
