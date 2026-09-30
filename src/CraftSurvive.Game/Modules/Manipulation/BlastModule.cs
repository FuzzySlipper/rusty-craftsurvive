using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// Fires charges. A fired charge is planned at once and resolved on the next update: the dust and
/// debris go out first, so the cloud is already in flight when the world changes, and the cleared
/// cells then go through the world's edit route as one transaction. Block entities standing in the
/// cleared cells are swept in the same step.
/// </summary>
internal sealed class BlastModule : IProductModule
{
    /// <summary>A charge's centre voxel, offset to the middle of the cell for the dust anchor.</summary>
    private const float CellCentre = 0.5f;

    private const int MaximumRadius = 16;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private readonly BlockEntityIndex entities;
    private BlastCharge? pending;
    private long fired;
    private long cleared;
    private long refused;
    private long swept;
    private long dustRefused;
    private double lastResolveMs;
    private string lastOutcome = "none";
    private string lastDustFailure = "none";

    internal BlastModule(IEngineContext engine, TerrainWorld terrain, WorldFrame frame, BlockEntityIndex entities)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(entities);
        this.engine = engine;
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

        EmitDust(charge);
        long started = Stopwatch.GetTimestamp();

        // A cell that turns out to be empty already is delivered, not refused: only a result that
        // says the route would not take the edit at all abandons the charge.
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
        swept += entities.Sweep(charge.Cleared);
        lastOutcome = $"cleared {charge.Cleared.Count} of {charge.Admission.Cells} cells";
    }

    /// <summary>
    /// The dust is presentation: a refused emission is counted and reported, and the charge still
    /// resolves. The charge's cell is global; the emitter's anchor and the debris' collision box
    /// are positions, so they are placed in the session's local frame.
    /// </summary>
    private void EmitDust(BlastCharge charge)
    {
        Vector3 centre = DustCentre(charge.Centre);
        ulong seed = BlastDust.ChargeSeed(charge.Centre);
        try
        {
            engine.Presentation.EmitParticles(BlastDust.Smoke(centre, terrain.AtlasSprite, seed));
            engine.Presentation.EmitParticles(BlastDust.Debris(centre, terrain.AtlasSprite, seed));
        }
        catch (EngineCallException exception)
        {
            dustRefused++;
            lastDustFailure = exception.Message;
        }
    }

    /// <summary>Where a charge's dust is anchored: the middle of its centre cell, in the local frame.</summary>
    internal Vector3 DustCentre(VoxelAddress cell) =>
        frame.ToLocal(cell.X + CellCentre, cell.Y + CellCentre, cell.Z + CellCentre);

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
        lastOutcome = $"fired: breaks {charge.Cleared.Count} of {charge.Admission.Cells} cells";
        return Readout();
    }

    internal string Readout() =>
        FormattableString.Invariant(
            $"blast fired={fired} pending={Pending} cleared={cleared} refused={refused} swept={swept} ")
        + FormattableString.Invariant(
            $"dustRefused={dustRefused} lastResolveMs={lastResolveMs:F2} last={lastOutcome} dustFailure={lastDustFailure} ")
        + (pending is BlastCharge charge ? FormattableString.Invariant($"dustAnchor={DustCentre(charge.Centre)} ") : string.Empty)
        + $"editTiming[{terrain.LastEditTiming}]";
}
