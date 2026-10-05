using System.Buffers;
using System.Buffers.Binary;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;
using Rusty.Engine.Persistence;

const ulong Seed = 0x5EED_CAFE_F00DUL;
const int MaximumHealth = 20;
SaveIdentity identity = new(TerrainGeneratorContract.CurrentVersion, Seed);
// --- one sample of every saved state, each through its own codec ----------------------------------
TerrainOverlaySnapshot overlay = new(Seed,
[
    new TerrainOverlayEntry(new VoxelAddress(-3, 4, 5), TerrainConstants.EmptyMaterial),
    new TerrainOverlayEntry(new VoxelAddress(2, 7, -1), TerrainConstants.StoneMaterial),
]);
DiscoverySnapshot journal = new(Seed,
[
    new DiscoveryEntry(1, -2, PoiKind.Ruin, 256, -512, DiscoveryStage.Seen, 10, 20),
    new DiscoveryEntry(3, 4, PoiKind.CaveMouth, 768, 1024, DiscoveryStage.Visited, 30, 40),
]);
BlockEntityIndex index = new();
index.Place(BlockEntityKind.Light, new VoxelAddress(9, 9, 9), 1);
index.Place(BlockEntityKind.Door, new VoxelAddress(-4, 12, 7), 1);
index.Place(BlockEntityKind.Container, new VoxelAddress(-4, 12, 8), 5);
BlockEntityRecord[] entities = index.Snapshot();
PlayerContinuation player = new(812.5, 14.0, -96.25, 1.25, -0.3, 17, 2, 350, 6);
WorldConditionsState conditions = new(12, 0.8125, Difficulty.Harsh);
SurvivalState tracks = SurvivalState.Fresh with { Satiety = 61.25, Breath = 7.5 };
CarriedItems carried = new([new SlotContents(0, ItemCatalog.Torch, 4), new SlotContents(3, ItemCatalog.Meat, 3), new SlotContents(20, ItemCatalog.Oil, 1)]);

WorldMapSave mapSave = new(3, WorldMapGenerator.Generate(new TerrainConfiguration(Seed, 4096)));

SavedForm[] forms =
[
    SavedForm.For(SaveManifest.WorldMap, new WorldMapCodec(), mapSave, MapFields.FieldCount * sizeof(float),
        (left, right) => left.Generation == right.Generation && left.Map.Fingerprint == right.Map.Fingerprint
            && left.Map.Fields.Elevation.SequenceEqual(right.Map.Fields.Elevation)
            && left.Map.Fields.Discharge.SequenceEqual(right.Map.Fields.Discharge)
            && left.Map.Rivers.Reaches.Count == right.Map.Rivers.Reaches.Count, null),
    SavedForm.For(SaveManifest.TerrainOverlay, new TerrainOverlayCodec(identity), overlay, TerrainOverlayCodec.RecordBytes,
        (left, right) => left.Entries.SequenceEqual(right.Entries), seed => new TerrainOverlayCodec(identity with { Seed = seed })),
    SavedForm.For(SaveManifest.DiscoveryJournal, new DiscoveryCodec(identity), journal, DiscoveryCodec.RecordBytes,
        (left, right) => left.Entries.SequenceEqual(right.Entries), seed => new DiscoveryCodec(identity with { Seed = seed })),
    SavedForm.For(SaveManifest.BlockEntities, new BlockEntityCodec(identity), entities, BlockEntityCodec.RecordBytes,
        (left, right) => left.SequenceEqual(right), seed => new BlockEntityCodec(identity with { Seed = seed })),
    SavedForm.For(SaveManifest.PlayerContinuation, new PlayerContinuationCodec(identity, MaximumHealth), player, PlayerContinuationCodec.RecordBytes,
        (left, right) => left == right, seed => new PlayerContinuationCodec(identity with { Seed = seed }, MaximumHealth)),
    SavedForm.For(SaveManifest.WorldConditions, new WorldConditionsCodec(identity), conditions, WorldConditionsCodec.RecordBytes,
        (left, right) => left == right, seed => new WorldConditionsCodec(identity with { Seed = seed })),
    SavedForm.For(SaveManifest.PlayerSurvival, new SurvivalCodec(identity), tracks, SurvivalCodec.RecordBytes,
        (left, right) => left == right, seed => new SurvivalCodec(identity with { Seed = seed })),
    SavedForm.For(SaveManifest.PlayerInventory, new InventoryCodec(identity), carried, InventoryCodec.RecordBytes,
        (left, right) => left.Slots.SequenceEqual(right.Slots), seed => new InventoryCodec(identity with { Seed = seed })),
];

