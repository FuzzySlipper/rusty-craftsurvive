using System.Numerics;

namespace CraftSurvive.Game.Modules.Feedback;

/// <summary>Something that happened in play that the player should hear or see.</summary>
internal enum Cue
{
    Footstep,
    Jump,
    Land,
    HardLanding,
    Splash,
    Grip,
    Hurt,
    Swing,
    Strike,
    Defeat,
    Blast,
    Place,
    Craft,
    Pickup,
    Discovery,
    Refused,
    Portal,
}

/// <summary>
/// One cue raised in an update: where it happened, in the session's local frame, or nowhere in
/// particular; and, when the same event should always look the same, its identity (zero when it
/// has none).
/// </summary>
internal readonly record struct RaisedCue(Cue Cue, Vector3? At, ulong Identity = 0UL);

/// <summary>
/// The cues raised this update, for the feedback module to present. Gameplay owners raise a cue
/// where they decide something happened; only the feedback module reads and clears them, and it
/// alone turns them into sound and particles.
/// </summary>
internal sealed class Cues
{
    /// <summary>The most cues kept for one update; a burst beyond it is dropped rather than queued up.</summary>
    internal const int Capacity = 32;

    private readonly List<RaisedCue> raised = [];

    internal long Dropped { get; private set; }

    internal void Raise(Cue cue) => Add(new RaisedCue(cue, null));

    internal void RaiseAt(Cue cue, Vector3 at, ulong identity = 0UL) => Add(new RaisedCue(cue, at, identity));

    /// <summary>Hands over this update's cues and starts the next update empty.</summary>
    internal RaisedCue[] Take()
    {
        RaisedCue[] taken = [.. raised];
        raised.Clear();
        return taken;
    }

    private void Add(RaisedCue cue)
    {
        if (raised.Count >= Capacity)
        {
            Dropped++;
            return;
        }

        raised.Add(cue);
    }
}
