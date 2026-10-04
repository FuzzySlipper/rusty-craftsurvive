using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Audio;

/// <summary>Live-debug adapter over the game's sound: read what has played, or play one cue now.</summary>
public sealed class SoundDebugModule : IDebugCommandModule
{
    private readonly SoundModule sounds;

    internal SoundDebugModule(SoundModule sounds) =>
        this.sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));

    [DebugCommand("craft.audio.readout", Description = "Reads the admitted clips, the ambience levels, what each cue has played and the device's completions.")]
    public string Readout() => sounds.Readout();

    [DebugCommand("craft.audio.cue", Description = "Plays one cue now by name (footstep, jump, blast, discovery, ...), to hear it live.")]
    public string Cue(string name) => sounds.PlayNow(name);
}
