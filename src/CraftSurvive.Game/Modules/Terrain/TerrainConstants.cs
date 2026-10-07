namespace CraftSurvive.Game.Modules.Terrain;

internal static class TerrainConstants
{
    internal const ulong DefaultSeed = 0x4352_4146_5453_5552UL;
    /// <summary>
    /// The configured finite extent in voxels per side, not a gameplay area target.
    /// Generation is chunk-local and residency is on demand.
    /// </summary>
    internal const int DefaultSize = 10_240;
    internal const int MinimumSize = 32;
    /// <summary>
    /// The largest extent the product accepts; raising it is a declared generator
    /// contract change rather than an implicit requirement for infinite generation.
    /// </summary>
    /// <summary>The largest world: a continent (#9549); regional worlds stop at 65,536.</summary>
    internal const int MaximumSize = 450_000;

    internal const int ChunkEdgeLength = 16;
    internal const int ChunkPlaneLength = ChunkEdgeLength * ChunkEdgeLength;
    internal const int ChunkVolume = ChunkPlaneLength * ChunkEdgeLength;
    // The request window is the ground the player sees: the drawn world ends at its edge, under
    // the distance fog (#9544). The retained ring is one wider, so crossing a chunk boundary
    // reuses chunks instead of regenerating them. Beyond 48 m the Engine draws coarse meshes
    // (TerrainPresentation.CoarseBeyondMetres), so the far rings cost half their triangles.
    internal const int RequestedChunkRadius = 8;
    internal const int RetainedChunkRadius = 9;
    /// <summary>
    /// How much residency work one update may do. More operations fill the window in fewer updates
    /// but make the worst update several times longer. Four kept streaming updates near the
    /// steady-state cost at a two-chunk window (#8895); the eight-chunk window admits twice as
    /// many per update so a boundary crossing's strip of columns fills in under a second.
    /// </summary>
    internal const int MaximumResidencyOperationsPerTick = 8;
    /// <summary>
    /// Residency keeps each column's surface band rather than its full height, so the cap is
    /// sized for steep ground: about five banded chunks per column across the retained ring,
    /// so the whole request window is always admitted (tests/TerrainResidency checks it).
    /// </summary>
    internal const int MaximumResidentChunks = 2_000;
    /// <summary>Chunks above and below the player kept in every column, so digging and climbing stay loaded.</summary>
    internal const int PlayerStoreyChunks = 1;

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
    internal const uint CollisionGroupAll = uint.MaxValue;
    internal const uint CollisionMaskAll = uint.MaxValue;
    internal const double EditReach = 8d;

    internal const float TerrainRoughness = 0.9f;
    internal const float MaterialAlpha = 1f;
    internal const float NoEmission = 0f;

    /// <summary>How many cells the player's edits may override; admission refuses an edit past it.</summary>
    internal const int MaximumOverlayEntries = 65_536;
}
