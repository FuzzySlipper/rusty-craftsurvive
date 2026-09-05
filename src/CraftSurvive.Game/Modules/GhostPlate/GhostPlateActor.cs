using System.Globalization;
using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.GhostPlate;

/// <summary>
/// Owns one ordinary retained source appearance and its Engine ghost plate.
/// Product commands queue typed desired values here; the product update applies
/// those values through the named Engine presentation operations.
/// </summary>
internal sealed class GhostPlateActor : IDisposable
{
    private readonly IEngineContext engine;
    private readonly GhostPlateConfiguration sourceConfiguration;
    private readonly Appearance sourceAppearance;

    private GhostPlatePresentation? presentation;
    private GhostPlatePlacement desiredPlacement;
    private GhostPlatePlacement appliedPlacement;
    private GhostPlateCaptureSettings desiredCapture;
    private GhostPlateCaptureSettings appliedCapture;
    private GhostPlateConfig desiredConfig;
    private GhostPlateConfig appliedConfig;
    private GhostPlatePresentationReadout lastReadout;
    private string presetName = "accepted";
    private bool desiredVisible = true;
    private bool appliedVisible;
    private bool recapturePending;
    private bool hasReadout;
    private bool started;
    private bool disposed;

    internal GhostPlateActor(IEngineContext engine, GhostPlateConfiguration configuration)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        sourceConfiguration = configuration;

        RenderResourceHandle sourceResource = engine.Animation.OpenAnimatedMesh(
            new AnimatedMeshResourceRequest(configuration.SourceContentPath));
        sourceAppearance = engine.Animation.CreateAnimatedMeshAppearance(
            new AnimatedMeshAppearanceRequest(sourceResource));

