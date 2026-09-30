using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// The owner of the player's continuation save. The player controller asks it once at start for
/// what the last session left, and hands it the current continuation every update; it writes one
/// every few seconds, and always when the session ends.
/// </summary>
internal sealed class PlayerContinuationStore
{
    /// <summary>Five seconds at 60 Hz: a crash loses at most a few steps of walking.</summary>
    internal const long SaveIntervalSteps = 300;

    private readonly ProductSaveSlot<PlayerContinuation> slot;
    private long lastSaveStep = long.MinValue;

    internal PlayerContinuationStore(IEngineContext engine, ProductStore store, SaveIdentity identity, int maximumHealth)
    {
        slot = new ProductSaveSlot<PlayerContinuation>(
            engine, store, SaveManifest.PlayerContinuation, new PlayerContinuationCodec(identity, maximumHealth));
    }

    /// <summary>What the player controller did with the restored continuation.</summary>
    internal string Applied { get; set; } = "none";

    /// <summary>The continuation the last session saved for this world, or null.</summary>
    internal PlayerContinuation? Restore() =>
        slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: PlayerContinuation saved } ? saved : null;

    /// <summary>Writes the continuation when the interval has passed since the last write.</summary>
    internal void SaveIfDue(long step, PlayerContinuation continuation)
    {
        if (lastSaveStep == long.MinValue)
        {
            lastSaveStep = step;
        }
        else if (step - lastSaveStep >= SaveIntervalSteps)
        {
            Save(continuation, step);
        }
    }

    internal void Save(PlayerContinuation continuation, long step)
    {
        slot.Save(continuation);
        lastSaveStep = step;
    }

    internal string Readout() =>
        $"continuation restore={slot.RestoreOutcome} applied={Applied} saves={slot.Saves} failure={slot.LastFailure ?? "none"}";
}
