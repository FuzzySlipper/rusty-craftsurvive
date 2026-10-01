using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// Decides whether the player climbs the face in front of them and, when they do, composes the
/// climb movement up its rail. <see cref="PlayerClimb"/> owns the rule; the Engine owns the solver.
/// </summary>
internal sealed class PlayerClimbProbe(IEngineContext engine)
{
    /// <summary>The last rail composed, or null when the player last walked.</summary>
    internal ClimbRail? LastRail { get; private set; }

    /// <summary>
    /// Composes a climb when the player faces a climbable face. Rail heights are global feet
    /// heights, so they are moved into the local frame by the player's own offset; the rail's
    /// position along the face is local the same way.
    /// </summary>
    internal bool TryClimb(SpatialSession session, PlayerWorldPosition world, Vector3 local, CharacterStance stance,
        Vector3 forward, float forwardIntent, bool holding, out CharacterMovementRequest movement)
    {
        movement = default;
        float halfHeight = PlayerBody.Height(stance) / 2f;
        double feetY = world.WorldY - halfHeight;
        LastRail = stance == CharacterStance.Crouched
            ? null
            : PlayerClimb.Find(world.WorldX, feetY, world.WorldZ, new Vector2(forward.X, forward.Z), forwardIntent, holding,
                (x, y, z) => Material(session, x, y, z));
        if (LastRail is not ClimbRail rail)
        {
            return false;
        }

        Vector3 railLocal = new(
            local.X + (float)(rail.X - world.WorldX),
            local.Y + (float)(rail.BottomFeetY - feetY),
            local.Z + (float)(rail.Z - world.WorldZ));
        float railHeight = (float)(rail.TopFeetY - rail.BottomFeetY);
        movement = new CharacterMovementRequest
        {
            Mode = CharacterMovementMode.Climbing,
            VerticalIntent = Math.Clamp(forwardIntent, -1f, 1f),
            Speed = PlayerConstants.ClimbSpeed,
            Minimum = railLocal,
            Maximum = railLocal + new Vector3(0f, railHeight, 0f),
            ClimbReach = PlayerConstants.ClimbReach,
        };
        return true;
    }

    private BlockId Material(SpatialSession session, long x, long y, long z)
    {
        VoxelReadout read = engine.Voxel.Read(new VoxelReadRequest(session, new VoxelAddress(x, y, z)));
        return read.Present && read.MaterialSlot <= ushort.MaxValue
            && BlockRegistry.TryGetBySlot((ushort)read.MaterialSlot, out BlockDefinition block)
            ? block.Id
            : BlockId.Air;
    }
}
