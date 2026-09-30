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
    // The request window is the chunks the player can reach soon; the retained ring is one wider,
    // so crossing a chunk boundary reuses chunks instead of regenerating them.
    internal const int RequestedChunkRadius = 2;
    internal const int RetainedChunkRadius = 3;
    /// <summary>
    /// How much residency work one update may do. More operations fill the window in fewer updates
    /// but make the worst update several times longer; four keeps streaming updates near the
    /// steady-state cost. Chosen by measurement, recorded with its pair and machine in Den (#8895).
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
    internal const string UiStreamName = "craftsurvive.terrain";
    internal const string UiStreamContract = "craftsurvive.terrain.v1";
    internal const uint CollisionGroupAll = uint.MaxValue;
    internal const uint CollisionMaskAll = uint.MaxValue;
    internal const double EditReach = 8d;

    internal const float TerrainRoughness = 0.9f;
    internal const float MaterialAlpha = 1f;
    internal const float NoEmission = 0f;

    /// <summary>How many cells the player's edits may override; admission refuses an edit past it.</summary>
    internal const int MaximumOverlayEntries = 65_536;
}