        desiredPlacement = configuration.Placement;
        appliedPlacement = desiredPlacement;
        desiredCapture = configuration.Capture;
        appliedCapture = desiredCapture;
        desiredConfig = configuration.Config;
        appliedConfig = desiredConfig;
    }

    internal ulong SourceObjectId => sourceConfiguration.SourceObjectId;

    internal string SourceContentPath => sourceConfiguration.SourceContentPath;

    /// <summary>
    /// Returns the placement that has actually reached the Engine. The product
    /// root uses this value when composing its complete source snapshot.
    /// </summary>
    internal GhostPlatePlacement Placement => appliedPlacement;

    /// <summary>
    /// Returns the placement queued by product/debug code, whether or not the
    /// next ordinary update has applied it to the retained presentation.
    /// </summary>
    internal GhostPlatePlacement DesiredPlacement => desiredPlacement;

    /// <summary>
    /// The hidden source is still part of the complete retained Appearance
    /// snapshot so the Engine can capture it by product object identity.
    /// </summary>
    internal AppearanceFact SourceAppearanceFact => new(
        sourceConfiguration.SourceObjectId,
        false,
        0,
        appliedPlacement.Transform,
        sourceAppearance,
        Visible: false,
        RenderLayer.Scene);

    /// <summary>
    /// The complete source snapshot must be published with this desired
    /// transform before <see cref="Update"/> invokes the Engine ghost update.
    /// That ordering lets the retained projector observe the same placement
    /// that the product requested instead of one frame of stale source state.
    /// </summary>
    internal AppearanceFact DesiredSourceAppearanceFact => new(
        sourceConfiguration.SourceObjectId,
        false,
        0,
        desiredPlacement.Transform,
        sourceAppearance,
        Visible: false,
        RenderLayer.Scene);

    internal void Start()
    {
        EnsureNotDisposed();
        if (started)
        {
            return;
        }

        if (desiredVisible)
        {
            CreatePresentation();
        }
        started = true;
    }

    /// <summary>
    /// Applies queued product changes through the ordinary generated Engine
    /// operations. Commands never invoke Engine calls directly, so a debug
    /// request and a normal product update use exactly the same path.
    /// </summary>
    internal void Update()
    {
        EnsureStarted();

        if (!desiredVisible)
        {
            if (presentation is not null)
            {
                DisposePresentation();
            }

            appliedVisible = false;
            appliedPlacement = desiredPlacement;
            appliedCapture = desiredCapture;
            appliedConfig = desiredConfig;
            recapturePending = false;
            hasReadout = false;
            return;
        }

        if (presentation is null)
        {
            CreatePresentation();
        }
        else
        {
            if (desiredPlacement != appliedPlacement || desiredConfig != appliedConfig)
            {
                engine.Presentation.UpdateGhostPlate(new UpdateGhostPlatePresentationRequest(
                    Presentation,
                    desiredPlacement,
                    desiredConfig));
                appliedPlacement = desiredPlacement;
                appliedConfig = desiredConfig;
            }

            if (recapturePending)
            {
                engine.Presentation.RecaptureGhostPlate(
                    new RecaptureGhostPlatePresentationRequest(Presentation, desiredCapture));
                appliedCapture = desiredCapture;
                recapturePending = false;
            }
        }

        appliedVisible = true;
        RefreshReadout();
    }

    /// <summary>Queues a complete product preset without changing source identity.</summary>
    internal string QueuePreset(string name)
    {
        EnsureStarted();
        if (!GhostPlateConfiguration.TryGetPreset(name, out string canonicalName, out GhostPlateConfiguration preset))
        {
            throw new ArgumentException(
                "Ghost preset must be accepted, current, wide, strict, or scene-lighting.",
                nameof(name));
        }

        presetName = canonicalName;
        desiredPlacement = preset.Placement;
        desiredCapture = preset.Capture;
        desiredConfig = preset.Config;
        recapturePending = true;
        return PendingSummary();
    }

    /// <summary>Queues capture resolution, framing, clip range, and lighting mode.</summary>
    internal string QueueCapture(
        ushort resolution,
        float azimuthDegrees,
        float elevationDegrees,
        float near,
        float far,
        float fieldOfViewDegrees,
        GhostPlateCaptureLightingMode lightingMode)
    {
        EnsureStarted();
        ValidateCapture(resolution, azimuthDegrees, elevationDegrees, near, far, fieldOfViewDegrees);
        ValidateLightingMode(lightingMode);
        desiredCapture = desiredCapture with
        {
            Resolution = resolution,
            AzimuthDegrees = azimuthDegrees,
            ElevationDegrees = elevationDegrees,
            Near = near,
            Far = far,
            FieldOfViewDegrees = fieldOfViewDegrees,
            Lighting = desiredCapture.Lighting with { Mode = lightingMode },
        };
        recapturePending = true;
        return PendingSummary();
    }

    /// <summary>Queues the typed lighting mode and intensity values for recapture.</summary>
    internal string QueueLighting(
        GhostPlateCaptureLightingMode lightingMode,
        float ambientIntensity,
        float keyIntensity,
        float fillIntensity)
    {
        EnsureStarted();
        ValidateLighting(lightingMode, ambientIntensity, keyIntensity, fillIntensity);
        desiredCapture = desiredCapture with
        {
            Lighting = desiredCapture.Lighting with
            {
                Mode = lightingMode,
                AmbientIntensity = ambientIntensity,
                KeyIntensity = keyIntensity,
                FillIntensity = fillIntensity,
            },
        };
        recapturePending = true;
        return PendingSummary();
    }

    /// <summary>Queues depth, anchor, mapping, shell, and direction-independent relief values.</summary>
    internal string QueueRelief(
        float depthRetention,
        GhostPlateAnchorPolicy anchorPolicy,
        float anchorValue,
        GhostPlateMapping plateMapping,
        GhostPlateShellMode shellMode,
        float shellDepthEpsilon)
    {
        EnsureStarted();
        ValidateRelief(depthRetention, anchorValue, shellDepthEpsilon);
        desiredConfig = desiredConfig with
        {
            DepthRetention = depthRetention,
            AnchorPolicy = anchorPolicy,
            AnchorValue = anchorValue,
            PlateMapping = plateMapping,
            ShellMode = shellMode,
            ShellDepthEpsilon = shellDepthEpsilon,
        };
        return PendingSummary();
    }

    /// <summary>Queues a supported hard-snap capture-bank size and hysteresis.</summary>
    internal string QueueDirection(byte sectorCount, float hysteresisDegrees)
    {
        EnsureStarted();
        if (sectorCount is not (1 or 4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "Ghost sectors must be 1, 4, 8, or 16.");
        }
        if (!float.IsFinite(hysteresisDegrees) || hysteresisDegrees is < 0f or > 22.5f)
        {
            throw new ArgumentOutOfRangeException(nameof(hysteresisDegrees), "Ghost hysteresis must be between 0 and 22.5 degrees.");
        }

        desiredConfig = desiredConfig with
        {
            SectorCount = sectorCount,
            SectorHysteresisDegrees = hysteresisDegrees,
        };
        return PendingSummary();
    }

    /// <summary>Queues translation and plate size while retaining the source rotation and scale.</summary>
    internal string QueuePlacement(float x, float y, float z, float width, float height)
    {
        EnsureStarted();
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ArgumentException("Ghost placement coordinates must be finite.");
        }
        if (!float.IsFinite(width) || width is < 0.05f or > 64f)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Ghost width must be between 0.05 and 64.");
        }
        if (!float.IsFinite(height) || height is < 0.05f or > 64f)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Ghost height must be between 0.05 and 64.");
        }

        desiredPlacement = desiredPlacement with
        {
            Transform = desiredPlacement.Transform with
            {
                Translation = new Vector3(x, y, z),
            },
            Width = width,
            Height = height,
        };
        return PendingSummary();
    }

    /// <summary>Queues retained-presentation visibility; Update performs the lifecycle operation.</summary>
    internal string QueueVisibility(bool visible)
    {
        EnsureStarted();
        desiredVisible = visible;
        return PendingSummary();
    }

    /// <summary>Queues a capture-bank rebuild for the next ordinary product update.</summary>
    internal string QueueRecapture()
    {
        EnsureStarted();
        recapturePending = true;
        return PendingSummary();
    }

    /// <summary>Explicit product restart policy; applies and reads the same pending state path immediately.</summary>
    internal void Recapture()
    {
        QueueRecapture();
        Update();
    }

    /// <summary>
    /// Attachment is deliberately side-effect free. Publishing the complete
    /// source snapshot lets the Engine rebase its retained ghost projector;
    /// this owner never recreates capture or emulates attachment in C#.
    /// </summary>
    internal void Attach()
    {
        EnsureStarted();
        if (presentation is not null)
        {
            RefreshReadout();
        }
    }

    /// <summary>Returns only retained state captured during an Engine product call.</summary>
    internal string DebugReadout()
    {
        EnsureNotDisposed();
        GhostPlatePresentationReadout readout = hasReadout ? lastReadout : default;
        bool pending = desiredPlacement != appliedPlacement
            || desiredCapture != appliedCapture
            || desiredConfig != appliedConfig
            || desiredVisible != appliedVisible
            || recapturePending;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"source={SourceContentPath};object={SourceObjectId};preset={presetName};visible={desiredVisible};appliedVisible={appliedVisible};pending={pending};resolution={desiredCapture.Resolution};azimuth={desiredCapture.AzimuthDegrees:F1};elevation={desiredCapture.ElevationDegrees:F1};near={desiredCapture.Near:F2};far={desiredCapture.Far:F1};fov={desiredCapture.FieldOfViewDegrees:F1};lighting={desiredCapture.Lighting.Mode}:{desiredCapture.Lighting.AmbientIntensity:F2}/{desiredCapture.Lighting.KeyIntensity:F2}/{desiredCapture.Lighting.FillIntensity:F2};depth={desiredConfig.DepthRetention:F3};anchor={desiredConfig.AnchorPolicy}:{desiredConfig.AnchorValue:F3};mapping={desiredConfig.PlateMapping};shell={desiredConfig.ShellMode}:{desiredConfig.ShellDepthEpsilon:F3};sectors={desiredConfig.SectorCount};hysteresis={desiredConfig.SectorHysteresisDegrees:F1};placement={desiredPlacement.Transform.Translation.X:F2},{desiredPlacement.Transform.Translation.Y:F2},{desiredPlacement.Transform.Translation.Z:F2};scale={desiredPlacement.Transform.Scale.X:F3},{desiredPlacement.Transform.Scale.Y:F3},{desiredPlacement.Transform.Scale.Z:F3};size={desiredPlacement.Width:F2}x{desiredPlacement.Height:F2};observed={readout.HasRendererObservation};sourceMatch={readout.SourceMatches};sector={readout.CurrentSector};offset={(readout.HasLocalAngularOffset ? readout.LocalAngularOffsetDegrees.ToString("F1", CultureInfo.InvariantCulture) : "none")};fallback={readout.FallbackActive}:{readout.FallbackReason};retained={readout.RetainedSectorCount}/{readout.RetainedMeshCount}/{readout.RetainedMaterialCount}");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        DisposePresentation();
        DisposeSourceAppearance();
    }

    /// <summary>
    /// Releases the retained renderer owner while its source is still present.
    /// The product root may then publish its next complete Appearance snapshot.
    /// </summary>
    internal void DisposePresentation()
    {
        if (disposed)
        {
            return;
        }

        presentation?.Dispose();
        presentation = null;
        appliedVisible = false;
    }

    /// <summary>
    /// Releases the ordinary source only after the product root has removed it
    /// from any live complete Appearance snapshot.
    /// </summary>
    internal void DisposeSourceAppearance()
    {
        if (disposed)
        {
            return;
        }
        if (presentation is not null)
        {
            throw new InvalidOperationException(
                "Dispose the ghost plate presentation before its source appearance.");
        }

        sourceAppearance.Dispose();
        disposed = true;
        started = false;
    }

    private GhostPlatePresentation Presentation => presentation
        ?? throw new InvalidOperationException("CraftSurvive ghost plate presentation is unavailable.");

    private void CreatePresentation()
    {
        presentation = engine.Presentation.CreateGhostPlate(
            new CreateGhostPlatePresentationRequest(
                sourceConfiguration.SourceObjectId,
                desiredPlacement,
                desiredCapture,
                desiredConfig));
        appliedPlacement = desiredPlacement;
        appliedCapture = desiredCapture;
        appliedConfig = desiredConfig;
        appliedVisible = true;
        recapturePending = false;
        RefreshReadout();
    }

    private void RefreshReadout()
    {
        if (presentation is null)
        {
            return;
        }

        lastReadout = engine.Presentation.ReadGhostPlate(Presentation);
        hasReadout = true;
    }

    private string PendingSummary()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"queued source={SourceContentPath};object={SourceObjectId};preset={presetName};visible={desiredVisible};recapture={recapturePending}");

    private void EnsureStarted()
    {
        EnsureNotDisposed();
        if (!started)
        {
            throw new InvalidOperationException("CraftSurvive ghost plate has not started.");
        }
    }

    private void EnsureNotDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(GhostPlateActor));
        }
    }

    private static void ValidateCapture(
        ushort resolution,
        float azimuthDegrees,
        float elevationDegrees,
        float near,
        float far,
        float fieldOfViewDegrees)
    {
        if (resolution is < 8 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Ghost resolution must be between 8 and 4096.");
        }
        if (!float.IsFinite(azimuthDegrees) || azimuthDegrees is < -360f or > 360f)
        {
            throw new ArgumentOutOfRangeException(nameof(azimuthDegrees), "Ghost azimuth must be between -360 and 360 degrees.");
        }
        if (!float.IsFinite(elevationDegrees) || elevationDegrees is < -89f or > 89f)
        {
            throw new ArgumentOutOfRangeException(nameof(elevationDegrees), "Ghost elevation must be between -89 and 89 degrees.");
        }
        if (!float.IsFinite(near) || near is < 0.001f or > 100f)
        {
            throw new ArgumentOutOfRangeException(nameof(near), "Ghost near clip must be between 0.001 and 100.");
        }
        if (!float.IsFinite(far) || far <= near + 0.001f || far > 10_000f)
        {
            throw new ArgumentOutOfRangeException(nameof(far), "Ghost far clip must exceed near clip and be at most 10000.");
        }
        if (!float.IsFinite(fieldOfViewDegrees) || fieldOfViewDegrees is < 10f or > 120f)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldOfViewDegrees), "Ghost field of view must be between 10 and 120 degrees.");
        }
    }

    private static void ValidateLighting(
        GhostPlateCaptureLightingMode lightingMode,
        float ambientIntensity,
        float keyIntensity,
        float fillIntensity)
    {
        ValidateLightingMode(lightingMode);
        if (!float.IsFinite(ambientIntensity) || ambientIntensity is < 0f or > 8f)
        {
            throw new ArgumentOutOfRangeException(nameof(ambientIntensity), "Ghost ambient intensity must be between 0 and 8.");
        }
        if (!float.IsFinite(keyIntensity) || keyIntensity is < 0f or > 8f)
        {
            throw new ArgumentOutOfRangeException(nameof(keyIntensity), "Ghost key intensity must be between 0 and 8.");
        }
        if (!float.IsFinite(fillIntensity) || fillIntensity is < 0f or > 8f)
        {
            throw new ArgumentOutOfRangeException(nameof(fillIntensity), "Ghost fill intensity must be between 0 and 8.");
        }
    }

    private static void ValidateLightingMode(GhostPlateCaptureLightingMode lightingMode)
    {
        if (lightingMode is not (GhostPlateCaptureLightingMode.Scene or GhostPlateCaptureLightingMode.Isolated))
        {
            throw new ArgumentOutOfRangeException(nameof(lightingMode), "Ghost lighting must be Scene or Isolated.");
        }
    }

    private static void ValidateRelief(float depthRetention, float anchorValue, float shellDepthEpsilon)
    {
        if (!float.IsFinite(depthRetention) || depthRetention is < 0.02f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(depthRetention), "Ghost depth retention must be between 0.02 and 1.");
        }
        if (!float.IsFinite(anchorValue) || anchorValue is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorValue), "Ghost anchor value must be between 0 and 1.");
        }
        if (!float.IsFinite(shellDepthEpsilon) || shellDepthEpsilon is < 0f or > 2f)
        {
            throw new ArgumentOutOfRangeException(nameof(shellDepthEpsilon), "Ghost shell tolerance must be between 0 and 2.");
        }
    }
}
