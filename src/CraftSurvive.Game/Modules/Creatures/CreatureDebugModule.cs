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

    [DebugCommand("craft.creatures.hunter", Description = "Assisted: places a hostile animal of a kind, or a full pack of one that hunts in packs (kind east south) from the player, in no region's population.")]
    public string Hunter(string kind, double east, double south) => creatures.PlaceHunterNear(kind, east, south);

    [DebugCommand("craft.creatures.unseen", Description = "Test aid (#10878): no creature sees the player for this many seconds (0 ends it), so hunters lose the player where they stand and search there, by the player's own light, for captures.")]
    public string Unseen(double seconds) => creatures.Unseen(seconds);

    [DebugCommand("craft.creatures.weaken", Description = "Assisted: leaves every creature one health, so the next blow that lands kills it where it is, as a fall from a run needs.")]
    public string Weaken() => creatures.WeakenAll();

    [DebugCommand("craft.creatures.route", Description = "Reads the navigation route from a world column (x z) to the player as cells, for diagnosis.")]
    public string Route(double x, double z) => creatures.RouteReadout(x, z);

    [DebugCommand("craft.creatures.rouse", Description = "Test aid (#10876): every creature at rest or idle wakes to the player, so a cave's whole population hunts, for cost runs.")]
    public string Rouse() => creatures.RouseAll();

    [DebugCommand("craft.creatures.routefrom", Description = "Reads the navigation route from a world point's floor (x y z) to the player as cells: a creature in a cave (#10876).")]
    public string RouteFrom(double x, double y, double z) => creatures.RouteReadout(x, z, y);

    [DebugCommand("craft.creatures.navigation", Description = "Publishes navigation around the player now and reports the columns it re-derived and reused, and its cost.")]
    public string Navigation() => creatures.PublishNavigation();
}
