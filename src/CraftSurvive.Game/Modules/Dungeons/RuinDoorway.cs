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
}
