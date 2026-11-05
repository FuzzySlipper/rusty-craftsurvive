using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Birds;

/// <summary>Live-debug adapters over the birds (#10630); it owns no state of its own.</summary>
public sealed class BirdDebugModule : IDebugCommandModule
{
    private readonly Func<BirdModule> birdSource;
    private BirdModule birds => birdSource();

    internal BirdDebugModule(Func<BirdModule> birds) => birdSource = birds ?? throw new ArgumentNullException(nameof(birds));

    [DebugCommand("craft.birds.readout", Description = "Birds (#10630): the flights in the air, and what updates have cost since the last readout.")]
    public string Readout() => birds.Readout();

    [DebugCommand("craft.birds.flight", Description = "Test aid (#10630): sends a flight of a kind from the edge of reach, whatever the ground and hour.")]
    public string Flight(string kind) => birds.SendNow(kind, overhead: false);

    [DebugCommand("craft.birds.overhead", Description = "Test aid (#10630): sends a flight of a kind and starts it at its nearest to the player, to look at.")]
    public string Overhead(string kind) => birds.SendNow(kind, overhead: true);

    [DebugCommand("craft.birds.clear", Description = "Test aid (#10630): every flight goes now.")]
    public string Clear() => birds.ClearNow();
}
