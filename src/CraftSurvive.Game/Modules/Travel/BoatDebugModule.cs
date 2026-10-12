using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>Live checks of the expedition's boat (#9879): build one, read it, and paddle it for a capture.</summary>
public sealed class BoatDebugModule : IDebugCommandModule
{
    private readonly Func<string> readout;
    private readonly Func<string, string> build;
    private readonly Func<double, double, double, string> paddle;
    private readonly Func<string> board;
    private readonly Func<string, long, string> travel;

    internal BoatDebugModule(Func<string> readout, Func<string, string> build, Func<double, double, double, string> paddle, Func<string> board,
        Func<string, long, string> travel)
    {
        this.travel = travel ?? throw new ArgumentNullException(nameof(travel));
        this.readout = readout ?? throw new ArgumentNullException(nameof(readout));
        this.build = build ?? throw new ArgumentNullException(nameof(build));
        this.paddle = paddle ?? throw new ArgumentNullException(nameof(paddle));
        this.board = board ?? throw new ArgumentNullException(nameof(board));
    }

    [DebugCommand("craft.boat.readout", Description = "Reads the expedition's boat: its kind, where it lies, its heading and velocity, whether it is moored, the player seated or aboard, and the water under it.")]
    public string Readout() => readout();

    [DebugCommand("craft.boat.build", Description = "Assisted: builds a raft or a canoe at the river beside the player, as the map's action does (the hours pass).")]
    public string Build(string kind) => build(kind);

    [DebugCommand("craft.boat.paddle", Description = "Assisted, captures only: holds a paddle for so many seconds while seated (forward -1 to 1 along the bow, turn -1 to 1), as the movement keys would.")]
    public string Paddle(double forward, double turn, double seconds) => paddle(forward, turn, seconds);

    [DebugCommand("craft.travel.action", Description = "Assisted: the journey's buttons with the map open - go, pause, halt, camp or answer:<choice> for a waiting event - and the travel speed (1, 2 or 4; 0 keeps it), as the map's controls do.")]
    public string Travel(string action, long speed) => travel(action, speed);

    [DebugCommand("craft.boat.board", Description = "Assisted: the board/stand action beside the boat, as the climb key does.")]
    public string Board() => board();
}
