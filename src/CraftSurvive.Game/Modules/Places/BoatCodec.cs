using System.Buffers;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>The expedition's boat as stored (#9879): its kind, where it lies, and which way its bow points (radians).</summary>
internal sealed record BoatSave(BoatKind Kind, double X, double Z, double Heading);

/// <summary>
/// The boat's stored form under <see cref="SaveManifest.TravelBoat"/>: the shared header, then one
/// record (the kind's code, zero, the position, the heading). No save means the expedition has no boat.
/// </summary>
internal sealed class BoatCodec(SaveIdentity identity) : IProductStateCodec<BoatSave>
{
    /// <summary>Two 32-bit codes and three 64-bit reals.</summary>
    internal const int RecordBytes = sizeof(int) * 2 + sizeof(double) * 3;
    private const double MaximumCoordinate = 1 << 20;

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: 1, RecordBytes);

    private static SaveKey Key => SaveManifest.TravelBoat;

    public void Encode(in BoatSave state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(BoatSave state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Int32((int)state.Kind); writer.Int32(0); writer.Double(state.X); writer.Double(state.Z); writer.Double(state.Heading);
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public BoatSave Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        if (count != 1) throw new InvalidOperationException($"{Key.Key} must hold exactly one boat.");
        SaveReader reader = SaveEnvelope.Records(payload);
        int kind = reader.Int32();
        if (!Enum.IsDefined((BoatKind)kind)) throw new InvalidOperationException($"{Key.Key} holds an unknown boat kind {kind}.");
        if (reader.Int32() != 0) throw new InvalidOperationException($"{Key.Key} holds a record that is not padded with zero.");
        BoatSave state = new((BoatKind)kind, reader.Double(), reader.Double(), reader.Double());
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, state));
        Validate(state);
        return state;
    }

    /// <summary>Refuses a boat no session could have left: off any world, or pointing nowhere.</summary>
    private static void Validate(BoatSave state)
    {
        if (!double.IsFinite(state.X) || !double.IsFinite(state.Z) || Math.Abs(state.X) > MaximumCoordinate || Math.Abs(state.Z) > MaximumCoordinate)
            throw new InvalidOperationException($"{Key.Key} lies off any world.");
        if (!double.IsFinite(state.Heading) || Math.Abs(state.Heading) > 2 * Math.PI) throw new InvalidOperationException($"{Key.Key} points nowhere.");
    }

    private static ulong Fingerprint(ulong seed, BoatSave state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix((long)state.Kind);
        hash.Mix(state.X);
        hash.Mix(state.Z);
        hash.Mix(state.Heading);
        return hash.Value;
    }
}
