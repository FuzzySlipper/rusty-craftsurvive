using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>The last water decision: the global cells read and what the Engine reported there.</summary>
internal readonly record struct PlayerWaterCheck(
    VoxelAddress Feet,
    bool FeetPresent,
    uint FeetSlot,
    VoxelAddress Eyes,
    bool FeetWater,
    bool EyesWater)
{
    internal bool InWater => FeetWater || EyesWater;

    public override string ToString() => FormattableString.Invariant(
        $"feet=({Feet.X},{Feet.Y},{Feet.Z});feetPresent={FeetPresent};feetSlot={FeetSlot};eyes=({Eyes.X},{Eyes.Y},{Eyes.Z});feetWater={FeetWater};eyesWater={EyesWater}");
}

/// <summary>
/// Decides whether the player is in water and, when they are, composes the swim movement around
/// them. The product owns the volume and the decision; the Engine owns the solver.
/// </summary>
internal sealed class PlayerWaterProbe(IEngineContext engine)
{
    private static readonly ushort WaterSlot = (ushort)Content.BlockId.Water;

    internal PlayerWaterCheck LastCheck { get; private set; }

    /// <summary>
    /// Reads the cell the player stands in, and the cell their eye is in when that one is dry, so
    /// waist-deep water swims as well as a submerged head. Voxel addresses are global cells, so
    /// they come from the world position; the swim volume is a position and so stays local.
    /// </summary>
    internal bool TrySwim(SpatialSession session, PlayerWorldPosition world, Vector3 local, CharacterStance stance,
        out CharacterMovementRequest movement)
    {
        movement = default;
        VoxelAddress feet = new(
            (long)Math.Floor(world.WorldX),
            (long)Math.Floor(world.WorldY - PlayerBody.EyeOffset(stance)),
            (long)Math.Floor(world.WorldZ));
        VoxelAddress eyes = new(
            (long)Math.Floor(world.WorldX),
            (long)Math.Floor(world.WorldY),
            (long)Math.Floor(world.WorldZ));
        VoxelReadout feetRead = engine.Voxel.Read(new VoxelReadRequest(session, feet));
        bool feetWater = feetRead.Present && feetRead.MaterialSlot == WaterSlot;
        bool eyesWater = false;
        if (!feetWater)
        {
            VoxelReadout eyesRead = engine.Voxel.Read(new VoxelReadRequest(session, eyes));
            eyesWater = eyesRead.Present && eyesRead.MaterialSlot == WaterSlot;
        }

        LastCheck = new PlayerWaterCheck(feet, feetRead.Present, feetRead.MaterialSlot, eyes, feetWater, eyesWater);
        if (!LastCheck.InWater)
        {
            return false;
        }

        Vector3 center = new(local.X, MathF.Floor(local.Y), local.Z);
        movement = new CharacterMovementRequest
        {
            Mode = CharacterMovementMode.Swimming,
            VerticalIntent = PlayerConstants.WaterVerticalNeutral,
            Speed = PlayerConstants.WaterSpeed,
            Acceleration = PlayerConstants.WaterAcceleration,
            Drag = PlayerConstants.WaterDrag,
            Minimum = center - new Vector3(PlayerConstants.WaterExtent, 0f, PlayerConstants.WaterExtent),
            Maximum = center + new Vector3(PlayerConstants.WaterExtent, PlayerConstants.WaterHeight, PlayerConstants.WaterExtent),
            GravityScale = PlayerConstants.WaterGravityScale,
            Buoyancy = PlayerConstants.WaterBuoyancy,
            ClimbReach = PlayerConstants.NoClimbReach,
        };
        return true;
    }
}
