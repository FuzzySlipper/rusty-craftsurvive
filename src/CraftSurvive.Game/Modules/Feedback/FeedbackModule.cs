using System.Numerics;
using CraftSurvive.Game.Modules.Audio;
using CraftSurvive.Game.Modules.World;

namespace CraftSurvive.Game.Modules.Feedback;

/// <summary>
/// Presents what happened this update: it takes the cues gameplay raised and has each heard (the
/// <see cref="SoundPlayer"/>) and seen (the <see cref="BurstEmitter"/>), then fades the ambience to
/// where the player is. It runs after the gameplay that raises cues, and holds no game state.
/// </summary>
internal sealed class FeedbackModule : IProductModule
{
    /// <summary>How far in front of the player a cue asked for by name is shown.</summary>
    private const float AskedCueMetres = 2.5f;

    private readonly Cues cues;
    private readonly SoundPlayer sounds;
    private readonly BurstEmitter bursts;
    private readonly Func<Surroundings> surroundings;
    private readonly Func<Vector3> ahead;

    /// <param name="ahead">A point some metres in front of the player's eyes, in the local frame.</param>
    internal FeedbackModule(Cues cues, SoundPlayer sounds, BurstEmitter bursts, Func<Surroundings> surroundings, Func<float, Vector3> ahead)
    {
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        this.sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        this.bursts = bursts ?? throw new ArgumentNullException(nameof(bursts));
        this.surroundings = surroundings ?? throw new ArgumentNullException(nameof(surroundings));
        ArgumentNullException.ThrowIfNull(ahead);
        this.ahead = () => ahead(AskedCueMetres);
    }

    public void Start()
    {
        sounds.Start();
        bursts.Start();
    }

    public void Update(ProductStep step)
    {
        foreach (RaisedCue raised in cues.Take())
        {
            Present(raised);
        }

        sounds.Fade(surroundings(), step.ElapsedSeconds);
    }

    /// <summary>A fresh play session drops anything raised before it; the beds keep looping and fade to the new place.</summary>
    public void Restart() => cues.Take();

    public void Dispose() => sounds.Dispose();

    /// <summary>Presents one cue now, by name, in front of the player: for a live check of each sound and burst.</summary>
    internal string PresentNow(string name)
    {
        if (!Enum.TryParse(name, ignoreCase: true, out Cue cue) || !Enum.IsDefined(cue) || int.TryParse(name, out _))
        {
            return $"refused: \"{name}\" is not a cue ({string.Join(", ", Enum.GetNames<Cue>()).ToLowerInvariant()})";
        }

        Present(new RaisedCue(cue, ahead()));
        return Readout();
    }

    internal string Readout() => $"{sounds.Readout()} dropped={cues.Dropped} {bursts.Readout()}";

    private void Present(RaisedCue raised)
    {
        sounds.Play(raised);
        bursts.Show(raised);
    }
}
