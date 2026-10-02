using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>Live-debug adapter over dungeons: read the state, enter at an entrance, leave by the way out.</summary>
public sealed class DungeonDebugModule : IDebugCommandModule
{
    private readonly DungeonModule dungeons;

    internal DungeonDebugModule(DungeonModule dungeons) =>
        this.dungeons = dungeons ?? throw new ArgumentNullException(nameof(dungeons));

    [DebugCommand("craft.dungeon.readout", Description = "Reads where the player stands with dungeons: state, loading progress and cost, the nearby entrance.")]
    public string Readout() => dungeons.Readout();

    [DebugCommand("craft.dungeon.enter", Description = "Enters the dungeon at the entrance the player stands at, as the UI's Enter does.")]
    public string Enter() => dungeons.Enter();

    [DebugCommand("craft.dungeon.approach", Description = "Chooses how the next dungeon entered is generated: a (carve and stamp), b (modules) or c (sculpted cave).")]
    public string Approach(string name) => dungeons.Choose(name);

    [DebugCommand("craft.dungeon.surface", Description = "Chooses how the next dungeon's voxels are surfaced: cubes, dc or mc (flat colours), dc-textured or mc-textured, dc-mixed (rock dual contoured, built blocks cubes) or dc-blocky (all dual contoured, built blocks crisp).")]
    public string Surface(string name) => dungeons.ChooseSurface(name);

    [DebugCommand("craft.dungeon.trust", Description = "Trial: on enters dungeons even when their route check fails; off restores the check.")]
    public string Trust(string on)
    {
        dungeons.AcceptUnwalkable = on == "on";
        return $"accept unwalkable dungeons: {dungeons.AcceptUnwalkable}";
    }

    [DebugCommand("craft.dungeon.blast", Description = "Blasts a sphere (radius in metres, default 2.5) out of the dungeon where the view meets it.")]
    public string Blast(string radius) => dungeons.Blast(DungeonModule.BlastRadius(radius), false);

    [DebugCommand("craft.dungeon.fill", Description = "Fills a sphere (radius in metres, default 2.5) of stone where the view meets the dungeon.")]
    public string Fill(string radius) => dungeons.Blast(DungeonModule.BlastRadius(radius), true);

    [DebugCommand("craft.dungeon.visit", Description = "Moves the player to a place in the dungeon: arrival, breach, loot, or floor0..floorN.")]
    public string Visit(string place) => dungeons.Visit(place);

    [DebugCommand("craft.dungeon.validate", Description = "Publishes the Engine's navigation over the loaded dungeon and routes arrival to breach and loot, and back.")]
    public string Validate() => dungeons.Validate();

    [DebugCommand("craft.dungeon.leave", Description = "Leaves the dungeon from its way out, as the UI's Leave does.")]
    public string Leave() => dungeons.Leave();
}
