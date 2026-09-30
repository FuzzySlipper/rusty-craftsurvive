using System.Buffers;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// The journal's stored form under <see cref="SaveManifest.DiscoveryJournal"/>: the shared header,
/// then one record per place in canonical cell order - its anchor cell, position, kind, stage and
/// both journal ticks. A kind or stage outside the known range is refused rather than adopted:
/// these numbers are persisted and published, so one that is not a known value is a blob this
/// build cannot interpret.
/// </summary>
internal sealed class DiscoveryCodec(SaveIdentity identity) : IProductStateCodec<DiscoverySnapshot>
{
    /// <summary>Cells, position, kind, stage and both ticks.</summary>
    internal const int RecordBytes = (sizeof(long) * 4) + sizeof(ushort) + sizeof(byte) + (sizeof(long) * 2);

    internal static SaveBounds Bounds { get; } = new(PoiConstants.MaximumDiscoveryEntries, RecordBytes);

    private static SaveKey Key => SaveManifest.DiscoveryJournal;

    public void Encode(in DiscoverySnapshot state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(DiscoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Seed != identity.Seed)
        {
            throw new InvalidOperationException("A journal can only be saved into its own world.");
        }

        DiscoveryEntry[] entries = snapshot.Entries;
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, entries.Length);
        SaveWriter records = SaveEnvelope.Records(bytes);
        foreach (DiscoveryEntry entry in entries)
        {
            records.Int64(entry.CellX);
            records.Int64(entry.CellZ);
            records.Int64(entry.X);
            records.Int64(entry.Z);
            records.UInt16((ushort)entry.Kind);
            records.Byte((byte)entry.Stage);
            records.Int64(entry.FirstSeenTick);
            records.Int64(entry.LastTick);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, entries));
        return bytes;
    }

    public DiscoverySnapshot Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        DiscoveryEntry[] entries = new DiscoveryEntry[count];
        SaveReader records = SaveEnvelope.Records(payload);
        for (int index = 0; index < count; index++)
        {
            long cellX = records.Int64();
            long cellZ = records.Int64();
            long x = records.Int64();
            long z = records.Int64();
            PoiKind kind = (PoiKind)records.UInt16();
            if ((long)kind < PoiConstants.FirstKind || (long)kind > PoiConstants.LastKind)
            {
                throw new InvalidOperationException($"Place {index} has kind {kind}, which is not a known kind.");
            }

            DiscoveryStage stage = (DiscoveryStage)records.Byte();
            if (stage is not (DiscoveryStage.Seen or DiscoveryStage.Visited))
            {
                throw new InvalidOperationException($"Place {index} has stage {stage}, which is not a known stage.");
            }

            entries[index] = new DiscoveryEntry(cellX, cellZ, kind, x, z, stage, records.Int64(), records.Int64());
        }

        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, entries));

        // The snapshot's own validation runs here: canonical order, unique places, a stage that
        // recorded something, and ticks that run forwards.
        return new DiscoverySnapshot(identity.Seed, entries);
    }

    private static ulong Fingerprint(ulong seed, DiscoveryEntry[] entries)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (DiscoveryEntry entry in entries)
        {
            hash.Mix(entry.CellX);
            hash.Mix(entry.CellZ);
            hash.Mix(entry.X);
            hash.Mix(entry.Z);
            hash.Mix((ulong)(ushort)entry.Kind);
            hash.Mix((ulong)(byte)entry.Stage);
            hash.Mix(entry.FirstSeenTick);
            hash.Mix(entry.LastTick);
        }

        return hash.Value;
    }
}
