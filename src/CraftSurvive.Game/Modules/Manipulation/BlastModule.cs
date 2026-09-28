using System.Numerics;
using System.Diagnostics;
using System.Globalization;
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
    private readonly BlockEntityIndex entities;
    private BlastSequence? pending;
    private long fired;
    private long cleared;
    private long stagesApplied;
    private long refused;
    private long swept;
    private double worstStageMs;
    private readonly List<double> stageMs = [];
    private string lastOutcome = "none";

    internal BlastModule(TerrainWorld terrain, BlockEntityIndex entities)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(entities);
        this.terrain = terrain;
        this.entities = entities;
    }

    /// <summary>How many recent stage costs the readout keeps, oldest dropped first.</summary>
    private const int StageHistoryLength = 8;

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

        int stageCells = sequence.CellsRemaining;

        // The dust emission is switched off. The bisection is finished and it exonerates everything
        // the product authors: a descriptor carrying only SignalId, Visible, Anchor and BurstCount -
        // no curves, no collision, no visual - and then the same with Sprite removed entirely both
        // die with SIGSEGV on the pinned pair. So the fault is in EmitParticles itself or in a
        // precondition this product does not yet satisfy, not in anything BlastDust writes. That is
        // an upstream question, filed rather than worked around; the blast is complete without the
        // dust, and a charge that crashes the runtime is not an option.
        long started = Stopwatch.GetTimestamp();
        // A charge removes terrain rather than placing it - a blast opens a hole, and the policy's
        // cell count is a count of removed cells. Player overlap is not consulted here: the charge
        // is aimed by whoever fired it, and the rule for shooting your own feet off belongs to the
        // interaction that acquires a target, not to the mechanism that resolves one.
        //
        // A stage whose cells are already empty is *delivered*, not refused: an explosion whose
        // sphere overlaps air changes nothing there, and a charge is spent when its cells have
        // been handed to the route, not when they happened to alter the world. Only a result that
        // says the route would not take the edit at all abandons the charge.
        // The charge sweeps the entities in the cells it actually cleared, in the same step that
        // clears them. A detonation that opened a door should take the door with it: leaving an
        // entity standing in a cell the blast turned to air is the same orphan the build path
        // already refuses, arriving from the other direction.
        bool delivered = sequence.Advance(stage =>
        {
            bool applied = terrain.TryEditCells(stage, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, null)
                is TerrainWorldEditApplied or TerrainWorldEditNoChanges;
            if (applied)
            {
                swept += entities.Sweep(stage);
            }

            return applied;
        });
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsedMs > worstStageMs)
        {
            worstStageMs = elapsedMs;
        }

        // Every stage's cost, in order, because a single maximum cannot tell a cold first stage
        // apart from the steady-state price of a charge - and the budget has to be derived from
        // the second of those.
        stageMs.Add(elapsedMs);
        if (stageMs.Count > StageHistoryLength)
        {
            stageMs.RemoveAt(0);
        }

        if (!delivered)
        {
            lastOutcome = $"stage refused by the edit route after {stagesApplied} stage(s)";
            pending = null;
            return;
        }

        stagesApplied++;
        cleared += stageCells;
        if (!sequence.Pending)
        {
            lastOutcome = $"resolved {sequence.Admission.Cells} cells in {sequence.StagesApplied} transaction(s)";
            pending = null;
        }
    }

    /// <summary>
    /// A charge's identity as a seed: the same blast in the same place produces the same dust. The
    /// product's generation contract rests on draws being pure functions of a seed, and an effect
    /// that broke that habit would be the first thing in the product to do so.
    /// </summary>
    private static ulong ChargeSeed(CraftSurvive.Game.Modules.Terrain.VoxelAddress centre) =>
        ((ulong)(uint)centre.X << 42) ^ ((ulong)(uint)centre.Y << 21) ^ (ulong)(uint)centre.Z;

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
        lastOutcome = $"{plan.Admission.Disposition} {plan.Admission.Cells} cells, one transaction";
        return Readout();
    }

    [DebugCommand("craft.blast.readout", Description = "Reports charges fired, cells cleared, stages run, refusals, and the worst stage latency.")]
    public string Readout() =>
        $"blast fired={fired} pending={Pending} cleared={cleared} stages={stagesApplied} refused={refused} "
        + $"worstStageMs={worstStageMs:F2} stagesMs=[{string.Join(", ", stageMs.Select(ms => ms.ToString("F2", CultureInfo.InvariantCulture)))}] "
        + $"last={lastOutcome} editTiming[{terrain.LastEditTiming}]";
}
