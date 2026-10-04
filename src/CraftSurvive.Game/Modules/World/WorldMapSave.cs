using System.Buffers;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.World;

internal sealed record WorldMapSave(long Generation, WorldMap Map);

/// <summary>The map's samples are persisted, not merely a seed that a later implementation might reinterpret.</summary>
internal sealed class WorldMapCodec : IProductStateCodec<WorldMapSave>
{
    private const int FieldsPerNode = 6;
    private const int RecordBytes = FieldsPerNode * sizeof(double);
    private static SaveBounds Bounds => new(WorldMap.MaximumNodes + 1, RecordBytes);
    private static SaveKey Key => SaveManifest.WorldMap;

    public void Encode(in WorldMapSave state, IBufferWriter<byte> destination)
    {
        TerrainConfiguration config = state.Map.Configuration;
        byte[] bytes = SaveEnvelope.Allocate(Key, new(config.GeneratorVersion, config.Seed), Bounds, state.Map.Nodes.Length + 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Int64(state.Generation);
        writer.Int64(config.Size);
        // Reserve the rest of the first record; these bytes are required to remain zero.
        for (int i = 2; i < FieldsPerNode; i++) writer.Int64(0);
        foreach (MapSample n in state.Map.Nodes)
        {
            writer.Double(n.Elevation); writer.Double(n.Temperature); writer.Double(n.Moisture);
            writer.Double(n.Rock); writer.Double(n.Detail); writer.Double(n.Passage);
        }
        SaveEnvelope.Seal(bytes, Fingerprint(state));
        destination.Write(bytes);
    }

    public WorldMapSave Decode(ReadOnlySpan<byte> payload)
    {
        SaveIdentity identity = SaveEnvelope.IdentityOf(payload);
        if (identity.GeneratorVersion != TerrainGeneratorContract.CurrentVersion)
            throw new InvalidOperationException("World map uses a retired terrain recipe.");
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        if (count < 2) throw new InvalidOperationException("World map has no samples.");
        SaveReader reader = SaveEnvelope.Records(payload);
        long generation = reader.Int64();
        int size = checked((int)reader.Int64());
        if (generation < 1) throw new InvalidOperationException("World map has no generation identity.");
        for (int i = 2; i < FieldsPerNode; i++)
            if (reader.Int64() != 0) throw new InvalidOperationException("World map has unsupported configuration fields.");
        MapSample[] samples = new MapSample[count - 1];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = new(reader.Double(), reader.Double(), reader.Double(), reader.Double(), reader.Double(), reader.Double());
        WorldMapSave state = new(generation, new(new(identity.Seed, size, identity.GeneratorVersion), samples));
        SaveEnvelope.Verify(Key, payload, Fingerprint(state));
        return state;
    }

    private static ulong Fingerprint(WorldMapSave state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(state.Map.Fingerprint);
        hash.Mix(state.Generation);
        return hash.Value;
    }
}
