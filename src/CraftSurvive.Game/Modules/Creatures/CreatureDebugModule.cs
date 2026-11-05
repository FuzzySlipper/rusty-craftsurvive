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

    [DebugCommand("craft.creatures.restart", Description = "Clears the creatures; hostile animals return with their homes, where they stand.")]
    public string Restart()
    {
        creatures.Restart();
        return creatures.Readout();
    }

    [DebugCommand("craft.creatures.routing", Description = "Reads what creature routing has cost per update and in navigation queries; 'reset' starts a new measurement.")]
    public string Routing(string mode) => creatures.RoutingReadout(mode == "reset");

    [DebugCommand("craft.creatures.ambush", Description = "Assisted: hostile creatures close in around the player, as a travel encounter's ambush does (once the ground is streamed).")]
    public string Ambush(long count)
    {
        creatures.Ambush((int)Math.Clamp(count, 1, 24));
        return creatures.Readout();
    }

    [DebugCommand("craft.creatures.hunter", Description = "Assisted: places a hostile animal of a kind (kind east south) from the player, in no region's population.")]
    public string Hunter(string kind, double east, double south) => creatures.PlaceHunterNear(kind, east, south);

    [DebugCommand("craft.creatures.route", Description = "Reads the navigation route from a world column (x z) to the player as cells, for diagnosis.")]
    public string Route(double x, double z) => creatures.RouteReadout(x, z);

    [DebugCommand("craft.creatures.navigation", Description = "Publishes navigation around the player now and reports the columns it re-derived and reused, and its cost.")]
    public string Navigation() => creatures.PublishNavigation();
}
