using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Survival;

/// <summary>
/// The survival tracks' stored form under <see cref="SaveManifest.PlayerSurvival"/>: how fed the
/// player is and how much air they hold. Progress toward the next point regained or lost is a
/// within-session fraction and is not stored.
/// </summary>
internal sealed class SurvivalCodec(SaveIdentity identity) : IProductStateCodec<SurvivalState>
{
    /// <summary>Two 64-bit reals.</summary>
    internal const int RecordBytes = sizeof(double) * 2;

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
        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state.Satiety, state.Breath));
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
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, satiety, breath));
        SurvivalState state = SurvivalState.Fresh with { Satiety = satiety, Breath = breath };
        Validate(state);
        return state;
    }

    private static void Validate(SurvivalState state)
    {
        if (!double.IsFinite(state.Satiety) || state.Satiety < 0d || state.Satiety > SurvivalRules.MaximumSatiety
            || !double.IsFinite(state.Breath) || state.Breath < 0d || state.Breath > SurvivalRules.MaximumBreathSeconds)
        {
            throw new InvalidOperationException($"{Key.Key} holds hunger or breath out of range.");
        }
    }

    private static ulong Fingerprint(ulong seed, double satiety, double breath)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        hash.Mix(satiety);
        hash.Mix(breath);
        return hash.Value;
    }
}
