using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.World;

internal enum SaveRestoreOutcome
{
    /// <summary>Nothing was stored: a new world.</summary>
    Absent,

    /// <summary>The stored form decoded for this world and is the state now.</summary>
    Restored,

    /// <summary>The stored form did not decode for this world; it is set aside and the world starts fresh.</summary>
    Discarded,
}

/// <summary>What restoring one key decided, and why.</summary>
internal readonly record struct SaveRestoreDecision<TState>(SaveRestoreOutcome Outcome, TState? State, string Reason)
{
    /// <summary>Whether the stored bytes should be kept as the key's backup: only a discarded, non-empty save.</summary>
    internal bool KeepsBackup { get; init; }

    internal string Describe() => Outcome switch
    {
        SaveRestoreOutcome.Absent => "absent",
        SaveRestoreOutcome.Restored => "restored",
        _ => $"discarded: {Reason}",
    };
}

/// <summary>
/// The restore rule every saved key follows, as a pure function of what the store returned, so it
/// can be checked without the Engine: absent stays absent, bytes that decode are restored, and
/// bytes that do not are discarded with the codec's reason.
/// </summary>
internal static class SaveRestore
{
    internal static SaveRestoreDecision<TState> Decide<TState>(
        bool present, ReadOnlySpan<byte> bytes, IProductStateCodec<TState> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (!present)
        {
            return new SaveRestoreDecision<TState>(SaveRestoreOutcome.Absent, default, "nothing stored");
        }

        try
        {
            return new SaveRestoreDecision<TState>(SaveRestoreOutcome.Restored, codec.Decode(bytes), "decoded");
        }
        catch (Exception refusal) when (refusal is InvalidOperationException or ArgumentException or OverflowException)
        {
            return new SaveRestoreDecision<TState>(SaveRestoreOutcome.Discarded, default, refusal.Message)
            {
                KeepsBackup = bytes.Length > 0,
            };
        }
    }
}
