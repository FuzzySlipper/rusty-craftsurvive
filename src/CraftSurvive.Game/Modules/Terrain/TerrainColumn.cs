namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// One column of generated ground: its surface height and how much it changes across
/// its immediate neighbours. Published as its own type because more than the recipe
/// reasons about terrain this way - chunk generation, the substrate proof, and site
/// placement, which needs the slope to know whether a hillside can carry a way in.
/// </summary>
internal readonly record struct TerrainColumn(long Surface, long Slope);
