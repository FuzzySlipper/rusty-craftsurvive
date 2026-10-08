using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// The placed pieces' stored form under <see cref="SaveManifest.BuildPieces"/> (#9729): the shared
/// header, then one record per piece in canonical order - its grid anchor, kind, material and turn.
/// A record whose kind, material or turn is unknown, or whose material the kind does not allow,
/// refuses the whole save rather than standing a piece that cannot be drawn.
/// </summary>
internal sealed class BuildPieceCodec(SaveIdentity identity) : IProductStateCodec<PlacedPiece[]>
{
    /// <summary>Three 64-bit grid coordinates, then the kind, material and turn.</summary>
    internal const int RecordBytes = (sizeof(long) * 3) + (sizeof(byte) * 3);

    internal static SaveBounds Bounds { get; } = new(BuildPieceSet.MaximumPieces, RecordBytes);

    private static SaveKey Key => SaveManifest.BuildPieces;

    public void Encode(in PlacedPiece[] state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(PlacedPiece[] pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, pieces.Length);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        for (int index = 0; index < pieces.Length; index++)
        {
            PlacedPiece piece = pieces[index];
            if (index > 0 && pieces[index - 1].CompareTo(piece) >= 0)
            {
                throw new InvalidOperationException("Build pieces are saved in canonical order, each once.");
            }

            writer.Int64(piece.X);
            writer.Int64(piece.Y);
            writer.Int64(piece.Z);
            writer.Byte((byte)piece.Kind);
            writer.Byte((byte)piece.Material);
            writer.Byte(piece.Turn);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, pieces));
        return bytes;
    }

    public PlacedPiece[] Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        PlacedPiece[] pieces = new PlacedPiece[count];
        SaveReader reader = SaveEnvelope.Records(payload);
        for (int index = 0; index < count; index++)
        {
            long x = reader.Int64(), y = reader.Int64(), z = reader.Int64();
            PieceKind kind = (PieceKind)reader.Byte();
            PieceMaterial material = (PieceMaterial)reader.Byte();
            byte turn = reader.Byte();
            if (!Enum.IsDefined(kind) || !Enum.IsDefined(material) || turn >= PlacedPiece.Turns || !PieceCatalog.Allows(kind, material))
            {
                throw new InvalidOperationException($"Build piece {index} ({kind}, {material}, turn {turn}) is not a piece that can stand.");
            }

            pieces[index] = new PlacedPiece(kind, material, x, y, z, turn);
            if (index > 0 && pieces[index - 1].CompareTo(pieces[index]) >= 0)
            {
                throw new InvalidOperationException($"{Key.Key} records are not in canonical order.");
            }
        }

        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, pieces));
        return pieces;
    }

    private static ulong Fingerprint(ulong seed, PlacedPiece[] pieces)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (PlacedPiece piece in pieces)
        {
            hash.Mix(piece.X);
            hash.Mix(piece.Y);
            hash.Mix(piece.Z);
            hash.Mix((ulong)piece.Kind);
            hash.Mix((ulong)piece.Material);
            hash.Mix(piece.Turn);
        }

        return hash.Value;
    }
}
