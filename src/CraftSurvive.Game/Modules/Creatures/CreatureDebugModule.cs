using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Live-debug adapters over the creature module; it owns no state of its own.</summary>
public sealed class CreatureDebugModule : IDebugCommandModule
{
    private readonly CreatureModule creatures;

    internal CreatureDebugModule(CreatureModule creatures)
    {
        this.creatures = creatures ?? throw new ArgumentNullException(nameof(creatures));
    }

    [DebugCommand("craft.creatures.readout", Description = "Reads every creature, the player's vitals and progress, and the last creature event.")]
    public string Readout() => creatures.Readout();

    [DebugCommand("craft.creatures.restart", Description = "Clears the creatures and places a fresh starting set around the player where they stand.")]
    public string Restart()
    {
        creatures.Restart();
        return creatures.Readout();
    }

    [DebugCommand("craft.creatures.route", Description = "Publishes navigation around the player and evaluates one step from the first creature toward them.")]
    public string Route() => creatures.ProbeNavigation();
}
