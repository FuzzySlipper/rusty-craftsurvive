using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Inventory;

/// <summary>What the player carries, as stored: one count per kind of item, in catalogue-code order.</summary>
internal sealed record CarriedItems(IReadOnlyList<ItemCount> Items);

/// <summary>
/// The carried items' stored form under <see cref="SaveManifest.PlayerInventory"/>: the shared header
/// and one record per kind carried. A kind is stored by its catalogue code, never by name.
/// </summary>
internal sealed class InventoryCodec(SaveIdentity identity) : IProductStateCodec<CarriedItems>
{
    /// <summary>A 32-bit code and a 32-bit count.</summary>
    internal const int RecordBytes = sizeof(int) * 2;

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: ItemCatalog.All.Count, RecordBytes);

    private static SaveKey Key => SaveManifest.PlayerInventory;

    public void Encode(in CarriedItems state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(CarriedItems state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state.Items);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, state.Items.Count);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        foreach (ItemCount carried in state.Items)
        {
            writer.Int32(carried.Item.Code);
            writer.Int32(carried.Count);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state.Items));
        return bytes;
    }

    public CarriedItems Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        SaveReader reader = SaveEnvelope.Records(payload);
        List<ItemCount> items = new(count);
        for (int index = 0; index < count; index++)
        {
            int code = reader.Int32();
            int quantity = reader.Int32();
            if (!ItemCatalog.TryFind(code, out CatalogItem item))
            {
                throw new InvalidOperationException($"{Key.Key} holds an unknown item code {code}.");
            }

            items.Add(new ItemCount(item, quantity));
        }

        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, items));
        Validate(items);
        return new CarriedItems(items);
    }

    /// <summary>Refuses a load no session could have produced: unordered or repeated kinds, or counts out of range.</summary>
    private static void Validate(IReadOnlyList<ItemCount> items)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].Count <= 0 || (ulong)items[index].Count > ItemCatalog.StackMaximum)
            {
                throw new InvalidOperationException($"{Key.Key} holds {items[index].Count} {items[index].Item.Id}.");
            }

            if (index > 0 && items[index - 1].Item.Code >= items[index].Item.Code)
            {
                throw new InvalidOperationException($"{Key.Key} holds its kinds out of order.");
            }
        }
    }

    private static ulong Fingerprint(ulong seed, IReadOnlyList<ItemCount> items)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (ItemCount carried in items)
        {
            hash.Mix((long)carried.Item.Code);
            hash.Mix((long)carried.Count);
        }

        return hash.Value;
    }
}
