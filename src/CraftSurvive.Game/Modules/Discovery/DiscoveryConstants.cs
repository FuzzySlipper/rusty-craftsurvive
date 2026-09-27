namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// The journal's stored form: how it is named in the product's persistence scope, and
/// the fixed shape of its bytes.
///
/// The journal is a sibling key in the same scope as the terrain overlay rather than a
/// section inside it. They are different state families with different owners - voxel
/// overrides belong to the world, discovery belongs to the places the contract defines -
/// and the envelope policy the world already applies (an identity in the header, an
/// all-or-nothing decode, a discarded save kept as one backup) is what each key gets.
/// </summary>
internal static class DiscoveryConstants
{
    /// <summary>The journal's key inside the product's persistence scope.</summary>
    internal const string PersistenceKey = "discovery/journal";

    /// <summary>Where a journal that did not match this world is kept, as the one backup.</summary>
    internal const string BackupPersistenceKey = "discovery/journal.backup";

    /// <summary>The stored schema. A change to the entry layout raises it.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>
    /// The stored form's magic, so a blob from another key is rejected rather than
    /// decoded as a journal.
    /// </summary>
    internal const uint Magic = 0x4A4F_5552;

    /// <summary>Magic, schema, generation version, seed, entry count, and a fingerprint.</summary>
    internal const int HeaderBytes = 32;

    /// <summary>Cells, position, kind, stage, and both ticks, written at fixed offsets.</summary>
    internal const int EntryBytes = 51;

    /// <summary>
    /// The largest journal this product will store. It bounds both what is written and
    /// what is accepted on load, so a corrupt count cannot ask for an arbitrary allocation.
    /// </summary>
    internal const int MaximumJournalBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How often the world asks what the player can see, in update ticks. A player at a
    /// walking pace covers a fraction of a metre per tick and the notice radius is 128 m,
    /// so asking every tick would spend the work of eighty asks to answer the same thing.
    /// </summary>
    internal const int NoticeIntervalTicks = 10;
    /// <summary>
    /// How many rows a kind-filtered query returns. It is small on purpose, so the answer is small enough to
    /// arrive whole: the count is always reported, so a caller that needs the rest can narrow
    /// the radius rather than read every site in range.
    /// </summary>
    internal const int MaximumFindRows = 12;

}
