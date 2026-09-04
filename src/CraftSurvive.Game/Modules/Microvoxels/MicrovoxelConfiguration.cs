using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Microvoxels;

/// <summary>
/// Product-owned material values shared by the named microvoxel presets and
/// the live debug setters. The palette color itself stays owned by the
/// admitted source; these values describe the common matte presentation.
/// </summary>
internal readonly record struct MicrovoxelMaterialSettings(
    float Roughness,
    Color TextureTint,
    Vector3 EmissionColor,
    float EmissionIntensity,
    bool DoubleSided)
{
    internal static readonly MicrovoxelMaterialSettings Matte = new(
        MicrovoxelConfiguration.MatteRoughness,
        MicrovoxelConfiguration.OpaqueTextureTint,
        MicrovoxelConfiguration.NoEmission,
        MicrovoxelConfiguration.NoEmissionIntensity,
        MicrovoxelConfiguration.SingleSided);
}

/// <summary>
/// A named product presentation posture. Source selection and admission
/// limits remain fixed in <see cref="MicrovoxelConfiguration"/>; this value
/// is the mutable desired state that a debug command can replace before the
/// owning presentation applies it during its ordinary update.
/// </summary>
internal readonly record struct MicrovoxelPresentationPreset(
    string Name,
    Vector3 Placement,
    Quaternion Rotation,
    Vector3 Scale,
    MicrovoxelMaterialSettings Material,
    bool Visible)
{
    internal Transform Transform => new(Placement, Rotation, Scale);
}

/// <summary>
/// Typed live state returned to the product debug adapter. It intentionally
/// reports both the desired product values and the latest Engine aggregate
/// presentation facts, rather than inventing a parallel renderer readout.
/// </summary>
internal readonly record struct MicrovoxelPresentationReadout(
    string SelectedObjectAssetId,
    string SelectedSourceContentPath,
    string SelectedPreset,
    bool Started,
    bool UpdatePending,
    Vector3 Placement,
    Vector3 Scale,
    MicrovoxelMaterialSettings Material,
    bool Visible,
    PresentationReadout EnginePresentation);

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
    internal const float OpaqueTextureTintValue = 1f;
    internal const float NoEmissionIntensity = 0f;
    internal const bool SingleSided = false;
    internal const ulong MaximumWoodlandShrineSourceBytes = 128UL * 1024UL;
    internal const uint MaximumWoodlandShrineDimension = 128U;
    internal const ulong MaximumWoodlandShrineVoxelCount = 65_536UL;
    internal const uint MaximumMagicaVoxelSourceChunks = 384U;
    internal const uint MaximumMagicaVoxelMaterialSlots = 255U;
    internal static readonly Vector3 WoodlandShrinePlacement = new(5.5f, 3.5f, 6f);
    internal static readonly Vector3 UnitScale = Vector3.One;
    internal static readonly Color OpaqueTextureTint = new(
        OpaqueTextureTintValue,
        OpaqueTextureTintValue,
        OpaqueTextureTintValue,
        OpaqueTextureTintValue);
    internal static readonly Vector3 NoEmission = Vector3.Zero;

    internal const string AcceptedPresetName = "accepted";
    internal const string ClosePresetName = "close";
    internal const string CompactPresetName = "compact";
    internal const float ClosePresetScale = 1.25f;
    internal const float CompactPresetScale = 0.75f;

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

    internal MicrovoxelPresentationPreset AcceptedPreset => new(
        AcceptedPresetName,
        Placement,
        Rotation,
        Scale,
        MicrovoxelMaterialSettings.Matte with { Roughness = Roughness },
        Visible: true);

    internal static bool TryGetPreset(
        string name,
        out string canonicalName,
        out MicrovoxelPresentationPreset preset)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "default":
            case AcceptedPresetName:
            case "matte":
                canonicalName = AcceptedPresetName;
                preset = WoodlandShrine.AcceptedPreset;
                return true;

            case ClosePresetName:
            case "close-up":
                canonicalName = ClosePresetName;
                preset = WoodlandShrine.AcceptedPreset with
                {
                    Name = ClosePresetName,
                    Scale = UnitScale * ClosePresetScale,
                };
                return true;

            case CompactPresetName:
                canonicalName = CompactPresetName;
                preset = WoodlandShrine.AcceptedPreset with
                {
                    Name = CompactPresetName,
                    Scale = UnitScale * CompactPresetScale,
                };
                return true;

            default:
                canonicalName = string.Empty;
                preset = default;
                return false;
        }
    }
}
