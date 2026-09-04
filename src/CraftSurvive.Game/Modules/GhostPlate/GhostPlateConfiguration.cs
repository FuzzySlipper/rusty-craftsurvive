using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.GhostPlate;

/// <summary>
/// Product-owned selection and tuning for the first CraftSurvive ghost actor.
/// The Engine owns capture, directional selection, and retained presentation;
/// these values describe only the product's chosen source and presentation.
/// </summary>
internal readonly record struct GhostPlateConfiguration(
    string SourceContentPath,
    ulong SourceObjectId,
    Transform Transform,
    float Width,
    float Height,
    GhostPlateCaptureSettings Capture,
    GhostPlateConfig Config)
{
    internal const string SourceContentPathValue = "animations/spatial-wizard.glb";
    internal const ulong SourceObjectIdValue = 3UL;

    internal const ushort CaptureResolution = 128;
    internal const float CaptureAzimuthDegrees = 0f;
    internal const float CaptureElevationDegrees = 18f;
    internal const float CaptureNear = 0.1f;
    internal const float CaptureFar = 40f;
    internal const float CaptureFieldOfViewDegrees = 55f;

    internal const float AmbientIntensity = 1.8f;
    internal const float KeyIntensity = 3f;
    internal const float FillIntensity = 1.4f;
    internal const float DepthRetention = 0.15f;
    internal const float AnchorValue = 0.5f;
    internal const float ShellDepthEpsilon = 0.12f;
    internal const byte SectorCount = 8;
    internal const float SectorHysteresisDegrees = 3f;
    internal const float GhostWidth = 2.5f;
    internal const float GhostHeight = 2.5f;

    internal const ushort CurrentCaptureResolution = 256;
    internal const float CurrentCaptureElevationDegrees = 10f;
    internal const float CurrentCaptureFar = 20f;
    internal const float CurrentAmbientIntensity = 0.5f;
    internal const float CurrentKeyIntensity = 1f;
    internal const float CurrentFillIntensity = 0.25f;
    internal const float CurrentDepthRetention = 0.25f;
    internal const float CurrentShellDepthEpsilon = 0.02f;
    internal const float CurrentSectorHysteresisDegrees = 4f;

    // Keep the comparison actor on the clear route in front of the authored
    // bridge. The pre-C# lab placement is occluded by this product's terrain.
    internal static readonly Vector3 SourcePlacement = new(0.5f, 5.2f, 4f);
    internal static readonly Vector3 SourceScale = new(1.9977713f, 1.9977713f, 1.9977713f);
    internal static readonly Vector3 AmbientColor = new(0.25f, 0.25f, 0.25f);
    internal static readonly Vector3 KeyDirection = new(0.5f, 1f, 0.25f);
    internal static readonly Vector3 KeyColor = Vector3.One;
    internal static readonly Vector3 FillDirection = new(-0.5f, 0.25f, -1f);
    internal static readonly Vector3 FillColor = new(0.5f, 0.5f, 0.5f);

    internal static GhostPlateConfiguration Default => new(
        SourceContentPathValue,
        SourceObjectIdValue,
        new Transform(SourcePlacement, Quaternion.Identity, SourceScale),
        GhostWidth,
        GhostHeight,
        new GhostPlateCaptureSettings(
            CaptureResolution,
            CaptureAzimuthDegrees,
            CaptureElevationDegrees,
            CaptureNear,
            CaptureFar,
            CaptureFieldOfViewDegrees,
            new GhostPlateCaptureLighting(
                GhostPlateCaptureLightingMode.Isolated,
                AmbientColor,
                AmbientIntensity,
                KeyDirection,
                KeyColor,
                KeyIntensity,
                FillDirection,
                FillColor,
                FillIntensity)),
        new GhostPlateConfig(
            DepthRetention,
            GhostPlateAnchorPolicy.BoundsCenter,
            AnchorValue,
            GhostPlateMapping.PlateLocked,
            GhostPlateShellMode.WholeMesh,
            ShellDepthEpsilon,
            SectorCount,
            SectorHysteresisDegrees));

    /// <summary>
    /// Returns the small set of product-owned look presets used by the live
    /// debug lane. Every preset keeps the admitted source and object identity
    /// fixed; only the retained ghost settings and plate layout vary.
    /// </summary>
    internal static bool TryGetPreset(
        string name,
        out string canonicalName,
        out GhostPlateConfiguration preset)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "default":
            case "accepted":
                canonicalName = "accepted";
                preset = Default;
                return true;

            case "current":
            case "csharp-baseline":
                canonicalName = "current";
                preset = Default with
                {
                    Capture = Default.Capture with
                    {
                        Resolution = CurrentCaptureResolution,
                        ElevationDegrees = CurrentCaptureElevationDegrees,
                        Far = CurrentCaptureFar,
                        Lighting = Default.Capture.Lighting with
                        {
                            AmbientIntensity = CurrentAmbientIntensity,
                            KeyIntensity = CurrentKeyIntensity,
                            FillIntensity = CurrentFillIntensity,
                        },
                    },
                    Config = Default.Config with
                    {
                        DepthRetention = CurrentDepthRetention,
                        ShellMode = GhostPlateShellMode.RepairedSource,
                        ShellDepthEpsilon = CurrentShellDepthEpsilon,
                        SectorHysteresisDegrees = CurrentSectorHysteresisDegrees,
                    },
                };
                return true;

            case "wide":
                canonicalName = "wide";
                preset = Default with
                {
                    Width = 2.6f,
                    Height = 3.6f,
                    Capture = Default.Capture with
                    {
                        Resolution = 96,
                        FieldOfViewDegrees = 65f,
                    },
                    Config = Default.Config with
                    {
                        DepthRetention = 0.35f,
                        SectorCount = 4,
                        SectorHysteresisDegrees = 8f,
                    },
                };
                return true;

            case "strict":
                canonicalName = "strict";
                preset = Default with
                {
                    Capture = Default.Capture with
                    {
                        Resolution = 48,
                    },
                    Config = Default.Config with
                    {
                        DepthRetention = 0.15f,
                        PlateMapping = GhostPlateMapping.ProjectiveSurface,
                        ShellMode = GhostPlateShellMode.StrictSource,
                        ShellDepthEpsilon = 0f,
                        SectorCount = 8,
                        SectorHysteresisDegrees = 4f,
                    },
                };
                return true;

            case "scene-lighting":
                canonicalName = "scene-lighting";
                preset = Default with
                {
                    Capture = Default.Capture with
                    {
                        Lighting = Default.Capture.Lighting with
                        {
                            Mode = GhostPlateCaptureLightingMode.Scene,
                        },
                    },
                };
                return true;

            default:
                canonicalName = string.Empty;
                preset = default;
                return false;
        }
    }

    internal GhostPlatePlacement Placement => new(Transform, Width, Height);
}
