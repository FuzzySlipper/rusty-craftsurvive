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
    /// <summary>
    /// A tree is drawn as a mesh (TerrainTrees); in the voxels it is only this many cells of
    /// invisible trunk core above its ground, enough to stop a standing body and a creature.
    /// </summary>
    internal const int TreeCoreHeight = 3;
    /// <summary>A tree's size about its kind's authored height, and the draw's resolution.</summary>
    internal const double TreeScaleMinimum = 0.8;
    internal const double TreeScaleMaximum = 1.25;
    internal const long TreeDrawResolution = 1000;
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
    /// blasted through either: bedrock refuses both by its block properties. The wall
    /// follows the ground it stands on, rising a fixed height above it, so a mountain
    /// border and a sea border are both closed without a wall hundreds of metres tall.
    /// </summary>
    internal const int WorldFloorThickness = 1;
    internal const int WorldWallThickness = 2;
    internal const int WorldWallRise = 24;
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
    internal const int HashFractionShift = 11;
    internal const ulong HashFractionMaximum = (1UL << 53) - 1UL;
    internal const int CoordinateRotation = 29;
    internal const int FirstHashShift = 30;
    internal const int SecondHashShift = 27;
    internal const int FinalHashShift = 31;
}
