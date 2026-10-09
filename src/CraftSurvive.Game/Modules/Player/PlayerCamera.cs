using System.Globalization;
using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// The player's first-person camera: created once, then sampled every update at the eye with a
/// short presentation delay the Engine interpolates across.
/// </summary>
internal sealed class PlayerCamera(IEngineContext engine) : IDisposable
{
    private Camera? camera;
    /// <summary>The Engine interpolates the eye's position between samples, a short delay behind the latest.</summary>
    private const CameraInterpolation Interpolation = CameraInterpolation.Position;
    private const double DelaySeconds = PlayerConstants.CameraPresentationDelaySeconds;
    private bool cut = true;

    /// <summary>The vertical field of view, in degrees: the player's option (#9759), taken on the next publication.</summary>
    internal double FieldOfViewDegrees { get; set; } = PlayerConstants.CameraFieldOfViewDegrees;
    private ulong publications;
    private ulong publishedUpdate;

    internal void Create(Vector3 eye, LookState look, ulong update)
    {
        camera = engine.CameraView.CreateCamera(Descriptor(eye, look));
        engine.CameraView.SetActiveCamera(camera);
        publications = 1UL;
        publishedUpdate = update;
    }

    /// <summary>The next sample jumps rather than interpolating: a teleport or a rebase moved the eye.</summary>
    internal void Cut() => cut = true;

    internal void Activate()
    {
        if (camera is not null) engine.CameraView.SetActiveCamera(camera);
        Cut();
    }

    internal void Publish(Vector3 eye, LookState look, double sampleTimeSeconds, ulong update)
    {
        Camera active = camera ?? throw new InvalidOperationException("CraftSurvive camera is unavailable.");
        engine.CameraView.UpdateCameraSample(new CameraSampleRequest(active, Descriptor(eye, look),
            sampleTimeSeconds, DelaySeconds, Interpolation, cut ? (byte)1 : (byte)0));
        cut = false;
        publications = checked(publications + 1UL);
        publishedUpdate = update;
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"cameraPresentation={Interpolation};cameraDelaySeconds={DelaySeconds};cameraPublications={publications};cameraPublishedUpdate={publishedUpdate}");

    public void Dispose()
    {
        if (camera is null)
        {
            return;
        }

        engine.CameraView.ClearActiveCamera(new ClearActiveCameraRequest(0U));
        camera.Dispose();
        camera = null;
    }

    private CameraDescriptor Descriptor(Vector3 eye, LookState look) => new(
        new CameraPose(eye, Angles.ToDegrees(look.PitchRadians), Angles.ToDegrees(look.YawRadians)),
        CameraBasisMode.Derived,
        default,
        new CameraProjection(CameraProjectionKind.Perspective, FieldOfViewDegrees, 0d,
            PlayerConstants.CameraNearDistance, PlayerConstants.CameraFarDistance),
        new CameraViewport(PlayerConstants.CameraViewportOrigin, PlayerConstants.CameraViewportOrigin,
            PlayerConstants.CameraViewportExtent, PlayerConstants.CameraViewportExtent));
}
