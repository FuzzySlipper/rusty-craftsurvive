using System.Buffers.Binary;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// Stable bounded binary storage for the journal. It mirrors the terrain overlay codec's
/// discipline, and for the same reasons: a fixed header carries the world's identity, so a
/// save from another seed or another generation version is recognised rather than
/// misread; the entry count is checked against the payload length before anything is
    /// A fingerprint catches a blob whose bytes were altered in place. Truncation is caught
/// corrupted in the middle, which a length check alone cannot.
///
/// Everything here throws on bad input. That is deliberate, and it is the opposite of the
/// rule the journal's own update path follows: this runs on load, where the caller can
/// discard the save and regenerate - so refusing loudly is safe, while a silent partial
/// read would quietly invent a history the player never had.
/// </summary>
internal static class DiscoveryCodec
{
    internal static byte[] Encode(DiscoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DiscoveryEntry[] entries = snapshot.Entries;
        int byteLength = checked(DiscoveryConstants.HeaderBytes + (entries.Length * DiscoveryConstants.EntryBytes));
        if (byteLength > DiscoveryConstants.MaximumJournalBytes)
        {
            throw new InvalidOperationException(
                $"A journal must not exceed {DiscoveryConstants.MaximumJournalBytes} bytes.");
        }

        byte[] bytes = new byte[byteLength];
        Span<byte> destination = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(destination, DiscoveryConstants.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(sizeof(uint)), DiscoveryConstants.SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(sizeof(uint) + sizeof(int)), TerrainConstants.GenerationVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(sizeof(uint) + (sizeof(int) * 2)), snapshot.Seed);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.Slice(sizeof(uint) + sizeof(int) + sizeof(uint) + sizeof(ulong)), entries.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(DiscoveryConstants.HeaderBytes - sizeof(ulong)), Fingerprint(snapshot.Seed, entries));

        int offset = DiscoveryConstants.HeaderBytes;
        foreach (DiscoveryEntry entry in entries)
        {
            Span<byte> target = destination.Slice(offset, DiscoveryConstants.EntryBytes);
            BinaryPrimitives.WriteInt64LittleEndian(target, entry.CellX);
            BinaryPrimitives.WriteInt64LittleEndian(target.Slice(8), entry.CellZ);
            BinaryPrimitives.WriteInt64LittleEndian(target.Slice(16), entry.X);
            BinaryPrimitives.WriteInt64LittleEndian(target.Slice(24), entry.Z);
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(32), (ushort)entry.Kind);
            target[34] = (byte)entry.Stage;
            BinaryPrimitives.WriteInt64LittleEndian(target.Slice(35), entry.FirstSeenTick);
            BinaryPrimitives.WriteInt64LittleEndian(target.Slice(43), entry.LastTick);
            offset += DiscoveryConstants.EntryBytes;
        }

        return bytes;
    }

    internal static DiscoverySnapshot Decode(ulong expectedSeed, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > DiscoveryConstants.MaximumJournalBytes)
        {
            throw new InvalidOperationException(
                $"A journal must not exceed {DiscoveryConstants.MaximumJournalBytes} bytes.");
        }

        if (bytes.Length < DiscoveryConstants.HeaderBytes)
        {
            throw new InvalidOperationException("Stored journal is incomplete.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != DiscoveryConstants.Magic)
        {
            throw new InvalidOperationException("Stored journal has an unrecognised format.");
        }

        int schema = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(sizeof(uint)));
        if (schema != DiscoveryConstants.SchemaVersion)
        {
            throw new InvalidOperationException($"Stored journal uses unsupported schema {schema}.");
        }

        uint generation = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(sizeof(uint) + sizeof(int)));
        if (generation != TerrainConstants.GenerationVersion)
        {
            throw new InvalidOperationException(
                $"Stored journal was written for generation {generation}, not {TerrainConstants.GenerationVersion}.");
        }

        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(sizeof(uint) + (sizeof(int) * 2)));
        if (seed != expectedSeed)
        {
            throw new InvalidOperationException("Stored journal belongs to a different world.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(sizeof(uint) + sizeof(int) + sizeof(uint) + sizeof(ulong)));
        if (count < 0 || count > PoiConstants.MaximumDiscoveryEntries)
        {
            throw new InvalidOperationException($"Stored journal declares {count} entries.");
        }

        int expectedLength = checked(DiscoveryConstants.HeaderBytes + (count * DiscoveryConstants.EntryBytes));
        if (bytes.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"Stored journal is {bytes.Length} bytes but declares {count} entries.");
        }

        DiscoveryEntry[] entries = new DiscoveryEntry[count];
        int offset = DiscoveryConstants.HeaderBytes;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> source = bytes.Slice(offset, DiscoveryConstants.EntryBytes);
            long cellX = BinaryPrimitives.ReadInt64LittleEndian(source);
            long cellZ = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8));
            long x = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16));
            long z = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(24));
            PoiKind kind = (PoiKind)BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(32));
            // A kind or stage outside the known range is refused rather than adopted: these numbers
            // are persisted and published, so a value that is not one of them is a blob this build
            // cannot interpret, and the codec's job is to say so loudly while refusing is still safe.
            if ((int)kind < (int)PoiConstants.FirstKind || (int)kind > (int)PoiConstants.LastKind)
            {
                throw new InvalidOperationException($"Place {index} has kind {kind}, which is not a known kind.");
            }
            DiscoveryStage stage = (DiscoveryStage)source[34];
            entries[index] = new DiscoveryEntry(cellX, cellZ, kind, x, z, stage,
                BinaryPrimitives.ReadInt64LittleEndian(source.Slice(35)),
                BinaryPrimitives.ReadInt64LittleEndian(source.Slice(43)));
            offset += DiscoveryConstants.EntryBytes;
        }

        ulong fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.Slice(DiscoveryConstants.HeaderBytes - sizeof(ulong)));
        if (fingerprint != Fingerprint(seed, entries))
        {
            throw new InvalidOperationException("Stored journal does not match its own fingerprint.");
        }

        // The snapshot's own validation runs here: canonical order, unique places, a kind
        // that exists, and a stage that recorded something.
        return new DiscoverySnapshot(seed, entries);
    }

    private static ulong Fingerprint(ulong seed, DiscoveryEntry[] entries)
    {
        ulong hash = 0xcbf2_9ce4_8422_2325UL ^ seed;
        foreach (DiscoveryEntry entry in entries)
        {
            hash = Mix(hash, (ulong)entry.CellX);
            hash = Mix(hash, (ulong)entry.CellZ);
            hash = Mix(hash, (ulong)entry.X);
            hash = Mix(hash, (ulong)entry.Z);
            hash = Mix(hash, (ulong)(ushort)entry.Kind);
            hash = Mix(hash, (byte)entry.Stage);
            hash = Mix(hash, (ulong)entry.FirstSeenTick);
            hash = Mix(hash, (ulong)entry.LastTick);
        }

        return hash;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            hash ^= value;
            return hash * 0x100_0000_01b3UL;
        }
    }
}
