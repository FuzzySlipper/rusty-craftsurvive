using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// The player's body as the character controller sees it: its configuration, its stance
/// geometry, and the motion it starts from.
/// </summary>
internal static class PlayerBody
{
    internal static CharacterControllerConfig Configure(CharacterControllerConfig baseline) => baseline with
    {
        Shape = baseline.Shape with
        {
            StandingHeight = PlayerConstants.StandingHeight,
            CrouchedHeight = PlayerConstants.CrouchedHeight,
            Radius = PlayerConstants.CapsuleRadius,
            ContactSkin = PlayerConstants.ContactSkin,
        },
        Ground = baseline.Ground with
        {
            ForwardSpeed = PlayerConstants.GroundSpeed,
            BackwardSpeed = PlayerConstants.GroundSpeed,
            StrafeSpeed = PlayerConstants.GroundSpeed,
            Acceleration = PlayerConstants.GroundAcceleration,
            Braking = PlayerConstants.GroundBraking,
            Friction = PlayerConstants.GroundFriction,
        },
        Air = baseline.Air with
        {
            MaximumSpeed = PlayerConstants.GroundSpeed,
            Acceleration = PlayerConstants.AirAcceleration,
            WishSpeedCap = PlayerConstants.GroundSpeed,
        },
        Vertical = baseline.Vertical with
        {
            Gravity = PlayerConstants.Gravity,
            JumpSpeed = PlayerConstants.JumpSpeed,
            TerminalFallSpeed = PlayerConstants.TerminalFallSpeed,
        },
        Surface = baseline.Surface with
        {
            MaximumSlopeRadians = Angles.ToRadians(PlayerConstants.MaximumSlopeDegrees),
            MaximumStepHeight = PlayerConstants.MaximumStepHeight,
            FloorSnapDistance = PlayerConstants.FloorSnapDistance,
            FloorSnapSpeedLimit = PlayerConstants.FloorSnapSpeedLimit,
        },
        ExternalMotion = baseline.ExternalMotion with { ExternalDecayPerSecond = PlayerConstants.ExternalDecayPerSecond },
    };

    internal static CharacterControllerConfig WithSprintSpeed(CharacterControllerConfig baseline) => baseline with
    {
        Ground = baseline.Ground with
        {
            ForwardSpeed = PlayerConstants.SprintSpeed,
            BackwardSpeed = PlayerConstants.SprintSpeed,
            StrafeSpeed = PlayerConstants.SprintSpeed,
        },
    };

    internal static LookConfig Look { get; } = new(
        PlayerConstants.LookRadiansPerInputUnit,
        PlayerConstants.LookRadiansPerInputUnit,
        PlayerConstants.MinimumPitchRadians,
        PlayerConstants.MaximumPitchRadians,
        PlayerConstants.LookMaximumDeltaRadians,
        false,
        true,
        true);

    internal static float Height(CharacterStance stance) => stance == CharacterStance.Crouched
        ? PlayerConstants.CrouchedHeight
        : PlayerConstants.StandingHeight;

    /// <summary>How far the eye sits above the capsule's centre.</summary>
    internal static float EyeOffset(CharacterStance stance)
    {
        float eyeHeight = stance == CharacterStance.Crouched
            ? PlayerConstants.CrouchedEyeHeight
            : PlayerConstants.StandingEyeHeight;
        return eyeHeight - (Height(stance) / 2f);
    }

    /// <summary>A body standing still where it was placed.</summary>
    internal static CharacterMotion AtRest(Vector3 localPosition) => new(
        Vector3.Zero,
        Vector3.Zero,
        false,
        CharacterStance.Standing,
        0f,
        0f,
        0f,
        false,
        0UL,
        Vector3.Zero,
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.Zero,
        localPosition.Y,
        localPosition.Y,
        0UL,
        0UL);

    /// <summary>The swim movement in a volume about the player's column, from a floor to a top (the swim probe's, and the checks').</summary>
    internal static CharacterMovementRequest Swim(Vector3 floor, Vector3 top) => new()
    {
        Mode = CharacterMovementMode.Swimming,
        VerticalIntent = PlayerConstants.WaterVerticalNeutral,
        Speed = PlayerConstants.WaterSpeed,
        Acceleration = PlayerConstants.WaterAcceleration,
        Drag = PlayerConstants.WaterDrag,
        Minimum = floor - new Vector3(PlayerConstants.WaterExtent, 0f, PlayerConstants.WaterExtent),
        Maximum = top + new Vector3(PlayerConstants.WaterExtent, 0f, PlayerConstants.WaterExtent),
        GravityScale = PlayerConstants.WaterGravityScale,
        Buoyancy = PlayerConstants.WaterBuoyancy,
        ClimbReach = PlayerConstants.NoClimbReach,
    };
}

internal static class Angles
{
    internal static float ToRadians(double degrees) => checked((float)(degrees * Math.PI / 180d));

    internal static double ToDegrees(float radians) => radians * 180d / Math.PI;
}
