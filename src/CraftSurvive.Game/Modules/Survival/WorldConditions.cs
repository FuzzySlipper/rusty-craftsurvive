using System.Buffers;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Survival;

/// <summary>How hard the world is on the player. Stored by value: append only, never renumber.</summary>
internal enum Difficulty
{
    /// <summary>No hunger damage, no drowning damage, creatures do not seek the player out at night.</summary>
    Gentle = 0,

    Normal = 1,

    /// <summary>Hunger and air run out faster and hurt more; night is longer in effect.</summary>
    Harsh = 2,
}

/// <summary>The world's own conditions that outlive a session: the time of day and the difficulty.</summary>
internal readonly record struct WorldConditionsState(long Day, double DayFraction, Difficulty Difficulty)
{
    internal WorldTime Time => new(Day, DayFraction);

    internal static WorldConditionsState Fresh => new(WorldClock.Start.Day, WorldClock.Start.DayFraction, Difficulty.Normal);
}

/// <summary>The conditions' stored form under <see cref="SaveManifest.WorldConditions"/>: the shared header and one record.</summary>
internal sealed class WorldConditionsCodec(SaveIdentity identity) : IProductStateCodec<WorldConditionsState>
{
    /// <summary>A 64-bit day, a 64-bit real and a 32-bit difficulty.</summary>
    internal const int RecordBytes = sizeof(long) + sizeof(double) + sizeof(int);

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: 1, RecordBytes);

    private static SaveKey Key => SaveManifest.WorldConditions;

    public void Encode(in WorldConditionsState state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(WorldConditionsState state)
    {
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, count: 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Int64(state.Day);
        writer.Double(state.DayFraction);
        writer.Int32((int)state.Difficulty);
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public WorldConditionsState Decode(ReadOnlySpan<byte> payload)
    {
        if (SaveEnvelope.Open(Key, identity, Bounds, payload) != 1)
        {
            throw new InvalidOperationException($"{Key.Key} holds exactly one record.");
        }

        SaveReader reader = SaveEnvelope.Records(payload);
        WorldConditionsState state = new(reader.Int64(), reader.Double(), (Difficulty)reader.Int32());
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, state));
        Validate(state);
        return state;
    }

    /// <summary>Refuses conditions no session could have produced.</summary>
    private static void Validate(WorldConditionsState state)
    {
        if (state.Day < 0 || !double.IsFinite(state.DayFraction) || state.DayFraction < 0d || state.DayFraction >= 1d)
        {
            throw new InvalidOperationException($"{Key.Key} holds a time that is not a time of day.");
        }

        if (!Enum.IsDefined(state.Difficulty))
        {
            throw new InvalidOperationException($"{Key.Key} holds an unknown difficulty {(int)state.Difficulty}.");
        }
    }

    private static ulong Fingerprint(ulong seed, WorldConditionsState state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix(state.Day);
        hash.Mix(state.DayFraction);
        hash.Mix((long)state.Difficulty);
        return hash.Value;
    }
}
