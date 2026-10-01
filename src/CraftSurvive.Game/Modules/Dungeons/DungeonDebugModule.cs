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

    [DebugCommand("craft.dungeon.leave", Description = "Leaves the dungeon from its way out, as the UI's Leave does.")]
    public string Leave() => dungeons.Leave();
}
