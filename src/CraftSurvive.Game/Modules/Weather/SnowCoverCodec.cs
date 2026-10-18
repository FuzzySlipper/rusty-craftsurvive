using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// The snow cover's stored form under <see cref="SaveManifest.WeatherSnow"/> (#10068): the shared header, then
/// one record per kept cell - its two indices, its depth and the hour it stands at.
/// </summary>
internal sealed class SnowCoverCodec(SaveIdentity identity) : IProductStateCodec<IReadOnlyList<SnowCell>>
{
    /// <summary>Two 32-bit indices and two 64-bit reals.</summary>
    internal const int RecordBytes = (sizeof(int) * 2) + (sizeof(double) * 2);

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: SnowCell.MostCells, RecordBytes);

    private static SaveKey Key => SaveManifest.WeatherSnow;

    public void Encode(in IReadOnlyList<SnowCell> state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(IReadOnlyList<SnowCell> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, state.Count);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        foreach (SnowCell cell in state)
        {
            writer.Int32(cell.CellX); writer.Int32(cell.CellZ); writer.Double(cell.Depth); writer.Double(cell.Hours);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public IReadOnlyList<SnowCell> Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        SaveReader reader = SaveEnvelope.Records(payload);
        List<SnowCell> cells = new(count);
        for (int index = 0; index < count; index++) cells.Add(new SnowCell(reader.Int32(), reader.Int32(), reader.Double(), reader.Double()));
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, cells));
        Validate(cells);
        return cells;
    }

    /// <summary>Refuses cover no session could have produced: a depth out of range, an hour off the clock, a cell twice or out of order.</summary>
    private static void Validate(IReadOnlyList<SnowCell> state)
    {
        if (state.Count > SnowCell.MostCells) throw new InvalidOperationException($"{Key.Key} keeps more cells than the cover does.");
        for (int index = 0; index < state.Count; index++)
        {
            SnowCell cell = state[index];
            if (!double.IsFinite(cell.Depth) || cell.Depth < 0 || cell.Depth > SnowCell.MostDepth || !double.IsFinite(cell.Hours))
                throw new InvalidOperationException($"{Key.Key} holds a cell out of range.");
            if (index > 0 && (state[index - 1].CellZ, state[index - 1].CellX).CompareTo((cell.CellZ, cell.CellX)) >= 0)
                throw new InvalidOperationException($"{Key.Key} holds its cells out of order.");
        }
    }

    private static ulong Fingerprint(ulong seed, IReadOnlyList<SnowCell> state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (SnowCell cell in state)
        {
            hash.Mix((long)cell.CellX);
            hash.Mix((long)cell.CellZ);
            hash.Mix(cell.Depth);
            hash.Mix(cell.Hours);
        }

        return hash.Value;
    }
}
