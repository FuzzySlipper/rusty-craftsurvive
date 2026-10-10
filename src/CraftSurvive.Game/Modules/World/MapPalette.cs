using CraftSurvive.Game.Modules.Places;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>Flat overview colours for map environments, shared by every map presentation.</summary>
internal static class MapPalette
{
    /// <summary>Known-place markers: home is green, places reached orange, places only seen a duller tan, dungeon entrances violet, the sled where it was left wood brown.</summary>
    internal static Color Place(KnownPlaceKind kind) => kind switch
    {
        KnownPlaceKind.Home => new(0.35f, 0.9f, 0.45f, 1),
        KnownPlaceKind.Visited => new(1, 0.38f, 0.1f, 1),
        KnownPlaceKind.Entrance => new(0.62f, 0.32f, 0.95f, 1),
        KnownPlaceKind.Sled => new(0.55f, 0.36f, 0.18f, 1),
        _ => new(0.78f, 0.6f, 0.42f, 1),
    };

    /// <summary>Several places drawn as one marker at the current zoom (#9553).</summary>
    internal static readonly Color PlaceCluster = new(0.95f, 0.82f, 0.55f, 1);

    /// <summary>Weather fronts on the map (#9739), by kind id: rain slate blue, snow white, sand ochre, fog grey, the arcane glass storm a prismatic rose.</summary>
    internal static Color Weather(string kind) => kind switch
    {
        "rain" => new(0.36f, 0.46f, 0.62f, 1),
        "snow" => new(0.92f, 0.94f, 1, 1),
        "sand" => new(0.86f, 0.68f, 0.42f, 1),
        "fog" => new(0.78f, 0.8f, 0.8f, 1),
        "glass" => new(0.96f, 0.62f, 0.94f, 1),
        _ => new(0.7f, 0.7f, 0.75f, 1),
    };

    private const float RockBlend = 0.45f;

    // The environments' colours are steps of the content palette's ramps (content/style/palette.json, #9813),
    // so the map, the horizon and the authored materials share one palette: a ramp's name and its step
    // (0 dark to 4 light). WorldMap's checks hold them to the file.
    internal static readonly (string Ramp, int Step) StoneSource = ("stone", 2), RiverSource = ("water", 2);
    internal static readonly Color Stone = Hex(0x6f6a62);
    internal static readonly Color River = Hex(0x3f7378);

    /// <summary>Each environment's ramp and step in the content palette.</summary>
    internal static (string Ramp, int Step) Source(MapBiome biome) => biome switch
    {
        MapBiome.Sea => ("water", 1),
        MapBiome.IceField => ("snow", 3),
        MapBiome.Tundra => ("sage", 3),
        MapBiome.BorealForest => ("teal", 0),
        MapBiome.ColdSteppe => ("sage", 2),
        MapBiome.TemperateForest => ("foliage", 2),
        MapBiome.Grassland => ("meadow", 2),
        MapBiome.Shrubland => ("sandstone", 2),
        MapBiome.Desert => ("sandstone", 3),
        MapBiome.Rainforest => ("foliage", 1),
        _ => ("stone", 2),
    };

    internal static Color For(MapBiome biome) => biome switch
    {
        MapBiome.Sea => Hex(0x2c5560),
        MapBiome.IceField => Hex(0xe2e2dc),
        MapBiome.Tundra => Hex(0x9ea183),
        MapBiome.BorealForest => Hex(0x2d4a48),
        MapBiome.ColdSteppe => Hex(0x7a8160),
        MapBiome.TemperateForest => Hex(0x476530),
        MapBiome.Grassland => Hex(0x6c8a36),
        MapBiome.Shrubland => Hex(0xc7966c),
        MapBiome.Desert => Hex(0xe0b98a),
        MapBiome.Rainforest => Hex(0x2f4a27),
        _ => Hex(0x6f6a62),
    };

    /// <summary>A palette colour (sRGB hex, as the palette file writes it), used as the map's flat colours always have been.</summary>
    internal static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    /// <summary>A sample's environment colour, greyed toward stone as its rock is exposed.</summary>
    internal static Color Tint(MapSample sample)
    {
        Color baseColor = For(WorldMap.Biome(sample));
        float r = (float)sample.Rock * RockBlend;
        return new(baseColor.R + (Stone.R - baseColor.R) * r, baseColor.G + (Stone.G - baseColor.G) * r,
            baseColor.B + (Stone.B - baseColor.B) * r, 1);
    }
}
