using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>When a column of burnt ground last burnt (#10296): the column of chunks across x and z, and the world's hour.</summary>
internal readonly record struct ScarColumn(int ColumnX, int ColumnZ, double Hours)
{
    /// <summary>The ledger keeps at most this many columns; past it the oldest are forgotten (and their scars heal at once).</summary>
    internal const int MostColumns = 16384;
}

/// <summary>
/// The scar ledger's stored form under <see cref="SaveManifest.ChemistryScars"/> (#10296): the shared header, then one
/// record per column - its two indices and the hour it burnt - in order of z, then x.
/// </summary>
internal sealed class BurnScarsCodec(SaveIdentity identity) : IProductStateCodec<IReadOnlyList<ScarColumn>>
{
    /// <summary>Two 32-bit indices and a 64-bit real.</summary>
    internal const int RecordBytes = (sizeof(int) * 2) + sizeof(double);

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: ScarColumn.MostColumns, RecordBytes);

    private static SaveKey Key => SaveManifest.ChemistryScars;

    public void Encode(in IReadOnlyList<ScarColumn> state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(IReadOnlyList<ScarColumn> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, state.Count);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        foreach (ScarColumn column in state)
        {
            writer.Int32(column.ColumnX); writer.Int32(column.ColumnZ); writer.Double(column.Hours);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public IReadOnlyList<ScarColumn> Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        SaveReader reader = SaveEnvelope.Records(payload);
        List<ScarColumn> columns = new(count);
        for (int index = 0; index < count; index++) columns.Add(new ScarColumn(reader.Int32(), reader.Int32(), reader.Double()));
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, columns));
        Validate(columns);
        return columns;
    }

    /// <summary>Refuses a ledger no session could have produced: an hour off the clock, a column twice or out of order.</summary>
    private static void Validate(IReadOnlyList<ScarColumn> state)
    {
        if (state.Count > ScarColumn.MostColumns) throw new InvalidOperationException($"{Key.Key} keeps more columns than the ledger does.");
        for (int index = 0; index < state.Count; index++)
        {
            if (!double.IsFinite(state[index].Hours)) throw new InvalidOperationException($"{Key.Key} holds an hour off the clock.");
            if (index > 0 && (state[index - 1].ColumnZ, state[index - 1].ColumnX).CompareTo((state[index].ColumnZ, state[index].ColumnX)) >= 0)
                throw new InvalidOperationException($"{Key.Key} holds its columns out of order.");
        }
    }

    private static ulong Fingerprint(ulong seed, IReadOnlyList<ScarColumn> state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (ScarColumn column in state)
        {
            hash.Mix((long)column.ColumnX);
            hash.Mix((long)column.ColumnZ);
            hash.Mix(column.Hours);
        }

        return hash.Value;
    }
}
