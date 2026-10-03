using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>Named CraftSurvive player policy retained from the Rust donor.</summary>
internal static class PlayerConstants
{
    /// <summary>
    /// Water movement, composed by the product and solved by the Engine. The volume is
    /// an axis-aligned box around the player's own column; submersion and buoyancy are
    /// the Engine's, and the numbers here are the product's tuning of them.
    /// </summary>
    internal const float WaterExtent = 4f;
    internal const float WaterHeight = 3f;
    internal const float WaterSpeed = 4f;
    internal const float WaterAcceleration = 8f;
    internal const float WaterDrag = 2f;
    internal const float WaterGravityScale = 1f;
    internal const float WaterBuoyancy = 1.5f;
    internal const float WaterVerticalNeutral = 0f;
    internal const float NoClimbReach = 0f;

    /// <summary>
    /// Climbing, composed by the product and solved by the Engine: how fast a body climbs a rail
    /// and how far from it the Engine takes hold. Which faces are climbable is <see cref="PlayerClimb"/>'s.
    /// </summary>
    internal const float ClimbSpeed = 2.5f;

    /// <summary>How long after the climb action the player takes hold of a face they reach: walking or jumping onto a wall just after pressing still grabs it.</summary>
    internal const double TakeHoldWindowSeconds = 0.5d;
    internal const float ClimbReach = 0.6f;

    // One 60 Hz sample of translation delay; orientation stays authoritative and immediate.
    internal const double CameraPresentationDelaySeconds = 1d / 60d;

    internal const double InitialPitchDegrees = -20d;

    /// <summary>How far above the ground the player's capsule starts, so the first step settles rather than penetrates.</summary>
    internal const float SpawnClearance = 0.05f;

    internal const ulong PlayerEntityId = World.ProductIds.PlayerEntity;

    internal const float StandingEyeHeight = 1.55f;
    internal const float CrouchedEyeHeight = 0.85f;
    // Keep person-scale coordinates precise without rebasing during ordinary room traversal.
    internal const float RebaseThreshold = 1024f;
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
    internal const float ControllerStickDeadzone = 0.15f;
    internal const float ControllerLookDegreesPerSecond = 108f;
    internal const float ControllerLookInputUnitsPerSecond = ControllerLookDegreesPerSecond / LookDegreesPerPointerUnit;
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

    /// <summary>The column the player starts on; they stand on its ground, whatever its height.</summary>
    internal static readonly System.Numerics.Vector2 SpawnColumn = new(8.5f, 12.5f);

    internal const double InitialYawDegrees = 0d;
}
