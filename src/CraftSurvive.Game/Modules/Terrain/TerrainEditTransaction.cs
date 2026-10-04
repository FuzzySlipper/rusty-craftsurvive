namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>What one edit came to, before the refreshes the world does around it.</summary>
internal abstract record TerrainEditTransactionOutcome;

/// <summary>Admission refused the edit; nothing reached the Engine.</summary>
internal sealed record TerrainEditRefused(TerrainEditRejected Rejection) : TerrainEditTransactionOutcome;

/// <summary>The Engine found nothing to change; the overlay is untouched.</summary>
internal sealed record TerrainEditUnchanged : TerrainEditTransactionOutcome;

/// <summary>The Engine applied the edit and the overlay recorded it.</summary>
internal sealed record TerrainEditRecorded(TerrainOverlayReceipt Receipt) : TerrainEditTransactionOutcome;

/// <summary>
/// The order every edit follows, as a pure function over the one Engine step: admission first -
/// bounds, the player's body and the overlay's capacity - then the Engine, only for an admitted
/// edit, then the overlay, only for an edit the Engine accepted. Because the overlay is checked
/// before the Engine is asked, the scene and the save cannot disagree about an edit.
/// </summary>
internal static class TerrainEditTransaction
{
    /// <param name="applyToEngine">Applies the admitted edits as one Engine transaction; returns whether anything changed.</param>
    internal static TerrainEditTransactionOutcome Run(TerrainEditRequest request, Func<VoxelAddress, bool>? playerOverlaps,
        TerrainOverlayState overlay, Func<TerrainEditAccepted, bool> applyToEngine,
        Action<IReadOnlyList<TerrainVoxelEdit>>? committed = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(applyToEngine);
        TerrainEditAdmissionResult admission = TerrainEditAdmission.Admit(request, playerOverlaps, overlay);
        if (admission is TerrainEditRejected rejected)
        {
            return new TerrainEditRefused(rejected);
        }

        TerrainEditAccepted accepted = (TerrainEditAccepted)admission;
        TerrainEditTransactionOutcome outcome = applyToEngine(accepted)
            ? new TerrainEditRecorded(overlay.Apply(accepted))
            : new TerrainEditUnchanged();
        // NoChanges is still an accepted final cell state. Reconcile dependent product state
        // only after Engine admission and overlay recording, never for a refused/failed edit.
        committed?.Invoke(accepted.Edits);
        return outcome;
    }
}
