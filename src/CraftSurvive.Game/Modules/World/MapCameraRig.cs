using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The map's camera, locked to a focus (the party): the wheel zooms between close range and the
/// whole map, and a secondary-button drag orbits; Q/E orbit, R/F tilt and Z/X zoom while held.
/// There is no free flight. Forward vectors come from the Engine's look integration, so the camera
/// always aims at the focus.
/// </summary>
internal sealed class MapCameraRig : IDisposable
{
    private const float ZoomPerWheelUnit = 0.0015f;
    private const float OrbitRadiansPerPointerUnit = 0.006f;
    private const float MinimumPitchDegrees = -85;
    private const float MaximumPitchDegrees = -12;
    private const float InitialPitchDegrees = -60;
    private const double FieldOfView = 55;
    private const float NearFraction = 0.004f;
    private const float FarMargin = 2.5f;
    private const double SampleSeconds = 1d / 60d;
    private const double DelaySeconds = 1d / 60d;
    /// <summary>Held-key rates per update: radians of orbit or tilt, and a zoom factor exponent.</summary>
    private const float KeyOrbitPerUpdate = 0.03f;
    private const float KeyZoomPerUpdate = 0.03f;

    private readonly IEngineContext engine;
    private readonly Camera camera;
    private readonly float minimumDistance, maximumDistance, mapWidth;
    private float yaw;
    private float pitch = InitialPitchDegrees * MathF.PI / 180;
    private float distance;
    private bool orbiting;
    private float heldYaw, heldPitch, heldZoom;
    private ulong samples;

    internal MapCameraRig(IEngineContext engine, Vector3 focus, float minimumDistance, float maximumDistance, float mapWidth)
    {
        this.engine = engine;
        this.minimumDistance = minimumDistance;
        this.maximumDistance = maximumDistance;
        this.mapWidth = mapWidth;
        Focus = focus;
        distance = maximumDistance;
        camera = engine.CameraView.CreateCamera(Descriptor());
    }

    internal Vector3 Focus { get; set; }
    internal float Distance => distance;
    internal string Readout => FormattableString.Invariant(
        $"distance={distance:F1};yawDegrees={yaw * 180 / MathF.PI:F0};pitchDegrees={pitch * 180 / MathF.PI:F0};orbiting={orbiting}");

    internal void Activate()
    {
        engine.CameraView.SetActiveCamera(camera);
        Publish();
    }

    /// <summary>Apply this update's wheel and orbit input; publishes a new pose and reports whether it changed.</summary>
    internal bool Steer(ReadOnlySpan<ProductInputEvent> events)
    {
        bool changed = false;
        foreach (ProductInputEvent input in events)
        {
            switch (input.Kind)
            {
                case InputEventKind.Clear:
                    orbiting = false;
                    heldYaw = heldPitch = heldZoom = 0;
                    break;
                case InputEventKind.Key when input.Edge != InputEdge.None:
                    float held = input.Edge == InputEdge.Pressed ? 1 : 0;
                    switch (input.Keyboard)
                    {
                        case KeyboardControl.KeyQ: heldYaw = held; break;
                        case KeyboardControl.KeyE: heldYaw = -held; break;
                        case KeyboardControl.KeyR: heldPitch = -held; break;
                        case KeyboardControl.KeyF: heldPitch = held; break;
                        case KeyboardControl.KeyZ: heldZoom = -held; break;
                        case KeyboardControl.KeyX: heldZoom = held; break;
                    }
                    break;
                case InputEventKind.Wheel:
                    distance = Math.Clamp(distance * MathF.Exp(input.Y * ZoomPerWheelUnit), minimumDistance, maximumDistance);
                    changed = true;
                    break;
                case InputEventKind.PointerButton when input.PointerButton == PointerButton.Secondary:
                    orbiting = input.Edge == InputEdge.Pressed;
                    break;
                case InputEventKind.PointerDelta when orbiting:
                    Turn(-input.X * OrbitRadiansPerPointerUnit, -input.Y * OrbitRadiansPerPointerUnit);
                    changed = true;
                    break;
            }
        }
        if (heldYaw != 0 || heldPitch != 0 || heldZoom != 0)
        {
            Turn(heldYaw * KeyOrbitPerUpdate, heldPitch * KeyOrbitPerUpdate);
            distance = Math.Clamp(distance * MathF.Exp(heldZoom * KeyZoomPerUpdate), minimumDistance, maximumDistance);
            changed = true;
        }
        if (changed) Publish();
        return changed;
    }

    /// <summary>Set the view directly (debug assistance); distance and pitch are clamped as for input.</summary>
    internal void Set(float distanceUnits, float yawDegrees, float pitchDegrees)
    {
        distance = Math.Clamp(distanceUnits, minimumDistance, maximumDistance);
        yaw = yawDegrees * MathF.PI / 180;
        pitch = Math.Clamp(pitchDegrees, MinimumPitchDegrees, MaximumPitchDegrees) * MathF.PI / 180;
        Publish();
    }

    private void Turn(float yawRadians, float pitchRadians)
    {
        yaw += yawRadians;
        pitch = Math.Clamp(pitch + pitchRadians, MinimumPitchDegrees * MathF.PI / 180, MaximumPitchDegrees * MathF.PI / 180);
    }

    public void Dispose() => camera.Dispose();

    private void Publish()
    {
        samples++;
        engine.CameraView.UpdateCameraSample(new CameraSampleRequest(camera, Descriptor(), samples * SampleSeconds,
            DelaySeconds, CameraInterpolation.Latest, 1));
    }

    private CameraDescriptor Descriptor()
    {
        LookState look = new(yaw, pitch);
        Vector3 forward = Look.IntegrateClamped(new LookRequest(look, Vector2.Zero, PlayerBody.Look)).Forward;
        Vector3 eye = Focus - forward * distance;
        return new(new CameraPose(eye, pitch * 180 / MathF.PI, yaw * 180 / MathF.PI), CameraBasisMode.Derived, default,
            new(CameraProjectionKind.Perspective, FieldOfView, 0, Math.Max(distance * NearFraction, 0.001f), distance * 2 + mapWidth * FarMargin),
            CameraViewports.Full);
    }
}
