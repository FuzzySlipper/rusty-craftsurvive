using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>Live-debug adapter over the river stage (#9878): read it whole, at a column, or the reaches about a point.</summary>
public sealed class RiverStageDebugModule : IDebugCommandModule
{
    /// <summary>The most reaches one listing names.</summary>
    private const int MostListed = 12;

    private readonly Func<RiverStageModule> stageSource;
    private readonly Func<WaterModel> waterSource;
    private readonly Func<double, double, int> reachAt;

    internal RiverStageDebugModule(Func<RiverStageModule> stage, Func<WaterModel> water, Func<double, double, int> reachAt)
    {
        stageSource = stage ?? throw new ArgumentNullException(nameof(stage));
        waterSource = water ?? throw new ArgumentNullException(nameof(water));
        this.reachAt = reachAt ?? throw new ArgumentNullException(nameof(reachAt));
    }

    [DebugCommand("craft.water.stage", Description = "Reads the river stage (#9878) and, at a world column, its reach's flow, stage over the usual, flood, ice and dry, with the water owner's sample there.")]
    public string Stage(double x, double z)
    {
        WaterSample s = waterSource().Sample(x, z);
        return FormattableString.Invariant($"{stageSource().Readout(reachAt(x, z))} sample=top:{s.Top:F2},bed:{s.Bed:F2},depth:{s.Depth:F2},body:{s.Body},ice:{s.Ice}");
    }

    [DebugCommand("craft.water.reaches", Description = "Lists the walked reaches with a mean flow between least and most (m³/s) passing within metres of a world point (#9878), largest first: where, mean and bankfull flow, flow and stage now, and the crossing's state.")]
    public string Reaches(double x, double z, double metres, double leastFlow, double mostFlow) => stageSource().NearbyReadout(x, z, metres, leastFlow, mostFlow, MostListed);
}
