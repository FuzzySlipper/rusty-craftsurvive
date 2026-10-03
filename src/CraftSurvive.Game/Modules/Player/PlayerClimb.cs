using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// A vertical rail up the face in front of the player, in global coordinates: where the body
/// hangs along the face, and the feet heights it climbs between.
/// </summary>
internal readonly record struct ClimbRail(double X, double Z, double BottomFeetY, double TopFeetY);

/// <summary>How the player stands with a face: not holding one, taking hold this step, or holding on.</summary>
internal enum ClimbGrip
{
    None,
    Taking,
    Holding,
}

/// <summary>
/// The climbing rule: which face the player may climb and the rail up it. Climbing is deliberate:
/// the player takes hold with the climb action (<see cref="PlayerStamina"/> decides whether they
/// have the strength), never by walking into a wall. A face is climbable when the block it belongs
/// to says so (<see cref="BlockDefinition.Climbable"/>) and it rises past a step, so the
/// controller's own step still takes a single block. The rail runs from the cell
/// below the feet to the top of the face's climbable run, and the top is where the feet clear the
/// ledge, so walking on at the top steps onto it. The Engine solves the climb; this decides it.
/// </summary>
internal static class PlayerClimb
{
    /// <summary>How close the body's surface must be to the face to take hold of it.</summary>
    internal const double HoldReachMetres = 0.5d;

    /// <summary>How far the body hangs off the face, beyond its radius.</summary>
    internal const double StandoffMetres = 0.03d;

    /// <summary>The most cells a single rail climbs; a taller face is climbed a rail at a time.</summary>
    internal const int MaximumRailCells = 32;

    /// <summary>Keeps a body standing exactly on a cell boundary in that cell.</summary>
    private const double FeetCellEpsilon = 1e-3d;

    private const float FacingEpsilon = 1e-4f;

    /// <summary>
    /// The rail the player climbs, or null when they should walk: neither holding on nor taking hold,
    /// no climbable face in front within reach, or stepping back onto ground. Taking hold needs the
    /// face to rise past a step; holding on keeps the top cell to the ledge. <paramref name="forwardIntent"/>
    /// moves the player along the face. <paramref name="material"/> reads the block at a global cell.
    /// </summary>
    internal static ClimbRail? Find(
        double x,
        double feetY,
        double z,
        Vector2 facing,
        float forwardIntent,
        ClimbGrip grip,
        Func<long, long, long, BlockId> material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (facing.LengthSquared() < FacingEpsilon || grip == ClimbGrip.None)
        {
            return null;
        }

        // The face is the one the player looks toward along the nearer axis.
        bool alongX = MathF.Abs(facing.X) >= MathF.Abs(facing.Y);
        int sign = alongX ? Math.Sign(facing.X) : Math.Sign(facing.Y);
        long cellX = (long)Math.Floor(x);
        long cellZ = (long)Math.Floor(z);
        long feet = (long)Math.Floor(feetY + FeetCellEpsilon);
        long wallX = alongX ? cellX + sign : cellX;
        long wallZ = alongX ? cellZ : cellZ + sign;
        double position = alongX ? x : z;
        double face = (alongX ? cellX : cellZ) + (sign > 0 ? 1d : 0d);
        if (Math.Abs(face - position) > PlayerConstants.CapsuleRadius + HoldReachMetres)
        {
            return null;
        }

        // Stepping back onto ground lets go.
        if (forwardIntent < 0f && BlockRegistry.Get(material(cellX, feet - 1, cellZ)).Collidable)
        {
            return null;
        }

        bool climbable(long y) => BlockRegistry.Get(material(wallX, y, wallZ)).Climbable;
        if (!climbable(feet + 1) && !(grip == ClimbGrip.Holding && climbable(feet)))
        {
            return null;
        }

        long top = feet;
        while (top < feet + MaximumRailCells && climbable(top + 1))
        {
            top++;
        }

        double rail = face - (sign * (PlayerConstants.CapsuleRadius + StandoffMetres));
        return new ClimbRail(
            alongX ? rail : x,
            alongX ? z : rail,
            feet - 1,
            top + 1 + PlayerConstants.SpawnClearance);
    }
}
