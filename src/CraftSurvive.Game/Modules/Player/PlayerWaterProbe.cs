using System.Numerics;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>The last water decision: where it was read, what was found, and (from the water owner) the surface.</summary>
internal readonly record struct PlayerWaterCheck(
    VoxelAddress Feet,
    bool FeetPresent,
    uint FeetSlot,
    VoxelAddress Eyes,
    bool FeetWater,
    bool EyesWater,
    double Surface = double.NaN,
    WaterBody Body = WaterBody.None)
{
    internal bool InWater => FeetWater || EyesWater;

    public override string ToString() => FormattableString.Invariant(
        $"feet=({Feet.X},{Feet.Y},{Feet.Z});feetPresent={FeetPresent};feetSlot={FeetSlot};eyes=({Eyes.X},{Eyes.Y},{Eyes.Z});feetWater={FeetWater};eyesWater={EyesWater};surface={Surface:F2};body={Body}");
}

/// <summary>
/// Decides whether the player is in water and, when they are, composes the swim movement around
/// them. The product owns the volume and the decision; the Engine owns the solver, and measures
/// immersion and a submerged head against the volume's top. In the overworld the water owner says
/// where water stands, at its real surface (#9877); a separate space (a dungeon, a study) still reads
/// water cells, as its own content may hold them.
/// </summary>
internal sealed class PlayerWaterProbe(IEngineContext engine)
{
    private static readonly ushort WaterSlot = (ushort)Content.BlockId.Water;

    /// <summary>The swim volume reaches this far below the bed, so a body standing on it is wholly inside.</summary>
    private const float BelowBed = 2f;

    /// <summary>Feet this close under a frozen surface are on the ice, not in the water below it.</summary>
    private const double IceThickness = 0.3;

    internal PlayerWaterCheck LastCheck { get; private set; }

    /// <param name="waters">The overworld's water owner; null in a separate space, which reads water cells.</param>
    internal bool TrySwim(SpatialSession session, PlayerWorldPosition world, Vector3 local, CharacterStance stance,
        out CharacterMovementRequest movement, WaterModel? waters = null)
    {
        movement = default;
        double feetY = world.WorldY - PlayerBody.EyeOffset(stance);
        VoxelAddress feet = new((long)Math.Floor(world.WorldX), (long)Math.Floor(feetY), (long)Math.Floor(world.WorldZ));
        VoxelAddress eyes = new((long)Math.Floor(world.WorldX), (long)Math.Floor(world.WorldY), (long)Math.Floor(world.WorldZ));
        if (waters is not null)
        {
            WaterSample sample = waters.Sample(world.WorldX, world.WorldZ);
            // On ice (#9878) the player stands on the ice's collision; only one already under it swims.
            bool onIce = sample.Ice && feetY >= sample.Top - IceThickness;
            bool feetIn = sample.Wet && !onIce && feetY < sample.Top, eyesIn = sample.Wet && !onIce && world.WorldY < sample.Top;
            LastCheck = new PlayerWaterCheck(feet, false, 0, eyes, feetIn, eyesIn, sample.Top, sample.Body);
            if (!LastCheck.InWater) return false;

            // The volume's top is the water's surface, carried into the local frame by the eye's offset.
            float top = local.Y + (float)(sample.Top - world.WorldY);
            float bottom = top - (float)sample.Depth - BelowBed;
            movement = Swim(new Vector3(local.X, bottom, local.Z), new Vector3(local.X, top, local.Z));
            return true;
        }

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

        float cellFloor = MathF.Floor(local.Y);
        movement = Swim(new Vector3(local.X, cellFloor, local.Z), new Vector3(local.X, cellFloor + PlayerConstants.WaterHeight, local.Z));
        return true;
    }

    /// <summary>The swim movement in a volume about the player's column, from a floor to a top.</summary>
    private static CharacterMovementRequest Swim(Vector3 floor, Vector3 top) => new()
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
