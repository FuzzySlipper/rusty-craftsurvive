using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>Live-debug adapter over the horizon backdrop (#9779): whether it is linked, what it has streamed, and where it is sunk.</summary>
public sealed class HorizonDebugModule : IDebugCommandModule
{
    private readonly Func<HorizonBackdrop> horizon;
    private readonly Func<Sky.DayNightSky> sky;

    internal HorizonDebugModule(Func<HorizonBackdrop> horizon, Func<Sky.DayNightSky> sky)
    {
        this.horizon = horizon ?? throw new ArgumentNullException(nameof(horizon));
        this.sky = sky ?? throw new ArgumentNullException(nameof(sky));
    }

    [DebugCommand("craft.horizon.fog", Description = "Tuning: sets the open fog's density while the horizon is shown (exponential, per metre).")]
    public string Fog(double density) => sky().SetHorizonFog((float)density);

    [DebugCommand("craft.horizon.readout", Description = "Reads the horizon backdrop: linked, scale, chunks streamed, the sunk zone's centre and the far field's reach.")]
    public string Readout() => horizon().Readout();

    [DebugCommand("craft.horizon.show", Description = "Assisted: shows (1) or hides (0) the horizon backdrop, for comparison captures.")]
    public string Show(long shown)
    {
        horizon().Enabled = shown != 0;
        return horizon().Readout();
    }
}
