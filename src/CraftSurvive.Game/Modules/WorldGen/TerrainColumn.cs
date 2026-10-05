namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// One column of generated ground: its surface height, how much it changes across its
/// immediate neighbours, the highest water voxel standing over it (the sea, or a river
/// channel's surface), and the map geography it was sampled from. Published as its own type
/// because more than the recipe reasons about terrain this way - chunk generation, the
/// generator fingerprint, and site placement, which needs the slope to know whether a
/// hillside can carry a way in. Geography is resolved once per column, never per voxel.
/// </summary>
internal readonly record struct TerrainColumn(long Surface, long Slope, long WaterTop = GenerationConstants.WaterLevel,
    MapSample Geography = default);
