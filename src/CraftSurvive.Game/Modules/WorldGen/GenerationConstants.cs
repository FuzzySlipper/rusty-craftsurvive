namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The generator's tuning: how high the ground rises, how the noise is mixed, where water
/// and trees stand, and how the finite world is bounded. Every value here shapes generated
/// output, so changing one changes the world - and the generator's golden fingerprint, which
/// a managed check pins per version.
/// </summary>
internal static class GenerationConstants
{
    // Surface features are decided per anchor cell, never per chunk: a tree that
    // overhangs a boundary must be the same tree whichever chunk asks first.
    internal const int FeatureCellSize = 8;
    internal const int FeatureCellOneIn = 24;
    internal const int TreeMinimumHeight = 3;
    internal const int TreeHeightRange = 3;
    internal const int TreeCanopyRadius = 2;
    internal const int FeatureCacheLimit = 4096;
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
    private const int WorldWallClearance = 6;
    internal const int WorldWallTop = (int)(WorldMap.MaximumElevation + WorldMap.LocalReliefLimit) + WorldWallClearance;
    /// <summary>
    /// How far below the local ground a structure may cut. The character has no climb reach
    /// and steps one block, so a deeper hole is one a player can walk into and not walk out
    /// of - a trap rather than a route. A way in is therefore cut to a stepped depth, and the
    /// depth of a real descent belongs to the slice that owns the load transition.
    /// </summary>
    internal const long MaximumStructureStepBelowGround = 1;
    internal const int TerrainDepth = 9;
    internal const int TerrainHeadroom = 16;
    internal const int MinimumTerrainHeight = -2;
    internal const int TopsoilSlopeMaximum = 2;
    internal const int SubsoilSlopeMaximum = 3;
    internal const int SubsoilDepthMaximum = 3;
    internal const ulong CoordinateXMultiplier = 0x9E37_79B9_7F4A_7C15UL;
    internal const ulong CoordinateZMultiplier = 0xBF58_476D_1CE4_E5B9UL;
    internal const ulong CoordinateHashMultiplier = 0x94D0_49BB_1331_11EBUL;
    internal const ulong RollingNoiseSalt = 0xA076_1D64_78BD_642FUL;
    internal const ulong DetailNoiseSalt = 0xE703_7ED1_A0B4_28DBUL;
    internal const int HashFractionShift = 11;
    internal const ulong HashFractionMaximum = (1UL << 53) - 1UL;
    internal const int CoordinateRotation = 29;
    internal const int FirstHashShift = 30;
    internal const int SecondHashShift = 27;
    internal const int FinalHashShift = 31;
    internal const int BroadNoiseScale = 20;
    internal const int RollingNoiseScale = 9;
    internal const int DetailNoiseScale = 4;
    internal const double BroadWeight = 4d;
    internal const double BroadCenter = 0.5d;
    internal const double RidgeWeight = 3d;
    internal const double DetailDeviationWeight = 2d;
}
