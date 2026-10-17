namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Which topsoil a place's climate gives open ground (#10041): lush meadow where it is moist, dry meadow across a
/// broad band as it dries and heats, and sand only in deep desert. Grassland, shrubland and the desert's edges are
/// grass, sun-dried or not; dune sand is rare. The band's edge wanders with a broad noise, so lush and dry ground
/// meet in drifts tens of metres across rather than column by column. Pure, so the near ground, the vegetation
/// tiles and the far field agree (the far field leaves the noise out: past it a drift is a pixel).
/// </summary>
internal static class Soils
{
    /// <summary>Ground this dry or drier is lush meadow no longer: the dry band runs from here down to <see cref="DryMoisture"/>.</summary>
    private const double LushMoisture = 0.4, DryMoisture = 0.2;

    /// <summary>Heat dries ground: past this temperature, each unit of it counts as this much less moisture.</summary>
    private const double DryingFrom = 0.55, HeatDrying = 1.0;

    /// <summary>Only ground drier than this is dune sand: the deep desert.</summary>
    private const double DeepDesertMoisture = 0.05;

    /// <summary>The noise that wanders the band's edge: its scale in metres, and how far it moves dryness either way.</summary>
    private const int DriftScale = 56;
    private const double DriftReach = 0.35;
    private const ulong DriftSalt = 0xD27E_5011_0000_0041UL;

    /// <summary>How dry a place's climate is, 0 (lush) to 1 (dry), from its moisture less what heat takes.</summary>
    internal static double Dryness(MapSample sample)
    {
        double wet = sample.Moisture - (HeatDrying * Math.Max(0, sample.Temperature - DryingFrom));
        return Math.Clamp((LushMoisture - wet) / (LushMoisture - DryMoisture), 0, 1);
    }

    /// <summary>Whether open ground here is dry meadow rather than lush, its edge wandering with the drift.</summary>
    internal static bool Dry(MapSample sample, double x, double z, ulong seed)
    {
        double drift = (TerrainRecipe.ValueNoise(seed ^ DriftSalt, x, z, DriftScale) - 0.5) * 2 * DriftReach;
        return Dryness(sample) + drift > 0.5;
    }

    /// <summary>Whether open ground here is dry meadow, without the drift: for the far field.</summary>
    internal static bool Dry(MapSample sample) => Dryness(sample) > 0.5;

    /// <summary>Whether a place is deep desert, whose open ground is dune sand.</summary>
    internal static bool DeepDesert(MapSample sample) => !WorldMap.Frozen(sample) && sample.Moisture < DeepDesertMoisture;
}
