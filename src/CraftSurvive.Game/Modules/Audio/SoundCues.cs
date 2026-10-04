using System.Numerics;

namespace CraftSurvive.Game.Modules.Audio;

/// <summary>Something that happened in play that has a sound.</summary>
internal enum SoundCue
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

/// <summary>One cue raised in an update: heard where it happened, or everywhere when it has no place.</summary>
internal readonly record struct RaisedCue(SoundCue Cue, Vector3? At);

/// <summary>
/// The cues raised this update, for the sound module to play. Gameplay owners raise a cue where
/// they decide something happened; only the sound module reads and clears them, and it alone
/// talks to the Engine's audio. A cue's position is in the session's local frame, as the listener's is.
/// </summary>
internal sealed class SoundCues
{
    /// <summary>The most cues kept for one update; a burst beyond it is dropped rather than queued up.</summary>
    internal const int Capacity = 32;

    private readonly List<RaisedCue> raised = [];

    internal long Dropped { get; private set; }

    internal void Raise(SoundCue cue) => Add(new RaisedCue(cue, null));

    internal void RaiseAt(SoundCue cue, Vector3 at) => Add(new RaisedCue(cue, at));

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
