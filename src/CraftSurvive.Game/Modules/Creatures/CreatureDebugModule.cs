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

    [DebugCommand("craft.creature.spawn", Description = "Test aid (#10926): spawns any hunter or herd kind in no population - kind, count, where (here, ahead:<metres>, at:<x>,<y>,<z> on the floor under that point in caves too, home:<cave home key>) and how it starts (default, rest, idle, hunt; flee for a herd: it runs when pressed); returns the new ids.")]
    public string Spawn(string kind, int count, string where, string start) => creatures.Spawn(kind, count, where, start);

    [DebugCommand("craft.creature.select", Description = "Test aid (#10926): selects one creature to inspect, watch and override - an id, nearest, nearest:<kind> or looked-at (nearest the view's line); says when it is gone.")]
    public string Select(string which) => creatures.Select(which);

    [DebugCommand("craft.creature.inspect", Description = "Test aid (#10926): the selected creature whole - kind, home, pack place; state and timers; perception (sees, alerted, sight, wake and reach); route (outcome, waypoint, path, stuck, back-off, why); body (feet, lift, speed, yaw); animation (the clip it wants and why, what its mesh plays); any override.")]
    public string Inspect() => creatures.Inspect();

    [DebugCommand("craft.creature.watch", Description = "Test aid (#10926): watches the selected creature for this many seconds (0 stops), logging each change of state, clip, route, sight, alarm, health or wakefulness with the update step and the facts then; read gives the log.")]
    public string Watch(string seconds) => creatures.Watch(seconds);

    [DebugCommand("craft.creature.force", Description = "Test aid (#10926): holds the selected creature - state <rest|idle|alert|pursue|search|attack>, clip <role>, goto <x>,<z> (it searches its way there), freeze (no rules, no move, clip held), health <n> - or release; give a value of - where none is needed.")]
    public string Force(string what, string value) => creatures.Force(what, value);

    [DebugCommand("craft.creature.labels", Description = "Test aid (#10926): labels over creatures on the Engine's inspection overlay (id, kind, state, clip, route, override) - off, selected or all; the Engine draws them while engine.inspect.overlay show is on.")]
    public string Labels(string mode) => creatures.Labels(mode);

    [DebugCommand("craft.creatures.why", Description = "Diagnosis (#10924): why creatures cannot route, on the navigation published around the player - all (every awake creature whose last route failed), an id, or homes (each held cave home's floor, flagged UNROUTABLE when no route could start there): outside the published box, or how the column reads (support, capsule overlap and with what, too steep, nothing) and the nearest support on its level.")]
    public string Why(string which) => creatures.WhyReadout(which);

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
