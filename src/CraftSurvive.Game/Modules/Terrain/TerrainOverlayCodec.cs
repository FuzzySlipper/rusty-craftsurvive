using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The overlay's stored form under <see cref="SaveManifest.TerrainOverlay"/>: the shared header,
/// then one record per edited cell in canonical address order - the cell and the material it holds.
/// It has no Engine dependency beyond the codec shape, so it is checked without a runtime.
/// </summary>
internal sealed class TerrainOverlayCodec(SaveIdentity identity) : IProductStateCodec<TerrainOverlaySnapshot>
{
    /// <summary>Three 64-bit coordinates and the material.</summary>
    internal const int RecordBytes = (sizeof(long) * 3) + sizeof(ushort);

    internal static SaveBounds Bounds { get; } = new(TerrainConstants.MaximumOverlayEntries, RecordBytes);

    private static SaveKey Key => SaveManifest.TerrainOverlay;

    public void Encode(in TerrainOverlaySnapshot state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(TerrainOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Seed != identity.Seed)
        {
            throw new InvalidOperationException("A terrain overlay can only be saved into its own world.");
        }

        TerrainOverlayEntry[] entries = snapshot.Entries;
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, entries.Length);
        SaveWriter records = SaveEnvelope.Records(bytes);
        foreach (TerrainOverlayEntry entry in entries)
        {
            records.Int64(entry.Address.X);
            records.Int64(entry.Address.Y);
            records.Int64(entry.Address.Z);
            records.UInt16(entry.Material);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, entries));
        return bytes;
    }

    public TerrainOverlaySnapshot Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        TerrainOverlayEntry[] entries = new TerrainOverlayEntry[count];
        SaveReader records = SaveEnvelope.Records(payload);
        for (int index = 0; index < count; index++)
        {
            entries[index] = new TerrainOverlayEntry(
                new VoxelAddress(records.Int64(), records.Int64(), records.Int64()),
                records.UInt16());
            if (index > 0 && entries[index - 1].Address.CompareTo(entries[index].Address) >= 0)
            {
                throw new InvalidOperationException($"{Key.Key} records are not in canonical order.");
            }
        }

        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, entries));
        return new TerrainOverlaySnapshot(identity.Seed, entries);
    }

    private static ulong Fingerprint(ulong seed, TerrainOverlayEntry[] entries)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (TerrainOverlayEntry entry in entries)
        {
            hash.Mix(entry.Address.X);
            hash.Mix(entry.Address.Y);
            hash.Mix(entry.Address.Z);

            // Empty is stored as zero and every material one above its slot, as the first schema did.
            hash.Mix(entry.Material == TerrainConstants.EmptyMaterial ? 0UL : (ulong)(entry.Material + 1));
        }

        return hash.Value;
    }
}
