namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The product's identity spaces in one place. Appearance object ids, spatial entity ids and
/// entity-store component keys are separate spaces in the Engine; within each, the product keeps
/// its owners in disjoint ranges so no two owners can mint the same id.
/// </summary>
internal static class ProductIds
{
    /// <summary>The player: a spatial entity and the target creatures perceive.</summary>
    internal const ulong PlayerEntity = 1UL;

    /// <summary>Creature appearance objects are this base plus the creature id.</summary>
    internal const ulong CreatureAppearanceBase = 0x1_0000UL;

    /// <summary>
    /// Creature perception observers are this base plus the creature id, so an observer never
    /// shares an id with the player it is looking for.
    /// </summary>
    internal const ulong CreatureObserverBase = 0x2_0000UL;

    /// <summary>A sculpted dungeon's rock mesh: one appearance object.</summary>
    internal const ulong DungeonRockObject = 0x3_0000UL;

    /// <summary>The expedition's sled, standing where the party left it (#9473).</summary>
    internal const ulong SledObject = 0x3_1000UL;

    internal const ulong WorldMapObject = 0x4_0000UL;
    internal const ulong WorldMapPartyObject = 0x4_0001UL;
    internal const ulong WorldMapWaypointObject = 0x4_0002UL;
    /// <summary>Route ribbon markers on the faceted map; up to 0xF00 of them.</summary>
    internal const ulong WorldMapRouteBase = 0x4_0100UL;
    internal const int WorldMapRouteLimit = 0xF00;
    /// <summary>Map clutter instances (trees, rocks) on the faceted map's detail patch; up to 0xF000 of them.</summary>
    internal const ulong WorldMapClutterBase = 0x4_1000UL;
    internal const int WorldMapClutterLimit = 0xF000;
    /// <summary>Known-place markers on the faceted map: home and every journal place.</summary>
    internal const ulong WorldMapPlaceBase = 0x5_0000UL;
    internal const int WorldMapPlaceLimit = 0x2000;

    /// <summary>Weather fronts on the faceted map (#9739): a disc each, and a trail of dots where each is heading.</summary>
    internal const ulong WorldMapWeatherBase = 0x5_2000UL;
    internal const int WorldMapWeatherLimit = 0x400;
    internal const ulong WorldMapWeatherTrackBase = 0x5_2400UL;
    internal const int WorldMapWeatherTrackLimit = 0x1000;

    /// <summary>Weather fronts standing on the horizon, in the backdrop (HorizonWeather, #9780).</summary>
    internal const ulong HorizonFrontBase = 0x8_0000UL;
    internal const int HorizonFrontLimit = 0x100;

    /// <summary>The overworld's drawn trees (TerrainTrees, #9665); up to 0x4000 of them.</summary>
    internal const ulong TreeObjectBase = 0x6_0000UL;
    internal const int TreeObjectLimit = 0x4000;

    /// <summary>The building look study's mesh cabin (BuildStudy, #9684).</summary>
    internal const ulong BuildStudyObject = 0x7_0000UL;

    /// <summary>The placement ghost of the selected build piece (#9729).</summary>
    internal const ulong BuildPieceGhost = 0x7_0001UL;

    /// <summary>Placed build pieces (#9729); up to <see cref="Building.BuildPieceSet.MaximumPieces"/> of them.</summary>
    internal const ulong BuildPieceBase = 0x7_1000UL;

    /// <summary>Retained light ids: the sky's sun (or moon), its occlusion layer (an ambient light) and its hemisphere fill.</summary>
    internal const ulong SunLight = 1UL;

    internal const ulong AmbientLight = 2UL;

    internal const ulong FillLight = 3UL;

    /// <summary>Placed lamps' retained lights are this base plus their slot in the lamp pool.</summary>
    internal const ulong LampLightBase = 0x100UL;

    /// <summary>A dungeon's own lights are this base plus their index in its layout.</summary>
    internal const ulong DungeonLightBase = 0x200UL;

    /// <summary>Retained fire emitters: three per lamp slot and per dungeon torch slot (#9547).</summary>
    internal const ulong LampFireBase = 0x300UL;
    internal const ulong DungeonFireBase = 0x400UL;

    /// <summary>Entity-store component keys.</summary>
    internal const uint PlayerRuntimeComponent = 1U;

    internal const uint CreatureRuntimeComponent = 0x1001U;
}
