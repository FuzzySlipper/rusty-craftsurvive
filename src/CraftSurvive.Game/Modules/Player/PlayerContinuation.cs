using System.Buffers;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// What a player carries from one session of a world into the next: where their feet stand in
/// world coordinates, which way they look, their health and defeats, and what they have earned.
/// Level is not stored; it follows from experience by the progression rules.
/// </summary>
internal readonly record struct PlayerContinuation(
    double FeetX,
    double FeetY,
    double FeetZ,
    double YawRadians,
    double PitchRadians,
    int Health,
    int Defeats,
    int Experience,
    int ItemsCollected);

/// <summary>The continuation's stored form under <see cref="SaveManifest.PlayerContinuation"/>: the shared header and one record.</summary>
internal sealed class PlayerContinuationCodec(SaveIdentity identity, int maximumHealth) : IProductStateCodec<PlayerContinuation>
{
    /// <summary>Five 64-bit reals and four 32-bit counts.</summary>
    internal const int RecordBytes = (sizeof(double) * 5) + (sizeof(int) * 4);

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: 1, RecordBytes);

    private static SaveKey Key => SaveManifest.PlayerContinuation;

    public void Encode(in PlayerContinuation state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(PlayerContinuation state)
    {
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, count: 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Double(state.FeetX);
        writer.Double(state.FeetY);
        writer.Double(state.FeetZ);
        writer.Double(state.YawRadians);
        writer.Double(state.PitchRadians);
        writer.Int32(state.Health);
        writer.Int32(state.Defeats);
        writer.Int32(state.Experience);
        writer.Int32(state.ItemsCollected);
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public PlayerContinuation Decode(ReadOnlySpan<byte> payload)
    {
        if (SaveEnvelope.Open(Key, identity, Bounds, payload) != 1)
        {
            throw new InvalidOperationException($"{Key.Key} holds exactly one record.");
        }

        SaveReader reader = SaveEnvelope.Records(payload);
        PlayerContinuation state = new(
            reader.Double(), reader.Double(), reader.Double(), reader.Double(), reader.Double(),
            reader.Int32(), reader.Int32(), reader.Int32(), reader.Int32());
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, state));
        Validate(state);
        return state;
    }

    /// <summary>Refuses a continuation no session could have produced.</summary>
    private void Validate(PlayerContinuation state)
    {
        foreach (double value in (ReadOnlySpan<double>)[state.FeetX, state.FeetY, state.FeetZ, state.YawRadians, state.PitchRadians])
        {
            if (!double.IsFinite(value))
            {
                throw new InvalidOperationException($"{Key.Key} holds a value that is not a number.");
            }
        }

        if (Math.Abs(state.FeetX) > TerrainConstants.MaximumCoordinateMagnitude
            || Math.Abs(state.FeetY) > TerrainConstants.MaximumCoordinateMagnitude
            || Math.Abs(state.FeetZ) > TerrainConstants.MaximumCoordinateMagnitude)
        {
            throw new InvalidOperationException($"{Key.Key} stands outside any world.");
        }

        if (state.Health < 0 || state.Health > maximumHealth || state.Defeats < 0
            || state.Experience < 0 || state.ItemsCollected < 0)
        {
            throw new InvalidOperationException($"{Key.Key} holds vitals or progress out of range.");
        }
    }

    private static ulong Fingerprint(ulong seed, PlayerContinuation state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix(state.FeetX);
        hash.Mix(state.FeetY);
        hash.Mix(state.FeetZ);
        hash.Mix(state.YawRadians);
        hash.Mix(state.PitchRadians);
        hash.Mix(state.Health);
        hash.Mix(state.Defeats);
        hash.Mix(state.Experience);
        hash.Mix(state.ItemsCollected);
        return hash.Value;
    }
}
