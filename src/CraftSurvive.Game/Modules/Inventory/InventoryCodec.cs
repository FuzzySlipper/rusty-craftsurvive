using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Inventory;

/// <summary>What the player carries, as stored: each occupied slot, in slot order.</summary>
internal sealed record CarriedItems(IReadOnlyList<SlotContents> Slots);

/// <summary>
/// The carried items' stored form under <see cref="SaveManifest.PlayerInventory"/>: the shared header
/// and one record per occupied slot. A kind is stored by its catalogue code, never by name. A save
/// from before there were slots (schema 1, one count per kind) still loads: its items are laid out
/// into slots as a pickup would place them.
/// </summary>
internal sealed class InventoryCodec(SaveIdentity identity) : IProductStateCodec<CarriedItems>
{
    /// <summary>A 32-bit slot, a 32-bit code and a 32-bit count.</summary>
    internal const int RecordBytes = sizeof(int) * 3;

    /// <summary>Schema 1's record: a 32-bit code and a 32-bit count, one per kind carried.</summary>
    internal const int KindRecordBytes = sizeof(int) * 2;

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: InventorySlots.Count, RecordBytes);

    private static SaveBounds KindBounds { get; } = new(MaximumRecords: ItemCatalog.All.Count, KindRecordBytes);

    private static SaveKey Key => SaveManifest.PlayerInventory;

    /// <summary>The same key as it was before slots, to open a schema 1 save.</summary>
    private static SaveKey KindKey => Key with { Schema = 1 };

    public void Encode(in CarriedItems state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(CarriedItems state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state.Slots);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, state.Slots.Count);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        foreach (SlotContents contents in state.Slots)
        {
            writer.Int32(contents.Slot);
            writer.Int32(contents.Item.Code);
            writer.Int32(contents.Count);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state.Slots));
        return bytes;
    }

    public CarriedItems Decode(ReadOnlySpan<byte> payload)
    {
        if (SaveEnvelope.SchemaOf(payload) == KindKey.Schema)
        {
            return new CarriedItems(InventorySlots.LayOut(DecodeKinds(payload)));
        }

        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        SaveReader reader = SaveEnvelope.Records(payload);
        List<SlotContents> slots = new(count);
        for (int index = 0; index < count; index++)
        {
            int slot = reader.Int32();
            CatalogItem item = Item(reader.Int32());
            slots.Add(new SlotContents(slot, item, reader.Int32()));
        }

        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, slots));
        Validate(slots);
        return new CarriedItems(slots);
    }

    /// <summary>Reads a schema 1 save: one count per kind, in catalogue-code order.</summary>
    private List<ItemCount> DecodeKinds(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(KindKey, identity, KindBounds, payload);
        SaveReader reader = SaveEnvelope.Records(payload);
        List<ItemCount> items = new(count);
        for (int index = 0; index < count; index++)
        {
            CatalogItem item = Item(reader.Int32());
            items.Add(new ItemCount(item, reader.Int32()));
        }

        SaveFingerprint hash = SaveFingerprint.Start(identity.Seed);
        foreach (ItemCount carried in items)
        {
            hash.Mix((long)carried.Item.Code);
            hash.Mix((long)carried.Count);
        }

        SaveEnvelope.Verify(KindKey, payload, hash.Value);
        if (items.Any(carried => carried.Count <= 0 || (ulong)carried.Count > ItemCatalog.StackMaximum))
        {
            throw new InvalidOperationException($"{Key.Key} holds a count out of range.");
        }

        return items;
    }

    private static CatalogItem Item(int code) =>
        ItemCatalog.TryFind(code, out CatalogItem item) ? item : throw new InvalidOperationException($"{Key.Key} holds an unknown item code {code}.");

    /// <summary>Refuses a load no session could have produced: slots out of range or order, or counts out of range.</summary>
    private static void Validate(IReadOnlyList<SlotContents> slots)
    {
        for (int index = 0; index < slots.Count; index++)
        {
            SlotContents contents = slots[index];
            if (!InventorySlots.IsSlot(contents.Slot))
            {
                throw new InvalidOperationException($"{Key.Key} names slot {contents.Slot}.");
            }

            if (contents.Count <= 0 || (ulong)contents.Count > ItemCatalog.StackMaximum)
            {
                throw new InvalidOperationException($"{Key.Key} holds {contents.Count} {contents.Item.Id}.");
            }

            if (index > 0 && slots[index - 1].Slot >= contents.Slot)
            {
                throw new InvalidOperationException($"{Key.Key} holds its slots out of order.");
            }
        }
    }

    private static ulong Fingerprint(ulong seed, IReadOnlyList<SlotContents> slots)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (SlotContents contents in slots)
        {
            hash.Mix((long)contents.Slot);
            hash.Mix((long)contents.Item.Code);
            hash.Mix((long)contents.Count);
        }

        return hash.Value;
    }
}
