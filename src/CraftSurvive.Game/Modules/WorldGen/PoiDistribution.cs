namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Macro geographic, regional, and civilization context at a candidate site (#11185),
/// read by continental POI placement so landmarks follow the world's biomes, frontier
/// bounds, and distance from settlements rather than scattering uniformly.
/// </summary>
internal readonly record struct PoiContext(
    MapBiome Biome,
    double Rock,
    double SettlementDistanceMetres,
    bool BeyondFrontier,
    bool InNeck);

/// <summary>
/// Per-biome continental POI density and relative kind weights (#11185).
/// </summary>
internal readonly record struct PoiBiomeProfile(
    double Density,
    double StandingStones,
    double Ruin,
    double CaveMouth,
    double DungeonEntrance,
    double VantagePoint);

/// <summary>
/// Biome-, region-, and civilization-aware POI placement rules for continental worlds (#11185).
///
/// Regional worlds keep uniform placement for small-scale traversal checks; continents (320-450 km)
/// vary both density and kind mix across biomes, suppress sites past the forbidden frontier, thin
/// wild danger around town doorsteps, and enrich remote wilderness with ancient delving sites.
/// </summary>
internal static class PoiDistribution
{
    // --- civilization gradient distances (metres from nearest settlement seat or hold) ---
    /// <summary>Immediate town outskirts: calm approach with no dungeon entrances on a town's doorstep.</summary>
    internal const double TownOutskirtsMetres = 1_500;

    /// <summary>Settled river hinterland around towns and port: gentler ruins and stones, rarer deep dungeons.</summary>
    internal const double SettledHinterlandMetres = 12_000;

    /// <summary>Beyond this distance from every settlement begins the remote ancient frontier.</summary>
    internal const double DeepFrontierMetres = 35_000;

    // --- civilization and region multipliers ---
    private const double OutskirtsDensity = 0.50;
    private const double HinterlandDensity = 0.85;
    private const double DeepFrontierDensity = 1.15;
    private const double NeckDensity = 0.50;

    private const double OutskirtsRuinWeight = 0.60;
    private const double OutskirtsStonesWeight = 1.20;
    private const double HinterlandDungeonWeight = 0.35;
    private const double HinterlandRuinWeight = 0.90;
    private const double DeepFrontierDungeonWeight = 1.80;
    private const double DeepFrontierRuinWeight = 1.30;

    private const double NeckVantageWeight = 1.50;
    private const double NeckStonesWeight = 1.30;
    private const double NeckRuinWeight = 0.50;

    // --- terrain / karst rock modulation ---
    private const double KarstRockThreshold = 0.35;
    private const double KarstCaveMaximumBoost = 1.80;

    // --- per-biome base profiles (density per 256 m cell; relative kind weights) ---
    private static readonly PoiBiomeProfile SeaProfile = new(0.00, 0, 0, 0, 0, 0);
    private static readonly PoiBiomeProfile IceFieldProfile = new(0.03, 55, 5, 15, 5, 20);
    private static readonly PoiBiomeProfile TundraProfile = new(0.08, 40, 20, 15, 15, 10);
    private static readonly PoiBiomeProfile AlpineProfile = new(0.11, 15, 10, 35, 20, 20);
    private static readonly PoiBiomeProfile DesertProfile = new(0.12, 20, 35, 10, 25, 10);
    private static readonly PoiBiomeProfile BorealForestProfile = new(0.14, 25, 20, 30, 15, 10);
    private static readonly PoiBiomeProfile ColdSteppeProfile = new(0.14, 30, 25, 15, 15, 15);
    private static readonly PoiBiomeProfile RainforestProfile = new(0.15, 15, 30, 30, 20, 5);
    private static readonly PoiBiomeProfile GrasslandProfile = new(0.16, 30, 35, 10, 10, 15);
    private static readonly PoiBiomeProfile TemperateForestProfile = new(0.18, 20, 30, 25, 15, 10);
    private static readonly PoiBiomeProfile ShrublandProfile = new(0.20, 20, 30, 25, 15, 10);

    internal static PoiBiomeProfile ProfileFor(MapBiome biome) => biome switch
    {
        MapBiome.Sea => SeaProfile,
        MapBiome.IceField => IceFieldProfile,
        MapBiome.Tundra => TundraProfile,
        MapBiome.BorealForest => BorealForestProfile,
        MapBiome.ColdSteppe => ColdSteppeProfile,
        MapBiome.TemperateForest => TemperateForestProfile,
        MapBiome.Grassland => GrasslandProfile,
        MapBiome.Shrubland => ShrublandProfile,
        MapBiome.Desert => DesertProfile,
        MapBiome.Rainforest => RainforestProfile,
        MapBiome.Alpine => AlpineProfile,
        _ => GrasslandProfile,
    };

    /// <summary>Probability in [0, 1] that a continental 256 m anchor cell holds a POI.</summary>
    internal static double Density(in PoiContext context)
    {
        if (context.BeyondFrontier)
        {
            return 0;
        }

        double density = ProfileFor(context.Biome).Density;
        if (density <= 0)
        {
            return 0;
        }

        if (context.SettlementDistanceMetres < TownOutskirtsMetres)
        {
            density *= OutskirtsDensity;
        }
        else if (context.SettlementDistanceMetres < SettledHinterlandMetres)
        {
            density *= HinterlandDensity;
        }
        else if (context.SettlementDistanceMetres > DeepFrontierMetres)
        {
            density *= DeepFrontierDensity;
        }

        if (context.InNeck)
        {
            density *= NeckDensity;
        }

        return Math.Clamp(density, 0, 1);
    }

    /// <summary>
    /// Selects an eligible <see cref="PoiKind"/> from biome, terrain, karst rock, region, and
    /// settlement-distance weights using <paramref name="unitDraw"/> in [0, 1). Ineligible kinds
    /// (e.g. caves on flat ground, ruins on steep cliffs, dungeons on town doorsteps, lookouts in
    /// coastal lowlands) receive zero weight before the draw so biome proportions stay faithful.
    /// </summary>
    internal static PoiKind PickKind(in PoiContext context, TerrainColumn column, long relief, double unitDraw)
    {
        PoiBiomeProfile profile = ProfileFor(context.Biome);
        double stones = profile.StandingStones;
        double ruin = column.Slope <= PoiConstants.FlatSlopeMaximum ? profile.Ruin : 0;
        double cave = relief >= PoiConstants.ReliefMinimum ? profile.CaveMouth : 0;
        double dungeon = relief >= PoiConstants.ReliefMinimum ? profile.DungeonEntrance : 0;
        double vantage = column.Surface >= PoiConstants.ContinentalVantageMinimumHeight ? profile.VantagePoint : 0;

        if (cave > 0 && context.Rock > KarstRockThreshold)
        {
            double share = Math.Clamp((context.Rock - KarstRockThreshold) / (1.0 - KarstRockThreshold), 0, 1);
            cave *= 1.0 + ((KarstCaveMaximumBoost - 1.0) * share);
        }

        if (context.SettlementDistanceMetres < TownOutskirtsMetres)
        {
            dungeon = 0;
            ruin *= OutskirtsRuinWeight;
            stones *= OutskirtsStonesWeight;
        }
        else if (context.SettlementDistanceMetres < SettledHinterlandMetres)
        {
            dungeon *= HinterlandDungeonWeight;
            ruin *= HinterlandRuinWeight;
        }
        else if (context.SettlementDistanceMetres > DeepFrontierMetres)
        {
            dungeon *= DeepFrontierDungeonWeight;
            ruin *= DeepFrontierRuinWeight;
        }

        if (context.InNeck)
        {
            vantage *= NeckVantageWeight;
            stones *= NeckStonesWeight;
            ruin *= NeckRuinWeight;
        }

        double total = stones + ruin + cave + dungeon + vantage;
        if (total <= 0)
        {
            return PoiKind.StandingStones;
        }

        double target = Math.Clamp(unitDraw, 0, 1 - 1e-12) * total;
        if (target < stones) return PoiKind.StandingStones;
        target -= stones;
        if (target < ruin) return PoiKind.Ruin;
        target -= ruin;
        if (target < cave) return PoiKind.CaveMouth;
        target -= cave;
        if (target < dungeon) return PoiKind.DungeonEntrance;
        return PoiKind.VantagePoint;
    }
}
