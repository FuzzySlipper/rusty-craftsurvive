namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// One column of generated ground: its surface height, how much it changes across its
/// immediate neighbours, the water surface over it (<see cref="WaterRules"/>: the sea, a river's
/// surface over its channel or a lake's level, in fractional metres), and the map geography it
/// was sampled from. Published as its own type
/// because more than the recipe reasons about terrain this way - chunk generation, the
/// generator fingerprint, and site placement, which needs the slope to know whether a
/// hillside can carry a way in. Geography is resolved once per column, never per voxel.
/// </summary>
internal readonly record struct TerrainColumn(long Surface, long Slope, double WaterSurface = WaterRules.SeaSurface,
    MapSample Geography = default)
{
    /// <summary>The highest cell any part of which lies under the water: what decides in whole cells (a sand bed, a tree, a deck).</summary>
    internal long WaterTop => (long)Math.Ceiling(WaterSurface) - 1;

    /// <summary>Whether water stands over the column's ground.</summary>
    internal bool Submerged => Surface <= WaterTop;
}
