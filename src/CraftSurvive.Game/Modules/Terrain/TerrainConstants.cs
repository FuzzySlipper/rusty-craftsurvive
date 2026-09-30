namespace CraftSurvive.Game.Modules.Terrain;

internal static class TerrainConstants
{
    internal const ulong DefaultSeed = 0x4352_4146_5453_5552UL;
    /// <summary>
    /// The finite world's extent in voxels per side: 10,240 is 640 chunks of 16, or
    /// about 105 km2 at one-metre voxels. Generation is chunk-local and residency is
    /// on demand, so the extent costs nothing until a chunk near it is requested.
    /// </summary>
    internal const int DefaultSize = 10_240;
    internal const int MinimumSize = 32;
    /// <summary>
    /// The largest extent the product accepts. A finite world of about 100 km2 is
    /// 10,240 voxels per side; the ceiling is set well above it so a larger world is
    /// a declared contract change rather than a silent one.
    /// </summary>
    internal const int MaximumSize = 65_536;

    internal const int ChunkEdgeLength = 16;
    internal const int ChunkPlaneLength = ChunkEdgeLength * ChunkEdgeLength;
    internal const int ChunkVolume = ChunkPlaneLength * ChunkEdgeLength;
    // Sized against the measured live figure in `docs/live-proofs.md`: generation
    // costs about 2.2 ms per 16-cubed chunk on the current machine, so a 5x5 request
    // window is roughly 55 ms of generation work spread across the bounded
    // operations below, and the retained ring is one wider so a boundary crossing
    // reuses chunks instead of regenerating them.
    internal const int RequestedChunkRadius = 2;
    internal const int RetainedChunkRadius = 3;
    /// <summary>
    /// How much residency work one update may do. Measured: at 16 the first update
    /// filled the whole 5x5 window at once and cost 189.5 ms - a visible hitch as the
    /// world appears - while steady-state streaming settled at 12.7 ms. Four ops keeps
    /// the worst update in the same range as the steady state and spends a few more
    /// updates filling the window, which a player experiences as the world arriving
    /// smoothly rather than stuttering once.
    /// </summary>
    internal const int MaximumResidencyOperationsPerTick = 4;
    internal const int MaximumResidentChunks = 64;

    // Material slots are block ids: see Content/BlockRegistry.cs for the floor and
    // BlockId for the stable numbering. These names stay because generation and
    // tests read them, and they must always equal the registry's slot.
    internal const ushort GrassMaterial = (ushort)Content.BlockId.Grass;
    internal const ushort DirtMaterial = (ushort)Content.BlockId.Dirt;
    internal const ushort StoneMaterial = (ushort)Content.BlockId.Stone;
    internal const ushort EmptyMaterial = (ushort)Content.BlockId.Air;
    internal const ushort MaximumMaterial = 4_095;
    internal const long MaximumCoordinateMagnitude = 1_000_000;
    internal const int MaximumBrushRadius = 2;

    internal const double VoxelSize = 1d;
    internal const uint VoxelChunkSize = ChunkEdgeLength;
    internal const string PersistenceScope = "craftsurvive";
    internal const string OverlayPersistenceKey = "terrain/overlay";

    /// <summary>
    /// Where the previous overlay generation is kept. One backup, so a discarded
    /// save is recoverable without keeping a history.
    /// </summary>
    internal const string OverlayBackupPersistenceKey = "terrain/overlay.backup";
    internal const string UiStreamName = "craftsurvive.terrain";
    internal const string UiStreamContract = "craftsurvive.terrain.v1";
    internal const uint CollisionGroupAll = uint.MaxValue;
    internal const uint CollisionMaskAll = uint.MaxValue;
    internal const double EditReach = 8d;

    internal const float TerrainRoughness = 0.9f;
    internal const float MaterialAlpha = 1f;
    internal const float NoEmission = 0f;

    internal const int OverlaySchemaVersion = 1;
    internal const int MaximumOverlayEntries = 65_536;
    internal const int MaximumOverlayBytes = 8 * 1024 * 1024;
    internal const int OverlayHeaderBytes = 32;
    internal const int OverlayEntryBytes = 26;
    internal const uint OverlayMagic = 0x4F54_5343;
    internal const ulong OverlayFingerprintOffset = 0xCBF2_9CE4_8422_2325UL;
    internal const ulong OverlayFingerprintPrime = 0x0000_0100_0000_01B3UL;

}
