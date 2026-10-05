using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>Where the expedition calls home on the map: a ground position in world metres.</summary>
internal readonly record struct HomeMarker(double X, double Z);

/// <summary>The home marker's stored form under <see cref="SaveManifest.TravelHome"/>: one record of two reals.</summary>
internal sealed class HomeMarkerCodec(SaveIdentity identity) : IProductStateCodec<HomeMarker>
{
    /// <summary>Two 64-bit reals.</summary>
    internal const int RecordBytes = sizeof(double) * 2;

    /// <summary>A marker this far from the origin is no place on any world the product generates.</summary>
    private const double MaximumCoordinate = 1 << 20;

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: 1, RecordBytes);

    private static SaveKey Key => SaveManifest.TravelHome;

    public void Encode(in HomeMarker state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(HomeMarker state)
    {
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, count: 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Double(state.X);
        writer.Double(state.Z);
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public HomeMarker Decode(ReadOnlySpan<byte> payload)
    {
        if (SaveEnvelope.Open(Key, identity, Bounds, payload) != 1)
        {
            throw new InvalidOperationException($"{Key.Key} holds exactly one record.");
        }

        SaveReader reader = SaveEnvelope.Records(payload);
        HomeMarker state = new(reader.Double(), reader.Double());
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, state));
        Validate(state);
        return state;
    }

    private static void Validate(HomeMarker state)
    {
        if (!double.IsFinite(state.X) || !double.IsFinite(state.Z) || Math.Abs(state.X) > MaximumCoordinate || Math.Abs(state.Z) > MaximumCoordinate)
        {
            throw new InvalidOperationException($"{Key.Key} holds a position off any world.");
        }
    }

    private static ulong Fingerprint(ulong seed, HomeMarker state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix(state.X);
        hash.Mix(state.Z);
        return hash.Value;
    }
}
