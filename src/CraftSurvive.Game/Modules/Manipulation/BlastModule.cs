using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// Fires charges and resolves them one bounded stage per update.
///
/// This is the slice's blast event at its smallest useful size: plan the sphere, ask the policy
/// what it costs, and hand the world at most one stage each update so no frame is held longer
/// than the budget the policy is written against. The presentation that covers the remainder of
/// the update is the next piece of the slice; this is the part that has to be true first, because
/// a visual effect over an unbounded stall is a distraction rather than a covering.
/// </summary>
public sealed class BlastModule : IDebugCommandModule
{
    private readonly TerrainWorld terrain;
    private BlastSequence? pending;
    private long fired;
    private long cleared;
    private long stagesApplied;
    private long refused;
    private double worstStageMs;
    private string lastOutcome = "none";

    internal BlastModule(TerrainWorld terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        this.terrain = terrain;
    }

    /// <summary>True while a charge still has stages to run.</summary>
    internal bool Pending => pending?.Pending ?? false;

    internal long Fired => fired;

    /// <summary>
    /// Advances the pending charge by at most one stage. Called once per product update, which is
    /// what bounds a large charge: the policy decides how many transactions it takes, and this
    /// spends one of them here.
    /// </summary>
    internal void Update()
    {
        BlastSequence? sequence = pending;
        if (sequence is null || !sequence.Pending)
        {
            pending = null;
            return;
        }

        long started = Stopwatch.GetTimestamp();
        // A charge removes terrain rather than placing it - a blast opens a hole, and the policy's
        // cell count is a count of removed cells. Player overlap is not consulted here: the charge
        // is aimed by whoever fired it, and the rule for shooting your own feet off belongs to the
        // interaction that acquires a target, not to the mechanism that resolves one.
        bool applied = sequence.Advance(stage =>
            terrain.TryEditCells(stage, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, null)
                is TerrainWorldEditApplied);
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsedMs > worstStageMs)
        {
            worstStageMs = elapsedMs;
        }

        if (!applied)
        {
            lastOutcome = $"stage refused by the edit route after {stagesApplied} stage(s)";
            pending = null;
            return;
        }

        stagesApplied++;
        cleared += sequence.Admission.CellsPerStage;
        if (!sequence.Pending)
        {
            lastOutcome = $"resolved {sequence.Admission.Cells} cells in {sequence.StagesApplied} stage(s)";
            pending = null;
        }
    }

    [DebugCommand("craft.blast.fire", Description = "Fires a charge at a cell: removes a sphere of the given radius there, staged to fit the per-blast budget.")]
    public string Fire(long x, long y, long z, long radius)
    {
        BlastSequence plan = BlastSequence.Plan(
            new VoxelAddress((int)x, (int)y, (int)z),
            (int)Math.Clamp(radius, 0, 16));
        if (!plan.Admission.Applies)
        {
            refused++;
            lastOutcome = $"refused: {plan.Admission.Cells} cells is past the {BlastPolicy.MaximumCells}-cell maximum";
            return Readout();
        }

        pending = plan;
        fired++;
        lastOutcome = $"{plan.Admission.Disposition} {plan.Admission.Cells} cells over {plan.Admission.Stages} stage(s)";
        return Readout();
    }

    [DebugCommand("craft.blast.readout", Description = "Reports charges fired, cells cleared, stages run, refusals, and the worst stage latency.")]
    public string Readout() =>
        $"blast fired={fired} pending={Pending} cleared={cleared} stages={stagesApplied} refused={refused} "
        + $"worstStageMs={worstStageMs:F2} last={lastOutcome}";
}
