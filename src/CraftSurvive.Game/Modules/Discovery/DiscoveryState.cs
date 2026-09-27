using System.Globalization;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// What the player has learned about one place. The stage only ever rises, so the
/// first-seen tick is kept even when the site is later visited.
/// </summary>
/// <summary>
/// One found place. The ticks are session-relative: a restored entry keeps the ticks it was
/// saved with, while the module's own counter restarts each run, so ticks order places within
/// a session and are not comparable across one.
/// </summary>
internal readonly record struct DiscoveryEntry(
    long CellX,
    long CellZ,
    PoiKind Kind,
    long X,
    long Z,
    DiscoveryStage Stage,
    long FirstSeenTick,
    long LastTick)
{
    /// <summary>The place's name, derived from the key rather than stored beside it.</summary>
    internal string SiteId => PoiSite.IdFor(CellX, CellZ);

    internal string StageName => Stage.ToString();
}

/// <summary>
/// An immutable, canonically ordered copy of the journal. Canonical order is what makes
/// the encoded bytes a function of the facts rather than of the order they were learned,
/// so the same knowledge always saves to the same blob.
/// </summary>
internal sealed class DiscoverySnapshot
{
    private readonly DiscoveryEntry[] entries;

    internal DiscoverySnapshot(ulong seed, DiscoveryEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Seed = seed;
        if (entries.Length > PoiConstants.MaximumDiscoveryEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(entries),
                $"A journal holds at most {PoiConstants.MaximumDiscoveryEntries} places.");
        }

        this.entries = entries.OrderBy(entry => entry.CellX).ThenBy(entry => entry.CellZ).ToArray();
        for (int index = 0; index < this.entries.Length; index++)
        {
            DiscoveryEntry entry = this.entries[index];
            if (entry.Kind == PoiKind.None)
            {
                throw new ArgumentException("A journal entry must name a kind of place.", nameof(entries));
            }

            if (entry.Stage == DiscoveryStage.None)
            {
                throw new ArgumentException("A journal entry must record something learned.", nameof(entries));
            }

            if (index > 0 && this.entries[index - 1].CellX == entry.CellX
                && this.entries[index - 1].CellZ == entry.CellZ)
            {
                throw new ArgumentException("A journal names each place once.", nameof(entries));
            }
        }
    }

    /// <summary>The world this journal belongs to. A save carries it so it can be checked.</summary>
    internal ulong Seed { get; }

    internal DiscoveryEntry[] Entries => entries.ToArray();

    internal int Count => entries.Length;
}

/// <summary>
/// Sole owner of what the player has found.
///
/// It holds facts, not state that other systems derive from: a site's existence and
/// position come from the generation contract, and this records only that the player has
/// seen or reached it, and when. Two properties follow from that split. A journal entry
/// is small enough to keep every one of them, and it remains meaningful across sessions
/// because the identity it stores - the anchor cell - names the same place in the same
/// world rather than a handle that only exists while a scene is resident.
///
/// Nothing here throws on the update path. A journal that is full refuses new entries and
/// counts the refusals, because an exception raised inside a product update is not a lost
/// fact but a lost runtime.
/// </summary>
internal sealed class DiscoveryState
{
    private readonly SortedDictionary<(long CellX, long CellZ), DiscoveryEntry> entries = [];
    private readonly ulong seed;
    private long refused;
    private DiscoveryEntry? last;

    internal DiscoveryState(ulong seed) => this.seed = seed;

    internal int Count => entries.Count;

    internal int VisitedCount
    {
        get
        {
            int visited = 0;
            foreach (DiscoveryEntry entry in entries.Values)
            {
                if (entry.Stage == DiscoveryStage.Visited)
                {
                    visited++;
                }
            }

            return visited;
        }
    }

    internal int SeenCount => Count - VisitedCount;

    /// <summary>Entries the journal could not take because it was full.</summary>
    internal long Refused => refused;

    /// <summary>The most recent place learned, for the readout.</summary>
    internal DiscoveryEntry? Last => last;

    /// <summary>The entry for a place, by the anchor cell that names it.</summary>
    internal DiscoveryEntry? Find(long cellX, long cellZ) =>
        entries.TryGetValue((cellX, cellZ), out DiscoveryEntry entry) ? entry : null;

    /// <summary>
    /// Records that a place has been reached or seen. Returns whether anything changed,
    /// so a caller can tell a first sighting from a repeat. A lower stage never demotes a
    /// higher one: once a place has been stood at, seeing it from a ridge does not undo it.
    /// </summary>
    internal bool Notice(PoiSite site, DiscoveryStage stage, long tick)
    {
        if (stage == DiscoveryStage.None)
        {
            return false;
        }

        if (entries.TryGetValue((site.CellX, site.CellZ), out DiscoveryEntry existing))
        {
            if (existing.Stage >= stage)
            {
                return false;
            }

            DiscoveryEntry promoted = existing with { Stage = stage, LastTick = tick };
            entries[(site.CellX, site.CellZ)] = promoted;
            last = promoted;
            return true;
        }

        if (entries.Count >= PoiConstants.MaximumDiscoveryEntries)
        {
            refused++;
            return false;
        }

        DiscoveryEntry entry = new(site.CellX, site.CellZ, site.Kind, site.X, site.Z, stage, tick, tick);
        entries.Add((site.CellX, site.CellZ), entry);
        last = entry;
        return true;
    }

    internal DiscoverySnapshot Snapshot() => new(seed, entries.Values.ToArray());

    /// <summary>
    /// Replaces the journal with the facts a save recorded. Validation is strict and throws,
    /// because this runs at startup on the load path where the caller can discard a save
    /// that does not match this world - which is not true of the update path above.
    /// </summary>
    internal void Restore(DiscoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Seed != seed)
        {
            throw new InvalidOperationException("Stored journal belongs to a different world.");
        }

        entries.Clear();
        last = null;
        foreach (DiscoveryEntry entry in snapshot.Entries)
        {
            if (!entries.TryAdd((entry.CellX, entry.CellZ), entry))
            {
                throw new InvalidOperationException("A journal names each place once.");
            }

            if (last is null || entry.LastTick >= last.Value.LastTick)
            {
                last = entry;
            }
        }
    }

    /// <summary>
    /// The journal as one line, for the readout and the live lane. It says what has been
    /// learned and what is most recent, which is what makes discovery legible without
    /// asking a debug command for a specific site.
    /// </summary>
    internal string Readout()
    {
        string recent = last is DiscoveryEntry entry
            ? string.Create(CultureInfo.InvariantCulture,
                $"{entry.SiteId} {entry.Kind} {entry.StageName}@{entry.X},{entry.Z} tick={entry.LastTick}")
            : "none";
        return string.Create(CultureInfo.InvariantCulture,
            $"places={Count} visited={VisitedCount} seen={SeenCount} refused={refused} last={recent}");
    }
}