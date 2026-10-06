using System.Buffers;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>The sled as stored: where it stands and what it holds, one count per kind.</summary>
internal sealed record SledSave(double X, double Z, IReadOnlyList<ItemCount> Cargo);

/// <summary>
/// The sled's stored form under <see cref="SaveManifest.TravelSled"/>: the shared header, then one
/// place record (code 0, count 0, the position) and one record per kind carried (code, count, zeros).
/// A kind is stored by its catalogue code, never by name.
/// </summary>
internal sealed class SledCodec(SaveIdentity identity) : IProductStateCodec<SledSave>
{
    /// <summary>A 32-bit code, a 32-bit count and two 64-bit reals.</summary>
    internal const int RecordBytes = sizeof(int) * 2 + sizeof(double) * 2;
    private const int PlaceCode = 0;
    private const double MaximumCoordinate = 1 << 20;

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: ItemCatalog.All.Count + 1, RecordBytes);

    private static SaveKey Key => SaveManifest.TravelSled;

    public void Encode(in SledSave state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(SledSave state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, state.Cargo.Count + 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Int32(PlaceCode); writer.Int32(0); writer.Double(state.X); writer.Double(state.Z);
        foreach (ItemCount carried in state.Cargo)
        {
            writer.Int32(carried.Item.Code); writer.Int32(carried.Count); writer.Double(0); writer.Double(0);
        }
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public SledSave Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        if (count < 1) throw new InvalidOperationException($"{Key.Key} has no place record.");
        SaveReader reader = SaveEnvelope.Records(payload);
        if (reader.Int32() != PlaceCode || reader.Int32() != 0) throw new InvalidOperationException($"{Key.Key} does not begin with its place.");
        double x = reader.Double(), z = reader.Double();
        List<ItemCount> cargo = new(count - 1);
        for (int index = 1; index < count; index++)
        {
            int code = reader.Int32();
            CatalogItem item = ItemCatalog.TryFind(code, out CatalogItem found) ? found : throw new InvalidOperationException($"{Key.Key} holds an unknown item code {code}.");
            cargo.Add(new ItemCount(item, reader.Int32()));
            if (BitConverter.DoubleToInt64Bits(reader.Double()) != 0 || BitConverter.DoubleToInt64Bits(reader.Double()) != 0) throw new InvalidOperationException($"{Key.Key} holds a kind record that is not padded with zeros.");
        }
        SledSave state = new(x, z, cargo);
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, state));
        Validate(state);
        return state;
    }

    /// <summary>Refuses a load no session could have produced: a place off any world, kinds repeated or out of order, counts out of range.</summary>
    private static void Validate(SledSave state)
    {
        if (!double.IsFinite(state.X) || !double.IsFinite(state.Z) || Math.Abs(state.X) > MaximumCoordinate || Math.Abs(state.Z) > MaximumCoordinate)
            throw new InvalidOperationException($"{Key.Key} stands off any world.");
        for (int index = 0; index < state.Cargo.Count; index++)
        {
            if (state.Cargo[index].Count <= 0) throw new InvalidOperationException($"{Key.Key} holds a count out of range.");
            if (index > 0 && state.Cargo[index - 1].Item.Code >= state.Cargo[index].Item.Code)
                throw new InvalidOperationException($"{Key.Key} holds its kinds out of order.");
        }
        if (state.Cargo.Sum(carried => carried.Count) > Modules.Travel.Sled.Capacity)
            throw new InvalidOperationException($"{Key.Key} holds more than a sled carries.");
    }

    private static ulong Fingerprint(ulong seed, SledSave state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix(state.X);
        hash.Mix(state.Z);
        foreach (ItemCount carried in state.Cargo)
        {
            hash.Mix((long)carried.Item.Code);
            hash.Mix((long)carried.Count);
        }
        return hash.Value;
    }
}
