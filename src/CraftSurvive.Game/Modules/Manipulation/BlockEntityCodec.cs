using System.Buffers;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// The block entities' stored form under <see cref="SaveManifest.BlockEntities"/>: the shared
/// header, then one record per entity in canonical cell order - the cell, the kind and its state.
/// </summary>
internal sealed class BlockEntityCodec(SaveIdentity identity) : IProductStateCodec<BlockEntityRecord[]>
{
    /// <summary>Three 64-bit coordinates, the kind and the state.</summary>
    internal const int RecordBytes = (sizeof(long) * 3) + sizeof(byte) + sizeof(ushort);

    /// <summary>Every entity stands in an edited cell, so the overlay's bound is also the entities' bound.</summary>
    internal static SaveBounds Bounds { get; } = new(TerrainConstants.MaximumOverlayEntries, RecordBytes);

    private static SaveKey Key => SaveManifest.BlockEntities;

    public void Encode(in BlockEntityRecord[] state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(BlockEntityRecord[] records)
    {
        ArgumentNullException.ThrowIfNull(records);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, records.Length);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        for (int index = 0; index < records.Length; index++)
        {
            BlockEntityRecord record = records[index];
            if (index > 0 && records[index - 1].Cell.CompareTo(record.Cell) >= 0)
            {
                throw new InvalidOperationException("Block entities are saved in canonical cell order, one per cell.");
            }

            writer.Int64(record.Cell.X);
            writer.Int64(record.Cell.Y);
            writer.Int64(record.Cell.Z);
            writer.Byte((byte)record.Kind);
            writer.UInt16(record.State);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, records));
        return bytes;
    }

    public BlockEntityRecord[] Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        BlockEntityRecord[] records = new BlockEntityRecord[count];
        SaveReader reader = SaveEnvelope.Records(payload);
        for (int index = 0; index < count; index++)
        {
            VoxelAddress cell = new(reader.Int64(), reader.Int64(), reader.Int64());
            BlockEntityKind kind = (BlockEntityKind)reader.Byte();
            if (!Enum.IsDefined(kind))
            {
                throw new InvalidOperationException($"Block entity {index} has kind {kind}, which is not a known kind.");
            }

            records[index] = new BlockEntityRecord(kind, cell, reader.UInt16());
            if (index > 0 && records[index - 1].Cell.CompareTo(cell) >= 0)
            {
                throw new InvalidOperationException($"{Key.Key} records are not in canonical order.");
            }
        }

        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, records));
        return records;
    }

    private static ulong Fingerprint(ulong seed, BlockEntityRecord[] records)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (BlockEntityRecord record in records)
        {
            hash.Mix(record.Cell.X);
            hash.Mix(record.Cell.Y);
            hash.Mix(record.Cell.Z);
            hash.Mix((ulong)record.Kind);
            hash.Mix((ulong)record.State);
        }

        return hash.Value;
    }
}
