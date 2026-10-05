using System.Buffers;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.World;

internal sealed record WorldMapSave(long Generation, WorldMap Map);

/// <summary>
/// The map's simulated fields are persisted, not merely a seed that a later implementation
/// might reinterpret. Rivers, rock and relief allowance are rebuilt from them on restore.
/// </summary>
internal sealed class WorldMapCodec : IProductStateCodec<WorldMapSave>
{
    private const int RecordBytes = MapFields.FieldCount * sizeof(float);
    private const int HeaderPadding = RecordBytes - sizeof(long) - sizeof(int);
    private static SaveBounds Bounds => new(MapGrid.MaximumNodes + 1, RecordBytes);
    private static SaveKey Key => SaveManifest.WorldMap;

    public void Encode(in WorldMapSave state, IBufferWriter<byte> destination)
    {
        TerrainConfiguration config = state.Map.Configuration;
        MapFields fields = state.Map.Fields;
        int count = fields.Grid.Count;
        byte[] bytes = SaveEnvelope.Allocate(Key, new(config.GeneratorVersion, config.Seed), Bounds, count + 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Int64(state.Generation);
        writer.Int32(config.Size);
        // Reserve the rest of the first record; these bytes are required to remain zero.
        for (int i = 0; i < HeaderPadding; i++) writer.Byte(0);
        for (int i = 0; i < count; i++)
        {
            writer.Single(fields.Elevation[i]); writer.Single(fields.Temperature[i]); writer.Single(fields.Moisture[i]);
            writer.Single(fields.Hardness[i]); writer.Single(fields.Discharge[i]);
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
        int size = reader.Int32();
        if (generation < 1) throw new InvalidOperationException("World map has no generation identity.");
        for (int i = 0; i < HeaderPadding; i++)
            if (reader.Byte() != 0) throw new InvalidOperationException("World map has unsupported configuration fields.");
        TerrainConfiguration configuration = new TerrainConfiguration(identity.Seed, size, identity.GeneratorVersion).Validate();
        MapGrid grid = MapGrid.For(size);
        if (count - 1 != grid.Count) throw new InvalidOperationException("World map node count does not match its extent.");
        float[] elevation = new float[grid.Count], temperature = new float[grid.Count], moisture = new float[grid.Count];
        float[] hardness = new float[grid.Count], discharge = new float[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            elevation[i] = reader.Single(); temperature[i] = reader.Single(); moisture[i] = reader.Single();
            hardness[i] = reader.Single(); discharge[i] = reader.Single();
        }
        WorldMapSave state = new(generation, new(configuration, new MapFields(grid, elevation, temperature, moisture, hardness, discharge)));
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
