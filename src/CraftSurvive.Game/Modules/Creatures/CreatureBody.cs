using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Where a creature's body stands after a step: its capsule's centre (session-local) and the motion it carries on.</summary>
internal readonly record struct CreatureBodyState(Vector3 Centre, CharacterMotion Motion);

/// <summary>
/// A creature moves as a body (#9737): the Engine's character step moves its capsule - the one navigation
/// routes it with - against the walking session's collision, so it cannot pass through a wall, a post, a
/// doorway's jamb or a terrain edge, and stands on whatever floor the step finds. Only the heading and how
/// far it wants to go are the creature's; the collision is the Engine's.
/// </summary>
internal static class CreatureBody
{
    /// <summary>The body reaches its pace quickly and stops as quickly: a creature does not slide past where it means to stand.</summary>
    private const float Acceleration = 40f, Braking = 40f;

    /// <summary>A step longer than this is split, so a slow update cannot carry a body through a thin wall.</summary>
    internal const float MaximumStepSeconds = 1f / 20f;

    /// <summary>How high a creature steps: the ground's own terraces are up to two blocks.</summary>
    internal const uint MaximumStepCells = 2U;
    internal const double StepMetres = TerrainConstants.VoxelSize * MaximumStepCells;

    /// <summary>A capsule the player's width and a little taller.</summary>
    private const float Radius = 0.3f, Height = 1.8f, MaximumSlopeDegrees = 45f;

    /// <summary>How far a creature's standing capsule is held clear of what it stands on.</summary>
    private const float ContactSkin = 0.02f;

    /// <summary>A creature's body as navigation routes it and its step moves it: its capsule, stepping the ground's two-block terraces.</summary>
    internal static CharacterControllerConfig Capsule(CharacterControllerConfig baseline) => baseline with
    {
        Shape = baseline.Shape with { Radius = Radius, StandingHeight = Height, ContactSkin = ContactSkin },
        Surface = baseline.Surface with
        {
            MaximumStepHeight = (float)StepMetres,
            MaximumSlopeRadians = Player.Angles.ToRadians(MaximumSlopeDegrees),
        },
    };

    /// <summary>The capsule, moving at a creature kind's pursuit speed.</summary>
    internal static CharacterControllerConfig Configure(CharacterControllerConfig navigationBody, float speed) => navigationBody with
    {
        Ground = navigationBody.Ground with
        {
            ForwardSpeed = speed,
            BackwardSpeed = speed,
            StrafeSpeed = speed,
            Acceleration = Acceleration,
            Braking = Braking,
        },
        Air = navigationBody.Air with { MaximumSpeed = speed, WishSpeedCap = speed },
    };

    /// <summary>The capsule's centre over feet at a session-local point.</summary>
    internal static Vector3 CentreOver(Vector3 feet, CharacterControllerConfig body) => feet + (Vector3.UnitY * (body.Shape.StandingHeight / 2f));

    /// <summary>The feet under a capsule's centre.</summary>
    internal static Vector3 FeetUnder(Vector3 centre, CharacterControllerConfig body) => centre - (Vector3.UnitY * (body.Shape.StandingHeight / 2f));

    /// <summary>A body standing still where it is placed.</summary>
    internal static CharacterMotion AtRest(Vector3 centre) => new(
        Vector3.Zero, Vector3.Zero, false, CharacterStance.Standing, 0f, 0f, 0f, false, 0UL,
        Vector3.Zero, Vector3.Zero, Quaternion.Identity, Vector3.Zero, centre.Y, centre.Y, 0UL, 0UL);

    /// <summary>
    /// The facing that walks a body along a planar direction (session X, Z): yaw 0 faces -Z, a quarter
    /// turn faces +X, as the camera and the player's look count it.
    /// </summary>
    internal static float Yaw(Vector2 direction) => MathF.Atan2(direction.X, -direction.Y);

    /// <summary>
    /// Moves a body toward a point on the plane for some seconds, at most <paramref name="travel"/> metres,
    /// in steps no longer than <see cref="MaximumStepSeconds"/>. Null when the Engine refuses the first step
    /// because the body is lodged in collision; the caller leaves it where it was, and stuck detection replans.
    /// </summary>
    internal static CreatureBodyState? Step(ISpatialService spatial, SpatialSession session, CreatureBodyState body,
        CharacterControllerConfig config, Vector2 toward, double travel, double seconds, Func<ulong> sequence)
    {
        CreatureBodyState state = body;
        int steps = Math.Max(1, (int)Math.Ceiling(seconds / MaximumStepSeconds));
        float stepSeconds = (float)(seconds / steps);
        double remaining = travel;
        for (int i = 0; i < steps; i++)
        {
            Vector2 offset = toward - new Vector2(state.Centre.X, state.Centre.Z);
            double leg = Math.Min(offset.Length(), remaining);
            float pace = config.Ground.ForwardSpeed * stepSeconds;
            // Short of its pace near where it means to stand, it asks for only the share of a full stride it needs.
            float share = leg <= 1e-4 || pace <= 0 ? 0f : (float)Math.Min(1d, leg / pace);
            CharacterControllerCommand command = new(
                share > 0 ? new Vector2(0f, share) : Vector2.Zero,
                share > 0 ? Yaw(offset) : 0f,
                false, false, false, Vector3.Zero, Vector3.Zero, stepSeconds, sequence());
            CharacterStepReceipt receipt;
            try
            {
                receipt = spatial.ProposeCharacterStep(new CharacterStepRequest(session, state.Centre, state.Motion, default,
                    ReadOnlyMemory<CharacterObstacle>.Empty, ReadOnlyMemory<CharacterMeshInstance>.Empty, config, command));
            }
            catch (EngineCallException refusal) when (Player.PlayerRecovery.IsPenetration(refusal))
            {
                if (i == 0) return null;
                break;
            }

            Vector3 moved = receipt.Transform.Translation - state.Centre;
            remaining -= new Vector2(moved.X, moved.Z).Length();
            state = new CreatureBodyState(receipt.Transform.Translation, receipt.Motion);
        }

        return state;
    }
}
