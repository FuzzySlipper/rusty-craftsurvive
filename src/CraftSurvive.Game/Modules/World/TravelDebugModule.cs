using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.World;

/// <summary>Live checks of overland travel: its state, known places, travel events and the world's bounds (#9470, #9471, #9817).</summary>
public sealed class TravelDebugModule : IDebugCommandModule
{
    private readonly Func<string> readout;
    private readonly Func<string, string> forceEvent;
    private readonly Func<double, double, string> bound;
    private readonly Func<double, double, string> plan;
    private readonly Func<string, string> planTo;

    internal TravelDebugModule(Func<string> readout, Func<string, string> forceEvent, Func<double, double, string> bound, Func<double, double, string> plan,
        Func<string, string> planTo)
    {
        this.planTo = planTo;
        this.readout = readout;
        this.forceEvent = forceEvent;
        this.bound = bound;
        this.plan = plan;
    }

    [DebugCommand("craft.travel.bound", Description = "Reads which side of the world's bounds a world point (x, z) lies on, and how far it is from the continent's land.")]
    public string Bound(double x, double z) => bound(x, z);

    [DebugCommand("craft.travel.plan", Description = "Assisted: plans the party's route to a world point (x, z) with the map open, as clicking the map does; reads the plan's message and where the route ends.")]
    public string Plan(double x, double z) => plan(x, z);

    [DebugCommand("craft.travel.to", Description = "Assisted: plans the party's route to a known place by its key (e.g. town:Brackwater) with the map open, as the map's place list does.")]
    public string To(string place) => planTo(place);

    [DebugCommand("craft.travel.readout", Description = "Reads the party's travel state, fatigue, event risk and any waiting event, the home marker and how many places are known.")]
    public string Readout() => readout();

    [DebugCommand("craft.travel.event", Description = "Assisted: raises a travel event now (encounter, discovery, weather or hazard) with the map open, to check its presentation and outcomes.")]
    public string Event(string kind) => forceEvent(kind);
}
