using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// Fires charges. A fired charge is planned at once and resolved on the next update: its cue goes
/// out first, so the cloud is already in flight when the world changes, and the cleared cells then
/// go through the world's edit route as one transaction. Block entities standing in the
/// cleared cells are swept in the same step.
/// </summary>
internal sealed class BlastModule : IProductModule
{
    /// <summary>A charge's centre voxel, offset to the middle of the cell for where it is seen and heard.</summary>
    private const float CellCentre = 0.5f;

    private const int MaximumRadius = 16;

    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private readonly BlockEntityIndex entities;
    private readonly Cues cues;
    private BlastCharge? pending;
    private long fired;
    private long cleared;
    private long refused;
    private long swept;
    private double lastResolveMs;
    private string lastOutcome = "none";

    internal BlastModule(TerrainWorld terrain, WorldFrame frame, BlockEntityIndex entities, Cues cues)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(entities);
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        this.terrain = terrain;
        this.frame = frame;
        this.entities = entities;
    }

    /// <summary>True while a fired charge waits for the next update.</summary>
    internal bool Pending => pending is not null;

    internal long Fired => fired;

    public void Start()
    {
    }

    /// <summary>A fresh session: a charge fired but not yet resolved is dropped.</summary>
    public void Restart() => pending = null;

    public void Dispose() => pending = null;

    /// <summary>Resolves the pending charge, if any. Called once per product update.</summary>
    public void Update(ProductStep time)
    {
        BlastCharge? charge = pending;
        pending = null;
        if (charge is null)
        {
            return;
        }

        // The blast is seen and heard before the edit, so the cloud is still billowing when the
        // cleared cells disappear and settles after: one event.
        cues.RaiseAt(Cue.Blast, DustCentre(charge.Centre), BlastDust.ChargeIdentity(charge.Centre));
        long started = Stopwatch.GetTimestamp();

        // A cell that turns out to be empty already is delivered, not refused: only a result that
        // says the route would not take the edit at all abandons the charge.
        int entitiesBefore = entities.Count;
        TerrainWorldEditResult result = charge.Cleared.Count == 0
            ? TerrainWorldEditResult.CastMiss
            : terrain.TryEditCells(charge.Cleared, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, null);
        lastResolveMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (charge.Cleared.Count == 0)
        {
            lastOutcome = $"reached {charge.Admission.Cells} cells and broke none";
            return;
        }

        if (result is not (TerrainWorldEditApplied or TerrainWorldEditNoChanges))
        {
            lastOutcome = $"refused by the edit route: {TerrainWorldEditResult.Format(result)}";
            return;
        }

        cleared += charge.Cleared.Count;
        swept += entitiesBefore - entities.Count;
        lastOutcome = $"cleared {charge.Cleared.Count} of {charge.Admission.Cells} cells";
    }

    /// <summary>
    /// Where a charge is seen and heard: the middle of its centre cell. The cell is global; the cue
    /// is a position, so it is placed in the session's local frame.
    /// </summary>
    internal Vector3 DustCentre(VoxelAddress cell) =>
        frame.ToLocal(cell.X + CellCentre, cell.Y + CellCentre, cell.Z + CellCentre);

    /// <summary>What the last request came to, for the player-facing UI.</summary>
    internal string LastOutcome => lastOutcome;

    /// <summary>A charge was admitted at this world point with this reach: built pieces within it fall (#9729).</summary>
    internal event Action<Vector3, float>? ChargeAdmitted;

    internal string Fire(long x, long y, long z, long radius)
    {
        BlastCharge charge = BlastCharge.Plan(
            new VoxelAddress((int)x, (int)y, (int)z),
            (int)Math.Clamp(radius, 0, MaximumRadius),
            terrain.MaterialAt);
        if (!charge.Admission.Applies)
        {
            refused++;
            lastOutcome = $"refused: {charge.Admission.Cells} cells is past the {BlastPolicy.MaximumCells}-cell maximum";
            return Readout();
        }

        pending = charge;
        fired++;
        ChargeAdmitted?.Invoke(new Vector3(x + (float)CellCentre, y + (float)CellCentre, z + (float)CellCentre), (float)charge.Radius + (float)CellCentre);
        lastOutcome = $"fired: breaks {charge.Cleared.Count} of {charge.Admission.Cells} cells";
        return Readout();
    }

    internal string Readout() =>
        FormattableString.Invariant(
            $"blast fired={fired} pending={Pending} cleared={cleared} refused={refused} swept={swept} ")
        + FormattableString.Invariant(
            $"lastResolveMs={lastResolveMs:F2} last={lastOutcome} ")
        + (pending is BlastCharge charge ? FormattableString.Invariant($"cueAt={DustCentre(charge.Centre)} ") : string.Empty)
        + $"editTiming[{terrain.LastEditTiming}]";
}
