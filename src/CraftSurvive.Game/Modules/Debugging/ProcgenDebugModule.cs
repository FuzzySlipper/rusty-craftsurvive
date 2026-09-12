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
    [DebugCommand("craft.procgen.breach", Description = "Introduces a side aperture in one closed gate without changing candidate intent; resets the physical run.")]
    public string Breach(long revision) => workbench.QueueAction("breach", revision);
    [DebugCommand("craft.procgen.repair", Description = "Restores the intended closed gates and resets the physical run.")]
    public string Repair(long revision) => workbench.QueueAction("repair", revision);
    [DebugCommand("craft.procgen.check", Description = "Checks the current realized mesh against candidate/state route and separation requirements.")]
    public string Check(long revision) => workbench.QueueAction("check", revision);
    [DebugCommand("craft.procgen.bank", Description = "Lists up to sixteen staged candidates with offline progression diagnostics; does not load a world.")]
    public string Bank() => workbench.Bank();
    [DebugCommand("craft.procgen.reference", Description = "Pins the current plan, state and diagnostics as a frozen comparison observation.")]
    public string Reference(long revision) => workbench.QueueAction("reference", revision);
    [DebugCommand("craft.procgen.comparison", Description = "Reads the frozen baseline, resolved decision differences and applicable semantic repairs.")]
    public string Comparison() => workbench.Comparison();
    [DebugCommand("craft.procgen.mend", Description = "Applies one canonical semantic repair, retains its parent, and rebuilds at the entrance.")]
    public string Mend(long revision, string operation) => workbench.QueueMend(revision, operation);
    [DebugCommand("craft.procgen.export", Description = "Exports the last applied semantic repair receipt with full parent/result artifacts for offline retention.")]
    public string Export() => workbench.ExportRepair();
}
