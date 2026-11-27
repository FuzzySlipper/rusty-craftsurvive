using System.Collections.Concurrent;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A cache of the generator's pure decisions that the update thread and the chunk workers read at once (#10907): a read takes
/// no lock, and a full cache lets go of its oldest quarter rather than all of it, so the decisions made ahead for a chunk on its
/// way to a worker mostly stay. Letting any go is always safe: every value is a pure function of its key and the contract.
/// </summary>
internal sealed class SharedCache<TKey, TValue>(int limit) where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> values = new();
    private readonly ConcurrentQueue<TKey> order = new();
    private readonly Lock trimming = new();
    private int trims;
    // How many values are kept, counted here (#11126): a concurrent dictionary's own count takes every one of its locks, and the
    // chunk workers and the exporter's crossing workers keeping decisions at once queued on it.
    private int count;

    internal int Count => Volatile.Read(ref count);

    /// <summary>How many times the cache filled and let its oldest quarter go.</summary>
    internal int Trims => Volatile.Read(ref trims);

    internal bool ContainsKey(TKey key) => values.ContainsKey(key);

    internal bool TryGetValue(TKey key, out TValue value) => values.TryGetValue(key, out value!);

    /// <summary>Keeps a decision. Two threads deciding the same key decide the same value, so the first kept stands.</summary>
    internal void Set(TKey key, TValue value)
    {
        if (!values.TryAdd(key, value))
        {
            return;
        }

        order.Enqueue(key);
        if (Interlocked.Increment(ref count) >= limit)
        {
            Trim();
        }
    }

    internal void Clear()
    {
        lock (trimming)
        {
            values.Clear();
            order.Clear();
            Volatile.Write(ref count, 0);
        }
    }

    private void Trim()
    {
        lock (trimming)
        {
            if (Volatile.Read(ref count) < limit)
            {
                return;
            }

            for (int i = 0; i < limit / 4 && order.TryDequeue(out TKey? oldest); i++)
            {
                if (values.TryRemove(oldest, out _))
                {
                    Interlocked.Decrement(ref count);
                }
            }

            trims++;
        }
    }
}
