using System.Buffers;
using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// The remnants' stored form under <see cref="SaveManifest.BuildRemnants"/> (#9731): the shared
/// header, then one fixed record per remnant in canonical piece order - the piece (as
/// <see cref="BuildPieceCodec"/> writes it), how many craters, and room for
/// <see cref="PieceRemnant.MaximumCraters"/> craters (centre and reach, unused ones zero). A remnant
/// is its description, so it regenerates exactly.
/// </summary>
internal sealed class RemnantCodec(SaveIdentity identity) : IProductStateCodec<PieceRemnant[]>
{
    private const int CraterBytes = sizeof(float) * 4;

    /// <summary>The piece's record, the crater count, then every crater slot.</summary>
    internal const int RecordBytes = BuildPieceCodec.RecordBytes + sizeof(byte) + (CraterBytes * PieceRemnant.MaximumCraters);

    internal static SaveBounds Bounds { get; } = new(RemnantSet.MaximumRemnants, RecordBytes);

    private static SaveKey Key => SaveManifest.BuildRemnants;

    public void Encode(in PieceRemnant[] state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(PieceRemnant[] remnants)
    {
        ArgumentNullException.ThrowIfNull(remnants);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, remnants.Length);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        for (int index = 0; index < remnants.Length; index++)
        {
            PieceRemnant remnant = remnants[index];
            if (index > 0 && remnants[index - 1].Piece.CompareTo(remnant.Piece) >= 0)
            {
                throw new InvalidOperationException("Remnants are saved in canonical piece order, each once.");
            }

            if (remnant.Craters.Count is < 1 or > PieceRemnant.MaximumCraters)
            {
                throw new InvalidOperationException($"A remnant holds one to {PieceRemnant.MaximumCraters} craters, not {remnant.Craters.Count}.");
            }

            PlacedPiece piece = remnant.Piece;
            writer.Int64(piece.X);
            writer.Int64(piece.Y);
            writer.Int64(piece.Z);
            writer.Byte((byte)piece.Kind);
            writer.Byte((byte)piece.Material);
            writer.Byte(piece.Turn);
            writer.Byte((byte)remnant.Craters.Count);
            for (int slot = 0; slot < PieceRemnant.MaximumCraters; slot++)
            {
                Crater crater = slot < remnant.Craters.Count ? remnant.Craters[slot] : default;
                writer.Single(crater.Centre.X);
                writer.Single(crater.Centre.Y);
                writer.Single(crater.Centre.Z);
                writer.Single(crater.Radius);
            }
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, remnants));
        return bytes;
    }

    public PieceRemnant[] Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        PieceRemnant[] remnants = new PieceRemnant[count];
        SaveReader reader = SaveEnvelope.Records(payload);
        for (int index = 0; index < count; index++)
        {
            long x = reader.Int64(), y = reader.Int64(), z = reader.Int64();
            PieceKind kind = (PieceKind)reader.Byte();
            PieceMaterial material = (PieceMaterial)reader.Byte();
            byte turn = reader.Byte();
            int craters = reader.Byte();
            if (!Enum.IsDefined(kind) || !Enum.IsDefined(material) || turn >= PlacedPiece.Turns || !PieceCatalog.Allows(kind, material)
                || craters is < 1 or > PieceRemnant.MaximumCraters)
            {
                throw new InvalidOperationException($"Remnant {index} ({kind}, {material}, turn {turn}, {craters} craters) is not a remnant that can stand.");
            }

            List<Crater> bites = [];
            for (int slot = 0; slot < PieceRemnant.MaximumCraters; slot++)
            {
                Crater crater = new(new Vector3(reader.Single(), reader.Single(), reader.Single()), reader.Single());
                if (slot >= craters)
                {
                    // Unused slots are written empty; anything else is not a remnant this codec wrote.
                    if (crater != default) throw new InvalidOperationException($"Remnant {index} has a crater past its count.");
                    continue;
                }

                if (!float.IsFinite(crater.Radius) || crater.Radius <= 0 || !float.IsFinite(crater.Centre.X + crater.Centre.Y + crater.Centre.Z))
                {
                    throw new InvalidOperationException($"Remnant {index} has a crater that is not a sphere.");
                }

                bites.Add(crater);
            }

            remnants[index] = new PieceRemnant(new PlacedPiece(kind, material, x, y, z, turn), bites);
            if (index > 0 && remnants[index - 1].Piece.CompareTo(remnants[index].Piece) >= 0)
            {
                throw new InvalidOperationException($"{Key.Key} records are not in canonical order.");
            }
        }

        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, remnants));
        return remnants;
    }

    private static ulong Fingerprint(ulong seed, PieceRemnant[] remnants)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (PieceRemnant remnant in remnants)
        {
            hash.Mix(remnant.Piece.X);
            hash.Mix(remnant.Piece.Y);
            hash.Mix(remnant.Piece.Z);
            hash.Mix((ulong)remnant.Piece.Kind);
            hash.Mix((ulong)remnant.Piece.Material);
            hash.Mix(remnant.Piece.Turn);
            foreach (Crater crater in remnant.Craters)
            {
                hash.Mix(crater.Centre.X);
                hash.Mix(crater.Centre.Y);
                hash.Mix(crater.Centre.Z);
                hash.Mix(crater.Radius);
            }
        }

        return hash.Value;
    }
}
