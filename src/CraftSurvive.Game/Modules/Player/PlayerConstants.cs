using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>Named CraftSurvive player policy retained from the Rust donor.</summary>
internal static class PlayerConstants
{
    internal const uint RuntimeComponentLocalId = 1U;
    internal static readonly Vector3 InitialEyePosition = new(8f, 7f, 12f);
    internal const double InitialYawDegrees = 0d;
    internal const double InitialPitchDegrees = -20d;

    internal const ulong PlayerEntityId = 1UL;
    internal const ulong PlatformEntityId = 2UL;
    internal static readonly Vector3 PlatformInitialCenter = new(0f, 4.25f, 9f);
    internal static readonly Vector3 PlatformHalfExtents = new(1.5f, 0.25f, 0.9f);
    internal static readonly Vector3 PlatformScale = PlatformHalfExtents * 2f;
    internal static readonly Color PlatformColor = new(0.72f, 0.48f, 0.18f, 1f);
    internal const float PlatformTravelMinimumX = -1.5f;
    internal const float PlatformTravelMaximumX = 1.5f;
    internal const float PlatformSpeed = 0.8f;
    internal const float PlatformActivityRadius = 32f;

    internal const float StandingEyeHeight = 1.55f;
    internal const float CrouchedEyeHeight = 0.85f;
    internal const float RebaseThreshold = 32f;
    internal const double ControllerStepSeconds = 1d / 120d;
    internal const double ControllerStepEpsilon = 0.000001d;

    internal const float SprintSpeed = 8f;
    internal const float ImpulseSpeed = 5.5f;
    internal const float ImpulseLift = 2.5f;
    internal const float StandingHeight = 1.75f;
    internal const float CrouchedHeight = 1f;
    internal const float CapsuleRadius = 0.3f;
    internal const float ContactSkin = 0.015f;
    internal const float GroundSpeed = 7f;
    internal const float GroundAcceleration = 48f;
    internal const float GroundBraking = 58f;
    internal const float GroundFriction = 9f;
    internal const float AirAcceleration = 10f;
    internal const float Gravity = 24f;
    internal const float JumpSpeed = 8.5f;
    internal const float TerminalFallSpeed = 24f;
    internal const float MaximumSlopeDegrees = 50f;
    internal const float MaximumStepHeight = 1.05f;
    internal const float FloorSnapDistance = 0.25f;
    internal const float FloorSnapSpeedLimit = 10f;
    internal const float ExternalDecayPerSecond = 3f;
    internal const float LookDegreesPerPointerUnit = 0.12f;
    internal const float LookRadiansPerInputUnit = LookDegreesPerPointerUnit * MathF.PI / 180f;
    internal const float LookMaximumDeltaRadians = MathF.PI;
    internal const float LookPitchEpsilonRadians = 0.0001f;
    internal const float MinimumPitchRadians = (-MathF.PI / 2f) + LookPitchEpsilonRadians;
    internal const float MaximumPitchRadians = (MathF.PI / 2f) - LookPitchEpsilonRadians;
    internal const ulong UninitializedCollisionWorldHash = 0UL;
    internal const ushort PlaceMaterial = 1;
    internal const int DefaultBrushRadius = 0;
    internal const int MinimumBrushRadius = 0;
    internal const int MediumBrushRadius = 1;
    internal const int MaximumBrushRadius = 2;

    internal const double CameraFieldOfViewDegrees = 70d;
    internal const double CameraNearDistance = 0.05d;
    internal const double CameraFarDistance = 1_000d;
    internal const double CameraViewportOrigin = 0d;
    internal const double CameraViewportExtent = 1d;
}
