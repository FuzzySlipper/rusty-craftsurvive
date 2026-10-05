using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// How the faceted map's ground is drawn (#9464). Flat uses one colour per environment; the
/// textured styles map environments onto four terrain layers the Engine blends across cell edges,
/// with exposed rock either blended as a layer or drawn on its own.
/// </summary>
internal sealed record MapSurfaceStyle(string Name, GroundTextureSet? Textures, Func<MapBiome, int> Layer,
    int? RockLayer, string? PlainRock)
{
    internal static MapSurfaceStyle Flat { get; } = new("flat", null, _ => -1, null, null);

    /// <summary>The walking-terrain maps re-used at map scale: grass, sand, snow and rock.</summary>
    internal static MapSurfaceStyle Ground { get; } = new("ground",
        new("textures/map-ground/", ["sage-ground", "dune-sand", "frost-stone", "ochre-rock"], "map-ground"),
        biome => biome switch
        {
            MapBiome.Desert or MapBiome.Shrubland => 1,
            MapBiome.IceField or MapBiome.Tundra => 2,
            MapBiome.Alpine => 3,
            _ => 0,
        }, 3, null);

    /// <summary>Stylized map-scale maps: meadow, forest floor, sand and snow, with rock drawn on its own.</summary>
    internal static MapSurfaceStyle Painted { get; } = new("painted",
        new("textures/map-painted/", ["meadow", "forest-floor", "sand", "snow"], "map-painted"),
        biome => biome switch
        {
            MapBiome.TemperateForest or MapBiome.BorealForest or MapBiome.Rainforest => 1,
            MapBiome.Desert or MapBiome.Shrubland => 2,
            MapBiome.IceField or MapBiome.Tundra => 3,
            MapBiome.Alpine => -1,
            _ => 0,
        }, null, "rock");

    internal static IReadOnlyList<MapSurfaceStyle> All { get; } = [Flat, Ground, Painted];

    internal static MapSurfaceStyle Named(string name) =>
        All.FirstOrDefault(style => style.Name == name) ?? throw new ArgumentException($"Unknown map style '{name}'.", nameof(name));
}
