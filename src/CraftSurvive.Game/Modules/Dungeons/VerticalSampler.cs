namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A hand-placed sketch of vertical route shapes (#7916), built from the module library rather than
/// grown: arrive above a chasm, cross it by the bridge, enter the building and take its stair down a
/// storey, then come back under the bridge along the chasm floor to the loot in a grotto beyond it.
/// The two crossings of the chasm lie a storey apart, and the chasm's climbable ledge faces offer a
/// way back up that walking does not need. Only the modules' own details vary with the seed.
/// </summary>
internal static class VerticalSampler
{
    private const int Upper = 3;
    private const int Lower = 2;

    /// <summary>
    /// The sketch, sculpted and walk-checked. Without its stair (<paramref name="withoutStair"/>) the
    /// lower storey is reached only by dropping off the bridge, and the way back only by climbing:
    /// a route the check must refuse.
    /// </summary>
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable, IReadOnlyList<PlacedModule> Modules) Generate(
        ulong seed, bool withoutStair = false)
    {
        PlacedModule arrival = new(DungeonModules.Arrival, 0, 5, Upper, 9);
        PlacedModule chasm = new(DungeonModules.Chasm, 0, 4, Lower, 6);
        PlacedModule breach = new(DungeonModules.Breach, 2, 5, Upper, 5);
        PlacedModule landing = new(DungeonModules.Room, 0, 5, Upper, 4);
        PlacedModule gallery = new(DungeonModules.Corridor, 1, 6, Upper, 4);
        PlacedModule stair = new(DungeonModules.Stair, 3, 7, Lower, 4);
        PlacedModule foot = new(DungeonModules.Room, 0, 8, Lower, 4);
        PlacedModule passage = new(DungeonModules.Corridor, 0, 8, Lower, 5);
        PlacedModule passageOn = new(DungeonModules.Corridor, 0, 8, Lower, 6);
        PlacedModule turn = new(DungeonModules.Room, 0, 8, Lower, 7);
        PlacedModule underBreach = new(DungeonModules.Breach, 1, 7, Lower, 7);
        PlacedModule grotto = new(DungeonModules.Arrival, 3, 3, Lower, 7);
        List<PlacedModule> placed = [arrival, chasm, breach, landing, gallery, stair, foot, passage, passageOn, turn, underBreach, grotto];
        if (withoutStair)
        {
            placed.Remove(stair);
        }

        return ModularDungeon.Placed(seed, placed, breach, grotto);
    }
}
