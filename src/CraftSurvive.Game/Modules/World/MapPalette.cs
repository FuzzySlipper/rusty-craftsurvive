using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>Flat overview colours for map environments, shared by every map presentation.</summary>
internal static class MapPalette
{
    private const float RockBlend = 0.45f;
    internal static readonly Color Stone = new(0.5f, 0.48f, 0.46f, 1);
    internal static readonly Color River = new(0.22f, 0.42f, 0.72f, 1);

    internal static Color For(MapBiome biome) => biome switch
    {
        MapBiome.Sea => new(0.16f, 0.31f, 0.45f, 1),
        MapBiome.IceField => new(0.92f, 0.94f, 0.96f, 1),
        MapBiome.Tundra => new(0.66f, 0.68f, 0.62f, 1),
        MapBiome.BorealForest => new(0.24f, 0.37f, 0.31f, 1),
        MapBiome.ColdSteppe => new(0.55f, 0.55f, 0.43f, 1),
        MapBiome.TemperateForest => new(0.2f, 0.41f, 0.19f, 1),
        MapBiome.Grassland => new(0.43f, 0.55f, 0.27f, 1),
        MapBiome.Shrubland => new(0.67f, 0.62f, 0.35f, 1),
        MapBiome.Desert => new(0.84f, 0.71f, 0.47f, 1),
        MapBiome.Rainforest => new(0.12f, 0.35f, 0.18f, 1),
        _ => new(0.52f, 0.5f, 0.48f, 1),
    };

    /// <summary>A sample's environment colour, greyed toward stone as its rock is exposed.</summary>
    internal static Color Tint(MapSample sample)
    {
        Color baseColor = For(WorldMap.Biome(sample));
        float r = (float)sample.Rock * RockBlend;
        return new(baseColor.R + (Stone.R - baseColor.R) * r, baseColor.G + (Stone.G - baseColor.G) * r,
            baseColor.B + (Stone.B - baseColor.B) * r, 1);
    }
}
