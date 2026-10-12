namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Which body of water stands at a place: none, the sea, a river or a lake.</summary>
internal enum WaterBody { None, Sea, River, Lake }

/// <summary>
/// The one rule for where water stands (#9877), so every view - walking terrain, the swim volume, the
/// near water surfaces, the far field, the horizon and the map - agrees: the sea's level everywhere
/// (ground below it floods), raised by a river's surface over its channel or a lake's level over its
/// basin. Surfaces are fractional metres; nothing is floored to a voxel.
/// </summary>
internal static class WaterRules
{
    /// <summary>The sea's surface (metres): where the map's land begins.</summary>
    internal const double SeaSurface = GenerationConstants.WaterLevel;

    /// <summary>The water surface over a place: the highest of the sea, its river and its lake.</summary>
    internal static double Surface(MapSample sample)
    {
        double surface = SeaSurface;
        if (sample.InRiver) surface = Math.Max(surface, sample.RiverSurface);
        if (sample.InLake) surface = Math.Max(surface, sample.LakeSurface);
        return surface;
    }

    /// <summary>The body of water over ground at a height, or none when the ground stands above every surface.</summary>
    internal static WaterBody Body(MapSample sample, double ground)
    {
        double surface = Surface(sample);
        if (ground >= surface) return WaterBody.None;
        if (sample.InLake && sample.LakeSurface >= surface) return WaterBody.Lake;
        if (sample.InRiver && sample.RiverSurface >= surface) return WaterBody.River;
        return WaterBody.Sea;
    }
}
