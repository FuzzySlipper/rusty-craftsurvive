using System.Numerics;

namespace CraftSurvive.Game.Modules.Terrain.Recipes;

// Product-authored local coordinates: X follows the wall, Y is up and the
// decorated face points toward -Z. The same placement carries every layer.
internal readonly record struct WallLayout(Vector3 Origin, float YawDegrees, float Length,
    float Height, float Thickness, WallOpening? Opening = null)
{
    internal Quaternion Rotation => Quaternion.CreateFromAxisAngle(Vector3.UnitY, YawDegrees * MathF.PI / 180f);
    internal Vector3 ToWorld(Vector3 local) => Origin + Vector3.Transform(local, Rotation);
}

internal readonly record struct WallOpening(float Center, float Width, float SpringHeight, bool Arched)
{
    internal float Left => Center - Width * 0.5f;
    internal float Right => Center + Width * 0.5f;
    internal float Top => SpringHeight + (Arched ? Width * 0.5f : 0f);
}

internal readonly record struct MasonryCourse(float Bottom, float Top, float Left, float Right);

internal static class MasonryLayout
{
    internal static IEnumerable<MasonryCourse> Courses(float start, float end, float height,
        float length, float courseHeight, float joint)
    {
        for (int row = 0; row * courseHeight < height; row++)
        {
            float phase = (row % 2) * length * 0.5f;
            for (float x = MathF.Floor((start - phase) / length) * length + phase; x < end; x += length)
            {
                float left = MathF.Max(start, x + joint);
                float right = MathF.Min(end, x + length - joint);
                float bottom = row * courseHeight + joint;
                float top = MathF.Min(height, (row + 1) * courseHeight - joint);
                if (right > left && top > bottom) yield return new(bottom, top, left, right);
            }
        }
    }
}
