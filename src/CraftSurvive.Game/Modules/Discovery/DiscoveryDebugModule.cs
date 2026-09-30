using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>Live-debug adapters over <see cref="DiscoveryModule"/>; it owns no state of its own.</summary>
public sealed class DiscoveryDebugModule : IDebugCommandModule
{
    private readonly DiscoveryModule discovery;

    internal DiscoveryDebugModule(DiscoveryModule discovery)
    {
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    [DebugCommand("craft.discovery.readout", Description = "Reads the journal: what has been seen or reached, and whether it is stored.")]
    public string Readout() => discovery.Readout();

    [DebugCommand("craft.discovery.near", Description = "Lists the places nearest the player, with kind, distance and what is known about each.")]
    public string Near(long radius) => discovery.Near(radius);

    [DebugCommand("craft.discovery.find", Description = "Lists the nearest places of one kind, capped, so a target is never lost to a truncated response.")]
    public string Find(string kind, long radius) => discovery.Find(kind, radius);

    [DebugCommand("craft.discovery.crossings", Description = "Lists the crossings nearest the player, with span, deck height and distance.")]
    public string Crossings(long radius) => discovery.Crossings(radius);
}
