using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// Decides whether the player climbs the face in front of them, given how they hold it, and when
/// they do composes the climb movement up its rail. <see cref="PlayerClimb"/> owns the rule; the Engine owns the solver.
/// </summary>
internal sealed class PlayerClimbProbe(IEngineContext engine)
{
    /// <summary>The last rail composed, or null when the player last walked.</summary>
    internal ClimbRail? LastRail { get; private set; }

    /// <summary>
    /// Composes a climb when the player holds, or takes hold of, a climbable face in front of them.
    /// Rail heights are global feet heights, so they are moved into the local frame by the player's
    /// own offset; the rail's position along the face is local the same way.
    /// </summary>
    internal bool TryClimb(SpatialSession session, PlayerWorldPosition world, Vector3 local, CharacterStance stance,
        Vector3 forward, float forwardIntent, ClimbGrip grip, out CharacterMovementRequest movement)
    {
        movement = default;
        float halfHeight = PlayerBody.Height(stance) / 2f;
        double feetY = world.WorldY - halfHeight;
        LastRail = stance == CharacterStance.Crouched
            ? null
            : PlayerClimb.Find(world.WorldX, feetY, world.WorldZ, new Vector2(forward.X, forward.Z), forwardIntent, grip,
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

    // --- Natural rock (#10126): the face read by rays rather than by blocks ---

    /// <summary>How far apart the looks up a natural face are, and the highest a single climb reads.</summary>
    private const double SampleSpacingMetres = 0.5d, HighestSampleMetres = 24d;

    /// <summary>The last natural face read, or null when the player last walked or climbed blocks.</summary>
    internal ClimbFace? LastFace { get; private set; }

    /// <summary>
    /// Composes a climb up the natural face in front, read by looking at it every half metre up from the feet: its
    /// steepness, whether it leans out, what it is made of, and where it gives way to a ledge (<see cref="ClimbHolds"/>).
    /// The rail stands off the face where it is first held; the Engine solves the climb up it.
    /// </summary>
    internal bool TryClimbNatural(SpatialSession session, Vector3 local, CharacterStance stance, Vector3 forward, float forwardIntent, ClimbGrip grip,
        out CharacterMovementRequest movement)
    {
        movement = default;
        LastRail = null;
        LastFace = null;
        Vector2 flat = new(forward.X, forward.Z);
        if (grip == ClimbGrip.None || stance == CharacterStance.Crouched || flat.LengthSquared() < 1e-6f)
        {
            return false;
        }

        Vector3 toward = Vector3.Normalize(new Vector3(flat.X, 0f, flat.Y));
        Vector3 feet = local - new Vector3(0f, PlayerBody.Height(stance) / 2f, 0f);
        (List<ClimbSample> samples, Vector3? heldAt) = Look(session, feet, toward);
        ClimbFace face = ClimbHolds.Read(samples);
        LastFace = face;
        if (!face.Holdable || heldAt is not Vector3 at || face.TopHeight <= 0d)
        {
            return false;
        }

        Vector3 railLocal = new(at.X - (toward.X * (PlayerConstants.CapsuleRadius + (float)PlayerClimb.StandoffMetres)), feet.Y,
            at.Z - (toward.Z * (PlayerConstants.CapsuleRadius + (float)PlayerClimb.StandoffMetres)));
        float height = (float)face.TopHeight + (face.TopsOut ? PlayerConstants.SpawnClearance : 0f);
        LastRail = new ClimbRail(railLocal.X, railLocal.Z, feet.Y, feet.Y + height);
        movement = new CharacterMovementRequest
        {
            Mode = CharacterMovementMode.Climbing,
            VerticalIntent = Math.Clamp(forwardIntent, -1f, 1f),
            Speed = PlayerConstants.ClimbSpeed,
            Minimum = railLocal,
            Maximum = railLocal + new Vector3(0f, height, 0f),
            ClimbReach = PlayerConstants.ClimbReach,
        };
        return true;
    }

    /// <summary>The looks up the face: what is within reach in front at each height, and where the first hold was found.</summary>
    private (List<ClimbSample> Samples, Vector3? HeldAt) Look(SpatialSession session, Vector3 feet, Vector3 toward)
    {
        List<ClimbSample> samples = [];
        Vector3? heldAt = null;
        double reach = PlayerConstants.CapsuleRadius + PlayerClimb.HoldReachMetres;
        for (double height = SampleSpacingMetres * 0.5d; height <= HighestSampleMetres; height += SampleSpacingMetres)
        {
            Vector3 origin = feet + new Vector3(0f, (float)height, 0f);
            SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(session, origin, toward, reach,
                new SpatialQueryFilter(Terrain.TerrainConstants.CollisionGroupAll, Terrain.TerrainConstants.CollisionMaskAll),
                ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
            if (!hit.Present)
            {
                samples.Add(new ClimbSample(height, null, Vector3.Zero, BlockId.Air));
                if (heldAt is not null)
                {
                    break;
                }

                continue;
            }

            BlockId block = hit.Kind == SpatialHitKind.Voxel ? Material(session, hit.VoxelX, hit.VoxelY, hit.VoxelZ) : BlockId.Stone;
            samples.Add(new ClimbSample(height, Math.Max(0d, hit.Distance - PlayerConstants.CapsuleRadius), hit.Normal, block));
            if (heldAt is null && hit.Normal.Y <= ClimbHolds.SteepestHoldUp && hit.Normal.Y >= ClimbHolds.OverhangDown)
            {
                heldAt = hit.Point;
            }
        }

        return (samples, heldAt);
    }

    /// <summary>A diagnostic read of the natural face in front, without climbing (#10126).</summary>
    internal string Describe(SpatialSession session, Vector3 local, Vector3 forward)
    {
        Vector3 toward = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));
        Vector3 feet = local - new Vector3(0f, PlayerBody.Height(CharacterStance.Standing) / 2f, 0f);
        (List<ClimbSample> samples, _) = Look(session, feet, toward);
        ClimbFace face = ClimbHolds.Read(samples);
        string looks = string.Join(" ", samples.Take(16).Select(s => s.Distance is double d
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{s.Height:F1}:{s.Block}/{s.Normal.Y:F2}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{s.Height:F1}:air")));
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"face holdable={face.Holdable} top={face.TopHeight:F1}m topsOut={face.TopsOut} grip={face.Grip:F2} stop={face.Stop}; looks {looks}");
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
