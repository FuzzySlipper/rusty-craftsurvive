using System.Numerics;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// Textured box geometry for built pieces: each face its own quad with a flat normal, UVs planar in
/// the box's own frame in metres over <see cref="MetresPerTile"/> (so plank grain and shingle rows
/// follow the piece, a roof slab's down its slope), wound anticlockwise from outside.
/// </summary>
internal static class BoxMesh
{
    /// <summary>Metres one construction map covers (scripts/generate-construction-textures.py).</summary>
    internal const float MetresPerTile = 2f;

    internal static void Add(PieceBox box, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<uint> indices)
    {
        Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, box.Pitch);
        Vector3 half = box.Half;
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        for (int axis = 0; axis < 3; axis++)
        {
            foreach (float sign in new[] { -1f, 1f })
            {
                Vector3 normal = axes[axis] * sign;
                Vector3 u = axes[(axis + 1) % 3], v = axes[(axis + 2) % 3];
                float hu = Component(half, (axis + 1) % 3), hv = Component(half, (axis + 2) % 3), hn = Component(half, axis);
                uint first = (uint)positions.Count;
                foreach ((float a, float b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    Vector3 local = (normal * hn) + (u * a * hu) + (v * b * hv);
                    positions.Add(box.Centre + Vector3.Transform(local, pitch));
                    normals.Add(Vector3.Transform(normal, pitch));
                    // Side faces map across and up (rows run horizontally); top and bottom across and along.
                    Vector2 uv = axis == 1
                        ? new Vector2(local.X, local.Z)
                        : new Vector2(axis == 0 ? local.Z : local.X, -local.Y);
                    uvs.Add(uv / MetresPerTile);
                }

                // u x v is the face's axis, so the corners run anticlockwise seen from its positive side.
                indices.AddRange(sign > 0
                    ? [first, first + 1, first + 2, first, first + 2, first + 3]
                    : [first, first + 2, first + 1, first, first + 3, first + 2]);
            }
        }
    }

    private static float Component(Vector3 vector, int axis) => axis switch { 0 => vector.X, 1 => vector.Y, _ => vector.Z };
}
