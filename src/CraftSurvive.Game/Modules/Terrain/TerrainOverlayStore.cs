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

    private readonly IEngineContext engine;
    private readonly ProductStore store;
    private readonly ulong seed;
    private bool dirty;
    private long changedAtStep;

    internal TerrainOverlayStore(IEngineContext engine, ProductStore store, ulong seed)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.seed = seed;
        Overlay = new TerrainOverlayState(seed);
    }

    internal TerrainOverlayState Overlay { get; }

    /// <summary>What happened to the saved overlay at start.</summary>
    internal string RestoreOutcome { get; private set; } = "none";

    internal long Saves { get; private set; }

    /// <summary>
    /// Reads the saved overlay. Worlds are disposable under the settled save policy: a save that
    /// does not match this world is discarded and the world regenerates, keeping the previous
    /// generation as one backup.
    /// </summary>
    internal void Restore()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store.Store, TerrainConstants.OverlayPersistenceKey));
        if (!engine.Persistence.DescribeBlob(blob).Present)
        {
            RestoreOutcome = "absent";
            return;
        }

        byte[] bytes = engine.Persistence.ReadBlobBytes(blob).ToArray();
        try
        {
            Overlay.Restore(TerrainOverlayCodec.Decode(seed, bytes));
            RestoreOutcome = "restored";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            if (bytes.Length > 0)
            {
                Write(TerrainConstants.OverlayBackupPersistenceKey, bytes);
            }

            RestoreOutcome = $"discarded: {exception.Message}";
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

    private void Save()
    {
        Write(TerrainConstants.OverlayPersistenceKey, TerrainOverlayCodec.Encode(Overlay.Snapshot()));
        dirty = false;
        Saves++;
    }

    private void Write(string key, byte[] bytes) => engine.Persistence.Save(new PersistenceSaveRequest(
        store.Store, key, PersistenceRevisionGuard.Any, 0, bytes));
}
