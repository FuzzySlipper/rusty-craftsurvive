using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.World;

/// <summary>The pause menu's assisted command (#9916).</summary>
public sealed class MenuDebugModule : IDebugCommandModule
{
    private readonly Func<string, string> pause;

    internal MenuDebugModule(Func<string, string> pause) => this.pause = pause;

    [DebugCommand("craft.menu.pause", Description = "Assisted (#9916): holds the game under the pause menu (on), lets it run again (off) or reads it (read), as Esc and Continue do.")]
    public string Pause(string state) => pause(state);
}
