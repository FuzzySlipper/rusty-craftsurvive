using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// The weather the world knows (Den <c>design/weather-and-environment</c>). Each kind forms only where
/// its climate allows and fades as it drifts out of it. Sizes, speeds and rates are first tunings,
/// meant to be experimented with.
/// </summary>
internal static class WeatherKinds
{
    /// <summary>Height, as a share of the world's peak, above which the glass storm may form: the highest few percent of the land.</summary>
    private const double UplandHeight = 0.75;

    internal static WeatherKind RainFront { get; } = new(
        "rain", "Rain front",
        Birth: ground => ground.Biome switch
        {
            MapBiome.TemperateForest or MapBiome.Rainforest or MapBiome.Grassland or MapBiome.BorealForest or MapBiome.Sea => 1,
            MapBiome.Shrubland => 0.3,
            _ => 0,
        },
        Sustain: ground => ground.Biome switch
        {
            MapBiome.Desert => 0.1,
            MapBiome.IceField => 0.2,
            MapBiome.Tundra => 0.3,
            MapBiome.ColdSteppe => 0.5,
            MapBiome.Alpine or MapBiome.Shrubland => 0.6,
            _ => 1,
        },
        RadiusMetres: 12_000, MetresPerHour: 1_500, LifeHours: 36,
        SpawnCellMetres: 40_000, SlotHours: 12, BirthChance: 0.35, Arcane: false,
        Channels: new(Precipitation: 0.8, Cloud: 1, Wind: 0.4, Cold: 0.1, Murk: 0.3, Arcane: 0),
        Effects: new(TravelCost: 1.4, SupplyUse: 1.1, EventRisk: 1.2, Wetting: 0.5, Chill: 0.05, Harm: 0, Sight: 0.7));

    internal static WeatherKind Snowstorm { get; } = new(
        "snow", "Snowstorm",
        Birth: ground => ground.Biome switch
        {
            MapBiome.IceField or MapBiome.Tundra or MapBiome.Alpine => 1,
            MapBiome.BorealForest or MapBiome.ColdSteppe => 0.7,
            _ => 0,
        },
        Sustain: ground => ground.Biome switch
        {
            MapBiome.IceField or MapBiome.Tundra or MapBiome.Alpine or MapBiome.BorealForest or MapBiome.ColdSteppe => 1,
            MapBiome.Sea => 0.5,
            MapBiome.TemperateForest => 0.4,
            MapBiome.Grassland => 0.3,
            _ => 0.1,
        },
        RadiusMetres: 15_000, MetresPerHour: 2_000, LifeHours: 30,
        SpawnCellMetres: 50_000, SlotHours: 18, BirthChance: 0.3, Arcane: false,
        Channels: new(Precipitation: 0.9, Cloud: 1, Wind: 0.9, Cold: 1, Murk: 0.8, Arcane: 0),
        Effects: new(TravelCost: 2.5, SupplyUse: 1.5, EventRisk: 1.5, Wetting: 0.2, Chill: 0.6, Harm: 0, Sight: 0.35));

    internal static WeatherKind Sandstorm { get; } = new(
        "sand", "Sandstorm",
        Birth: ground => ground.Biome switch
        {
            MapBiome.Desert => 1,
            MapBiome.Shrubland => 0.6,
            MapBiome.ColdSteppe => 0.4,
            _ => 0,
        },
        Sustain: ground => ground.Biome switch
        {
            MapBiome.Desert => 1,
            MapBiome.Shrubland => 0.8,
            MapBiome.ColdSteppe => 0.7,
            MapBiome.Grassland => 0.3,
            _ => 0.05,
        },
        RadiusMetres: 10_000, MetresPerHour: 2_500, LifeHours: 18,
        SpawnCellMetres: 45_000, SlotHours: 18, BirthChance: 0.3, Arcane: false,
        Channels: new(Precipitation: 0, Cloud: 0.5, Wind: 1, Cold: 0, Murk: 0.9, Arcane: 0),
        Effects: new(TravelCost: 2.2, SupplyUse: 1.3, EventRisk: 1.4, Wetting: 0, Chill: 0, Harm: 0, Sight: 0.3));

    internal static WeatherKind FogBank { get; } = new(
        "fog", "Fog bank",
        Birth: ground => ground.Biome switch
        {
            MapBiome.Rainforest => 1,
            MapBiome.TemperateForest or MapBiome.BorealForest => 0.8,
            MapBiome.Sea => 0.6,
            MapBiome.Grassland => 0.4,
            _ => 0,
        },
        Sustain: ground => ground.Biome switch
        {
            MapBiome.Desert or MapBiome.Shrubland => 0,
            MapBiome.Alpine or MapBiome.IceField => 0.3,
            _ => 1,
        },
        RadiusMetres: 6_000, MetresPerHour: 500, LifeHours: 12,
        SpawnCellMetres: 25_000, SlotHours: 12, BirthChance: 0.25, Arcane: false,
        Channels: new(Precipitation: 0, Cloud: 0.4, Wind: 0, Cold: 0.1, Murk: 0.9, Arcane: 0),
        Effects: new(TravelCost: 1.3, SupplyUse: 1, EventRisk: 1.3, Wetting: 0.1, Chill: 0, Harm: 0, Sight: 0.4));

    /// <summary>
    /// The rare arcane storm: a slow, enormous fall of ringing glass that wounds anyone out in it.
    /// It forms only over uplands but roams anywhere, a moving wall that reshapes routes for days.
    /// </summary>
    internal static WeatherKind GlassStorm { get; } = new(
        "glass", "Glass storm",
        Birth: ground => ground.Biome == MapBiome.Alpine || ground.Height >= UplandHeight ? 1 : 0,
        Sustain: ground => ground.Biome == MapBiome.Sea ? 0.5 : 1,
        RadiusMetres: 35_000, MetresPerHour: 800, LifeHours: 120,
        SpawnCellMetres: 130_000, SlotHours: 168, BirthChance: 1, Arcane: true,
        Channels: new(Precipitation: 0.6, Cloud: 0.8, Wind: 0.5, Cold: 0, Murk: 0.4, Arcane: 1),
        Effects: new(TravelCost: 4, SupplyUse: 2, EventRisk: 2, Wetting: 0, Chill: 0, Harm: 6, Sight: 0.6));

    /// <summary>Every kind, in a fixed order: the order sets each kind's salt and the listing order.</summary>
    internal static IReadOnlyList<WeatherKind> All { get; } = [RainFront, Snowstorm, Sandstorm, FogBank, GlassStorm];

    internal static WeatherKind? Find(string id) => All.FirstOrDefault(kind => string.Equals(kind.Id, id, StringComparison.OrdinalIgnoreCase));
}
