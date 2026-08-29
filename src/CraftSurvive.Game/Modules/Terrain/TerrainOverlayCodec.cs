using System.Buffers.Binary;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Stable bounded binary storage for product-owned overlay facts. Persistence
/// services may store these bytes later; this type has no transport or Engine
/// dependency and never crosses the C# native ABI directly.
/// </summary>
internal static class TerrainOverlayCodec
{
    internal static byte[] Encode(TerrainOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        TerrainOverlayEntry[] entries = snapshot.Entries;
        int byteLength = checked(TerrainConstants.OverlayHeaderBytes
            + (entries.Length * TerrainConstants.OverlayEntryBytes));
        if (byteLength > TerrainConstants.MaximumOverlayBytes)
        {
            throw new InvalidOperationException(
                $"Terrain overlay storage must not exceed {TerrainConstants.MaximumOverlayBytes} bytes.");
        }

        byte[] bytes = new byte[byteLength];
        Span<byte> destination = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(destination, TerrainConstants.OverlayMagic);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(sizeof(uint)), TerrainConstants.OverlaySchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(sizeof(uint) + sizeof(int)), TerrainConstants.GenerationVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(sizeof(uint) + (sizeof(int) * 2)), snapshot.Seed);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(sizeof(uint) + sizeof(int) + sizeof(uint) + sizeof(ulong)), entries.Length);
        ulong fingerprint = Fingerprint(snapshot.Seed, entries);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(TerrainConstants.OverlayHeaderBytes - sizeof(ulong)), fingerprint);

        int offset = TerrainConstants.OverlayHeaderBytes;
        foreach (TerrainOverlayEntry entry in entries)
        {
            WriteEntry(destination.Slice(offset, TerrainConstants.OverlayEntryBytes), entry);
            offset += TerrainConstants.OverlayEntryBytes;
        }

        return bytes;
    }

    internal static TerrainOverlaySnapshot Decode(ulong expectedSeed, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > TerrainConstants.MaximumOverlayBytes)
        {
            throw new InvalidOperationException(
                $"Terrain overlay storage must not exceed {TerrainConstants.MaximumOverlayBytes} bytes.");
        }

        if (bytes.Length < TerrainConstants.OverlayHeaderBytes)
        {
            throw new InvalidOperationException("Terrain overlay storage is incomplete.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        int schema = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(sizeof(uint)));
        uint generation = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(sizeof(uint) + sizeof(int)));
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(sizeof(uint) + (sizeof(int) * 2)));
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(sizeof(uint) + sizeof(int) + sizeof(uint) + sizeof(ulong)));
        ulong fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(TerrainConstants.OverlayHeaderBytes - sizeof(ulong)));
        if (magic != TerrainConstants.OverlayMagic || schema != TerrainConstants.OverlaySchemaVersion)
        {
            throw new InvalidOperationException("Terrain overlay storage uses an unsupported schema.");
        }

        if (generation != TerrainConstants.GenerationVersion)
        {
            throw new InvalidOperationException("Terrain overlay storage uses an unsupported terrain generation version.");
        }

        if (seed != expectedSeed)
        {
            throw new InvalidOperationException("Terrain overlay storage belongs to a different terrain seed.");
        }

        if (count < 0 || count > TerrainConstants.MaximumOverlayEntries)
        {
            throw new InvalidOperationException("Terrain overlay storage has an unsupported entry count.");
        }

        int expectedLength = checked(TerrainConstants.OverlayHeaderBytes
            + (count * TerrainConstants.OverlayEntryBytes));
        if (bytes.Length != expectedLength)
        {
            throw new InvalidOperationException("Terrain overlay storage length is not canonical.");
        }

        TerrainOverlayEntry[] entries = new TerrainOverlayEntry[count];
        int offset = TerrainConstants.OverlayHeaderBytes;
        for (int index = 0; index < count; index++)
        {
            entries[index] = ReadEntry(bytes.Slice(offset, TerrainConstants.OverlayEntryBytes));
            if (index > 0 && entries[index - 1].Address.CompareTo(entries[index].Address) >= 0)
            {
                throw new InvalidOperationException("Terrain overlay storage entries are not canonical.");
            }

            offset += TerrainConstants.OverlayEntryBytes;
        }

        TerrainOverlaySnapshot snapshot = new(seed, entries);
        if (fingerprint != Fingerprint(seed, snapshot.Entries))
        {
            throw new InvalidOperationException("Terrain overlay storage fingerprint does not match its entries.");
        }

        return snapshot;
    }

    private static void WriteEntry(Span<byte> destination, TerrainOverlayEntry entry)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, entry.Address.X);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(sizeof(long)), entry.Address.Y);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(sizeof(long) * 2), entry.Address.Z);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(sizeof(long) * 3), entry.Material);
    }

    private static TerrainOverlayEntry ReadEntry(ReadOnlySpan<byte> source) => new(
        new VoxelAddress(
            BinaryPrimitives.ReadInt64LittleEndian(source),
            BinaryPrimitives.ReadInt64LittleEndian(source.Slice(sizeof(long))),
            BinaryPrimitives.ReadInt64LittleEndian(source.Slice(sizeof(long) * 2))),
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(sizeof(long) * 3)));

    private static ulong Fingerprint(ulong seed, TerrainOverlayEntry[] entries)
    {
        ulong hash = TerrainConstants.OverlayFingerprintOffset ^ seed;
        foreach (TerrainOverlayEntry entry in entries)
        {
            hash = Mix(hash, unchecked((ulong)entry.Address.X));
            hash = Mix(hash, unchecked((ulong)entry.Address.Y));
            hash = Mix(hash, unchecked((ulong)entry.Address.Z));
            ulong encodedMaterial = entry.Material == TerrainConstants.EmptyMaterial
                ? 0UL
                : (ulong)(entry.Material + 1);
            hash = Mix(hash, encodedMaterial);
        }

        return hash;
    }

    private static ulong Mix(ulong hash, ulong value) => unchecked(
        (hash ^ value) * TerrainConstants.OverlayFingerprintPrime);
}
