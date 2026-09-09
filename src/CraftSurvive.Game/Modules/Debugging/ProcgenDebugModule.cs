using CraftSurvive.Game.Modules.LevelGeneration;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

public sealed class ProcgenDebugModule : IDebugCommandModule
{
    private readonly ProcgenWorkbench workbench;
    internal ProcgenDebugModule(ProcgenWorkbench workbench) => this.workbench = workbench;
    [DebugCommand("craft.procgen.readout", Description = "Reads the applied resolved candidate, model witness and scoped mesh checks.")]
    public string Readout() => workbench.Readout();
    [DebugCommand("craft.procgen.load", Description = "Loads an offline resolved artifact from admitted procgen/*.json content.")]
    public string Load(string path) => workbench.QueueLoad(path);
    [DebugCommand("craft.procgen.enter", Description = "Starts the physical run at the candidate entrance with the shortcut closed.")]
    public string Enter(long revision) => workbench.QueueAction("enter", revision);
    [DebugCommand("craft.procgen.reset", Description = "Resets the model witness and physical run for this candidate revision.")]
    public string Reset(long revision) => workbench.QueueAction("reset", revision);
    [DebugCommand("craft.procgen.step", Description = "Steps the abstract action witness without moving the player or changing world geometry.")]
    public string Step(long revision) => workbench.QueueAction("step", revision);
    [DebugCommand("craft.procgen.counterexample", Description = "Selects a failing model trace without altering the physical world.")]
    public string Counterexample(long revision) => workbench.QueueAction("counterexample", revision);
    [DebugCommand("craft.procgen.witness", Description = "Selects the completing model witness without altering the physical world.")]
    public string Witness(long revision) => workbench.QueueAction("witness", revision);
    [DebugCommand("craft.procgen.use", Description = "Interacts with a nearby motif station using the physical state.")]
    public string Use(long revision) => workbench.QueueAction("use", revision);
}
