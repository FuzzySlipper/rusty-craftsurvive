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

internal static class MapBiomes
{
    private const double IceTemperature = 0.14;
    private const double TundraTemperature = 0.28;
    private const double CoolTemperature = 0.45;
    private const double HotTemperature = 0.62;
    private const double TropicalTemperature = 0.72;
    private const double DesertMoisture = 0.22;
    private const double BorealMoisture = 0.4;
    private const double ForestMoisture = 0.55;
    private const double ShrubMoisture = 0.45;
    private const double RainforestMoisture = 0.7;
    private const double AlpineRock = 0.72;

    internal static MapBiome Classify(MapSample s)
    {
        if (s.Elevation < GenerationConstants.WaterLevel) return MapBiome.Sea;
        if (s.Temperature < IceTemperature) return MapBiome.IceField;
        if (s.Rock >= AlpineRock) return MapBiome.Alpine;
        if (s.Temperature < TundraTemperature) return MapBiome.Tundra;
        if (s.Temperature < CoolTemperature) return s.Moisture > BorealMoisture ? MapBiome.BorealForest : MapBiome.ColdSteppe;
        if (s.Moisture < DesertMoisture) return MapBiome.Desert;
        if (s.Temperature > TropicalTemperature && s.Moisture > RainforestMoisture) return MapBiome.Rainforest;
        if (s.Temperature > HotTemperature && s.Moisture < ShrubMoisture) return MapBiome.Shrubland;
        return s.Moisture > ForestMoisture ? MapBiome.TemperateForest : MapBiome.Grassland;
    }

    internal static string Name(MapBiome biome) => biome switch
    {
        MapBiome.Sea => "Sea",
        MapBiome.IceField => "Ice field",
        MapBiome.Tundra => "Tundra",
        MapBiome.BorealForest => "Boreal forest",
        MapBiome.ColdSteppe => "Cold steppe",
        MapBiome.TemperateForest => "Temperate forest",
        MapBiome.Grassland => "Grassland",
        MapBiome.Shrubland => "Shrubland",
        MapBiome.Desert => "Desert",
        MapBiome.Rainforest => "Rainforest",
        _ => "Alpine heights",
    };

    internal static bool Frozen(MapBiome biome) => biome is MapBiome.IceField or MapBiome.Tundra;
    internal static bool Arid(MapBiome biome) => biome is MapBiome.Desert or MapBiome.Shrubland;
    /// <summary>One tree per this many feature cells on open soil, or 0 where none grow.</summary>
    internal static int TreeOneIn(MapBiome biome) => biome switch
    {
        MapBiome.Rainforest => 4,
        MapBiome.TemperateForest => 6,
        MapBiome.BorealForest => 7,
        MapBiome.Grassland => 40,
        MapBiome.ColdSteppe => 90,
        _ => 0,
    };
}
