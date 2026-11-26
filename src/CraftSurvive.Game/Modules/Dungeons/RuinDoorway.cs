namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A ruin's doorway (#10961): <paramref name="Floor"/> is the middle of its opening on its floor (layout voxels or, once
/// placed, a space's metres), and <paramref name="FacesX"/> whether one passes through it along x. The kit's doorways are
/// <see cref="Width"/> across and <see cref="Height"/> tall.
/// </summary>
internal readonly record struct RuinDoorway(System.Numerics.Vector3 Floor, bool FacesX)
{
    internal const float Width = 2f, Height = 3f;

    /// <summary>The way through it, along the ground.</summary>
    internal System.Numerics.Vector2 Through => FacesX ? System.Numerics.Vector2.UnitX : System.Numerics.Vector2.UnitY;

    /// <summary>
    /// The doorway carried into another frame by a placement (#11097: a cave's under the world, turned a quarter at a time):
    /// its floor point carried, and its way through turned with it, so a quarter turn makes one passed along x one passed along z.
    /// </summary>
    internal RuinDoorway Placed(System.Func<System.Numerics.Vector3, System.Numerics.Vector3> toWorld)
    {
        System.Numerics.Vector3 floor = toWorld(Floor);
        System.Numerics.Vector3 through = toWorld(Floor + new System.Numerics.Vector3(Through.X, 0, Through.Y)) - floor;
        return new RuinDoorway(floor, System.MathF.Abs(through.X) > System.MathF.Abs(through.Z));
    }
}
