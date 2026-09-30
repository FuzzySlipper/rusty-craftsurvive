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
    private CameraInterpolation interpolation = CameraInterpolation.Position;
    private double delaySeconds = PlayerConstants.CameraPresentationDelaySeconds;
    private bool cut = true;
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

    internal void Publish(Vector3 eye, LookState look, double sampleTimeSeconds, ulong update)
    {
        Camera active = camera ?? throw new InvalidOperationException("CraftSurvive camera is unavailable.");
        engine.CameraView.UpdateCameraSample(new CameraSampleRequest(active, Descriptor(eye, look),
            sampleTimeSeconds, delaySeconds, interpolation, cut ? (byte)1 : (byte)0));
        cut = false;
        publications = checked(publications + 1UL);
        publishedUpdate = update;
    }

    internal void SetPresentation(CameraInterpolation mode, double delay)
    {
        if (!double.IsFinite(delay) || delay <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be finite and positive.");
        }

        interpolation = mode;
        delaySeconds = delay;
        cut = true;
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"cameraPresentation={interpolation};cameraDelaySeconds={delaySeconds};cameraPublications={publications};cameraPublishedUpdate={publishedUpdate}");

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

    private static CameraDescriptor Descriptor(Vector3 eye, LookState look) => new(
        new CameraPose(eye, Angles.ToDegrees(look.PitchRadians), Angles.ToDegrees(look.YawRadians)),
        CameraBasisMode.Derived,
        default,
        new CameraProjection(CameraProjectionKind.Perspective, PlayerConstants.CameraFieldOfViewDegrees, 0d,
            PlayerConstants.CameraNearDistance, PlayerConstants.CameraFarDistance),
        new CameraViewport(PlayerConstants.CameraViewportOrigin, PlayerConstants.CameraViewportOrigin,
            PlayerConstants.CameraViewportExtent, PlayerConstants.CameraViewportExtent));
}