// --- the manifest names every saved key, once ------------------------------------------------------
Check.That(SaveManifest.All.Count == forms.Length, $"the manifest lists {SaveManifest.All.Count} keys but {forms.Length} codecs write one");
Check.That(forms.Select(form => form.Key).ToHashSet().SetEquals(SaveManifest.All), "every codec must write a key the manifest lists");
string[] names = [.. SaveManifest.All.SelectMany(key => new[] { key.Key, key.BackupKey })];
Check.That(names.Distinct().Count() == names.Length, "keys and backup keys must all be distinct");
Check.That(SaveManifest.All.Select(key => key.Magic).Distinct().Count() == SaveManifest.All.Count, "every key must have its own magic");
Check.That(SaveManifest.All.All(key => key.BackupKey == key.Key + ".backup"), "a key's backup is kept beside it");

foreach (SavedForm form in forms)
{
    form.Run(Check.That);
}

// --- the restore rule, as the product applies it to every key -------------------------------------
TerrainOverlayCodec overlayCodec = new(identity);
byte[] honest = overlayCodec.Encode(overlay);
SaveRestoreDecision<TerrainOverlaySnapshot> absent = SaveRestore.Decide(false, [], overlayCodec);
Check.That(absent.Outcome == SaveRestoreOutcome.Absent && absent.State is null && !absent.KeepsBackup, "nothing stored must restore as absent");
SaveRestoreDecision<TerrainOverlaySnapshot> restored = SaveRestore.Decide(true, honest, overlayCodec);
Check.That(restored.Outcome == SaveRestoreOutcome.Restored && restored.State is not null
    && restored.State.Entries.SequenceEqual(overlay.Entries) && !restored.KeepsBackup, "a save for this world must restore");
byte[] otherWorld = new TerrainOverlayCodec(identity with { Seed = Seed + 1 }).Encode(new TerrainOverlaySnapshot(Seed + 1, []));
SaveRestoreDecision<TerrainOverlaySnapshot> discarded = SaveRestore.Decide(true, otherWorld, overlayCodec);
Check.That(discarded.Outcome == SaveRestoreOutcome.Discarded && discarded.State is null && discarded.KeepsBackup,
    "a save for another world must be discarded and kept as the backup");
SaveRestoreDecision<TerrainOverlaySnapshot> emptyBlob = SaveRestore.Decide(true, [], overlayCodec);
Check.That(emptyBlob.Outcome == SaveRestoreOutcome.Discarded && !emptyBlob.KeepsBackup, "an empty stored blob is discarded with nothing to back up");
byte[] olderGenerator = new TerrainOverlayCodec(identity with { GeneratorVersion = identity.GeneratorVersion - 1 }).Encode(overlay);
Check.That(SaveRestore.Decide(true, olderGenerator, overlayCodec).Outcome == SaveRestoreOutcome.Discarded,
    "a save from another generator version must be discarded");

// --- the overlay's stored form is the one earlier sessions wrote ----------------------------------
// The shared envelope took over the overlay's header without changing a byte of it, so overlays
// saved before the envelope still restore. Pinned as literal layout, not restated arithmetic.
Check.That(honest.Length == 32 + (2 * 26), $"two overlay cells must store 84 bytes, stored {honest.Length}");
Check.That(BinaryPrimitives.ReadUInt32LittleEndian(honest) == 0x4F54_5343 && BinaryPrimitives.ReadInt32LittleEndian(honest.AsSpan(4)) == 1,
    "the overlay keeps its first magic and schema");
Check.That(BinaryPrimitives.ReadInt64LittleEndian(honest.AsSpan(32)) == -3 && BinaryPrimitives.ReadUInt16LittleEndian(honest.AsSpan(32 + 24)) == TerrainConstants.EmptyMaterial,
    "an overlay record is three coordinates then the material");

