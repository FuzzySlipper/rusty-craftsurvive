using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Live-debug adapters over the creature module; it owns no state of its own.</summary>
public sealed class CreatureDebugModule : IDebugCommandModule
{
    private readonly Func<CreatureModule> creaturesSource;
    private CreatureModule creatures => creaturesSource();

    internal CreatureDebugModule(Func<CreatureModule> creatures)
    {
        this.creaturesSource = creatures ?? throw new ArgumentNullException(nameof(creatures));
    }

    [DebugCommand("craft.creatures.readout", Description = "Reads every creature, the player's vitals and progress, and the last creature event.")]
    public string Readout() => creatures.Readout();

    [DebugCommand("craft.creatures.restart", Description = "Clears the creatures and places a fresh starting set around the player where they stand.")]
    public string Restart()
    {
        creatures.Restart();
        return creatures.Readout();
    }

    [DebugCommand("craft.creatures.navigation", Description = "Publishes navigation around the player now and reports the columns it re-derived and reused, and its cost.")]
    public string Navigation() => creatures.PublishNavigation();
}
