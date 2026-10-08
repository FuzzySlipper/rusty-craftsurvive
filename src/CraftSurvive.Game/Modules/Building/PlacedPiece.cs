using System.Numerics;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// A piece standing in the world: its kind and material, its anchor on the placement grid in
/// <see cref="PlacedPiece.GridMetres"/> steps (world coordinates, not the walking frame's), and how
/// many quarter turns about Y it is turned.
/// </summary>
internal readonly record struct PlacedPiece(PieceKind Kind, PieceMaterial Material, long X, long Y, long Z, byte Turn)
    : IComparable<PlacedPiece>
{
    /// <summary>The placement grid: a quarter metre on every axis.</summary>
    internal const float GridMetres = 0.25f;
    internal const int Turns = 4;

    /// <summary>The anchor in world metres.</summary>
    internal Vector3 Anchor => new(X * GridMetres, Y * GridMetres, Z * GridMetres);

    internal Quaternion Rotation => Quaternion.CreateFromAxisAngle(Vector3.UnitY, Turn * MathF.PI / 2f);

    /// <summary>A box's centre in world metres and its rotation (the piece's turn, then its own pitch).</summary>
    internal (Vector3 Centre, Quaternion Rotation, Vector3 Half) World(PieceBox box)
    {
        Quaternion turn = Rotation;
        return (Anchor + Vector3.Transform(box.Centre, turn), turn * Quaternion.CreateFromAxisAngle(Vector3.UnitX, box.Pitch), box.Half);
    }

    /// <summary>Canonical order for the save: by position, then kind and turn.</summary>
    public int CompareTo(PlacedPiece other)
    {
        int order = X.CompareTo(other.X);
        if (order == 0) order = Y.CompareTo(other.Y);
        if (order == 0) order = Z.CompareTo(other.Z);
        if (order == 0) order = Kind.CompareTo(other.Kind);
        if (order == 0) order = Turn.CompareTo(other.Turn);
        return order == 0 ? Material.CompareTo(other.Material) : order;
    }
}

/// <summary>Where a ray meets a piece: which piece, how far along, the point and the face's normal (world).</summary>
internal readonly record struct PieceHit(int Index, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>Ray and box geometry for pieces, and where a new piece goes when aimed at a surface.</summary>
internal static class PieceGeometry
{
    /// <summary>A placement nudges this far off the aimed surface before snapping, so it lands on the open side.</summary>
    private const float SurfaceNudge = 0.05f;

    /// <summary>
    /// The nearest piece box a ray meets within reach. Each box is tested in its own frame
    /// (slab method), so pitched roof slabs are exact.
    /// </summary>
    internal static PieceHit? Cast(IReadOnlyList<PlacedPiece> pieces, Vector3 origin, Vector3 direction, float reach)
    {
        PieceHit? best = null;
        for (int index = 0; index < pieces.Count; index++)
        {
            PlacedPiece piece = pieces[index];
            if (Vector3.DistanceSquared(piece.Anchor, origin) > (reach + 4f) * (reach + 4f)) continue;
            foreach (PieceBox box in PieceCatalog.Boxes(piece.Kind))
            {
                (Vector3 centre, Quaternion rotation, Vector3 half) = piece.World(box);
                if (RayBox(origin, direction, centre, rotation, half, out float distance, out Vector3 normal)
                    && distance <= reach && (best is not PieceHit current || distance < current.Distance))
                {
                    best = new PieceHit(index, distance, origin + (direction * distance), normal);
                }
            }
        }

        return best;
    }

    /// <summary>A ray against an oriented box: the entry distance and the world normal of the face entered.</summary>
    internal static bool RayBox(Vector3 origin, Vector3 direction, Vector3 centre, Quaternion rotation, Vector3 half,
        out float distance, out Vector3 normal)
    {
        Quaternion inverse = Quaternion.Inverse(rotation);
        Vector3 o = Vector3.Transform(origin - centre, inverse), d = Vector3.Transform(direction, inverse);
        float near = float.NegativeInfinity, far = float.PositiveInfinity;
        int axis = -1;
        float sign = 0f;
        for (int a = 0; a < 3; a++)
        {
            float oa = Component(o, a), da = Component(d, a), ha = Component(half, a);
            if (MathF.Abs(da) < 1e-8f)
            {
                if (MathF.Abs(oa) > ha) { distance = 0; normal = default; return false; }
                continue;
            }

            float t1 = (-ha - oa) / da, t2 = (ha - oa) / da;
            float enter = MathF.Min(t1, t2), exit = MathF.Max(t1, t2);
            if (enter > near)
            {
                near = enter;
                axis = a;
                sign = da > 0 ? -1f : 1f;
            }

            far = MathF.Min(far, exit);
        }

        if (axis < 0 || near > far || far < 0 || near < 0)
        {
            distance = 0;
            normal = default;
            return false;
        }

        distance = near;
        normal = Vector3.Transform(Axis(axis) * sign, rotation);
        return true;
    }

    /// <summary>
    /// Where a piece of this kind goes when aimed at a surface point with this normal, the player
    /// facing <paramref name="facing"/>: snapped to the grid, standing on the surface (or, against a
    /// wall's face, set out from it), turned so its +Z faces back toward the player.
    /// </summary>
    internal static PlacedPiece Place(PieceKind kind, PieceMaterial material, Vector3 point, Vector3 normal, Vector3 facing)
    {
        byte turn = TurnFacing(-facing);
        Vector3 anchor = point + (normal * SurfaceNudge);
        bool againstSide = MathF.Abs(normal.Y) < 0.5f;
        if (againstSide && kind is PieceKind.Floor)
        {
            // A floor against a wall reaches out from it.
            anchor += normal * (PieceCatalog.FloorSide / 2);
        }

        if (!againstSide && normal.Y < 0)
        {
            // Aimed at an underside: hang from it rather than sink into it.
            anchor.Y -= kind is PieceKind.Beam ? PieceCatalog.BeamSide : PieceCatalog.WallHeight;
        }

        return new PlacedPiece(kind, material, Snap(anchor.X), SnapUp(anchor.Y, normal.Y), Snap(anchor.Z), turn);
    }

    /// <summary>The quarter turn whose rotated +Z is nearest this horizontal direction.</summary>
    internal static byte TurnFacing(Vector3 direction)
    {
        float angle = MathF.Atan2(direction.X, direction.Z);
        int turn = (int)MathF.Round(angle / (MathF.PI / 2f));
        return (byte)(((turn % PlacedPiece.Turns) + PlacedPiece.Turns) % PlacedPiece.Turns);
    }

    private static long Snap(float metres) => (long)MathF.Round(metres / PlacedPiece.GridMetres);

    /// <summary>Upward-facing surfaces round the footing up a step, so a piece never sinks into what it stands on.</summary>
    private static long SnapUp(float metres, float normalY) =>
        normalY > 0.5f ? (long)MathF.Ceiling((metres - SurfaceNudge) / PlacedPiece.GridMetres) : Snap(metres);

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static Vector3 Axis(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
}
