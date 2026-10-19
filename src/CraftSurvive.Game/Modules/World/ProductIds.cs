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

    /// <summary>The expedition's boat (#9879): its appearance, and the collider the player stands on aboard.</summary>
    internal const ulong BoatObject = 0x3_1001UL;
    internal const ulong BoatCarrier = 0x3_1002UL;

    internal const ulong WorldMapObject = 0x4_0000UL;
    internal const ulong WorldMapPartyObject = 0x4_0001UL;
    internal const ulong WorldMapWaypointObject = 0x4_0002UL;
    /// <summary>Route ribbon markers on the map; up to 0xF00 of them.</summary>
    internal const ulong WorldMapRouteBase = 0x4_0100UL;
    internal const int WorldMapRouteLimit = 0xF00;
    /// <summary>Map clutter instances (trees, rocks) on the map's detail patch; up to 0xF000 of them.</summary>
    internal const ulong WorldMapClutterBase = 0x4_1000UL;
    internal const int WorldMapClutterLimit = 0xF000;
    /// <summary>Known-place markers on the map: home and every journal place.</summary>
    internal const ulong WorldMapPlaceBase = 0x5_0000UL;
    internal const int WorldMapPlaceLimit = 0x2000;

    /// <summary>Weather fronts on the map (#9739): a disc each, and a trail of dots where each is heading.</summary>
    internal const ulong WorldMapWeatherBase = 0x5_2000UL;
    internal const int WorldMapWeatherLimit = 0x400;
    internal const ulong WorldMapWeatherTrackBase = 0x5_2400UL;
    internal const int WorldMapWeatherTrackLimit = 0x1000;

    /// <summary>The frontier's posts on the map (#9817), where it crosses land.</summary>
    internal const ulong WorldMapFrontierBase = 0x5_3400UL;
    internal const int WorldMapFrontierLimit = 0x400;

    /// <summary>River ribbons on the map (#9948): one per tier tile that a river crosses.</summary>
    internal const ulong WorldMapRiverBase = 0x5_4000UL;
    internal const int WorldMapRiverLimit = 0x2000;

    /// <summary>The sea and lake surfaces on the map (#9952): the sea's plane, then one a lake.</summary>
    internal const ulong WorldMapWaterBase = 0x5_6000UL;
    internal const int WorldMapWaterLimit = 0x400;

    /// <summary>Weather fronts standing on the horizon, in the backdrop (HorizonWeather, #9780).</summary>
    internal const ulong HorizonFrontBase = 0x8_0000UL;
    internal const int HorizonFrontLimit = 0x100;

    /// <summary>Known places standing on the horizon (HorizonLandmarks, #9781); their lights' particles follow the limit.</summary>
    internal const ulong HorizonLandmarkBase = 0x8_1000UL;
    internal const int HorizonLandmarkLimit = 0x100;

    /// <summary>The near standing trees' crowns as shelter query colliders (TerrainTrees, #9741); up to 0x4000 of them.</summary>
    internal const ulong TreeCanopyBase = 0x6_0000UL;
    internal const int TreeCanopyLimit = 0x4000;

    /// <summary>The building look study's mesh cabin (BuildStudy, #9684).</summary>
    internal const ulong BuildStudyObject = 0x7_0000UL;

    /// <summary>The water look study's surface lake (WaterStudy, #9819).</summary>
    internal const ulong WaterStudyObject = 0x7_0002UL;

    /// <summary>The near water surfaces, one object per tile (WaterSurfaces, #9877).</summary>
    internal const ulong WaterSurfaceBase = 0x7_8000UL;
    internal const int WaterSurfaceLimit = 0x2000;

    /// <summary>The placement ghost of the selected build piece (#9729).</summary>
    internal const ulong BuildPieceGhost = 0x7_0001UL;

    /// <summary>Placed build pieces (#9729); up to <see cref="Building.BuildPieceSet.MaximumPieces"/> of them.</summary>
    internal const ulong BuildPieceBase = 0x7_1000UL;

    /// <summary>Retained light ids: the sun, its occlusion layer (an ambient light), its hemisphere fill and the moon.</summary>
    internal const ulong SunLight = 1UL;

    internal const ulong AmbientLight = 2UL;

    internal const ulong FillLight = 3UL;

    internal const ulong MoonLight = 4UL;

    /// <summary>Placed lamps' retained lights are this base plus their slot in the lamp pool.</summary>
    internal const ulong LampLightBase = 0x100UL;

    /// <summary>A dungeon's own lights are this base plus their index in its layout.</summary>
    internal const ulong DungeonLightBase = 0x200UL;

    /// <summary>The player's lantern in a dungeon, for the darkness study (#10091).</summary>
    internal const ulong DungeonLanternLight = 0x2F0UL;

    /// <summary>Retained fire emitters: three per lamp slot and per dungeon torch slot (#9547).</summary>
    internal const ulong LampFireBase = 0x300UL;
    internal const ulong DungeonFireBase = 0x400UL;

    /// <summary>The composed town study: drawing, selectable services and foreground blockers.</summary>
    internal const ulong TownAppearanceBase = 0x9_0000UL;

    internal const ulong TownServiceBase = 0x9_1000UL;
    internal const ulong TownBlockerBase = 0x9_2000UL;
    internal const ulong TownLightBase = 0x500UL;
    internal const ulong TownEmitterBase = 0x600UL;

    /// <summary>The character style test's travellers (#10014), a test aid placed by live debug.</summary>
    internal const ulong CharacterStyleBase = 0x9_8000UL;

    /// <summary>The overworld places' kit pieces standing in the near ground (#9992), at most <see cref="PlaceMeshLimit"/>.</summary>
    internal const ulong PlaceMeshBase = 0x9_A000UL;

    internal const ulong PlaceMeshLimit = 0x1000UL;

    /// <summary>The overgrown places' ivy blankets (#10044), one per piece standing, at most <see cref="PlaceMeshLimit"/>.</summary>
    internal const ulong PlaceBlanketBase = 0x9_C000UL;

    /// <summary>The places' hanging moss and ivy cards (#10044): one mesh for every place near the player.</summary>
    internal const ulong PlaceMossCardsObject = 0x9_B000UL;

    /// <summary>The place look study's variants (#9992), a test aid placed by live debug.</summary>
    internal const ulong PlaceStudyBase = 0x9_9000UL;

    /// <summary>Footprints in the snow (#10068), one per pooled print (SnowTracks.PoolSize).</summary>
    internal const ulong SnowTrackBase = 0x9_D000UL;

    /// <summary>Entity-store component keys.</summary>
    internal const uint PlayerRuntimeComponent = 1U;

    internal const uint CreatureRuntimeComponent = 0x1001U;
}
