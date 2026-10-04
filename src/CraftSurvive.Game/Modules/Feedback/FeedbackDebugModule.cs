using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Feedback;

/// <summary>Live-debug adapter over the game's feedback: read what has been heard and seen, or present one cue now.</summary>
public sealed class FeedbackDebugModule : IDebugCommandModule
{
    private readonly FeedbackModule feedback;

    internal FeedbackDebugModule(FeedbackModule feedback) =>
        this.feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));

    [DebugCommand("craft.feedback.readout", Description = "Reads the admitted clips, the ambience levels, what each cue has played, the device's completions and the bursts shown.")]
    public string Readout() => feedback.Readout();

    [DebugCommand("craft.feedback.cue", Description = "Presents one cue now by name (land, splash, blast, strike, ...): its sound, and its burst in front of the player.")]
    public string Cue(string name) => feedback.PresentNow(name);
}
