using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The river stage's stored form under <see cref="SaveManifest.WaterStage"/> (#9878 review): the shared
/// header, then a record of the hour and one of the counts, then each reach's storage and frost, then
/// each cell's snow and ground water - two reals a record.
/// </summary>
internal sealed class RiverStageCodec(SaveIdentity identity) : IProductStateCodec<RiverStageState>
{
    /// <summary>Two 64-bit reals.</summary>
    internal const int RecordBytes = sizeof(double) * 2;

    /// <summary>At most this many reaches and cells: a continent's network and its precipitation cells, with room.</summary>
    private const int MostReaches = 131_072, MostCells = 32_768, HeaderRecords = 2;

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: HeaderRecords + MostReaches + MostCells, RecordBytes);

    private static SaveKey Key => SaveManifest.WaterStage;

    public void Encode(in RiverStageState state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(RiverStageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, HeaderRecords + state.Storage.Length + state.Snow.Length);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Double(state.Hours); writer.Double(0);
        writer.Double(state.Storage.Length); writer.Double(state.Snow.Length);
        for (int r = 0; r < state.Storage.Length; r++) { writer.Double(state.Storage[r]); writer.Double(state.Frost[r]); }
        for (int c = 0; c < state.Snow.Length; c++) { writer.Double(state.Snow[c]); writer.Double(state.Ground[c]); }
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public RiverStageState Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        if (count < HeaderRecords) throw new InvalidOperationException($"{Key.Key} has no header.");
        SaveReader reader = SaveEnvelope.Records(payload);
        double hours = reader.Double();
        if (BitConverter.DoubleToInt64Bits(reader.Double()) != 0) throw new InvalidOperationException($"{Key.Key} holds a header that is not padded with zero.");
        double reaches = reader.Double(), cells = reader.Double();
        if (reaches < 0 || cells < 0 || reaches > MostReaches || cells > MostCells || reaches != Math.Floor(reaches) || cells != Math.Floor(cells)
            || HeaderRecords + reaches + cells != count)
            throw new InvalidOperationException($"{Key.Key} counts do not match its records.");
        double[] storage = new double[(int)reaches], frost = new double[(int)reaches], snow = new double[(int)cells], ground = new double[(int)cells];
        for (int r = 0; r < storage.Length; r++) { storage[r] = reader.Double(); frost[r] = reader.Double(); }
        for (int c = 0; c < snow.Length; c++) { snow[c] = reader.Double(); ground[c] = reader.Double(); }
        RiverStageState state = new(hours, storage, frost, snow, ground);
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, state));
        Validate(state);
        return state;
    }

    /// <summary>Refuses a stage no session could have produced: an hour off the clock, a store not finite or below nothing.</summary>
    private static void Validate(RiverStageState state)
    {
        if (!double.IsFinite(state.Hours)) throw new InvalidOperationException($"{Key.Key} stands at no hour.");
        if (state.Storage.Length != state.Frost.Length || state.Snow.Length != state.Ground.Length || state.Storage.Length > MostReaches || state.Snow.Length > MostCells)
            throw new InvalidOperationException($"{Key.Key} holds mismatched stores.");
        foreach (double[] stores in new[] { state.Storage, state.Frost, state.Snow, state.Ground })
            foreach (double value in stores)
                if (!double.IsFinite(value) || value < 0) throw new InvalidOperationException($"{Key.Key} holds a store that is not a finite amount.");
    }

    private static ulong Fingerprint(ulong seed, RiverStageState state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix(state.Hours);
        foreach (double[] stores in new[] { state.Storage, state.Frost, state.Snow, state.Ground })
        {
            hash.Mix((long)stores.Length);
            foreach (double value in stores) hash.Mix(value);
        }

        return hash.Value;
    }
}
