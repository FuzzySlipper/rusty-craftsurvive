using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Microvoxels;

/// <summary>
/// Product-owned selection, admission bounds, material posture, and placement
/// for the first CraftSurvive high-density voxel-object presentation.
/// </summary>
internal readonly record struct MicrovoxelConfiguration(
    string ContentPath,
    string AssetId,
    double CellSize,
    MagicaVoxelPivotPolicy PivotPolicy,
    Vector3 Placement,
    Quaternion Rotation,
    Vector3 Scale,
    float Roughness,
    ulong MaximumSourceBytes,
    uint MaximumDimension,
    ulong MaximumVoxelCount,
    uint MaximumSourceChunks,
    uint MaximumMaterialSlots)
{
    internal const string WoodlandShrineContentPath = "voxels/woodland-shrine-nano-model-solid64.vox";
    internal const string WoodlandShrineAssetId = "voxel-object/craftsurvive/woodland-shrine-nano";
    internal const double WoodlandShrineCellSize = 0.05d;
    internal const float MatteRoughness = 1f;
    internal const ulong MaximumWoodlandShrineSourceBytes = 128UL * 1024UL;
    internal const uint MaximumWoodlandShrineDimension = 128U;
    internal const ulong MaximumWoodlandShrineVoxelCount = 65_536UL;
    internal const uint MaximumMagicaVoxelSourceChunks = 384U;
    internal const uint MaximumMagicaVoxelMaterialSlots = 255U;
    internal static readonly Vector3 WoodlandShrinePlacement = new(2.5f, 3f, 2f);
    internal static readonly Vector3 UnitScale = Vector3.One;

    internal static MicrovoxelConfiguration WoodlandShrine => new(
        WoodlandShrineContentPath,
        WoodlandShrineAssetId,
        WoodlandShrineCellSize,
        MagicaVoxelPivotPolicy.BaseCenter,
        WoodlandShrinePlacement,
        Quaternion.Identity,
        UnitScale,
        MatteRoughness,
        MaximumWoodlandShrineSourceBytes,
        MaximumWoodlandShrineDimension,
        MaximumWoodlandShrineVoxelCount,
        MaximumMagicaVoxelSourceChunks,
        MaximumMagicaVoxelMaterialSlots);

    internal Transform PresentationTransform => new(Placement, Rotation, Scale);
}
