using System.Numerics;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Configurable coarseness preset for the continent-scale underground cave-system plan (#11209).
/// The lattice is the site scan's spacing; box size, separation, system size and level count are set apart from it (round 2),
/// so a level changes the plan's detail rather than how many caves there are.
/// </summary>
internal sealed record UndergroundCoarseness(
    string Name,
    string Description,
    double SiteSpacingMetres,
    int BoxChunksX,
    int BoxChunksY,
    int BoxChunksZ,
    int MinSystemSeparationMetres,
    int MaxBoxesPerSystem,
    int MaxLevels,
    double TargetMultiBoxShare,
    int SubChambersPerBox,
    bool IncludeFieldGrid)
{
    /// <summary>Horizontal X extent of one box in metres (16 m per chunk).</summary>
    internal int BoxMetresX => BoxChunksX * 16;

    /// <summary>Vertical Y extent of one box in metres (16 m per chunk).</summary>
    internal int BoxMetresY => BoxChunksY * 16;

    /// <summary>Horizontal Z extent of one box in metres (16 m per chunk).</summary>
    internal int BoxMetresZ => BoxChunksZ * 16;
}

/// <summary>Depth band of a planned cave box, aligned with <c>CaveCommunities</c> depth tiers (15 / 45 / 75 m).</summary>
internal enum UndergroundDepthBand : byte
{
    Shallow = 0,
    Mid = 1,
    Deep = 2,
}

/// <summary>Face of a box where an inter-box passage, shaft or mouth crosses.</summary>
internal enum UndergroundFace : byte
{
    NegX = 0,
    PosX = 1,
    NegZ = 2,
    PosZ = 3,
    /// <summary>The box's floor (round 3): a shaft or pitch down to the level below.</summary>
    NegY = 4,
    /// <summary>The box's roof (round 3): a shaft up to the level above, or a sinkhole to the surface.</summary>
    PosY = 5,
}

/// <summary>Character of a link between two neighbouring boxes.</summary>
internal enum UndergroundLinkKind : byte
{
    DryPassage = 0,
    StreamTube = 1,
    WetSump = 2,
    TallRift = 3,
    /// <summary>A vertical shaft between stacked levels (round 3): a pitch to climb or descend.</summary>
    Shaft = 4,
}

/// <summary>How a cave system breaks to the surface.</summary>
internal enum UndergroundMouthKind : byte
{
    /// <summary>A level meets a slope: a tendril runs from the box's face to the hillside.</summary>
    Hillside = 0,
    /// <summary>A shaft from the top box's roof up to the ground.</summary>
    Sinkhole = 1,
    /// <summary>The lowest level meets the valley: where the system's water comes out.</summary>
    Resurgence = 2,
    /// <summary>A box face meets a steep cliff and opens directly, with no tendril (round 3).</summary>
    Cliff = 3,
}

/// <summary>
/// What the climate made of a system's caves (round 3): the rock decides whether caves exist, the climate only their style.
/// </summary>
internal enum UndergroundStyle : byte
{
    /// <summary>Wet ground: streams still run through the caves.</summary>
    ActiveRiver = 0,
    /// <summary>Dry ground: old caves a wetter past left, drained and decorated.</summary>
    Relict = 1,
    /// <summary>Dry ground far above its base level: dissolved by water rising from below rather than rain falling.</summary>
    Hypogene = 2,
    /// <summary>Cold ground: ice fills the caves.</summary>
    Ice = 3,
}

/// <summary>Major planned underground landmarks hosted inside a natural karst cave system.</summary>
internal enum UndergroundBigSpaceKind : byte
{
    UndergroundLake = 0,
    FogSpace = 1,
    CaveRuin = 2,
    GreatHall = 3,
}

/// <summary>Coarse room / node role inside a box when the coarseness level pre-plans interior topology.</summary>
internal enum UndergroundRoomKind : byte
{
    Chamber = 0,
    Junction = 1,
    Shaft = 2,
    Pool = 3,
    Pocket = 4,
    Hall = 5,
}

/// <summary>One pre-planned room or junction inside a box (local box coordinates in metres).</summary>
internal sealed record UndergroundRoom(
    int Id,
    UndergroundRoomKind Kind,
    float X,
    float Y,
    float Z,
    float RadiusX,
    float RadiusY,
    float RadiusZ,
    float WaterLevel);

/// <summary>One pre-planned conduit segment joining two rooms inside a box.</summary>
internal sealed record UndergroundConduit(
    int FromRoom,
    int ToRoom,
    float Radius,
    float WaterLevel,
    bool IsLoop);

/// <summary>
/// One contiguous chunk-aligned box belonging to a planned cave system. Boxes sit on the system's own lattice
/// (<see cref="GridX"/>, <see cref="GridY"/>, <see cref="GridZ"/>), so neighbours on any axis share a whole face with no gap.
/// </summary>
internal sealed record UndergroundBox(
    int Index,
    int GridX,
    int GridY,
    int GridZ,
    double OriginX,
    double OriginY,
    double OriginZ,
    int ChunksX,
    int ChunksY,
    int ChunksZ,
    float GroundElevation,
    float GroundMinimum,
    float DepthBelowSurface,
    float WaterTableLocalY,
    UndergroundDepthBand DepthBand,
    ulong BoxSeed,
    IReadOnlyList<UndergroundRoom> Rooms,
    IReadOnlyList<UndergroundConduit> Conduits)
{
    /// <summary>The box's top in world metres.</summary>
    internal double TopY => OriginY + (ChunksY * 16.0);
}

/// <summary>
/// An explicit passage port joining two neighbouring boxes in a system across their shared face.
/// Both boxes agree on exact world coordinates, face offset, radius, water level, and deterministic seam seed.
/// On a side face <see cref="FaceU"/> runs along the face and <see cref="FaceV"/> is the height above the first box's floor;
/// on a floor or roof (a shaft) they are the box-local x and z.
/// </summary>
internal sealed record UndergroundLink(
    int FromBox,
    int ToBox,
    UndergroundFace FromFace,
    UndergroundFace ToFace,
    float FaceU,
    float FaceV,
    float WorldX,
    float WorldY,
    float WorldZ,
    float RadiusMetres,
    float WaterLevelWorldY,
    UndergroundLinkKind Kind,
    ulong SeamSeed);

/// <summary>
/// A surface opening belonging to a planned cave system (round 3: where a level meets the ground). The port is on
/// <see cref="Face"/> of box <see cref="BoxIndex"/> at (<see cref="PortU"/>, <see cref="PortV"/>) as a link's is; the
/// surface point (<see cref="WorldX"/>, <see cref="WorldY"/>, <see cref="WorldZ"/>) is where its tendril comes out, which for a
/// cliff mouth is the face itself.
/// </summary>
internal sealed record UndergroundMouth(
    int BoxIndex,
    UndergroundFace Face,
    float PortU,
    float PortV,
    float PortWorldY,
    double WorldX,
    double WorldY,
    double WorldZ,
    float TendrilMetres,
    float SlopeAtMouth,
    int Level,
    UndergroundMouthKind Kind);

/// <summary>A major underground landmark (lake, fog space, cave-hosted ruin, or great hall) placed inside a box.</summary>
internal sealed record UndergroundBigSpace(
    int BoxIndex,
    UndergroundBigSpaceKind Kind,
    float LocalX,
    float LocalY,
    float LocalZ,
    float RadiusMetres);

/// <summary>
/// The ground along a system's main axis (round 3), for its side-on section: sampled every <see cref="StepMetres"/> from
/// <see cref="StartMetres"/> (measured along the axis from the anchor box's centre).
/// </summary>
internal sealed record UndergroundSection(
    UndergroundFace Axis,
    float StartMetres,
    float StepMetres,
    IReadOnlyList<float> Ground);

/// <summary>One bounded natural karst cave system (either a single box or a cluster of 2..N linked boxes).</summary>
internal sealed record UndergroundSystem(
    string Id,
    int TileX,
    int TileZ,
    ulong Seed,
    double AnchorX,
    double AnchorZ,
    float GroundElevation,
    MapBiome Biome,
    float Hardness,
    float Solubility,
    float Discharge,
    float Temperature,
    float Moisture,
    float MaxDepthMetres,
    IReadOnlyList<UndergroundBox> Boxes,
    IReadOnlyList<UndergroundLink> Links,
    IReadOnlyList<UndergroundMouth> Mouths,
    IReadOnlyList<UndergroundBigSpace> BigSpaces)
{
    internal bool IsMultiBox => Boxes.Count > 1;

    /// <summary>What the climate made of the caves (round 3).</summary>
    internal UndergroundStyle Style { get; init; }

    /// <summary>The local base level (world Y): the lowest ground the system's water can reach nearby, its valley floor or spring.</summary>
    internal float BaseLevelY { get; init; }

    /// <summary>How far the anchor's ground stands above the base level.</summary>
    internal float ReliefMetres { get; init; }

    /// <summary>How many stacked levels the system has.</summary>
    internal int Levels { get; init; } = 1;

    /// <summary>Whether the lowest level reaches down to the base level (false where the level count, not the relief, stopped it).</summary>
    internal bool ReachesBaseLevel { get; init; }

    /// <summary>Whether the system was placed by its region's floor (best remaining site by rank) rather than by its roll.</summary>
    internal bool FromRegionalFloor { get; init; }

    /// <summary>The ground along the system's main axis, for its side-on section.</summary>
    internal UndergroundSection? Section { get; init; }

    /// <summary>Lowest box floor (world Y).</summary>
    internal double FloorY => Boxes.Min(b => b.OriginY);

    /// <summary>Highest box top (world Y).</summary>
    internal double TopY => Boxes.Max(b => b.TopY);
}

/// <summary>Optional upfront 2D underground layer grid stored per 16 km tile at fine/detailed coarseness.</summary>
internal sealed record UndergroundTileGrid(
    int Side,
    double SpacingMetres,
    byte[] Solubility,
    short[] WaterTableMetres,
    byte[] DepthBand);

/// <summary>Deterministic underground plan for one 16 km region tile (<see cref="MapRegions.TileSpacing"/>).</summary>
internal sealed record UndergroundTilePlan(
    int TileX,
    int TileZ,
    string CoarsenessName,
    IReadOnlyList<UndergroundSystem> Systems,
    UndergroundTileGrid? FieldGrid)
{
    /// <summary>Systems a box overlap with an earlier system kept out (round 3 census).</summary>
    internal int RejectedForOverlap { get; init; }
}

/// <summary>
/// Pure, Engine-free, deterministic underground cave-system planner (#11209).
/// Reads only the continent map's fields plus a plan-only limestone field keyed with its own salt, so no runtime field or
/// golden changes; the ground the boxes are buried under comes from a caller's ground function (the continent's smooth ground
/// when none is given, the walked ground in the golden-seed exporter). Computed per 16 km region tile.
/// <para>Round 3 rules: the rock decides where caves are (the limestone belts, independent of climate); the climate decides
/// their style; the relief to the local base level decides their depth, with stacked levels joined by shafts and a mouth
/// where each level meets the slope; and each region gets a floor of systems, filled from its best-scoring sites.</para>
/// </summary>
internal static class UndergroundPlan
{
    // Plan-only salts (independent of every runtime terrain/POI/dungeon salt).
    private const ulong LimestoneSalt = 0x7A4D9B2E5C1F8361UL;
    private const ulong LimestoneDetailSalt = 0x3E91C4A7F20B65D3UL;
    private const ulong SiteSalt = 0x5B28F019D6A4E7C5UL;
    private const ulong GrowthSalt = 0x9C4E12B837F6A051UL;
    private const ulong LinkSalt = 0x6F10A8E4B92C53D7UL;
    private const ulong DetailSalt = 0x2D73E914C8A5B06FUL;

    // Limestone field: two belts of noise, broad and lens-scale. The rock alone decides whether caves exist (round 3).
    private const double BeltWavelengthMetres = 26_000;
    private const double LensWavelengthMetres = 5_200;
    private const double BeltWeight = 0.65;
    private const double LensWeight = 0.35;
    // The noise sits about 0.5; stretching it about its centre makes belts of limestone and wide country of none.
    private const double LithologyCentre = 0.5;
    private const double LithologyContrast = 2.4;
    /// <summary>Limestone at or above this is karst country: caves may form.</summary>
    internal const double KarstThreshold = 0.55;
    private const double HighKarstThreshold = 0.8;

    // Climate decides the style (round 3).
    private const double IceTemperature = MapBiomes.TundraTemperature;
    private const double WetMoisture = 0.4;

    // Site scan and density.
    private const double MinimumLandAboveSeaMetres = 5.0;
    private const double ReferenceSpacingMetres = 256.0;
    private const double BaseSiteProbabilityAt256m = 0.0115;
    private const double KarstDensityFloor = 0.35;
    private const double KarstDensityBoost = 2.4;
    private const double ReliefDensityBoost = 1.4;
    private const double ReliefForFullBoost = 500.0;
    private static readonly double[] ReliefProbeRadii = [900.0, 1_800.0];
    private const int ReliefProbeDirections = 8;

    // A floor of systems per region (16 km tile) and biome, filled by rank where chance left fewer.
    private const double FloorKarstAreaPerSystemKm2 = 45.0;
    private const int FloorAtMostPerBiome = 6;

    // Base level: the lowest ground the system's water reaches, searched on rings about the anchor.
    private static readonly double[] BaseProbeRadii = [192.0, 384.0, 640.0, 960.0, 1_400.0];
    private const int BaseProbeDirections = 12;
    /// <summary>The base level (the stream) runs this far up the lowest level's box.</summary>
    private const float BaseLevelShareOfBox = 0.3f;

    // Burial and the box lattice.
    private const float BoxCoverMetres = 12f;
    private const float FaceCoverMetres = 4f;
    private const float MinimumBoxFloorY = -GenerationConstants.TerrainDepth + 6f;
    private const float FootprintInsetMetres = 2f;
    private const int MaxRunAlongAxis = 4;
    private const int MaxRunUpperLevels = 2;
    /// <summary>How far a stacked level is followed toward the valley looking for where it meets the slope.</summary>
    private const int MaxReachColumns = 8;
    private const double LateralBranchChance = 0.35;
    private const int MostLateralBranches = 2;

    // Mouths where a level meets the slope (round 3).
    private const float MouthPortBelowTop = 14f;
    private const float MouthHeadroomMetres = 2.5f;
    /// <summary>A hillside mouth's tendril at most (today's caves run 60 m into the hill).</summary>
    private const float MaxTendrilMetres = 100f;
    /// <summary>A resurgence's at most: the stream passage out to a valley floor that flattens out runs longer.</summary>
    private const float MaxResurgenceTendrilMetres = 240f;
    private const float TendrilStepMetres = 2f;
    /// <summary>A tendril this short through ground this steep is a cliff: the box face opens directly.</summary>
    private const float CliffTendrilMetres = 8f;
    private const float CliffSlope = 1.0f;
    private const double SinkholeChance = 0.4;
    private const double SideMouthKeepChance = 0.85;

    // Sections.
    private const float SectionStepMetres = 16f;
    private const float SectionMarginMetres = 160f;

    // Depth bands (matching the communities' tiers).
    private const double ShallowBandLimit = 30.0;
    private const double MidBandLimit = 60.0;
    private const double HighDischargeThreshold = 25.0;

    // Port geometry (so ports never collide with KarstCave's outer rind or box corners).
    private const float PortHorizontalMarginMetres = 14f;
    private const float PortVerticalMarginMetres = 9f;
    private const float MinPortRadiusMetres = 1.6f;
    private const float MaxPortRadiusMetres = 3.1f;
    private const float ShaftRadiusMin = 2.2f;
    private const float ShaftRadiusMax = 3.4f;
    private const float WaterTableMinRatio = 0.18f;
    private const float WaterTableMaxRatio = 0.55f;
    private const float DrainedTableShare = 0.12f;

    // Big-space placement probabilities per box.
    private const double LakeChanceBase = 0.22;
    private const double FogSpaceChanceBase = 0.14;
    private const double RuinChanceBase = 0.18;
    private const double GreatHallChanceBase = 0.12;

    private const float BoxApartMetres = 8f;

    // Horizontal cardinal steps (dx, dz) and the faces they leave and enter by.
    private static readonly (int Dx, int Dz, UndergroundFace OutFace, UndergroundFace InFace)[] Neighbours =
    [
        (1, 0, UndergroundFace.PosX, UndergroundFace.NegX),
        (-1, 0, UndergroundFace.NegX, UndergroundFace.PosX),
        (0, 1, UndergroundFace.PosZ, UndergroundFace.NegZ),
        (0, -1, UndergroundFace.NegZ, UndergroundFace.PosZ),
    ];

    /// <summary>
    /// Presets (#11209 round 3): <c>fine</c> is the baseline Patch took after round 2, <c>detailed</c> its comparison. Both hold
    /// box size (96x64x96 m), separation (560 m), at most 12 boxes and 5 stacked levels per system, and vary the scan lattice and
    /// the upfront interior detail. Rounds 1-2's coarse/medium/fine-64m presets are retired (their numbers are in Den).
    /// </summary>
    internal static IReadOnlyList<UndergroundCoarseness> Presets { get; } =
    [
        new(
            Name: "fine",
            Description: "Baseline: 128 m scan lattice, 96x64x96 m boxes, up to 12 boxes in up to 5 stacked levels, 4 rooms + loops per box.",
            SiteSpacingMetres: 128,
            BoxChunksX: 6,
            BoxChunksY: 4,
            BoxChunksZ: 6,
            MinSystemSeparationMetres: 560,
            MaxBoxesPerSystem: 12,
            MaxLevels: 5,
            TargetMultiBoxShare: 0.30,
            SubChambersPerBox: 4,
            IncludeFieldGrid: false),
        new(
            Name: "detailed",
            Description: "Comparison: 64 m scan lattice, 96x64x96 m boxes, up to 12 boxes in up to 5 stacked levels, 6 rooms + loops per box + per-tile 2D karst grid.",
            SiteSpacingMetres: 64,
            BoxChunksX: 6,
            BoxChunksY: 4,
            BoxChunksZ: 6,
            MinSystemSeparationMetres: 560,
            MaxBoxesPerSystem: 12,
            MaxLevels: 5,
            TargetMultiBoxShare: 0.30,
            SubChambersPerBox: 6,
            IncludeFieldGrid: true),
    ];

    internal static UndergroundCoarseness Preset(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown underground coarseness preset '{name}'. Available: {string.Join(", ", Presets.Select(p => p.Name))}.");

    /// <summary>
    /// Plan-only limestone in [0, 1] at world point (x, z): the rock alone, with no climate or hardness in it (round 3: rounds
    /// 1-2 multiplied in moisture and a hardness fit, which kept caves out of dry and mountain country). Keyed with
    /// <see cref="LimestoneSalt"/> so no existing continent map field or RNG is affected.
    /// </summary>
    internal static double SolubilityAt(WorldMap continent, double x, double z)
    {
        ulong seed = continent.Configuration.Contract.GeographyNoiseSeed;
        double belt = 0.5 + (0.5 * MapNoise.Fbm(seed ^ LimestoneSalt, x / BeltWavelengthMetres, z / BeltWavelengthMetres, octaves: 4, persistence: 0.52));
        double lens = 0.5 + (0.5 * MapNoise.Fbm(seed ^ LimestoneDetailSalt, x / LensWavelengthMetres, z / LensWavelengthMetres, octaves: 3, persistence: 0.5));
        double lithology = (BeltWeight * belt) + (LensWeight * lens);
        return Math.Clamp(LithologyCentre + ((lithology - LithologyCentre) * LithologyContrast), 0.0, 1.0);
    }

    /// <summary>The style the climate gives a system's caves (round 3).</summary>
    internal static UndergroundStyle StyleFor(MapSample anchor, bool reachesBaseLevel)
    {
        if (anchor.Temperature < IceTemperature) return UndergroundStyle.Ice;
        if (anchor.Moisture >= WetMoisture) return UndergroundStyle.ActiveRiver;
        return reachesBaseLevel ? UndergroundStyle.Relict : UndergroundStyle.Hypogene;
    }

    /// <summary>The relief about a point on the smooth continent: how far its ground stands above the lowest ground within two kilometres.</summary>
    internal static double ReliefAt(WorldMap continent, double x, double z)
    {
        double here = continent.Broad(x, z).Elevation;
        double lowest = here;
        foreach (double radius in ReliefProbeRadii)
        {
            for (int d = 0; d < ReliefProbeDirections; d++)
            {
                double angle = d * (Math.PI * 2.0 / ReliefProbeDirections);
                double px = x + (Math.Cos(angle) * radius), pz = z + (Math.Sin(angle) * radius);
                if (!continent.Contains(px, pz)) continue;
                lowest = Math.Min(lowest, continent.Broad(px, pz).Elevation);
            }
        }

        return Math.Max(0.0, here - Math.Max(GenerationConstants.WaterLevel, lowest));
    }

    private readonly record struct Candidate(
        long CellX,
        long CellZ,
        double Priority,
        double X,
        double Z,
        MapSample Sample,
        MapBiome Biome,
        double Solubility,
        double Relief,
        bool Rolled,
        bool InTile);

    /// <summary>
    /// Builds the deterministic underground plan for one 16 km region tile (<paramref name="tileX"/>, <paramref name="tileZ"/>).
    /// Pure function of its arguments. <paramref name="groundAt"/> gives the ground boxes are buried under and mouths open
    /// onto; null takes the continent's smooth ground.
    /// </summary>
    internal static UndergroundTilePlan BuildTile(WorldMap continent, int tileX, int tileZ, UndergroundCoarseness coarseness,
        Func<double, double, double>? groundAt = null)
    {
        Func<double, double, double> ground = groundAt ?? ((x, z) => continent.Broad(x, z).Elevation);
        double half = MapRegions.TileSpacing * 0.5;
        (double centreX, double centreZ) = MapRegions.Centre((tileX, tileZ));
        double minX = centreX - half;
        double minZ = centreZ - half;
        double spacing = coarseness.SiteSpacingMetres;
        int cells = Math.Max(1, (int)Math.Round(MapRegions.TileSpacing / spacing));
        // Candidates in a ring about the tile take part in the separation, so a system near a tile edge keeps clear of its
        // neighbour's: every tile sees the same candidates near the edge and orders them the same way.
        int margin = (int)Math.Ceiling((coarseness.MinSystemSeparationMetres * 1.5) / spacing);

        ulong baseSeed = continent.Configuration.Contract.GeographyNoiseSeed;
        ulong presetSalt = HashString(coarseness.Name);
        FrontierBound? bound = FrontierBound.For(continent);

        byte[]? gridSolubility = coarseness.IncludeFieldGrid ? new byte[cells * cells] : null;
        short[]? gridWaterTable = coarseness.IncludeFieldGrid ? new short[cells * cells] : null;
        byte[]? gridDepthBand = coarseness.IncludeFieldGrid ? new byte[cells * cells] : null;

        // Area-normalised rate so total cave counts stay steady as lattice spacing refines (#11209 round 2).
        double spacingRatio = spacing / ReferenceSpacingMetres;
        double baseChance = BaseSiteProbabilityAt256m * spacingRatio * spacingRatio;
        double cellKm2 = spacing * spacing / 1_000_000.0;

        List<Candidate> candidates = [];
        Dictionary<MapBiome, double> karstAreaKm2 = [];
        long tileBaseCellX = (long)Math.Floor(minX / spacing);
        long tileBaseCellZ = (long)Math.Floor(minZ / spacing);

        for (int lz = -margin; lz < cells + margin; lz++)
        {
            double z = minZ + ((lz + 0.5) * spacing);
            for (int lx = -margin; lx < cells + margin; lx++)
            {
                double x = minX + ((lx + 0.5) * spacing);
                bool inTile = lx >= 0 && lz >= 0 && lx < cells && lz < cells;
                int idx = inTile ? (lz * cells) + lx : -1;

                if (!continent.Contains(x, z))
                {
                    if (inTile && gridWaterTable is not null) gridWaterTable[idx] = short.MinValue;
                    continue;
                }

                MapSample broad = continent.Broad(x, z);
                bool dryLand = broad.Elevation >= GenerationConstants.WaterLevel + MinimumLandAboveSeaMetres;
                double solubility = dryLand ? SolubilityAt(continent, x, z) : 0.0;

                if (inTile && gridSolubility is not null)
                {
                    gridSolubility[idx] = (byte)Math.Clamp(Math.Round(solubility * 255.0), 0, 255);
                    double relief = dryLand ? ReliefAt(continent, x, z) : 0.0;
                    gridDepthBand![idx] = (byte)ClassifyDepthBand(Math.Min(relief, MidBandLimit + 1));
                    gridWaterTable![idx] = dryLand
                        ? (short)Math.Clamp(Math.Round(broad.Elevation - Math.Min(relief, broad.Elevation - GenerationConstants.WaterLevel)), short.MinValue + 1, short.MaxValue)
                        : (short)GenerationConstants.WaterLevel;
                }

                if (!dryLand) continue;
                if (bound is not null && !bound.Open(x, z)) continue;
                double lakeLevel = continent.LakeLevelAt(x, z);
                if (double.IsFinite(lakeLevel) && broad.Elevation <= lakeLevel + 1.5) continue;
                if (solubility < KarstThreshold) continue;

                MapBiome biome = MapBiomes.Classify(broad);
                if (inTile) karstAreaKm2[biome] = karstAreaKm2.GetValueOrDefault(biome) + cellKm2;

                long cx = tileBaseCellX + lx;
                long cz = tileBaseCellZ + lz;
                ulong cellHash = TerrainRecipe.CoordinateHash(baseSeed ^ SiteSalt ^ presetSalt, cx, cz);
                double wx = x + ((Unit(cellHash, 1) - 0.5) * spacing * 0.65);
                double wz = z + ((Unit(cellHash, 2) - 0.5) * spacing * 0.65);
                if (!continent.Contains(wx, wz)) continue;
                if (bound is not null && !bound.Open(wx, wz)) continue;

                double reliefHere = ReliefAt(continent, wx, wz);
                double reliefFactor = Math.Clamp(reliefHere / ReliefForFullBoost, 0.0, 1.0);
                double karstExcess = (solubility - KarstThreshold) / (1.0 - KarstThreshold);
                double probability = baseChance * (KarstDensityFloor + (KarstDensityBoost * karstExcess)) * (1.0 + (ReliefDensityBoost * reliefFactor));
                bool rolled = TerrainRecipe.HashUnit(cellHash) < Math.Min(0.95, probability);
                // The rank a region's floor fills by: the most limestone and the most relief first.
                double priority = karstExcess + (0.6 * reliefFactor) + (0.05 * Unit(cellHash, 3));
                candidates.Add(new Candidate(cx, cz, priority, wx, wz, continent.Broad(wx, wz), biome, solubility, reliefHere, rolled, inTile));
            }
        }

        static int ByRank(Candidate a, Candidate b)
        {
            int cmp = b.Priority.CompareTo(a.Priority);
            if (cmp != 0) return cmp;
            cmp = a.CellX.CompareTo(b.CellX);
            return cmp != 0 ? cmp : a.CellZ.CompareTo(b.CellZ);
        }

        candidates.Sort(ByRank);
        double minSepSq = (double)coarseness.MinSystemSeparationMetres * coarseness.MinSystemSeparationMetres;
        List<(double X, double Z)> accepted = [];
        List<UndergroundBox> claimed = [];
        List<UndergroundSystem> systems = [];
        int rejectedOverlap = 0;

        bool TooClose(Candidate c) => accepted.Any(a => ((a.X - c.X) * (a.X - c.X)) + ((a.Z - c.Z) * (a.Z - c.Z)) < minSepSq);

        bool TryPlace(Candidate c, bool fromFloor)
        {
            if (TooClose(c)) return false;
            ulong systemSeed = TerrainRecipe.CoordinateHash(baseSeed ^ GrowthSalt ^ presetSalt, c.CellX, c.CellZ);
            string id = $"ug:{coarseness.Name}:{tileX}:{tileZ}:{systems.Count}";
            UndergroundSystem system = BuildSystem(id, tileX, tileZ, systemSeed, c, continent, bound, ground, coarseness) with { FromRegionalFloor = fromFloor };
            if (system.Boxes.Any(b => claimed.Any(o => Overlaps(b, o))))
            {
                if (c.InTile) rejectedOverlap++;
                return false;
            }

            accepted.Add((c.X, c.Z));
            claimed.AddRange(system.Boxes);
            if (c.InTile) systems.Add(system);
            return true;
        }

        // The roll first, in rank order across the tile and its ring.
        foreach (Candidate c in candidates)
        {
            if (c.Rolled) TryPlace(c, fromFloor: false);
        }

        // Then each biome's floor in this tile, from its best remaining karst sites: a dry or high region still gets its caves,
        // in its most limestone-rich, highest-relief places, and none where there is no limestone.
        foreach ((MapBiome biome, double area) in karstAreaKm2.OrderBy(p => p.Key))
        {
            int floor = Math.Min(FloorAtMostPerBiome, (int)Math.Floor(area / FloorKarstAreaPerSystemKm2));
            int have = systems.Count(s => s.Biome == biome);
            foreach (Candidate c in candidates)
            {
                if (have >= floor) break;
                if (!c.InTile || c.Biome != biome || c.Rolled) continue;
                if (TryPlace(c, fromFloor: true)) have++;
            }
        }

        UndergroundTileGrid? fieldGrid = coarseness.IncludeFieldGrid
            ? new UndergroundTileGrid(cells, spacing, gridSolubility!, gridWaterTable!, gridDepthBand!)
            : null;

        return new UndergroundTilePlan(tileX, tileZ, coarseness.Name, systems, fieldGrid) { RejectedForOverlap = rejectedOverlap };
    }

    private static bool Overlaps(UndergroundBox a, UndergroundBox b) =>
        a.OriginX < b.OriginX + (b.ChunksX * 16) + BoxApartMetres && b.OriginX < a.OriginX + (a.ChunksX * 16) + BoxApartMetres
        && a.OriginZ < b.OriginZ + (b.ChunksZ * 16) + BoxApartMetres && b.OriginZ < a.OriginZ + (a.ChunksZ * 16) + BoxApartMetres
        && a.OriginY < b.TopY + BoxApartMetres && b.OriginY < a.TopY + BoxApartMetres;

    /// <summary>The local base level about an anchor: the lowest ground within reach, and the way to it.</summary>
    private static (double BaseY, double DirX, double DirZ, double Distance) BaseLevel(WorldMap continent, Func<double, double, double> ground, double x, double z, double here)
    {
        double lowest = here, dirX = 1, dirZ = 0, distance = 0;
        foreach (double radius in BaseProbeRadii)
        {
            for (int d = 0; d < BaseProbeDirections; d++)
            {
                double angle = d * (Math.PI * 2.0 / BaseProbeDirections);
                double px = x + (Math.Cos(angle) * radius), pz = z + (Math.Sin(angle) * radius);
                if (!continent.Contains(px, pz)) continue;
                double g = ground(px, pz);
                // A farther point has to be lower by more than the slope a stream falls, so the nearest valley wins over a far one
                // a little lower.
                if (g < lowest - (radius * 0.01))
                {
                    lowest = g;
                    dirX = Math.Cos(angle);
                    dirZ = Math.Sin(angle);
                    distance = radius;
                }
            }
        }

        return (Math.Max(GenerationConstants.WaterLevel, lowest), dirX, dirZ, distance);
    }

    private static UndergroundSystem BuildSystem(
        string id,
        int tileX,
        int tileZ,
        ulong systemSeed,
        Candidate site,
        WorldMap continent,
        FrontierBound? bound,
        Func<double, double, double> ground,
        UndergroundCoarseness coarseness)
    {
        int bx = coarseness.BoxMetresX, by = coarseness.BoxMetresY, bz = coarseness.BoxMetresZ;
        MapSample anchorSample = site.Sample;
        double discharge = Math.Max(0.0, continent.Grid.Bilinear(continent.Fields.Discharge, site.X, site.Z));
        double hardness = Math.Clamp(continent.HardnessAt(site.X, site.Z), 0.0, 1.0);

        // The anchor box's footprint, centred on the site and snapped to whole metres.
        double origin0X = Math.Round(site.X - (bx * 0.5));
        double origin0Z = Math.Round(site.Z - (bz * 0.5));
        double CentreX(int gx) => origin0X + ((gx + 0.5) * bx);
        double CentreZ(int gz) => origin0Z + ((gz + 0.5) * bz);

        // The ground over a box's footprint (nine samples): the least of it, its centre, and the least on the face toward the
        // valley (frontDx, frontDz), which may stand lower; with no front given, the front is the least of all nine.
        Dictionary<(int, int, int, int), (double Min, double Centre, double Front)> footprints = [];
        (double Min, double Centre, double Front) Footprint(int gx, int gz, int frontDx, int frontDz)
        {
            if (footprints.TryGetValue((gx, gz, frontDx, frontDz), out var known)) return known;
            double ox = origin0X + (gx * bx), oz = origin0Z + (gz * bz);
            double min = double.MaxValue, front = double.MaxValue;
            double centre = ground(ox + (bx * 0.5), oz + (bz * 0.5));
            for (int iz = 0; iz <= 2; iz++)
            for (int ix = 0; ix <= 2; ix++)
            {
                double px = ox + FootprintInsetMetres + (ix * (bx - (2 * FootprintInsetMetres)) * 0.5);
                double pz = oz + FootprintInsetMetres + (iz * (bz - (2 * FootprintInsetMetres)) * 0.5);
                double g = ix == 1 && iz == 1 ? centre : ground(px, pz);
                // The row of samples on the face toward the valley may stand lower: a level meets the slope there.
                bool onFront = (frontDx > 0 && ix == 2) || (frontDx < 0 && ix == 0) || (frontDz > 0 && iz == 2) || (frontDz < 0 && iz == 0);
                if (onFront) front = Math.Min(front, g);
                else min = Math.Min(min, g);
            }

            var result = (min, centre, front == double.MaxValue ? min : front);
            footprints[(gx, gz, frontDx, frontDz)] = result;
            return result;
        }

        bool Usable(int gx, int gz)
        {
            double cx = CentreX(gx), cz = CentreZ(gz);
            if (!continent.Contains(cx, cz)) return false;
            if (bound is not null && !bound.Open(cx, cz)) return false;
            return continent.Broad(cx, cz).Elevation >= GenerationConstants.WaterLevel + 3.0;
        }

        double g0 = Footprint(0, 0, 0, 0).Min;
        (double baseY, double dirX, double dirZ, double baseDistance) = BaseLevel(continent, ground, site.X, site.Z, Footprint(0, 0, 0, 0).Centre);

        // The main axis: the lattice axis nearest the way down to the base level. Levels step along it toward the valley.
        bool alongX = Math.Abs(dirX) >= Math.Abs(dirZ);
        int sx = alongX ? Math.Sign(dirX) : 0, sz = alongX ? 0 : Math.Sign(dirZ);
        if (sx == 0 && sz == 0) sx = 1;
        UndergroundFace forward = Neighbours.First(n => n.Dx == sx && n.Dz == sz).OutFace;
        UndergroundFace backward = Neighbours.First(n => n.Dx == sx && n.Dz == sz).InFace;

        // The lowest level runs at the base level (its stream a third up the box), as deep as the levels allowed reach.
        float level0Y = (float)(baseY - (by * BaseLevelShareOfBox));
        int levels = (int)Math.Floor((g0 - BoxCoverMetres - level0Y) / by);
        bool reachesBase = true;
        if (levels > coarseness.MaxLevels)
        {
            levels = coarseness.MaxLevels;
            level0Y = (float)(g0 - BoxCoverMetres - (levels * by));
            reachesBase = false;
        }

        if (levels < 1)
        {
            levels = 1;
            level0Y = (float)(g0 - BoxCoverMetres - by);
        }

        if (level0Y < MinimumBoxFloorY)
        {
            level0Y = MinimumBoxFloorY;
            levels = Math.Clamp((int)Math.Floor((g0 - BoxCoverMetres - level0Y) / by), 1, coarseness.MaxLevels);
        }

        level0Y = MathF.Round(level0Y, 1);
        float LevelY(int k) => level0Y + (k * by);

        // Whether a box at column c of level k is buried: its cover over the footprint, and a little over the face toward the valley.
        bool Buried(int c, int k)
        {
            int gx = c * sx, gz = c * sz;
            if (!Usable(gx, gz)) return false;
            (double min, _, double front) = Footprint(gx, gz, sx, sz);
            double top = LevelY(k) + by;
            return min >= top + BoxCoverMetres && front >= top + FaceCoverMetres;
        }

        // The levels as a staircase down toward the valley (top first): each level starts under the front box of the level
        // above, where a shaft joins them, and runs toward the valley while it stays buried. The lower the level, the farther
        // it is buried, so the levels step down the slope and each can meet it. A single level on flat ground runs by chance
        // (the system share).
        double multiChance = Math.Clamp(coarseness.TargetMultiBoxShare * (0.65 + (0.85 * Math.Clamp((site.Solubility - KarstThreshold) / (1.0 - KarstThreshold), 0.0, 1.0))), 0.08, 0.65);
        int[] first = new int[levels], last = new int[levels];
        bool[] metSlope = new bool[levels];
        // The box budget is spent from the top down, so the levels near the surface keep their entrances; where it runs out
        // the lower levels are left off and the system stops above its base level.
        int spent = 0, lowest = levels - 1;
        for (int k = levels - 1; k >= 0; k--)
        {
            int from = k == levels - 1 ? 0 : last[k + 1];
            int cap = levels == 1
                ? (Unit(systemSeed, 10) < multiChance ? 1 + (int)Math.Floor(Unit(systemSeed, 11) * (MaxRunAlongAxis - 1)) : 0)
                : MaxReachColumns;
            int c = from;
            while (c - from < cap && Buried(c + 1, k)) c++;
            metSlope[k] = c - from < cap && Usable((c + 1) * sx, (c + 1) * sz) && !Buried(c + 1, k);
            // A level that never met the slope keeps a short run: its length bought nothing.
            if (!metSlope[k] && levels > 1) c = from + Math.Min(c - from, k == 0 ? MaxRunAlongAxis : MaxRunUpperLevels);
            if (spent + (c - from + 1) > coarseness.MaxBoxesPerSystem)
            {
                if (k < levels - 1) break;
                c = from + coarseness.MaxBoxesPerSystem - 1;
                metSlope[k] = false;
            }

            first[k] = from;
            last[k] = c;
            spent += c - from + 1;
            lowest = k;
        }

        if (lowest > 0)
        {
            levels -= lowest;
            level0Y += lowest * by;
            first = first[lowest..];
            last = last[lowest..];
            metSlope = metSlope[lowest..];
            reachesBase = false;
        }

        int top = levels - 1;
        List<(int GX, int GY, int GZ)> cellsList = [];
        Dictionary<(int, int, int), int> indexOf = [];
        List<(int From, int To, UndergroundFace OutFace, UndergroundFace InFace)> rawEdges = [];
        int Add(int gx, int gy, int gz)
        {
            int index = cellsList.Count;
            cellsList.Add((gx, gy, gz));
            indexOf[(gx, gy, gz)] = index;
            return index;
        }

        for (int k = 0; k < levels; k++)
        {
            int previous = -1;
            for (int c = first[k]; c <= last[k]; c++)
            {
                int index = Add(c * sx, k, c * sz);
                if (previous >= 0) rawEdges.Add((previous, index, forward, backward));
                previous = index;
            }
        }

        // A shaft joins each level to the one above, under the upper level's front box.
        for (int k = 0; k + 1 < levels; k++)
        {
            int c = first[k];
            rawEdges.Add((indexOf[(c * sx, k, c * sz)], indexOf[(c * sx, k + 1, c * sz)], UndergroundFace.PosY, UndergroundFace.NegY));
        }

        // A side branch or two off the main axis where the budget and the burial allow.
        for (int branch = 0; branch < MostLateralBranches && cellsList.Count < coarseness.MaxBoxesPerSystem; branch++)
        {
            if (Unit(systemSeed, (ulong)(50 + branch)) >= LateralBranchChance) continue;
            int k = (int)Math.Floor(Unit(systemSeed, (ulong)(60 + branch)) * levels);
            int c = first[k] + (int)Math.Floor(Unit(systemSeed, (ulong)(70 + branch)) * (last[k] - first[k] + 1));
            int side = Unit(systemSeed, (ulong)(80 + branch)) < 0.5 ? -1 : 1;
            int gx = (c * sx) + (sz * side), gz = (c * sz) + (sx * side);
            if (indexOf.ContainsKey((gx, k, gz)) || !Usable(gx, gz)) continue;
            (double min, _, _) = Footprint(gx, gz, 0, 0);
            if (min < LevelY(k) + by + BoxCoverMetres) continue;
            var step = Neighbours.First(n => n.Dx == sz * side && n.Dz == sx * side);
            int from = indexOf[(c * sx, k, c * sz)];
            rawEdges.Add((from, Add(gx, k, gz), step.OutFace, step.InFace));
        }

        int count = cellsList.Count;
        UndergroundStyle style = StyleFor(anchorSample, reachesBase);
        double[] boxGround = new double[count];
        double[] boxGroundMin = new double[count];
        float[] boxOriginY = new float[count];
        float[] boxDepth = new float[count];
        float[] boxWaterLocalY = new float[count];
        ulong[] boxSeeds = new ulong[count];
        float worldWater = style == UndergroundStyle.ActiveRiver
            ? (float)baseY + 1f
            : level0Y + (by * DrainedTableShare);
        for (int i = 0; i < count; i++)
        {
            (int gx, int gy, int gz) = cellsList[i];
            (double min, double centre, _) = Footprint(gx, gz, 0, 0);
            boxGround[i] = centre;
            boxGroundMin[i] = min;
            boxOriginY[i] = LevelY(gy);
            boxDepth[i] = (float)Math.Max(0.0, centre - (boxOriginY[i] + (by * 0.5)));
            float localWaterY = Math.Clamp(worldWater - boxOriginY[i], by * WaterTableMinRatio, by * WaterTableMaxRatio);
            boxWaterLocalY[i] = MathF.Round(localWaterY, 1);
            boxSeeds[i] = Mix(systemSeed, (ulong)(0x100 + i));
        }

        // Links with exact matching world coordinates strictly inside both boxes.
        List<UndergroundLink> links = new(rawEdges.Count);
        for (int e = 0; e < rawEdges.Count; e++)
        {
            var edge = rawEdges[e];
            ulong edgeSeed = Mix(systemSeed ^ LinkSalt, (ulong)(edge.From * 64 + edge.To));
            double fromOriginX = origin0X + (cellsList[edge.From].GX * bx);
            double fromOriginZ = origin0Z + (cellsList[edge.From].GZ * bz);
            float waterWorldY = MathF.Round((boxOriginY[edge.From] + boxWaterLocalY[edge.From] + boxOriginY[edge.To] + boxWaterLocalY[edge.To]) * 0.5f, 1);

            if (edge.OutFace is UndergroundFace.PosY)
            {
                float u = MathF.Round(PortHorizontalMarginMetres + ((float)Unit(edgeSeed, 1) * (bx - (2f * PortHorizontalMarginMetres))), 1);
                float v = MathF.Round(PortHorizontalMarginMetres + ((float)Unit(edgeSeed, 2) * (bz - (2f * PortHorizontalMarginMetres))), 1);
                float radius = MathF.Round(ShaftRadiusMin + ((float)Unit(edgeSeed, 3) * (ShaftRadiusMax - ShaftRadiusMin)), 2);
                links.Add(new UndergroundLink(edge.From, edge.To, edge.OutFace, edge.InFace, u, v,
                    (float)(fromOriginX + u), boxOriginY[edge.From] + by, (float)(fromOriginZ + v), radius, waterWorldY, UndergroundLinkKind.Shaft, edgeSeed));
                continue;
            }

            float lowWorldY = boxOriginY[edge.From] + PortVerticalMarginMetres;
            float highWorldY = boxOriginY[edge.From] + by - PortVerticalMarginMetres;
            bool xFace = edge.OutFace is UndergroundFace.PosX or UndergroundFace.NegX;
            float faceSpan = xFace ? bz : bx;
            float fu = MathF.Round(PortHorizontalMarginMetres + ((float)Unit(edgeSeed, 1) * MathF.Max(4f, faceSpan - (2f * PortHorizontalMarginMetres))), 1);
            float worldY = MathF.Round(lowWorldY + ((float)Unit(edgeSeed, 2) * (highWorldY - lowWorldY)), 1);
            float faceV = MathF.Round(worldY - boxOriginY[edge.From], 1);
            float worldX = edge.OutFace switch
            {
                UndergroundFace.PosX => (float)(fromOriginX + bx),
                UndergroundFace.NegX => (float)fromOriginX,
                _ => (float)(fromOriginX + fu),
            };
            float worldZ = edge.OutFace switch
            {
                UndergroundFace.PosZ => (float)(fromOriginZ + bz),
                UndergroundFace.NegZ => (float)fromOriginZ,
                _ => (float)(fromOriginZ + fu),
            };
            float portRadius = MathF.Round(MinPortRadiusMetres + ((float)Unit(edgeSeed, 3) * (MaxPortRadiusMetres - MinPortRadiusMetres)), 2);
            UndergroundLinkKind kind = style is UndergroundStyle.ActiveRiver && worldY < waterWorldY - 1.5f
                ? UndergroundLinkKind.WetSump
                : style is UndergroundStyle.ActiveRiver && worldY < waterWorldY + 3.0f
                    ? UndergroundLinkKind.StreamTube
                    : Unit(edgeSeed, 4) < 0.22
                        ? UndergroundLinkKind.TallRift
                        : UndergroundLinkKind.DryPassage;
            links.Add(new UndergroundLink(edge.From, edge.To, edge.OutFace, edge.InFace, fu, faceV, worldX, worldY, worldZ, portRadius, waterWorldY, kind, edgeSeed));
        }

        // Mouths: where a level met the slope, a tendril from its last box's valley face to the hillside (the lowest level's is
        // the resurgence); a face that meets a cliff opens directly. A sinkhole on the top box where chance has one or no level
        // reached the slope.
        List<UndergroundMouth> mouths = [];
        for (int k = 0; k < levels; k++)
        {
            if (!metSlope[k]) continue;
            if (k > 0 && Unit(systemSeed, (ulong)(90 + k)) >= SideMouthKeepChance) continue;
            int box = indexOf[(last[k] * sx, k, last[k] * sz)];
            // Only the level at the base level carries the system's water out: a resurgence.
            bool resurgence = k == 0 && reachesBase;
            double ox = origin0X + (cellsList[box].GX * bx), oz = origin0Z + (cellsList[box].GZ * bz);
            ulong ms = Mix(systemSeed, (ulong)(0x500 + k));
            float faceSpan = alongX ? bz : bx;
            float u = MathF.Round(PortHorizontalMarginMetres + ((float)Unit(ms, 1) * (faceSpan - (2f * PortHorizontalMarginMetres))), 1);
            float v = resurgence ? MathF.Round(Math.Clamp((float)baseY - boxOriginY[box] + 2f, PortVerticalMarginMetres, by - MouthPortBelowTop), 1) : by - MouthPortBelowTop;
            float portY = boxOriginY[box] + v;
            double faceX = sx > 0 ? ox + bx : sx < 0 ? ox : ox + u;
            double faceZ = sz > 0 ? oz + bz : sz < 0 ? oz : oz + u;
            float most = resurgence ? MaxResurgenceTendrilMetres : MaxTendrilMetres;
            (float tendril, double outX, double outZ, double outY, float slope) = Tendril(ground, faceX, faceZ, sx, sz, portY, most);
            if (tendril > most) continue;
            UndergroundMouthKind kind = tendril <= CliffTendrilMetres && slope >= CliffSlope
                ? UndergroundMouthKind.Cliff
                : resurgence ? UndergroundMouthKind.Resurgence : UndergroundMouthKind.Hillside;
            mouths.Add(new UndergroundMouth(box, forward, u, v, portY, Math.Round(outX, 1), Math.Round(outY, 1), Math.Round(outZ, 1),
                MathF.Round(tendril, 1), MathF.Round(slope, 2), k, kind));
        }

        if (mouths.Count == 0 || Unit(systemSeed, 30) < SinkholeChance)
        {
            int topBox = indexOf[(first[top] * sx, top, first[top] * sz)];
            ulong ms = Mix(systemSeed, 0x5FFUL);
            float u = MathF.Round(PortHorizontalMarginMetres + ((float)Unit(ms, 1) * (bx - (2f * PortHorizontalMarginMetres))), 1);
            float v = MathF.Round(PortHorizontalMarginMetres + ((float)Unit(ms, 2) * (bz - (2f * PortHorizontalMarginMetres))), 1);
            double wx = origin0X + (cellsList[topBox].GX * bx) + u, wz = origin0Z + (cellsList[topBox].GZ * bz) + v;
            double g = ground(wx, wz);
            float topY = boxOriginY[topBox] + by;
            mouths.Add(new UndergroundMouth(topBox, UndergroundFace.PosY, u, v, topY, Math.Round(wx, 1), Math.Round(g, 1), Math.Round(wz, 1),
                MathF.Round((float)(g - topY), 1), (float)Math.Round(SlopeAt(ground, wx, wz), 2), levels - 1, UndergroundMouthKind.Sinkhole));
        }

        // Big-space markers (lakes, fog spaces, ruins inside natural caves, great halls).
        List<UndergroundBigSpace> bigSpaces = [];
        for (int i = 0; i < count; i++)
        {
            ulong s = Mix(boxSeeds[i], 0xB165UL);
            float lx = MathF.Round((bx * 0.28f) + ((float)Unit(s, 1) * bx * 0.44f), 1);
            float lz = MathF.Round((bz * 0.28f) + ((float)Unit(s, 2) * bz * 0.44f), 1);
            bool wet = style == UndergroundStyle.ActiveRiver;
            if (wet && Unit(s, 3) < LakeChanceBase * (0.6 + anchorSample.Moisture))
            {
                float ly = MathF.Round(Math.Clamp(boxWaterLocalY[i] + 1.5f, 6f, by - 10f), 1);
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.UndergroundLake, lx, ly, lz, MathF.Round(8f + ((float)Unit(s, 4) * 10f), 1)));
            }
            else if (boxDepth[i] >= ShallowBandLimit && Unit(s, 5) < FogSpaceChanceBase)
            {
                float ly = MathF.Round((by * 0.35f) + ((float)Unit(s, 6) * by * 0.25f), 1);
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.FogSpace, lx, ly, lz, MathF.Round(9f + ((float)Unit(s, 7) * 9f), 1)));
            }
            else if (Unit(s, 8) < RuinChanceBase)
            {
                float ly = MathF.Round(Math.Clamp(boxWaterLocalY[i] + 6f, 10f, by - 10f), 1);
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.CaveRuin, lx, ly, lz, MathF.Round(7f + ((float)Unit(s, 9) * 6f), 1)));
            }
            else if (count >= 2 && (site.Solubility >= HighKarstThreshold || style == UndergroundStyle.Relict) && Unit(s, 10) < GreatHallChanceBase)
            {
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.GreatHall, lx, MathF.Round(by * 0.5f, 1), lz, MathF.Round(12f + ((float)Unit(s, 11) * 10f), 1)));
            }
        }

        // Final boxes (with upfront rooms and conduits).
        List<UndergroundBox> boxes = new(count);
        float maxDepth = 0f;
        for (int i = 0; i < count; i++)
        {
            double ox = origin0X + (cellsList[i].GX * bx);
            double oz = origin0Z + (cellsList[i].GZ * bz);
            maxDepth = MathF.Max(maxDepth, (float)(boxGround[i] - boxOriginY[i]));
            (IReadOnlyList<UndergroundRoom> rooms, IReadOnlyList<UndergroundConduit> conduits) =
                coarseness.SubChambersPerBox > 0
                    ? BuildBoxInterior(i, bx, by, bz, boxWaterLocalY[i], boxSeeds[i], coarseness.SubChambersPerBox, mouths, bigSpaces)
                    : (Array.Empty<UndergroundRoom>(), Array.Empty<UndergroundConduit>());
            boxes.Add(new UndergroundBox(
                i, cellsList[i].GX, cellsList[i].GY, cellsList[i].GZ,
                ox, boxOriginY[i], oz,
                coarseness.BoxChunksX, coarseness.BoxChunksY, coarseness.BoxChunksZ,
                (float)Math.Round(boxGround[i], 1),
                (float)Math.Round(boxGroundMin[i], 1),
                (float)Math.Round(boxDepth[i], 1),
                boxWaterLocalY[i],
                ClassifyDepthBand(boxDepth[i]),
                boxSeeds[i],
                rooms,
                conduits));
        }

        // The ground along the main axis from a box behind the anchor to beyond the farthest box and its tendrils.
        float axisLength = alongX ? bx : bz;
        float start = -(axisLength * 0.5f) - SectionMarginMetres;
        float end = ((last.Take(levels).Max() + 0.5f) * axisLength) + SectionMarginMetres;
        List<float> profile = [];
        for (float s = start; s <= end; s += SectionStepMetres)
        {
            double px = site.X + (sx * s), pz = site.Z + (sz * s);
            profile.Add((float)Math.Round(continent.Contains(px, pz) ? ground(px, pz) : GenerationConstants.WaterLevel, 1));
        }

        // The anchor box is centred on the site to within the metre snap, so the section's metres count from the site.
        return new UndergroundSystem(
            id, tileX, tileZ, systemSeed,
            Math.Round(site.X, 1), Math.Round(site.Z, 1),
            (float)Math.Round(Footprint(0, 0, 0, 0).Centre, 1),
            site.Biome,
            (float)Math.Round(hardness, 3),
            (float)Math.Round(site.Solubility, 3),
            (float)Math.Round(discharge, 2),
            (float)Math.Round(anchorSample.Temperature, 3),
            (float)Math.Round(anchorSample.Moisture, 3),
            (float)Math.Round(maxDepth, 1),
            boxes,
            links,
            mouths,
            bigSpaces)
        {
            Style = style,
            BaseLevelY = (float)Math.Round(baseY, 1),
            ReliefMetres = (float)Math.Round(Math.Max(0.0, g0 - baseY), 1),
            Levels = levels,
            ReachesBaseLevel = reachesBase,
            Section = new UndergroundSection(forward, start, SectionStepMetres, profile),
        };
    }

    /// <summary>
    /// From a point on a box face, along the face's outward axis at a port's height: how far until the ground stands below the
    /// port (with headroom) - the tendril - where it comes out, and the mean slope the ground fell over it.
    /// </summary>
    private static (float Metres, double X, double Z, double Y, float Slope) Tendril(Func<double, double, double> ground, double faceX, double faceZ, int sx, int sz, float portY, float most)
    {
        double atFace = ground(faceX, faceZ);
        for (float s = 0f; s <= most + TendrilStepMetres; s += TendrilStepMetres)
        {
            double px = faceX + (sx * s), pz = faceZ + (sz * s);
            double g = ground(px, pz);
            if (g <= portY + MouthHeadroomMetres)
            {
                float slope = (float)((atFace - g) / Math.Max(TendrilStepMetres, s));
                return (s, px, pz, g, slope);
            }
        }

        return (float.MaxValue, faceX, faceZ, atFace, 0f);
    }

    private static double SlopeAt(Func<double, double, double> ground, double x, double z)
    {
        const double probe = 8.0;
        double hx = ground(x + probe, z) - ground(x - probe, z);
        double hz = ground(x, z + probe) - ground(x, z - probe);
        return Math.Sqrt((hx * hx) + (hz * hz)) / (2.0 * probe);
    }

    private static (IReadOnlyList<UndergroundRoom> Rooms, IReadOnlyList<UndergroundConduit> Conduits) BuildBoxInterior(
        int boxIndex,
        int bx,
        int by,
        int bz,
        float waterLocalY,
        ulong boxSeed,
        int roomCount,
        IReadOnlyList<UndergroundMouth> mouths,
        IReadOnlyList<UndergroundBigSpace> bigSpaces)
    {
        List<UndergroundRoom> rooms = new(roomCount);
        List<UndergroundConduit> conduits = [];
        ulong seed = boxSeed ^ DetailSalt;

        // First room anchors at the box's mouth or first big space if present, else a chamber.
        UndergroundMouth? mouth = mouths.FirstOrDefault(m => m.BoxIndex == boxIndex);
        UndergroundBigSpace? big = bigSpaces.FirstOrDefault(b => b.BoxIndex == boxIndex);

        for (int r = 0; r < roomCount; r++)
        {
            ulong rs = Mix(seed, (ulong)(r + 1));
            float x, y, z, rx, ry, rz;
            UndergroundRoomKind kind;
            if (r == 0 && mouth is not null)
            {
                kind = UndergroundRoomKind.Shaft;
                (x, y, z) = mouth.Face switch
                {
                    UndergroundFace.PosY => (mouth.PortU, by * 0.72f, mouth.PortV),
                    UndergroundFace.PosX => (bx - 8f, mouth.PortV, mouth.PortU),
                    UndergroundFace.NegX => (8f, mouth.PortV, mouth.PortU),
                    UndergroundFace.PosZ => (mouth.PortU, mouth.PortV, bz - 8f),
                    _ => (mouth.PortU, mouth.PortV, 8f),
                };
                y = MathF.Round(y, 1);
                rx = 4.5f; ry = 5.5f; rz = 4.5f;
            }
            else if (r == 1 && big is not null)
            {
                kind = big.Kind == UndergroundBigSpaceKind.GreatHall ? UndergroundRoomKind.Hall
                    : big.Kind == UndergroundBigSpaceKind.UndergroundLake ? UndergroundRoomKind.Pool
                    : UndergroundRoomKind.Chamber;
                x = big.LocalX;
                y = big.LocalY;
                z = big.LocalZ;
                rx = big.RadiusMetres;
                ry = MathF.Round(Math.Clamp(big.RadiusMetres * 0.55f, 4f, by * 0.35f), 1);
                rz = big.RadiusMetres;
            }
            else
            {
                x = MathF.Round(12f + ((float)Unit(rs, 1) * MathF.Max(8f, bx - 24f)), 1);
                z = MathF.Round(12f + ((float)Unit(rs, 2) * MathF.Max(8f, bz - 24f)), 1);
                y = MathF.Round(10f + ((float)Unit(rs, 3) * MathF.Max(8f, by - 22f)), 1);
                double roleRoll = Unit(rs, 4);
                kind = y <= waterLocalY + 2f ? UndergroundRoomKind.Pool
                    : roleRoll < 0.35 ? UndergroundRoomKind.Chamber
                    : roleRoll < 0.70 ? UndergroundRoomKind.Junction
                    : UndergroundRoomKind.Pocket;
                rx = MathF.Round(3.5f + ((float)Unit(rs, 5) * 5.5f), 1);
                ry = MathF.Round(2.6f + ((float)Unit(rs, 6) * 3.8f), 1);
                rz = MathF.Round(3.5f + ((float)Unit(rs, 7) * 5.5f), 1);
            }

            rooms.Add(new UndergroundRoom(r, kind, x, y, z, rx, ry, rz, waterLocalY));
        }

        // Connect rooms via Prim's MST so the interior graph is fully connected.
        bool[] inTree = new bool[rooms.Count];
        inTree[0] = true;
        for (int added = 1; added < rooms.Count; added++)
        {
            int bestA = 0, bestB = 0;
            float bestDistSq = float.MaxValue;
            for (int a = 0; a < rooms.Count; a++)
            {
                if (!inTree[a]) continue;
                for (int b = 0; b < rooms.Count; b++)
                {
                    if (inTree[b]) continue;
                    float d2 = Vector3.DistanceSquared(new Vector3(rooms[a].X, rooms[a].Y, rooms[a].Z), new Vector3(rooms[b].X, rooms[b].Y, rooms[b].Z));
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            inTree[bestB] = true;
            float radius = MathF.Round(1.4f + ((float)Unit(Mix(seed, (ulong)(100 + added)), 1) * 1.4f), 2);
            conduits.Add(new UndergroundConduit(bestA, bestB, radius, waterLocalY, IsLoop: false));
        }

        // At finer/detailed levels (4+ rooms), add 1-2 anastomosing karst loop passages.
        if (rooms.Count >= 4)
        {
            int loops = rooms.Count >= 6 ? 2 : 1;
            for (int l = 0; l < loops; l++)
            {
                int a = (int)(Mix(seed, (ulong)(200 + l)) % (ulong)rooms.Count);
                int b = (a + 2 + l) % rooms.Count;
                if (a != b && !conduits.Any(c => (c.FromRoom == a && c.ToRoom == b) || (c.FromRoom == b && c.ToRoom == a)))
                {
                    float radius = MathF.Round(1.1f + ((float)Unit(Mix(seed, (ulong)(300 + l)), 1) * 1.1f), 2);
                    conduits.Add(new UndergroundConduit(a, b, radius, waterLocalY, IsLoop: true));
                }
            }
        }

        return (rooms, conduits);
    }

    private static UndergroundDepthBand ClassifyDepthBand(double depthMetres) =>
        depthMetres < ShallowBandLimit ? UndergroundDepthBand.Shallow
        : depthMetres < MidBandLimit ? UndergroundDepthBand.Mid
        : UndergroundDepthBand.Deep;

    /// <summary>Whether a system's discharge is a river's (for census and resurgence labelling).</summary>
    internal static bool HighDischarge(UndergroundSystem s) => s.Discharge >= HighDischargeThreshold;

    private static ulong Mix(ulong a, ulong b)
    {
        ulong x = a ^ (b * 0x9E3779B97F4A7C15UL);
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static double Unit(ulong seed, ulong stream) => TerrainRecipe.HashUnit(Mix(seed, stream));

    private static ulong HashString(string text)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (char c in text)
        {
            h ^= c;
            h *= 0x100000001B3UL;
        }
        return h;
    }
}
