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

    [DebugCommand("craft.creature.spawn", Description = "Test aid (#10926): spawns any hunter or herd kind in no population - kind, count, where (here, ahead:<metres>, at:<x>,<y>,<z> on the floor under that point in caves too, home:<cave home key>) and how it starts (a hunter: default, rest, idle, hunt; a herd: default, rest, idle, flee - running now); herds stand on the floor near the point's height, cave floors too; returns the new ids (herd:<id> for a herd animal).")]
    public string Spawn(string kind, int count, string where, string start) => creatures.Spawn(kind, count, where, start);

    [DebugCommand("craft.creature.select", Description = "Test aid (#10926): selects one creature, hunter or herd animal, to inspect, watch and override - a hunter's id, herd:<id>, nearest, nearest:<kind> or looked-at (nearest the view's line); says when it is gone.")]
    public string Select(string which) => creatures.Select(which);

    [DebugCommand("craft.creature.inspect", Description = "Test aid (#10926): the selected creature whole - kind, home, pack place; state and timers; perception (sees, alerted, sight, wake and reach); route (outcome, waypoint, path, stuck, back-off, why); body (feet, lift, speed, yaw); animation (the clip it wants and why, what its mesh plays); any override.")]
    public string Inspect() => creatures.Inspect();

    [DebugCommand("craft.creature.watch", Description = "Test aid (#10926): watches the selected creature for this many seconds (0 stops), logging each change of state, clip, route, sight, alarm, health or wakefulness with the update step and the facts then; read gives the log.")]
    public string Watch(string seconds) => creatures.Watch(seconds);

    [DebugCommand("craft.creature.force", Description = "Test aid (#10926): holds the selected creature - state (a hunter: rest|idle|alert|pursue|search|attack; a herd animal: rest|idle|look|warn|flee), clip <role>, goto <x>,<z> (a hunter searches its way there, a herd animal walks), freeze (no rules, no move, its playback paused and pose held), health <n> - or release; give a value of - where none is needed.")]
    public string Force(string what, string value) => creatures.Force(what, value);

    [DebugCommand("craft.creature.look", Description = "Test aid (#10956): the look creatures that wear shells wear from now on (bare, dome, split, full, or any for each its own); bodies already awake keep theirs.")]
    public string Look(string look) => creatures.Look(look);

    [DebugCommand("craft.creature.fade", Description = "Test aid (#10984): how much of every body faded into the mist shows, a share of it from the head back (0 to 1), sweeping there at its kind's pace; auto gives each its own (its rest, or whole while it attacks).")]
    public string Fade(string share) => creatures.Fade(share);

    [DebugCommand("craft.creature.labels", Description = "Test aid (#10926): labels over creatures on the Engine's inspection overlay (id, kind, state, clip, route, override) - off, selected or all; the Engine draws them while engine.inspect.overlay show is on.")]
    public string Labels(string mode) => creatures.Labels(mode);

    [DebugCommand("craft.creatures.why", Description = "Diagnosis (#10924): why creatures cannot route, on the navigation published around the player - all (every awake creature whose last route failed), an id, or homes (each held cave home's floor, flagged UNROUTABLE when no route could start there, unread when the published box does not reach it): outside the published box, or how the column reads (support, capsule overlap and with what, too steep, nothing) and the nearest support on its level.")]
    public string Why(string which) => creatures.WhyReadout(which);

    [DebugCommand("craft.creatures.unseen", Description = "Test aid (#10878): no creature sees the player for this many seconds (0 ends it), so hunters lose the player where they stand and search there, by the player's own light, for captures.")]
    public string Unseen(double seconds) => creatures.Unseen(seconds);

    [DebugCommand("craft.creatures.tenders", Description = "Reads the ruin tenders' state (#10961): menders' seals (where, how grown, how old, blows taken), brood bearers' broods and split pods, tenders' errands, and the doorways in the player's space.")]
    public string Tenders() => creatures.TendersReadout();

    [DebugCommand("craft.creatures.fixtures", Description = "Reads the ruin fixtures' state (#10963): each one's phase, how far the player is, its health, blows, spent vessels and lost lobes, the shut bowers, and the player's hold, numbing and stamina.")]
    public string Fixtures() => creatures.FixturesReadout();

    [DebugCommand("craft.creatures.drinks", Description = "Reads the implement-carriers' light drinking (#10966): the lantern's phase, share and who drinks it, the drinks whole, failed and cut, and each carrier's raise, distance and core glow.")]
    public string Drinks() => creatures.DrinksReadout();

    [DebugCommand("craft.creatures.marks", Description = "Reads the channel sifters' spore mark (#10971): the player's mark and its time left, the clouds hanging, each sifter's puff and rest, and the hunters drawn to the marked player.")]
    public string Marks() => creatures.MarksReadout();

    [DebugCommand("craft.creatures.mark", Description = "Assisted: marks the player as a channel sifter's cloud would (#10971), for the seconds given, drawing ruin hunters from the metres given.")]
    public string Mark(double seconds, double metres) => creatures.MarkPlayer(seconds, metres);

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
