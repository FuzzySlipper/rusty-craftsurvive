using System.Buffers;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// The population ledger's stored form under <see cref="SaveManifest.WildlifeLedger"/> (#10567): the
/// shared header, then one record per herd short of animals, in order of its id. Everything else
/// about a herd is seeded again from the world, so only what the player changed is kept.
/// </summary>
internal sealed class WildlifeLedgerCodec(SaveIdentity identity) : IProductStateCodec<IReadOnlyList<HerdLoss>>
{
    /// <summary>A 64-bit id, a 32-bit count and a 64-bit real.</summary>
    internal const int RecordBytes = sizeof(ulong) + sizeof(int) + sizeof(double);

    internal static SaveBounds Bounds { get; } = new(MaximumRecords: HerdLoss.MostHerds, RecordBytes);

    private static SaveKey Key => SaveManifest.WildlifeLedger;

    public void Encode(in IReadOnlyList<HerdLoss> state, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(state));
    }

    internal byte[] Encode(IReadOnlyList<HerdLoss> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        byte[] bytes = SaveEnvelope.Allocate(Key, identity, Bounds, state.Count);
        SaveWriter writer = SaveEnvelope.Records(bytes);
        foreach (HerdLoss loss in state)
        {
            writer.Int64(unchecked((long)loss.Herd)); writer.Int32(loss.Lost); writer.Double(loss.RecoveringSince);
        }

        SaveEnvelope.Seal(bytes, Fingerprint(identity.Seed, state));
        return bytes;
    }

    public IReadOnlyList<HerdLoss> Decode(ReadOnlySpan<byte> payload)
    {
        int count = SaveEnvelope.Open(Key, identity, Bounds, payload);
        SaveReader reader = SaveEnvelope.Records(payload);
        List<HerdLoss> losses = new(count);
        for (int index = 0; index < count; index++) losses.Add(new HerdLoss(unchecked((ulong)reader.Int64()), reader.Int32(), reader.Double()));
        SaveEnvelope.Verify(Key, payload, Fingerprint(identity.Seed, losses));
        Validate(losses);
        return losses;
    }

    /// <summary>Refuses a ledger no session could have produced: a herd twice or out of order, nothing lost, a day off the clock.</summary>
    private static void Validate(IReadOnlyList<HerdLoss> state)
    {
        for (int index = 0; index < state.Count; index++)
        {
            if (state[index].Lost <= 0) throw new InvalidOperationException($"{Key.Key} holds a herd that lost nothing.");
            if (!double.IsFinite(state[index].RecoveringSince) || state[index].RecoveringSince < 0)
                throw new InvalidOperationException($"{Key.Key} holds a day off the clock.");
            if (index > 0 && state[index - 1].Herd >= state[index].Herd)
                throw new InvalidOperationException($"{Key.Key} holds its herds out of order.");
        }
    }

    private static ulong Fingerprint(ulong seed, IReadOnlyList<HerdLoss> state)
    {
        SaveFingerprint hash = SaveFingerprint.Start(seed);
        foreach (HerdLoss loss in state)
        {
            hash.Mix(loss.Herd);
            hash.Mix((long)loss.Lost);
            hash.Mix(loss.RecoveringSince);
        }

        return hash.Value;
    }
}
