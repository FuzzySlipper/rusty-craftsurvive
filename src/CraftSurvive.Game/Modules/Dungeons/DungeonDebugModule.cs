using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>Live-debug adapter over dungeons: read the state, enter at an entrance, leave by the way out.</summary>
public sealed class DungeonDebugModule : IDebugCommandModule
{
    private readonly Func<DungeonModule> dungeonsSource;
    private DungeonModule dungeons => dungeonsSource();

    internal DungeonDebugModule(Func<DungeonModule> dungeons) =>
        this.dungeonsSource = dungeons ?? throw new ArgumentNullException(nameof(dungeons));

    [DebugCommand("craft.dungeon.readout", Description = "Reads where the player stands with dungeons: state, loading progress and cost, the nearby entrance.")]
    public string Readout() => dungeons.Readout();

    [DebugCommand("craft.dungeon.enter", Description = "Enters the dungeon at the entrance the player stands at, as the UI's Enter does.")]
    public string Enter() => dungeons.Enter();

    [DebugCommand("craft.dungeon.approach", Description = "Chooses how the next dungeon entered is generated: a (carve and stamp), b (modules), c (sculpted cave), v (the chasm sketch) or s (the shaft sketch).")]
    public string Approach(string name) => dungeons.Choose(name);

    [DebugCommand("craft.dungeon.seed", Description = "Makes every dungeon entered use a chosen seed (as the dungeon bank numbers them), or \"entrance\" to go back to each entrance's own.")]
    public string Seed(string seed) => dungeons.ChooseSeed(seed);

    [DebugCommand("craft.dungeon.surface", Description = "Chooses how the next dungeon's voxels are surfaced: cubes, dc (dual-contoured worn rock, building on the grid), faceted (flat-faceted rock), ruined (faceted rock, weathered building) or mc (marched rock, cube building).")]
    public string Surface(string name) => dungeons.ChooseSurface(name);

    [DebugCommand("craft.dungeon.blast", Description = "Carves a sphere of the given radius (metres) out of the loaded dungeon where the player aims, and reports the rebuild cost.")]
    public string Blast(float radius) => dungeons.Blast(radius);

    [DebugCommand("craft.dungeon.visit", Description = "Moves the player to a place in the dungeon: arrival, breach, loot, floor0..floorN, or a layout cell x,y,z.")]
    public string Visit(string place) => dungeons.Visit(place);

    [DebugCommand("craft.dungeon.validate", Description = "Publishes the Engine's navigation over the loaded dungeon and routes arrival to breach and loot, and back.")]
    public string Validate() => dungeons.Validate();

    [DebugCommand("craft.dungeon.leave", Description = "Leaves the dungeon from its way out, as the UI's Leave does.")]
    public string Leave() => dungeons.Leave();
}