// --- records the header cannot judge -----------------------------------------------------------
byte[] swapped = [.. honest];
Array.Copy(honest, 32, swapped, 32 + 26, 26);
Array.Copy(honest, 32 + 26, swapped, 32, 26);
Check.That(Refuses(overlayCodec, swapped), "an overlay whose records are out of canonical order must be refused");
BlockEntityCodec entityCodec = new(identity);
byte[] badEntityKind = entityCodec.Encode(entities);
badEntityKind[32 + 24] = 99;
Check.That(Refuses(entityCodec, badEntityKind), "a block entity of an unknown kind must be refused");
PlayerContinuationCodec playerCodec = new(identity, MaximumHealth);
Check.That(Throws(() => playerCodec.Encode(player with { Health = MaximumHealth + 1 })), "a continuation past maximum health cannot be saved");
Check.That(Throws(() => playerCodec.Encode(player with { FeetX = double.NaN })), "a continuation that is not a position cannot be saved");
byte[] twoRecords = [.. playerCodec.Encode(player)];
BinaryPrimitives.WriteInt32LittleEndian(twoRecords.AsSpan(20), 2);
Check.That(Refuses(playerCodec, [.. twoRecords, .. new byte[PlayerContinuationCodec.RecordBytes]]), "a continuation holds exactly one record");

WorldConditionsCodec conditionsCodec = new(identity);
Check.That(Throws(() => conditionsCodec.Encode(conditions with { DayFraction = 1.0 })), "a time past the end of the day cannot be saved");
Check.That(Throws(() => conditionsCodec.Encode(conditions with { Day = -1 })), "a day before the first cannot be saved");
Check.That(Throws(() => conditionsCodec.Encode(conditions with { Difficulty = (Difficulty)7 })), "an unknown difficulty cannot be saved");

SurvivalCodec survivalCodec = new(identity);
Check.That(Throws(() => survivalCodec.Encode(tracks with { Satiety = SurvivalRules.MaximumSatiety + 1 })), "a stomach past full cannot be saved");
Check.That(Throws(() => survivalCodec.Encode(tracks with { Breath = -1 })), "negative air cannot be saved");

InventoryCodec inventoryCodec = new(identity);
Check.That(Throws(() => inventoryCodec.Encode(new CarriedItems([new SlotContents(5, ItemCatalog.Torch, 1), new SlotContents(2, ItemCatalog.Meat, 1)]))),
    "slots out of order cannot be saved");
Check.That(Throws(() => inventoryCodec.Encode(new CarriedItems([new SlotContents(0, ItemCatalog.Meat, 0)]))), "an empty stack is not carried");
Check.That(Throws(() => inventoryCodec.Encode(new CarriedItems([new SlotContents(InventorySlots.Count, ItemCatalog.Meat, 1)]))), "a slot past the last cannot be saved");
byte[] unknownItem = inventoryCodec.Encode(carried);
BinaryPrimitives.WriteInt32LittleEndian(unknownItem.AsSpan(SaveEnvelope.HeaderBytes + sizeof(int)), 99);
Check.That(Refuses(inventoryCodec, unknownItem), "an unknown item code must be refused");
Check.That(inventoryCodec.Decode(inventoryCodec.Encode(new CarriedItems([]))).Slots.Count == 0, "carrying nothing round-trips");

// A save from before slots - one count per kind, schema 1 - still loads, laid out into slots as a
// pickup would place them, so a player's things survive the change.
SaveKey kindKey = SaveManifest.PlayerInventory with { Schema = 1 };
ItemCount[] kinds = [new(ItemCatalog.Meat, 3), new(ItemCatalog.Oil, 1), new(ItemCatalog.Torch, 4)];
byte[] kindSave = SaveEnvelope.Allocate(kindKey, identity, new SaveBounds(ItemCatalog.All.Count, InventoryCodec.KindRecordBytes), kinds.Length);
SaveWriter kindWriter = SaveEnvelope.Records(kindSave);
SaveFingerprint kindHash = SaveFingerprint.Start(identity.Seed);
foreach (ItemCount kind in kinds)
{
    kindWriter.Int32(kind.Item.Code);
    kindWriter.Int32(kind.Count);
    kindHash.Mix((long)kind.Item.Code);
    kindHash.Mix((long)kind.Count);
}

SaveEnvelope.Seal(kindSave, kindHash.Value);
CarriedItems migrated = inventoryCodec.Decode(kindSave);
Check.That(migrated.Slots.SequenceEqual([new SlotContents(0, ItemCatalog.Meat, 3), new SlotContents(1, ItemCatalog.Oil, 1), new SlotContents(2, ItemCatalog.Torch, 4)]),
    $"a schema 1 save must load into the first slots, loaded {string.Join(", ", migrated.Slots)}");

