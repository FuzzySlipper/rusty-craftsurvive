using System.Buffers;
using Rusty.Engine;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One saved key's reads and writes through the product's one store, for the owner of that
/// state. It restores by the shared rule, keeps a discarded save as the key's backup, and writes
/// only over the revision it last read or wrote: if anything else changed the key in between, the
/// write is refused and reported instead of overwriting it.
/// </summary>
internal sealed class ProductSaveSlot<TState>
{
    private readonly IEngineContext engine;
    private readonly ProductStore store;
    private readonly IProductStateCodec<TState> codec;

    /// <summary>The stored revision this session last read or wrote; zero while the key is absent.</summary>
    private ulong revision;

    internal ProductSaveSlot(IEngineContext engine, ProductStore store, SaveKey key, IProductStateCodec<TState> codec)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    internal SaveKey Key { get; }

    /// <summary>What the last restore decided, for readouts.</summary>
    internal string RestoreOutcome { get; private set; } = "not restored";

    internal long Saves { get; private set; }

    /// <summary>The last write this slot could not make, or null.</summary>
    internal string? LastFailure { get; private set; }

    /// <summary>
    /// Reads the key and decides what it restores to. A store that cannot be read at all throws:
    /// a product that cannot read its own save should not start and quietly forget it.
    /// </summary>
    internal SaveRestoreDecision<TState> Restore()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store.Store, Key.Key));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        byte[] bytes = info.Present ? engine.Persistence.ReadBlobBytes(blob).ToArray() : [];
        revision = info.Present ? info.Revision : 0;
        SaveRestoreDecision<TState> decision = SaveRestore.Decide(info.Present, bytes, codec);
        if (decision.KeepsBackup)
        {
            // The backup is a courtesy, not a condition: failing to keep it must not stop the start.
            try
            {
                engine.Persistence.Save(new PersistenceSaveRequest(
                    store.Store, Key.BackupKey, PersistenceRevisionGuard.Any, 0, bytes));
            }
            catch (EngineCallException failure)
            {
                LastFailure = $"backup not kept: {failure.Message}";
            }
        }

        RestoreOutcome = decision.Describe();
        return decision;
    }

    /// <summary>Writes the state over the revision this session holds. Returns whether it was written.</summary>
    internal bool Save(in TState state)
    {
        ArrayBufferWriter<byte> payload = new();
        codec.Encode(in state, payload);
        PersistenceSaveReceipt receipt = engine.Persistence.Save(new PersistenceSaveRequest(
            store.Store,
            Key.Key,
            revision == 0 ? PersistenceRevisionGuard.Absent : PersistenceRevisionGuard.Exact,
            revision,
            payload.WrittenMemory));
        if (receipt.Outcome == PersistenceSaveOutcome.RevisionConflict)
        {
            LastFailure = $"{Key.Key} was changed outside this session (stored revision {receipt.Revision}, held {revision}); not overwritten";
            return false;
        }

        revision = receipt.Revision;
        Saves++;
        return true;
    }

    /// <summary>Whether the key is stored now, and how many bytes it holds.</summary>
    internal (bool Present, int Bytes) Probe()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store.Store, Key.Key));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        return info.Present ? (true, checked((int)info.PayloadLen)) : (false, 0);
    }
}
