using System.Numerics;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>What a charge did to built work: pieces it destroyed, remnants it made or bit into, and what fell.</summary>
internal readonly record struct PieceBlast(int Destroyed, int Remnants, int Collapsed, IReadOnlyList<(Vector3 Low, Vector3 High)> Changed)
{
    /// <summary>
    /// Whether what holds built work up may have changed (#9733): a piece gone or broken, or a
    /// remnant's shape bitten into even when nothing new was made or fell.
    /// </summary>
    internal bool MovesSupport => Destroyed + Remnants + Collapsed > 0 || Changed.Count > 0;
}

/// <summary>
/// The owner of the remnants (#9731): pieces a charge has bitten into, kept as their piece and
/// craters in canonical piece order, with a revision for the store, the voxels and the collision.
/// The blast rule (<see cref="Blast"/>) moves pieces from the intact set into it.
/// </summary>
internal sealed class RemnantSet
{
    /// <summary>How many remnants a world holds (the save's bound); past it, a bitten piece falls to pieces.</summary>
    internal const int MaximumRemnants = 0x400;

    /// <summary>A piece whose middle lies within this share of a charge's reach is destroyed outright.</summary>
    internal const float CoreShare = 0.5f;

    private readonly List<PieceRemnant> remnants = [];

    internal IReadOnlyList<PieceRemnant> Remnants => remnants;

    internal long Revision { get; private set; }

    internal int Count => remnants.Count;

    internal PieceRemnant RemoveAt(int index)
    {
        PieceRemnant gone = remnants[index];
        remnants.RemoveAt(index);
        Revision++;
        return gone;
    }

    internal void Restore(IEnumerable<PieceRemnant> saved)
    {
        remnants.Clear();
        remnants.AddRange(saved);
        remnants.Sort((a, b) => a.Piece.CompareTo(b.Piece));
        Revision++;
    }

    internal PieceRemnant[] Snapshot() => [.. remnants];

    /// <summary>
    /// The remnant a new piece would stand in (#9731), or -1: what is left of a piece must be
    /// cleared before another is built through it.
    /// </summary>
    internal int InTheWay(PlacedPiece piece)
    {
        for (int index = 0; index < remnants.Count; index++)
        {
            if (remnants[index].Piece.CompareTo(piece) == 0 || PieceGeometry.Clash(remnants[index].Piece, piece, BuildPieceSet.OverlapTolerance)) return index;
        }

        return -1;
    }

    /// <summary>
    /// A charge at <paramref name="crater"/>: every intact piece it reaches leaves the intact set and
    /// is destroyed (its middle within <see cref="CoreShare"/> of the reach) or becomes a remnant
    /// bitten by the crater; every remnant it reaches takes the crater too. A remnant left with less
    /// than <see cref="PieceRemnant.CollapseShare"/> of its piece, or past
    /// <see cref="PieceRemnant.MaximumCraters"/>, falls to pieces. Returns what changed, with the
    /// bounds the voxels must redraw.
    /// </summary>
    internal PieceBlast Blast(BuildPieceSet pieces, Crater crater)
    {
        int destroyed = 0, made = 0, collapsed = 0;
        List<(Vector3, Vector3)> changed = [];
        for (int index = pieces.Count - 1; index >= 0; index--)
        {
            PlacedPiece piece = pieces.Pieces[index];
            if (PieceGeometry.Distance(piece, crater.Centre) >= crater.Radius) continue;
            pieces.RemoveAt(index);
            if (Vector3.Distance(Middle(piece), crater.Centre) <= crater.Radius * CoreShare || remnants.Count >= MaximumRemnants)
            {
                destroyed++;
                continue;
            }

            PieceRemnant remnant = new(piece, [crater]);
            if (remnant.ShareLeft() < PieceRemnant.CollapseShare)
            {
                collapsed++;
                continue;
            }

            // A piece is one remnant at most: one rebuilt where its remnant stood (placement refuses
            // that, but restored or assisted state may not) replaces it, the newer piece being whole.
            int at = remnants.BinarySearch(remnant, Order);
            if (at >= 0)
            {
                changed.Add(remnants[at].Bounds());
                remnants[at] = remnant;
            }
            else
            {
                remnants.Insert(~at, remnant);
            }

            changed.Add(remnant.Bounds());
            made++;
        }

        for (int index = remnants.Count - 1; index >= 0; index--)
        {
            PieceRemnant remnant = remnants[index];
            if (made > 0 && remnant.Craters.Count == 1 && remnant.Craters[0] == crater) continue;
            if (remnant.Distance(crater.Centre) >= crater.Radius) continue;
            changed.Add(remnant.Bounds());
            PieceRemnant bitten = remnant.WithCrater(crater);
            if (bitten.Craters.Count > PieceRemnant.MaximumCraters || bitten.ShareLeft() < PieceRemnant.CollapseShare)
            {
                remnants.RemoveAt(index);
                collapsed++;
            }
            else
            {
                remnants[index] = bitten;
            }
        }

        if (destroyed + made + collapsed > 0 || changed.Count > 0) Revision++;
        return new PieceBlast(destroyed, made, collapsed, changed);
    }

    private static readonly Comparer<PieceRemnant> Order = Comparer<PieceRemnant>.Create((a, b) => a.Piece.CompareTo(b.Piece));

    private static Vector3 Middle(PlacedPiece piece)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach ((Vector3 centre, _) in PieceGeometry.Bounds(piece))
        {
            sum += centre;
            count++;
        }

        return sum / Math.Max(1, count);
    }
}