// Slots: a pickup tops up its kind first, then the first empty slot, hotbar before pack, or leaves
// everything when it cannot all fit; spending takes from the pack before the hotbar; a move goes
// into an empty slot, merges onto its kind up to a full stack, or swaps a whole stack with another kind.
SlotContents[] held = [new(0, ItemCatalog.Meat, 98), new(4, ItemCatalog.Meat, 2), new(10, ItemCatalog.Oil, 5)];
IReadOnlyList<SlotChange>? pickup = InventorySlots.PlanTake(held, [new ItemCount(ItemCatalog.Meat, 4), new ItemCount(ItemCatalog.Hide, 1)]);
Check.That(pickup is not null && pickup.SequenceEqual([new SlotChange(0, ItemCatalog.Meat, 1), new SlotChange(4, ItemCatalog.Meat, 3), new SlotChange(1, ItemCatalog.Hide, 1)]),
    $"a pickup must top up its kind, then take the first empty slot, planned {string.Join(", ", pickup ?? [])}");
SlotContents[] full = [.. Enumerable.Range(0, InventorySlots.Count).Select(slot => new SlotContents(slot, ItemCatalog.Claw, 99))];
Check.That(InventorySlots.PlanTake(full, [new ItemCount(ItemCatalog.Claw, 1)]) is null, "a pickup that does not fit must be left whole");
IReadOnlyList<SlotChange>? spent = InventorySlots.PlanSpend([new(0, ItemCatalog.Meat, 2), new(12, ItemCatalog.Meat, 1)], ItemCatalog.Meat, 2);
Check.That(spent is not null && spent.SequenceEqual([new SlotChange(12, ItemCatalog.Meat, 1), new SlotChange(0, ItemCatalog.Meat, 1)]),
    "spending must take from the pack before the hotbar");
Check.That(InventorySlots.PlanSpend([new(0, ItemCatalog.Meat, 2), new(12, ItemCatalog.Meat, 1)], ItemCatalog.Meat, 1, preferredSlot: 0)![0].Slot == 0,
    "spending must take from the slot asked for first");
Check.That(InventorySlots.PlanSpend(held, ItemCatalog.Hide, 1) is null, "spending what is not carried must be refused");
SlotPlan intoEmpty = InventorySlots.PlanMove(held, 10, 20, 2);
Check.That(intoEmpty.Refusal is null && intoEmpty.Given.SequenceEqual([new SlotChange(20, ItemCatalog.Oil, 2)]), "part of a stack can move into an empty slot");
SlotPlan merge = InventorySlots.PlanMove(held, 4, 0, 0);
Check.That(merge.Refusal is null && merge.Given.SequenceEqual([new SlotChange(0, ItemCatalog.Meat, 1)]), "a merge moves only what the stack has room for");
SlotPlan swap = InventorySlots.PlanMove(held, 10, 4, 0);
Check.That(swap.Refusal is null && swap.Given.SequenceEqual([new SlotChange(4, ItemCatalog.Oil, 5), new SlotChange(10, ItemCatalog.Meat, 2)]), "whole stacks of different kinds swap");
Check.That(InventorySlots.PlanMove(held, 10, 4, 1).Refusal is not null, "part of a stack cannot swap");
Check.That(InventorySlots.PlanMove(held, 7, 3, 0).Refusal is not null && InventorySlots.PlanMove(held, 0, 0, 0).Refusal is not null
    && InventorySlots.PlanMove(held, 0, InventorySlots.Count, 0).Refusal is not null,
    "an empty source, the same slot and a slot past the last are refused");

// --- block entities: identities are per session, meaning survives -------------------------------
BlockEntityIndex reloaded = new();
long before = reloaded.Revision;
reloaded.Restore(entityCodec.Decode(entityCodec.Encode(index.Snapshot())));
Check.That(reloaded.Revision != before, "a restore counts as a change");
Check.That(reloaded.Count == 3 && reloaded.TryFind(new VoxelAddress(-4, 12, 7), out BlockEntity door)
    && door.Kind == BlockEntityKind.Door && door.State == 1, "a door must come back in its cell with its state");
