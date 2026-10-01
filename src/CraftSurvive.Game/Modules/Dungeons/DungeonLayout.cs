using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A dungeon ready to load: its volume, where the player arrives and where they leave from, and
/// where its lights hang, all in the volume's own coordinates. A sculpted dungeon also carries its
/// rock as a density the Engine meshes; its volume then holds only the building.
/// </summary>
internal sealed record DungeonLayout(string Name, DungeonVolume Volume, Vector3 Arrival, Vector3 Exit, IReadOnlyList<Vector3> Lights)
{
    /// <summary>The rock as a smooth surface, or null when the rock is voxels.</summary>
    internal RockDensity? Rock { get; init; }

    /// <summary>The most lights a dungeon hangs: the dungeon module's pool of retained lights.</summary>
    internal const int MaximumLights = 16;
}

/// <summary>
/// The first dungeon: one hand-made test chamber for the load path, not a generator. A cave hall
/// with a ruined brick room on a ledge above it, reached by stairs, and a way out where the player
/// arrives.
/// </summary>
internal static class TestChamber
{
    internal static DungeonLayout Build()
    {
        DungeonVolume volume = new(4, 3, 4, BlockId.Stone);

        // The cave: a wide low hall, and a rise toward the ledge.
        volume.CarveEllipsoid(32, 12, 32, 22, 8, 20);
        volume.CarveEllipsoid(44, 18, 22, 10, 9, 10);

        // A flat floor through the hall, so the arrival and the stairs stand on level ground.
        volume.Fill(new DungeonCell(12, 4, 14), new DungeonCell(52, 7, 50), BlockId.Stone);
        volume.Fill(new DungeonCell(12, 8, 14), new DungeonCell(52, 13, 50), BlockId.Air);

        // The building: a brick room on a ledge, its near wall broken open toward the cave.
        volume.Room(new DungeonCell(30, 21, 6), new DungeonCell(54, 28, 24), BlockId.Brick, BlockId.Planks);
        volume.Fill(new DungeonCell(31, 22, 24), new DungeonCell(40, 26, 24), BlockId.Air);
        volume.Fill(new DungeonCell(30, 14, 25), new DungeonCell(42, 21, 32), BlockId.Stone);
        volume.Fill(new DungeonCell(31, 22, 25), new DungeonCell(40, 26, 32), BlockId.Air);

        // Stairs from the hall floor up to the ledge at the broken wall.
        volume.Stairs(new DungeonCell(32, 8, 46), 0, -1, 14, 3, BlockId.Cobblestone);

        return new DungeonLayout(
            "test-chamber",
            volume,
            Arrival: new Vector3(20.5f, 8f, 40.5f),
            Exit: new Vector3(18.5f, 8f, 40.5f),
            Lights: [new Vector3(20.5f, 11.5f, 40.5f), new Vector3(33.5f, 12.5f, 30.5f), new Vector3(42.5f, 25.5f, 15.5f)]);
    }
}
