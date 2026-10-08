using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Survival;

/// <summary>
/// The survival tracks' stored form under <see cref="SaveManifest.PlayerSurvival"/>: how fed the
/// player is, how much air they hold, and how wet and chilled they are (#9741). Progress toward the
/// next point regained or lost is a within-session fraction and is not stored.
/// </summary>
internal sealed class SurvivalCodec(SaveIdentity identity) : IProductStateCodec<SurvivalState>
{
    /// <summary>Four 64-bit reals.</summary>
    internal const int RecordBytes = sizeof(double) * 4;

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: 1, RecordBytes);

    private static SaveKey Key => SaveManifest.PlayerSurvival;

    public void Encode(in SurvivalState state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(SurvivalState state)
    {
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, count: 1);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        writer.Double(state.Satiety);
        writer.Double(state.Breath);
        writer.Double(state.Wetness);
        writer.Double(state.Chill);
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state.Satiety, state.Breath, state.Wetness, state.Chill));
        return bytes;
    }

    public SurvivalState Decode(ReadOnlySpan<byte> payload)
    {
        if (SaveEnvelope.Open(Key, identity, Bounds, payload) != 1)
        {
            throw new InvalidOperationException($"{Key.Key} holds exactly one record.");
        }

        SaveReader reader = SaveEnvelope.Records(payload);
        double satiety = reader.Double();
        double breath = reader.Double();
        double wetness = reader.Double();
        double chill = reader.Double();
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, satiety, breath, wetness, chill));
        SurvivalState state = SurvivalState.Fresh with { Satiety = satiety, Breath = breath, Wetness = wetness, Chill = chill };
        Validate(state);
        return state;
    }

    private static void Validate(SurvivalState state)
    {
        if (!double.IsFinite(state.Satiety) || state.Satiety < 0d || state.Satiety > SurvivalRules.MaximumSatiety
            || !double.IsFinite(state.Breath) || state.Breath < 0d || state.Breath > SurvivalRules.MaximumBreathSeconds
            || !double.IsFinite(state.Wetness) || state.Wetness is < 0d or > 1d
            || !double.IsFinite(state.Chill) || state.Chill is < 0d or > 1d)
        {
            throw new InvalidOperationException($"{Key.Key} holds hunger, breath, wetness or chill out of range.");
        }
    }

    private static ulong Fingerprint(ulong seed, double satiety, double breath, double wetness, double chill)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix(satiety);
        hash.Mix(breath);
        hash.Mix(wetness);
        hash.Mix(chill);
        return hash.Value;
    }
}
