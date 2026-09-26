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
    internal const uint GenerationVersion = 2;

    // Surface features are decided per anchor cell, never per chunk: a tree that
    // overhangs a boundary must be the same tree whichever chunk asks first.
    internal const int FeatureCellSize = 8;
    internal const int FeatureCellOneIn = 24;
    internal const int TreeMinimumHeight = 3;
    internal const int TreeHeightRange = 3;
    internal const int TreeCanopyRadius = 2;
    internal const int FeatureCacheLimit = 4096;

    internal const int ChunkEdgeLength = 16;
    internal const int ChunkPlaneLength = ChunkEdgeLength * ChunkEdgeLength;
    internal const int ChunkVolume = ChunkPlaneLength * ChunkEdgeLength;
    internal const int RequestedChunkRadius = 1;
    internal const int RetainedChunkRadius = 2;
    internal const int MaximumResidencyOperationsPerTick = 16;
    internal const int MaximumResidentChunks = 64;

    /// <summary>
    /// The world's water level. It is part of the generation contract rather than a
    /// per-chunk decision, so every chunk agrees about where the sea ends.
    /// </summary>
    internal const int WaterLevel = 2;

    /// <summary>
    /// The finite world's authored edges. The floor is bedrock rather than an
    /// invisible plane, and the wall is the same material as the world's own rock so
    /// the border reads as terrain instead of as a bug. Nothing can be placed or
    /// blasted through either: bedrock refuses both by its block properties.
    /// </summary>
    internal const int WorldFloorThickness = 1;
    internal const int WorldWallThickness = 2;
    internal const int WorldWallTop = 6;

    internal const int TerrainDepth = 9;
    internal const int TerrainSummitHeight = 12;
    internal const int TerrainHeadroom = 16;
    internal const int MinimumTerrainHeight = -2;
    internal const int TopsoilSlopeMaximum = 2;
    internal const int SubsoilSlopeMaximum = 3;
    internal const int SubsoilDepthMaximum = 3;
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
    internal const uint PersistenceSchemaVersion = OverlaySchemaVersion;
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

    internal const ulong CoordinateXMultiplier = 0x9E37_79B9_7F4A_7C15UL;
    internal const ulong CoordinateZMultiplier = 0xBF58_476D_1CE4_E5B9UL;
    internal const ulong CoordinateHashMultiplier = 0x94D0_49BB_1331_11EBUL;
    internal const ulong RollingNoiseSalt = 0xA076_1D64_78BD_642FUL;
    internal const ulong DetailNoiseSalt = 0xE703_7ED1_A0B4_28DBUL;
    internal const ulong LargeNoiseSalt = 0x8EBC_6AF0_9C88_C6E3UL;
    internal const int HashFractionShift = 11;
    internal const ulong HashFractionMaximum = (1UL << 53) - 1UL;
    internal const int CoordinateRotation = 29;
    internal const int FirstHashShift = 30;
    internal const int SecondHashShift = 27;
    internal const int FinalHashShift = 31;

    internal const int BroadNoiseScale = 20;
    internal const int RollingNoiseScale = 9;
    internal const int DetailNoiseScale = 4;
    internal const int LargeNoiseScale = 48;
    internal const double HeightBase = 1d;
    internal const double One = 1d;
    internal const double BroadWeight = 4d;
    internal const double BroadCenter = 0.5d;
    internal const double BroadDeviationWeight = 8d;
    internal const double RidgeWeight = 3d;
    internal const double DetailDeviationWeight = 2d;
    internal const double LargeWeight = 3d;
    internal const double Two = 2d;
    internal const double SmoothstepFirstFactor = 3d;

    internal const int LandmarkHeightFirst = 8;
    internal const int LandmarkHeightSecond = 6;
    internal const int LandmarkHeightThird = 10;
}
