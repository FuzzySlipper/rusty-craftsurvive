namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Map-scale environments, classified from sampled climate, elevation and exposed rock.</summary>
internal enum MapBiome : byte
{
    Sea,
    IceField,
    Tundra,
    BorealForest,
    ColdSteppe,
    TemperateForest,
    Grassland,
    Shrubland,
    Desert,
    Rainforest,
    Alpine,
}
