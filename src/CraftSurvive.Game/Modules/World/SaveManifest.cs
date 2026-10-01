namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One persisted key: where it lives in the product's scope, where a discarded copy is kept,
/// which owner writes it, and the magic and schema its stored form begins with.
/// </summary>
internal sealed record SaveKey(string Key, string BackupKey, string Owner, uint Magic, int Schema);

/// <summary>
/// Every key the product saves, in one place. Each owner reads its key from here, so a new saved
/// state family is added here first, and nothing is persisted that this list does not name.
///
/// Every stored form shares <see cref="SaveEnvelope"/>'s header: the world identity (generator
/// version and seed) it belongs to, a record count and a fingerprint. Under the settled policy -
/// worlds are disposable - a save that does not decode for this world is discarded, the world
/// regenerates, and the discarded bytes are kept as the key's one backup.
///
/// The generated-chunk cache is not a save: it lives in its own scope, is keyed by the
/// generator's fingerprint, and can be deleted at any time without losing anything.
/// </summary>
internal static class SaveManifest
{
    /// <summary>The player's voxel edits: the overlay over the generated world.</summary>
    internal static SaveKey TerrainOverlay { get; } =
        new("terrain/overlay", "terrain/overlay.backup", "TerrainOverlayStore", 0x4F54_5343, 1);

    /// <summary>The places the player has seen or reached.</summary>
    internal static SaveKey DiscoveryJournal { get; } =
        new("discovery/journal", "discovery/journal.backup", "DiscoveryModule", 0x4A4F_5552, 1);

    /// <summary>The doors, containers and lights standing in edited cells.</summary>
    internal static SaveKey BlockEntities { get; } =
        new("build/entities", "build/entities.backup", "BlockEntityStore", 0x544E_4542, 1);

    /// <summary>Where the player stands, which way they look, and what they have earned.</summary>
    internal static SaveKey PlayerContinuation { get; } =
        new("player/continuation", "player/continuation.backup", "PlayerContinuationStore", 0x5952_4C50, 1);

    /// <summary>The time of day and the difficulty: the world's conditions.</summary>
    internal static SaveKey WorldConditions { get; } =
        new("world/conditions", "world/conditions.backup", "WorldConditionsModule", 0x444E_4F43, 1);

    internal static IReadOnlyList<SaveKey> All { get; } =
        [TerrainOverlay, DiscoveryJournal, BlockEntities, PlayerContinuation, WorldConditions];
}