Check.That(reloaded.All.Select(entity => entity.Id).SequenceEqual([1, 2, 3]), "restored entities get fresh identities in cell order");
long placed = reloaded.Revision;
reloaded.SetState(new VoxelAddress(-4, 12, 7), 0);
Check.That(reloaded.Revision == placed + 1, "changing an entity's state counts as a change");
Check.That(reloaded.Break(new VoxelAddress(0, 0, 0)) is false && reloaded.Revision == placed + 1, "breaking an empty cell changes nothing");

// --- discovery ticks continue across sessions -----------------------------------------------------
DiscoveryState fresh = new(Seed);
Check.That(fresh.ResumeTick == 0, "an empty journal starts at tick zero");
DiscoveryState continued = new(Seed);
continued.Restore(journal);
Check.That(continued.ResumeTick == 41, $"a restored journal resumes after its latest tick, not at {continued.ResumeTick}");

Console.WriteLine($"Save checks: {SaveManifest.All.Count} manifest keys, each codec's round trip and tamper matrix, the restore rule, "
    + "the overlay's unchanged layout, block-entity reload, and journal ticks resuming across sessions.");
return Check.Finish("SaveCore");

static bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or OverflowException)
    {
        return true;
    }
}

static bool Refuses<T>(IProductStateCodec<T> codec, byte[] bytes) => Throws(() => codec.Decode(bytes));

/// <summary>
/// One saved key's codec with a sample state, and the tamper matrix every stored form must refuse:
/// the header's identity, the counted length, and the fingerprint over the records.
/// </summary>
internal sealed record SavedForm(SaveKey Key, string Name, Action<Action<bool, string>> Run)
{
    internal static SavedForm For<T>(SaveKey key, IProductStateCodec<T> codec, T sample, int recordBytes,
        Func<T, T, bool> same, Func<ulong, IProductStateCodec<T>>? forSeed) => new(key, key.Key, check =>
    {
        byte[] honest = Encode(codec, sample);
        T back = codec.Decode(honest);
        check(same(sample, back), $"{key.Key} must round-trip");
        check(Encode(codec, back).SequenceEqual(honest), $"{key.Key} must encode the same facts to the same bytes");
        check(BinaryPrimitives.ReadUInt32LittleEndian(honest) == key.Magic, $"{key.Key} must start with its manifest magic");
        check(BinaryPrimitives.ReadInt32LittleEndian(honest.AsSpan(4)) == key.Schema, $"{key.Key} must carry its manifest schema");
        int count = BinaryPrimitives.ReadInt32LittleEndian(honest.AsSpan(20));
        check(honest.Length == SaveEnvelope.HeaderBytes + (count * recordBytes), $"{key.Key} must be its header plus its counted records");

        (string Case, byte[] Bytes)[] tampered =
        [
            ("an empty blob", []),
            ("a header cut short", honest[..(SaveEnvelope.HeaderBytes - 1)]),
            ("the wrong magic", Flip(honest, 0)),
            ("another schema", Flip(honest, 4)),
            ("another generator version", Flip(honest, 8)),
            ("another seed", Flip(honest, 12)),
            ("a negative count", With(honest, 20, -1)),
            ("a count past the records", With(honest, 20, count + 1)),
            ("a huge count", With(honest, 20, int.MaxValue)),
            ("a fingerprint changed", Flip(honest, 24)),
            ("a record byte changed", Flip(honest, honest.Length - 1)),
            ("the last byte cut", honest[..^1]),
            ("a byte appended", [.. honest, 0]),
            ("the blob twice over", [.. honest, .. honest]),
        ];
        foreach ((string name, byte[] bytes) in tampered)
        {
            check(Refuses(codec, bytes), $"{key.Key} must refuse {name}");
        }

        if (forSeed is not null) check(Refuses(forSeed(0x0BAD_5EEDUL), honest), $"{key.Key} must refuse a save from another world");
    });

    private static byte[] Encode<T>(IProductStateCodec<T> codec, T state)
    {
        ArrayBufferWriter<byte> writer = new();
        codec.Encode(in state, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] Flip(byte[] bytes, int offset)
    {
        byte[] copy = [.. bytes];
        copy[offset] ^= 0x5A;
        return copy;
    }

    private static byte[] With(byte[] bytes, int offset, int value)
    {
        byte[] copy = [.. bytes];
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    private static bool Refuses<T>(IProductStateCodec<T> codec, byte[] bytes)
    {
        try
        {
            codec.Decode(bytes);
            return false;
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or OverflowException)
        {
            return true;
        }
    }
}
