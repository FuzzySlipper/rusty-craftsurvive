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

    /// <summary>
    /// Where a piece of this kind goes when aimed at a host piece (#9730): at the host's socket that
    /// accepts the kind and lies nearest the aimed point, turned as the socket says; or null when the
    /// host has no socket for the kind.
    /// </summary>
    internal static PlacedPiece? Snap(PlacedPiece host, PieceKind kind, PieceMaterial material, Vector3 aimPoint, Vector3 facing)
    {
        PieceSocket? best = null;
        Vector3 bestAt = default;
        float bestDistance = float.MaxValue;
        Quaternion turn = host.Rotation;
        foreach (PieceSocket socket in PieceCatalog.SocketsOf(host.Kind))
        {
            if (Array.IndexOf(socket.Accepts, kind) < 0) continue;
            Vector3 at = host.Anchor + Vector3.Transform(socket.Offset, turn);
            float distance = Vector3.DistanceSquared(at, aimPoint);
            if (distance < bestDistance)
            {
                best = socket;
                bestAt = at;
                bestDistance = distance;
            }
        }

        if (best is not PieceSocket chosen) return null;
        byte placed;
        if (chosen.Facing)
        {
            placed = TurnFacing(-facing);
        }
        else
        {
            int turned = (host.Turn + chosen.Turn) % PlacedPiece.Turns;
            if (chosen.Flips)
            {
                // Of the two opposite turns, the one whose +Z faces back toward the player.
                Vector3 front = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turned * MathF.PI / 2f));
                if (Vector3.Dot(front, -facing) < 0) turned = (turned + 2) % PlacedPiece.Turns;
            }

            placed = (byte)turned;
        }

        return new PlacedPiece(kind, material, Snap(bestAt.X), Snap(bestAt.Y), Snap(bestAt.Z), placed);
    }

    /// <summary>
    /// A piece's boxes as world-axis bounds (centre, half extents): exact for the quarter turns, and
    /// a pitched slab as <see cref="PitchedSlices"/> stepped boxes along its slope. The character
    /// step collides with these (it takes no rotation), and overlap is judged on them.
    /// </summary>
    internal static IEnumerable<(Vector3 Centre, Vector3 Half)> Bounds(PlacedPiece piece)
    {
        foreach (PieceBox box in PieceCatalog.Boxes(piece.Kind))
        {
            foreach (PieceBox part in box.Pitch == 0 ? [box] : Slices(box))
            {
                (Vector3 centre, Quaternion rotation, Vector3 half) = piece.World(part);
                yield return (centre, Extent(rotation, half));
            }
        }
    }

    /// <summary>How many stepped boxes stand in for a pitched slab.</summary>
    internal const int PitchedSlices = 4;

    /// <summary>
    /// Whether two pieces' bounds overlap by more than <paramref name="tolerance"/> on every axis:
    /// pieces that touch, or meet at a corner or an end within a post's width, do not clash.
    /// </summary>
    internal static bool Clash(PlacedPiece a, PlacedPiece b, float tolerance)
    {
        foreach ((Vector3 ac, Vector3 ah) in Bounds(a))
        {
            foreach ((Vector3 bc, Vector3 bh) in Bounds(b))
            {
                Vector3 depth = ah + bh - Vector3.Abs(ac - bc);
                if (depth.X > tolerance && depth.Y > tolerance && depth.Z > tolerance) return true;
            }
        }

        return false;
    }

    private static IEnumerable<PieceBox> Slices(PieceBox box)
    {
        Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, box.Pitch);
        float slice = box.Half.Z * 2 / PitchedSlices;
        for (int index = 0; index < PitchedSlices; index++)
        {
            float along = -box.Half.Z + (slice * (index + 0.5f));
            yield return box with { Centre = box.Centre + Vector3.Transform(new Vector3(0, 0, along), pitch), Half = box.Half with { Z = slice / 2 } };
        }
    }

    private static Vector3 Extent(Quaternion rotation, Vector3 half) =>
        Vector3.Abs(Vector3.Transform(new Vector3(half.X, 0, 0), rotation))
        + Vector3.Abs(Vector3.Transform(new Vector3(0, half.Y, 0), rotation))
        + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, half.Z), rotation));

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
