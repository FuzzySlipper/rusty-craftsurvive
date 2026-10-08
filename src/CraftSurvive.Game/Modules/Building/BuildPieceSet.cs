namespace CraftSurvive.Game.Modules.Building;

/// <summary>Why a piece was or was not placed or removed.</summary>
internal enum PieceOutcome
{
    Placed,
    Removed,
    Occupied,
    Full,
    NotAllowed,
    Missing,
}

/// <summary>
/// The owner of the placed pieces (#9729): the one list of what stands, in canonical order, with a
/// revision that moves on every change so the store and presenter know when to act. Pure state:
/// drawing, collision and saving read it.
/// </summary>
internal sealed class BuildPieceSet
{
    /// <summary>How many pieces a world holds at most (also the save's bound and the draw ids' range).</summary>
    internal const int MaximumPieces = 0x2000;

    private readonly List<PlacedPiece> pieces = [];

    internal IReadOnlyList<PlacedPiece> Pieces => pieces;

    internal long Revision { get; private set; }

    internal int Count => pieces.Count;

    internal PieceOutcome Add(PlacedPiece piece)
    {
        if (!PieceCatalog.Allows(piece.Kind, piece.Material)) return PieceOutcome.NotAllowed;
        if (pieces.Count >= MaximumPieces) return PieceOutcome.Full;
        int at = pieces.BinarySearch(piece);
        if (at >= 0 || Overlaps(piece)) return PieceOutcome.Occupied;
        pieces.Insert(~at, piece);
        Revision++;
        return PieceOutcome.Placed;
    }

    internal PieceOutcome RemoveAt(int index)
    {
        if (index < 0 || index >= pieces.Count) return PieceOutcome.Missing;
        pieces.RemoveAt(index);
        Revision++;
        return PieceOutcome.Removed;
    }

    /// <summary>Removes every piece whose anchor lies within the radius (a blast, until damaged pieces become voxels, #9731).</summary>
    internal int RemoveWithin(System.Numerics.Vector3 centre, float radius)
    {
        int removed = pieces.RemoveAll(piece => System.Numerics.Vector3.Distance(piece.Anchor, centre) <= radius);
        if (removed > 0) Revision++;
        return removed;
    }

    /// <summary>Replaces everything with a saved set (already canonical and allowed, as the codec checks).</summary>
    internal void Restore(IEnumerable<PlacedPiece> saved)
    {
        pieces.Clear();
        pieces.AddRange(saved);
        pieces.Sort();
        Revision++;
    }

    internal PlacedPiece[] Snapshot() => [.. pieces];

    /// <summary>
    /// Whether the same kind already stands at the same anchor (any turn): a second identical
    /// footing is a double-click, not a build. Different kinds may share an anchor (a post at a
    /// wall's end, a roof over a beam).
    /// </summary>
    private bool Overlaps(PlacedPiece piece) =>
        pieces.Exists(other => other.Kind == piece.Kind && other.X == piece.X && other.Y == piece.Y && other.Z == piece.Z);
}
