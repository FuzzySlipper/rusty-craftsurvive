using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>Live-debug adapters over <see cref="BlastModule"/>; it owns no state of its own.</summary>
public sealed class BlastDebugModule : IDebugCommandModule
{
    private readonly BlastModule blast;

    internal BlastDebugModule(BlastModule blast)
    {
        this.blast = blast ?? throw new ArgumentNullException(nameof(blast));
    }

    [DebugCommand("craft.blast.fire", Description = "Fires a charge at a cell: breaks the blocks within the radius that the charge is strong enough to break, as one transaction.")]
    public string Fire(long x, long y, long z, long radius) => blast.Fire(x, y, z, radius);

    [DebugCommand("craft.blast.readout", Description = "Reports charges fired, cells cleared, refusals, dust refusals, and the last charge's cost.")]
    public string Readout() => blast.Readout();
}
