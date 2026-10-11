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

    [DebugCommand("craft.horizon.readout", Description = "Reads the horizon backdrop: linked, scale, the heightfield's tiles, vertices, held bytes and sampling, whether it is settled, the sunk zone's centre and the far field's reach.")]
    public string Readout() => horizon().Readout();

    [DebugCommand("craft.horizon.peaks", Description = "Assisted: the highest map ground in each of eight compass sectors within the given kilometres, with distance and bearing, for aimed captures.")]
    public string Peaks(double kilometres) => horizon().Peaks(kilometres);

    [DebugCommand("craft.horizon.exaggerate", Description = "Captures only: multiplies the horizon's heights above the player's ground (1 to 20; 1 is true scale) so a gentle map's ranges read beside the map view.")]
    public string Exaggerate(double factor) => horizon().Exaggerate(factor);

    [DebugCommand("craft.horizon.hold", Description = "Measuring only: 0 destroys the horizon's heightfield (samples, meshes, draws), 1 builds it again; the host's memory difference is its cost.")]
    public string Hold(long held) => horizon().Hold(held != 0);

    [DebugCommand("craft.horizon.tune", Description = "Tuning (#9848): the middle and coarsest rings' reaches in kilometres and the tiles committed an update; the heightfield is made again.")]
    public string Tune(double midKilometres, double coarsestKilometres, long commits) => horizon().Tune(midKilometres, coarsestKilometres, commits);

    [DebugCommand("craft.horizon.show", Description = "Assisted: shows (1) or hides (0) the horizon backdrop, for comparison captures.")]
    public string Show(long shown)
    {
        horizon().Enabled = shown != 0;
        return horizon().Readout();
    }
}
