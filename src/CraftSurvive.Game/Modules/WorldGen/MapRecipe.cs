namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The tuning of a map's generation (#9814): what shapes the land, as data, so it can be tried and
/// compared without editing code (tools/MapLab). The defaults are the generator's own: a world made
/// with <see cref="Default"/> is the world its seed and size have always made, so its fingerprint
/// and saves are unchanged. A recipe is not saved; a world's saved geography is.
/// </summary>
internal sealed record MapRecipe
{
    internal static MapRecipe Default { get; } = new();

    public ReliefRecipe Relief { get; init; } = new();

    public SimulationRecipe Simulation { get; init; } = new();
}

/// <summary>
/// The tectonic starting state's tuning (<see cref="MapRelief"/>). Lengths are metres on a regional
/// world; a continent scales them all by its <see cref="MapScale.Lengths"/>.
/// </summary>
internal sealed record ReliefRecipe
{
    // Wavelengths in metres: continents, mountain belts, regional activity, rock strata.
    public double ContinentWavelength { get; init; } = 7000;
    public double WarpWavelength { get; init; } = 3500;
    public double WarpDistance { get; init; } = 1400;
    public double BeltWavelength { get; init; } = 3200;
    public double ActivityWavelength { get; init; } = 5200;
    public double HardnessWavelength { get; init; } = 1100;
    public double TextureWavelength { get; init; } = 260;
    public int ContinentOctaves { get; init; } = 5;
    public int BeltOctaves { get; init; } = 4;
    public int TextureOctaves { get; init; } = 4;
    public double FractalPersistence { get; init; } = 0.5;
    public double BeltPersistence { get; init; } = 0.55;

    /// <summary>The share of the map under sea: at least the minimum, plus a seeded share of the range.</summary>
    public double MinimumSeaFraction { get; init; } = 0.12;
    public double SeaFractionRange { get; init; } = 0.16;
    /// <summary>The chance each border side is open sea rather than a mountain rim.</summary>
    public double SeaSideChance { get; init; } = 0.55;
    /// <summary>How far a sea border reaches inland: at most this many metres, or this share of the map.</summary>
    public double MaximumSeaReach { get; init; } = 2600;
    public double SeaReachFraction { get; init; } = 0.24;
    /// <summary>How far a mountain rim reaches inland: at most this many metres, or this share of the map.</summary>
    public double MaximumRangeReach { get; init; } = 1100;
    public double RangeReachFraction { get; init; } = 0.1;
    /// <summary>How much the border's reach wobbles along it, so the edge is not a ruled line.</summary>
    public double BorderWobble { get; init; } = 0.45;
    public double SeaBorderStrength { get; init; } = 0.9;
    public double RangeBorderLand { get; init; } = 0.5;
    public double RangeBorderUplift { get; init; } = 0.55;
    public double UplandWavelength { get; init; } = 1700;
    public double UplandUplift { get; init; } = 0.3;
    /// <summary>About the map's centre (where a new world starts) land is favoured and uplift calmed, over this radius.</summary>
    public double CentreReserveRadius { get; init; } = 420;
    public double CentreLandBias { get; init; } = 0.8;
    public double CentreCalm { get; init; } = 0.75;
    public double CoastRamp { get; init; } = 0.12;

    public double PlainUplift { get; init; } = 0.06;
    /// <summary>How strongly mountain belts lift, and how sharply their ridged noise peaks.</summary>
    public double BeltUplift { get; init; } = 1.5;
    public double BeltSharpness { get; init; } = 1.6;
    /// <summary>Where belts are active: a broad noise mapped from the floor (none) to the ceiling (full).</summary>
    public double ActivityFloor { get; init; } = -0.15;
    public double ActivityCeiling { get; init; } = 0.35;
    public double HardnessContrast { get; init; } = 1.1;
    public double InitialRelief { get; init; } = 14;
    public double InitialTexture { get; init; } = 3;
    public double SeaFloorDepth { get; init; } = 1;
    /// <summary>Simulation units of texture added when refining, enough to seed finer channels.</summary>
    public double RefinedTexture { get; init; } = 0.4;
    /// <summary>Simulation units of rise per kilometre inland, so drainage organises toward the sea.</summary>
    public double InlandRise { get; init; } = 1.6;
}

/// <summary>The simulation's tuning (<see cref="MapSimulation"/>): erosion, the height scale and slopes.</summary>
internal sealed record SimulationRecipe
{
    /// <summary>The land height scaled to the map's peak elevation: this quantile of all land.</summary>
    public double PeakQuantile { get; init; } = 0.995;
    /// <summary>
    /// The peak elevation in metres, overriding the map scale's (240 m regional, 1,800 m continental);
    /// null keeps the scale's. Summits above it are compressed toward the scale's ceiling.
    /// </summary>
    public double? PeakElevation { get; init; }
    // Most evolution runs on a lattice of twice the spacing; a short refinement at full
    // resolution then organises the finer valleys. This keeps a 10 km world to seconds.
    public int CoarseSteps { get; init; } = 80;
    public int CoarseClimateInterval { get; init; } = 27;
    public int RefineSteps { get; init; } = 10;
    /// <summary>Angle of repose for map-scale slopes, about 40 degrees.</summary>
    public double TalusSlope { get; init; } = 0.84;
    public int TalusIterations { get; init; } = 40;
}
