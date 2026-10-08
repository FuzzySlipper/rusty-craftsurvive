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
    internal static SaveKey WorldMap { get; } =
        new("world/map", "world/map.backup", "WorldCatalog", 0x50414D57, 1);
    /// <summary>The player's voxel edits: the overlay over the generated world.</summary>
    internal static SaveKey TerrainOverlay { get; } =
        new("terrain/overlay", "terrain/overlay.backup", "TerrainOverlayStore", 0x4F54_5343, 1);

    /// <summary>The places the player has seen or reached.</summary>
    internal static SaveKey DiscoveryJournal { get; } =
        new("discovery/journal", "discovery/journal.backup", "DiscoveryModule", 0x4A4F_5552, 1);

    /// <summary>The doors, containers and lights standing in edited cells.</summary>
    internal static SaveKey BlockEntities { get; } =
        new("build/entities", "build/entities.backup", "BlockEntityStore", 0x544E_4542, 1);

    /// <summary>The construction pieces the player has built (#9729).</summary>
    internal static SaveKey BuildPieces { get; } =
        new("build/pieces", "build/pieces.backup", "BuildPieceStore", 0x5345_4350, 1);

    /// <summary>Where the player stands, which way they look, and what they have earned.</summary>
    internal static SaveKey PlayerContinuation { get; } =
        new("player/continuation", "player/continuation.backup", "PlayerContinuationStore", 0x5952_4C50, 1);

    /// <summary>The time of day and the difficulty: the world's conditions.</summary>
    internal static SaveKey WorldConditions { get; } =
        new("world/conditions", "world/conditions.backup", "WorldConditionsModule", 0x444E_4F43, 1);

    /// <summary>How fed the player is and how much air they hold.</summary>
    internal static SaveKey PlayerSurvival { get; } =
        new("player/survival", "player/survival.backup", "SurvivalModule", 0x5649_5653, 1);

    /// <summary>What the player carries.</summary>
    internal static SaveKey PlayerInventory { get; } =
        new("player/inventory", "player/inventory.backup", "InventoryModule", 0x5652_4E49, 2);

    /// <summary>Where the expedition calls home on the map.</summary>
    internal static SaveKey TravelHome { get; } =
        new("travel/home", "travel/home.backup", "HomeMarkerStore", 0x454D_4F48, 1);

    /// <summary>Where the expedition's sled stands and what it holds.</summary>
    internal static SaveKey TravelSled { get; } =
        new("travel/sled", "travel/sled.backup", "SledStore", 0x4445_4C53, 1);

    internal static IReadOnlyList<SaveKey> All { get; } =
        [WorldMap, TerrainOverlay, DiscoveryJournal, BlockEntities, BuildPieces, PlayerContinuation, WorldConditions, PlayerSurvival, PlayerInventory, TravelHome, TravelSled];
}
