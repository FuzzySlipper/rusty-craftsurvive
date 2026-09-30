using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The player's edits and their save. The overlay is restored at start; after an edit it is saved
/// once the world has been quiet for <see cref="SaveDelaySteps"/> Engine steps, and always on
/// dispose. A string of edits therefore costs one encode and one write, not one per edit.
/// </summary>
internal sealed class TerrainOverlayStore
{
    /// <summary>A quarter of a second at 60 Hz: long enough to fold a burst of edits into one save.</summary>
    internal const long SaveDelaySteps = 15;

    private readonly ProductSaveSlot<TerrainOverlaySnapshot> slot;
    private bool dirty;
    private long changedAtStep;

    internal TerrainOverlayStore(IEngineContext engine, ProductStore store, SaveIdentity identity)
    {
        slot = new ProductSaveSlot<TerrainOverlaySnapshot>(
            engine, store, SaveManifest.TerrainOverlay, new TerrainOverlayCodec(identity));
        Overlay = new TerrainOverlayState(identity.Seed);
    }

    internal TerrainOverlayState Overlay { get; }

    /// <summary>What happened to the saved overlay at start.</summary>
    internal string RestoreOutcome => slot.RestoreOutcome;

    internal long Saves => slot.Saves;

    /// <summary>The last save this store could not make, or none.</summary>
    internal string LastFailure => slot.LastFailure ?? "none";

    /// <summary>
    /// Reads the saved overlay. A save that does not match this world is discarded and kept as
    /// the key's backup, and the world starts from the generator alone.
    /// </summary>
    internal void Restore()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: TerrainOverlaySnapshot saved })
        {
            Overlay.Restore(saved);
        }
    }

    /// <summary>Records that the overlay changed at this step; the save follows once edits pause.</summary>
    internal void MarkChanged(long step)
    {
        dirty = true;
        changedAtStep = step;
    }

    /// <summary>Saves a changed overlay once it has been quiet long enough.</summary>
    internal void SaveIfDue(long step)
    {
        if (dirty && step - changedAtStep >= SaveDelaySteps)
        {
            Save();
        }
    }

    /// <summary>Saves a changed overlay now.</summary>
    internal void Flush()
    {
        if (dirty)
        {
            Save();
        }
    }

    /// <summary>
    /// A write refused because the key changed outside this session stays refused: the edits are
    /// still in the world, and the readout reports why they are not being saved.
    /// </summary>
    private void Save()
    {
        slot.Save(Overlay.Snapshot());
        dirty = false;
    }
}
