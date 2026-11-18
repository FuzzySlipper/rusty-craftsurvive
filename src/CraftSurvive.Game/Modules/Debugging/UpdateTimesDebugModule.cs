using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

/// <summary>Live-debug adapter over the walking updates' durations (#10889).</summary>
public sealed class UpdateTimesDebugModule : IDebugCommandModule
{
    private readonly Func<World.UpdateTimes> times;

    internal UpdateTimesDebugModule(Func<World.UpdateTimes> times) => this.times = times ?? throw new ArgumentNullException(nameof(times));

    [DebugCommand("craft.updates", Description = "Reads how long the last walking updates took on the update thread (median, p95, p99, worst and how many went over a 60 Hz frame); reset clears them, to measure a walk on its own.")]
    public string Updates(string action = "")
    {
        if (action == "reset")
        {
            times().Reset();
            return "update times cleared";
        }

        return times().Readout();
    }
}
