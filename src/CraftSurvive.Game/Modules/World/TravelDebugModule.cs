using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.World;

/// <summary>Live checks of overland travel: its state, known places and travel events (#9470, #9471).</summary>
public sealed class TravelDebugModule : IDebugCommandModule
{
    private readonly Func<string> readout;
    private readonly Func<string, string> forceEvent;

    internal TravelDebugModule(Func<string> readout, Func<string, string> forceEvent)
    {
        this.readout = readout;
        this.forceEvent = forceEvent;
    }

    [DebugCommand("craft.travel.readout", Description = "Reads the party's travel state, fatigue, event risk and any waiting event, the home marker and how many places are known.")]
    public string Readout() => readout();

    [DebugCommand("craft.travel.event", Description = "Assisted: raises a travel event now (encounter, discovery, weather or hazard) with the map open, to check its presentation and outcomes.")]
    public string Event(string kind) => forceEvent(kind);
}
