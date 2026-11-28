using System.Numerics;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Configurable coarseness preset for the continent-scale underground cave-system plan (#11209).
/// Explores the trade-off between coarse system skeletons and mildly detailed upfront underground layers.
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

/// <summary>Face of a box where an inter-box passage link crosses.</summary>
internal enum UndergroundFace : byte
{
    NegX = 0,
    PosX = 1,
    NegZ = 2,
    PosZ = 3,
}

/// <summary>Character of a link between two neighbouring boxes.</summary>
internal enum UndergroundLinkKind : byte
{
    DryPassage = 0,
    StreamTube = 1,
    WetSump = 2,
    TallRift = 3,
}

/// <summary>How a cave system breaks to the surface.</summary>
internal enum UndergroundMouthKind : byte
{
    Hillside = 0,
    Sinkhole = 1,
    Resurgence = 2,
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
/// One contiguous chunk-aligned box belonging to a planned cave system.
/// Neighbouring boxes in the same system differ by +/-1 on one horizontal lattice axis so their faces touch with zero gap.
/// </summary>
internal sealed record UndergroundBox(
    int Index,
    int GridX,
    int GridZ,
    double OriginX,
    double OriginY,
    double OriginZ,
    int ChunksX,
    int ChunksY,
    int ChunksZ,
    float GroundElevation,
    float DepthBelowSurface,
    float WaterTableLocalY,
    UndergroundDepthBand DepthBand,
    ulong BoxSeed,
    IReadOnlyList<UndergroundRoom> Rooms,
    IReadOnlyList<UndergroundConduit> Conduits);

/// <summary>
/// An explicit passage port joining two neighbouring boxes in a system across their shared face.
/// Both boxes agree on exact world coordinates, face offset, radius, water level, and deterministic seam seed.
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

/// <summary>A surface opening belonging to a planned cave system.</summary>
internal sealed record UndergroundMouth(
    int BoxIndex,
    double WorldX,
    double WorldY,
    double WorldZ,
    float LocalEntryX,
    float LocalEntryZ,
    UndergroundMouthKind Kind);

/// <summary>A major underground landmark (lake, fog space, cave-hosted ruin, or great hall) placed inside a box.</summary>
internal sealed record UndergroundBigSpace(
    int BoxIndex,
    UndergroundBigSpaceKind Kind,
    float LocalX,
    float LocalY,
    float LocalZ,
    float RadiusMetres);

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
    UndergroundTileGrid? FieldGrid);

/// <summary>
/// Pure, Engine-free, deterministic underground cave-system planner (#11209).
/// Reads only the continent map's fields (elevation, hardness, discharge, lakes/sinks, climate/biome, frontier)
/// plus a plan-only limestone solubility field keyed with its own salt so zero runtime fields or goldens change.
/// Computed per 16 km region tile so any level of coarseness can be benchmarked and inspected in the CMS.
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

    // Limestone / solubility field tuning.
    private const double BeltWavelengthMetres = 26_000;
    private const double LensWavelengthMetres = 5_200;
    private const double BeltWeight = 0.65;
    private const double LensWeight = 0.35;
    private const double HardnessIdealCentre = 0.56;
    private const double HardnessIdealHalfWidth = 0.36;
    private const double MoistureDissolutionFloor = 0.45;
    private const double MoistureDissolutionSpan = 0.55;
    private const double KarstThreshold = 0.32;
    private const double HighKarstThreshold = 0.62;

    // Surface & depth constraints (respecting GenerationConstants.WaterLevel and CaveCommunities depth bands).
    private const double MinimumLandAboveSeaMetres = 5.0;
    private const double SlopeProbeMetres = 96.0;
    private const double HillsideSlopeMin = 0.05;
    private const double LakeSinkSearchMetres = 900.0;
    private const double HighDischargeThreshold = 25.0;
    private const double ShallowBandLimit = 30.0;
    private const double MidBandLimit = 60.0;
    private const float MostDepthMetres = 90f;
    private const double DepthFromHeight = 0.75;
    private const double DepthFromRock = 0.25;
    private const float BoxCoverMetres = 10f;
    private const float MinimumBoxFloorY = -GenerationConstants.TerrainDepth + 6f;

    // Candidate density calibration (calibrated around the 256 m reference cell so system density stays realistic).
    private const double ReferenceSpacingMetres = 256.0;
    private const double BaseSiteProbabilityAt256m = 0.011;
    private const double SolubilityDensityBoost = 2.6;
    private const double DischargeDensityBoost = 0.55;
    private const double SlopeDensityBoost = 0.45;
    private const double FineDetailDiscoveryPower = 0.35;

    // Box geometry & port margins (so ports never collide with KarstCave's outer rind or box corners).
    private const float PortHorizontalMarginMetres = 14f;
    private const float PortVerticalMarginMetres = 9f;
    private const float MinPortRadiusMetres = 1.6f;
    private const float MaxPortRadiusMetres = 3.1f;
    private const float MouthInsetMetres = 14f;
    private const float WaterTableMinRatio = 0.18f;
    private const float WaterTableMaxRatio = 0.55f;

    // Big-space placement probabilities per system.
    private const double LakeChanceBase = 0.22;
    private const double FogSpaceChanceBase = 0.14;
    private const double RuinChanceBase = 0.18;
    private const double GreatHallChanceBase = 0.12;

    // Horizontal cardinal steps for box-to-box growth.
    private static readonly (int Dx, int Dz, UndergroundFace OutFace, UndergroundFace InFace)[] Neighbours =
    [
        (1, 0, UndergroundFace.PosX, UndergroundFace.NegX),
        (-1, 0, UndergroundFace.NegX, UndergroundFace.PosX),
        (0, 1, UndergroundFace.PosZ, UndergroundFace.NegZ),
        (0, -1, UndergroundFace.NegZ, UndergroundFace.PosZ),
    ];

    /// <summary>
    /// Four coarseness levels spanning macro system skeletons through a mildly detailed upfront underground layer (#11209).
    /// </summary>
    internal static IReadOnlyList<UndergroundCoarseness> Presets { get; } =
    [
        new(
            Name: "coarse",
            Description: "Macro skeleton: 512 m scan lattice, 96x64x96 m boxes, 1-4 boxes/system, boxes + links + mouths only.",
            SiteSpacingMetres: 512,
            BoxChunksX: 6,
            BoxChunksY: 4,
            BoxChunksZ: 6,
            MinSystemSeparationMetres: 900,
            MaxBoxesPerSystem: 4,
            TargetMultiBoxShare: 0.25,
            SubChambersPerBox: 0,
            IncludeFieldGrid: false),
        new(
            Name: "medium",
            Description: "Consensus baseline: 256 m lattice, 96x64x96 m boxes, 1-5 boxes/system (~70/30 split), primary waypoints & water table.",
            SiteSpacingMetres: 256,
            BoxChunksX: 6,
            BoxChunksY: 4,
            BoxChunksZ: 6,
            MinSystemSeparationMetres: 560,
            MaxBoxesPerSystem: 5,
            TargetMultiBoxShare: 0.30,
            SubChambersPerBox: 2,
            IncludeFieldGrid: false),
        new(
            Name: "fine",
            Description: "Finer fit: 128 m lattice, 64x48x64 m modular boxes, 1-6 boxes/system, multi-chamber & branch graph per box.",
            SiteSpacingMetres: 128,
            BoxChunksX: 4,
            BoxChunksY: 3,
            BoxChunksZ: 4,
            MinSystemSeparationMetres: 360,
            MaxBoxesPerSystem: 6,
            TargetMultiBoxShare: 0.35,
            SubChambersPerBox: 4,
            IncludeFieldGrid: false),
        new(
            Name: "detailed",
            Description: "Mildly detailed upfront layer: 64 m lattice, 64x48x64 m boxes, 1-8 boxes/system, full room/conduit/loop graph + per-tile 2D karst field.",
            SiteSpacingMetres: 64,
            BoxChunksX: 4,
            BoxChunksY: 3,
            BoxChunksZ: 4,
            MinSystemSeparationMetres: 260,
            MaxBoxesPerSystem: 8,
            TargetMultiBoxShare: 0.40,
            SubChambersPerBox: 6,
            IncludeFieldGrid: true),
    ];

    internal static UndergroundCoarseness Preset(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown underground coarseness preset '{name}'. Available: {string.Join(", ", Presets.Select(p => p.Name))}.");

    /// <summary>
    /// Plan-only limestone / carbonate solubility in [0, 1] at world point (x, z).
    /// Keyed with <see cref="LimestoneSalt"/> so no existing continent map field or RNG is affected.
    /// </summary>
    internal static double SolubilityAt(WorldMap continent, double x, double z)
    {
        ulong seed = continent.Configuration.Contract.GeographyNoiseSeed;
        double belt = 0.5 + (0.5 * MapNoise.Fbm(seed ^ LimestoneSalt, x / BeltWavelengthMetres, z / BeltWavelengthMetres, octaves: 4, persistence: 0.52));
        double lens = 0.5 + (0.5 * MapNoise.Fbm(seed ^ LimestoneDetailSalt, x / LensWavelengthMetres, z / LensWavelengthMetres, octaves: 3, persistence: 0.5));
        double lithology = Math.Clamp((BeltWeight * belt) + (LensWeight * lens), 0.0, 1.0);

        // Carbonate rock forms in cohesive mid-to-firm strata rather than loose mud or crystalline granite peaks.
        double hardness = Math.Clamp(continent.HardnessAt(x, z), 0.0, 1.0);
        double rockFit = 1.0 - Math.Clamp(Math.Abs(hardness - HardnessIdealCentre) / HardnessIdealHalfWidth, 0.0, 1.0);
        rockFit = 0.35 + (0.65 * rockFit);

        MapSample sample = continent.Broad(x, z);
        double dissolution = MoistureDissolutionFloor + (MoistureDissolutionSpan * Math.Clamp(sample.Moisture, 0.0, 1.0));
        double score = lithology * rockFit * dissolution * 1.35;
        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>
    /// Builds the deterministic underground plan for one 16 km region tile (<paramref name="tileX"/>, <paramref name="tileZ"/>).
    /// Pure function of (<paramref name="continent"/>, <paramref name="tileX"/>, <paramref name="tileZ"/>, <paramref name="coarseness"/>).
    /// </summary>
    internal static UndergroundTilePlan BuildTile(WorldMap continent, int tileX, int tileZ, UndergroundCoarseness coarseness)
    {
        double half = MapRegions.TileSpacing * 0.5;
        (double centreX, double centreZ) = MapRegions.Centre((tileX, tileZ));
        double minX = centreX - half;
        double minZ = centreZ - half;
        double spacing = coarseness.SiteSpacingMetres;
        int cells = Math.Max(1, (int)Math.Round(MapRegions.TileSpacing / spacing));

        ulong baseSeed = continent.Configuration.Contract.GeographyNoiseSeed;
        ulong presetSalt = HashString(coarseness.Name);
        FrontierBound? bound = FrontierBound.For(continent);
        double peak = Math.Max(1.0, continent.Scale.PeakElevation);

        byte[]? gridSolubility = coarseness.IncludeFieldGrid ? new byte[cells * cells] : null;
        short[]? gridWaterTable = coarseness.IncludeFieldGrid ? new short[cells * cells] : null;
        byte[]? gridDepthBand = coarseness.IncludeFieldGrid ? new byte[cells * cells] : null;

        // Area-normalised candidate rate with a mild fine-scale discovery bonus so finer lattices resolve smaller pockets
        // without exploding system density linearly with cell count.
        double spacingRatio = spacing / ReferenceSpacingMetres;
        double areaScale = spacingRatio * spacingRatio;
        double discoveryFactor = Math.Pow(ReferenceSpacingMetres / spacing, FineDetailDiscoveryPower);
        double baseChance = BaseSiteProbabilityAt256m * areaScale * discoveryFactor;

        List<(long CellX, long CellZ, double Priority, double X, double Z, MapSample Sample, MapBiome Biome, double Hardness, double Solubility, double Discharge, double Slope, bool NearLake)> candidates = [];

        long tileBaseCellX = (long)Math.Floor(minX / spacing);
        long tileBaseCellZ = (long)Math.Floor(minZ / spacing);

        for (int lz = 0; lz < cells; lz++)
        {
            double z = minZ + ((lz + 0.5) * spacing);
            for (int lx = 0; lx < cells; lx++)
            {
                double x = minX + ((lx + 0.5) * spacing);
                int idx = (lz * cells) + lx;

                if (!continent.Contains(x, z))
                {
                    if (gridWaterTable is not null) gridWaterTable[idx] = short.MinValue;
                    continue;
                }

                MapSample broad = continent.Broad(x, z);
                double elevation = broad.Elevation;
                bool dryLand = elevation >= GenerationConstants.WaterLevel + MinimumLandAboveSeaMetres;
                double solubility = dryLand ? SolubilityAt(continent, x, z) : 0.0;

                if (gridSolubility is not null)
                {
                    gridSolubility[idx] = (byte)Math.Clamp(Math.Round(solubility * 255.0), 0, 255);
                    double depth = EstimateDepthMetres(elevation, peak, broad.Rock);
                    gridDepthBand![idx] = (byte)ClassifyDepthBand(depth);
                    gridWaterTable![idx] = dryLand
                        ? (short)Math.Clamp(Math.Round(EstimateWorldWaterTable(elevation, broad.Moisture, depth)), short.MinValue + 1, short.MaxValue)
                        : (short)GenerationConstants.WaterLevel;
                }

                if (!dryLand) continue;
                if (bound is not null && !bound.Open(x, z)) continue;

                // Keep natural cave mouths out of open lake beds.
                double lakeLevel = continent.LakeLevelAt(x, z);
                if (double.IsFinite(lakeLevel) && elevation <= lakeLevel + 1.5) continue;

                MapBiome biome = MapBiomes.Classify(broad);
                double biomeWeight = BiomeKarstMultiplier(biome);
                if (biomeWeight <= 0.0) continue;
                if (solubility < KarstThreshold) continue;

                long cx = tileBaseCellX + lx;
                long cz = tileBaseCellZ + lz;
                ulong cellHash = TerrainRecipe.CoordinateHash(baseSeed ^ SiteSalt ^ presetSalt, cx, cz);

                // Jitter anchor slightly inside cell so sites do not sit on an artificial grid.
                double jitterX = (Unit(cellHash, 1) - 0.5) * spacing * 0.65;
                double jitterZ = (Unit(cellHash, 2) - 0.5) * spacing * 0.65;
                double wx = x + jitterX;
                double wz = z + jitterZ;
                if (!continent.Contains(wx, wz)) continue;
                if (bound is not null && !bound.Open(wx, wz)) continue;

                double discharge = Math.Max(0.0, continent.Grid.Bilinear(continent.Fields.Discharge, wx, wz));
                double dischargeFactor = Math.Clamp(Math.Log10(1.0 + discharge) / 2.2, 0.0, 1.0);
                double slope = SurfaceSlope(continent, wx, wz);
                double slopeFactor = Math.Clamp(slope / 0.22, 0.0, 1.0);
                bool nearLake = NearLakeMargin(continent, wx, wz);

                double karstExcess = (solubility - KarstThreshold) / (1.0 - KarstThreshold);
                double probability = baseChance
                    * biomeWeight
                    * (0.45 + (SolubilityDensityBoost * karstExcess))
                    * (1.0 + (DischargeDensityBoost * dischargeFactor) + (SlopeDensityBoost * slopeFactor) + (nearLake ? 0.35 : 0.0));

                double roll = TerrainRecipe.HashUnit(cellHash);
                if (roll >= Math.Min(0.95, probability)) continue;

                double hardness = Math.Clamp(continent.HardnessAt(wx, wz), 0.0, 1.0);
                double priority = solubility + (0.25 * dischargeFactor) + (0.15 * slopeFactor) + (0.1 * Unit(cellHash, 3));
                candidates.Add((cx, cz, priority, wx, wz, broad, biome, hardness, solubility, discharge, slope, nearLake));
            }
        }

        // Deterministic priority ordering + minimum separation so systems remain bounded and never overlap.
        candidates.Sort((a, b) =>
        {
            int cmp = b.Priority.CompareTo(a.Priority);
            if (cmp != 0) return cmp;
            cmp = a.CellX.CompareTo(b.CellX);
            return cmp != 0 ? cmp : a.CellZ.CompareTo(b.CellZ);
        });

        double minSepSq = (double)coarseness.MinSystemSeparationMetres * coarseness.MinSystemSeparationMetres;
        List<UndergroundSystem> systems = [];
        List<(double X, double Z)> accepted = [];

        foreach (var c in candidates)
        {
            bool tooClose = false;
            for (int i = 0; i < accepted.Count; i++)
            {
                double dx = accepted[i].X - c.X;
                double dz = accepted[i].Z - c.Z;
                if ((dx * dx) + (dz * dz) < minSepSq)
                {
                    tooClose = true;
                    break;
                }
            }

            if (tooClose) continue;
            accepted.Add((c.X, c.Z));

            int localIndex = systems.Count;
            ulong systemSeed = TerrainRecipe.CoordinateHash(baseSeed ^ GrowthSalt ^ presetSalt, c.CellX, c.CellZ);
            string id = $"ug:{coarseness.Name}:{tileX}:{tileZ}:{localIndex}";
            UndergroundSystem system = BuildSystem(
                id, tileX, tileZ, systemSeed, c.X, c.Z, c.Sample, c.Biome,
                c.Hardness, c.Solubility, c.Discharge, c.Slope, c.NearLake,
                continent, bound, peak, coarseness);
            systems.Add(system);
        }

        UndergroundTileGrid? fieldGrid = coarseness.IncludeFieldGrid
            ? new UndergroundTileGrid(cells, spacing, gridSolubility!, gridWaterTable!, gridDepthBand!)
            : null;

        return new UndergroundTilePlan(tileX, tileZ, coarseness.Name, systems, fieldGrid);
    }

    private static UndergroundSystem BuildSystem(
        string id,
        int tileX,
        int tileZ,
        ulong systemSeed,
        double anchorX,
        double anchorZ,
        MapSample anchorSample,
        MapBiome biome,
        double hardness,
        double solubility,
        double discharge,
        double slope,
        bool nearLake,
        WorldMap continent,
        FrontierBound? bound,
        double peak,
        UndergroundCoarseness coarseness)
    {
        int bxMetres = coarseness.BoxMetresX;
        int byMetres = coarseness.BoxMetresY;
        int bzMetres = coarseness.BoxMetresZ;

        // Decide whether this cave is a single box (~65-75%) or a multi-box karst cluster (~25-40%).
        double multiChance = coarseness.TargetMultiBoxShare
            * (0.65 + (0.85 * Math.Clamp((solubility - KarstThreshold) / (1.0 - KarstThreshold), 0.0, 1.0)))
            * (discharge >= HighDischargeThreshold ? 1.25 : 1.0)
            * (nearLake ? 1.15 : 1.0);
        bool multi = coarseness.MaxBoxesPerSystem > 1 && Unit(systemSeed, 10) < Math.Clamp(multiChance, 0.08, 0.65);

        int targetBoxes = 1;
        if (multi)
        {
            double sizeRoll = Unit(systemSeed, 11);
            // Skew toward 2..4 boxes, with deeper/high-karst clusters reaching MaxBoxesPerSystem.
            double power = solubility >= HighKarstThreshold ? 1.15 : 1.65;
            int span = Math.Max(1, coarseness.MaxBoxesPerSystem - 1);
            targetBoxes = 2 + (int)Math.Floor(Math.Pow(sizeRoll, power) * span);
            targetBoxes = Math.Clamp(targetBoxes, 2, coarseness.MaxBoxesPerSystem);
        }

        // Anchor box sits with its centre at (anchorX, anchorZ), snapped to integer metres for clean chunk alignment.
        double origin0X = Math.Round(anchorX - (bxMetres * 0.5));
        double origin0Z = Math.Round(anchorZ - (bzMetres * 0.5));

        List<(int GX, int GZ)> gridCells = [(0, 0)];
        HashSet<(int GX, int GZ)> occupied = [(0, 0)];
        List<(int From, int To, UndergroundFace OutFace, UndergroundFace InFace)> rawEdges = [];

        // Grow a bounded branching tree of contiguous boxes along karst-favorable directions.
        for (int step = 1; step < targetBoxes; step++)
        {
            int bestFrom = -1;
            int bestGx = 0, bestGz = 0;
            UndergroundFace bestOut = UndergroundFace.PosX, bestIn = UndergroundFace.NegX;
            double bestScore = double.NegativeInfinity;

            for (int b = 0; b < gridCells.Count; b++)
            {
                (int gx, int gz) = gridCells[b];
                for (int d = 0; d < Neighbours.Length; d++)
                {
                    var n = Neighbours[d];
                    int nx = gx + n.Dx;
                    int nz = gz + n.Dz;
                    if (occupied.Contains((nx, nz))) continue;

                    double cx = origin0X + ((nx + 0.5) * bxMetres);
                    double cz = origin0Z + ((nz + 0.5) * bzMetres);
                    if (!continent.Contains(cx, cz)) continue;
                    if (bound is not null && !bound.Open(cx, cz)) continue;

                    MapSample ns = continent.Broad(cx, cz);
                    if (ns.Elevation < GenerationConstants.WaterLevel + 3.0) continue;

                    double nSol = SolubilityAt(continent, cx, cz);
                    double nDis = Math.Max(0.0, continent.Grid.Bilinear(continent.Fields.Discharge, cx, cz));
                    double jitter = Unit(Mix(systemSeed, (ulong)(step * 31 + b * 7 + d)), 12);
                    // Prefer elongated drainage-aligned karst chains over dense 2x2 blobs, with occasional branches.
                    int adjacentOccupied = CountAdjacent(occupied, nx, nz);
                    double compactnessPenalty = adjacentOccupied > 1 ? 0.35 : 0.0;
                    double score = (nSol * 0.9) + (0.15 * Math.Log10(1.0 + nDis)) + (0.45 * jitter) - compactnessPenalty;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestFrom = b;
                        bestGx = nx;
                        bestGz = nz;
                        bestOut = n.OutFace;
                        bestIn = n.InFace;
                    }
                }
            }

            if (bestFrom < 0) break;

            int newIdx = gridCells.Count;
            gridCells.Add((bestGx, bestGz));
            occupied.Add((bestGx, bestGz));
            rawEdges.Add((bestFrom, newIdx, bestOut, bestIn));
        }

        // Occasionally add a loop edge when two already-placed boxes happen to touch (natural karst anastomosing loop).
        if (gridCells.Count >= 4 && Unit(systemSeed, 19) < 0.35)
        {
            for (int a = 0; a < gridCells.Count; a++)
            for (int b = a + 1; b < gridCells.Count; b++)
            {
                if (rawEdges.Any(e => (e.From == a && e.To == b) || (e.From == b && e.To == a))) continue;
                int dx = gridCells[b].GX - gridCells[a].GX;
                int dz = gridCells[b].GZ - gridCells[a].GZ;
                if (Math.Abs(dx) + Math.Abs(dz) != 1) continue;
                var dir = Neighbours.First(n => n.Dx == dx && n.Dz == dz);
                rawEdges.Add((a, b, dir.OutFace, dir.InFace));
                break;
            }
        }

        // Compute consistent vertical placement for every box so shared-face links have generous vertical overlap.
        double anchorDepth = EstimateDepthMetres(anchorSample.Elevation, peak, anchorSample.Rock);
        float baseOriginY = ComputeBoxOriginY((float)anchorSample.Elevation, (float)anchorDepth, byMetres);

        // Compute per-box metadata first, then build links, mouths, big spaces, and (for medium/fine/detailed) interior rooms.
        double[] boxGround = new double[gridCells.Count];
        float[] boxOriginY = new float[gridCells.Count];
        float[] boxDepth = new float[gridCells.Count];
        float[] boxWaterLocalY = new float[gridCells.Count];
        ulong[] boxSeeds = new ulong[gridCells.Count];

        for (int i = 0; i < gridCells.Count; i++)
        {
            double cx = origin0X + ((gridCells[i].GX + 0.5) * bxMetres);
            double cz = origin0Z + ((gridCells[i].GZ + 0.5) * bzMetres);
            MapSample s = continent.Broad(cx, cz);
            boxGround[i] = s.Elevation;
            double d = EstimateDepthMetres(s.Elevation, peak, s.Rock);
            boxDepth[i] = (float)d;

            // Keep linked boxes near a shared stratigraphic elevation (gentle dip, clamped safely below local ground).
            float dipOffset = ((gridCells[i].GX * 2.5f) + (gridCells[i].GZ * 1.5f));
            float candidateY = baseOriginY + dipOffset;
            float maxUnderGround = (float)s.Elevation - BoxCoverMetres - byMetres;
            boxOriginY[i] = Math.Clamp(MathF.Min(candidateY, maxUnderGround), MinimumBoxFloorY, MathF.Max(MinimumBoxFloorY, maxUnderGround));

            float worldWaterY = (float)EstimateWorldWaterTable(s.Elevation, s.Moisture, d);
            float localWaterY = Math.Clamp(worldWaterY - boxOriginY[i], byMetres * WaterTableMinRatio, byMetres * WaterTableMaxRatio);
            boxWaterLocalY[i] = MathF.Round(localWaterY, 1);
            boxSeeds[i] = Mix(systemSeed, (ulong)(0x100 + i));
        }

        // Build inter-box links with exact matching world coordinates on shared faces.
        List<UndergroundLink> links = new(rawEdges.Count);
        for (int e = 0; e < rawEdges.Count; e++)
        {
            var edge = rawEdges[e];
            ulong edgeSeed = Mix(systemSeed ^ LinkSalt, (ulong)(edge.From * 64 + edge.To));

            bool xFace = edge.OutFace is UndergroundFace.PosX or UndergroundFace.NegX;
            float faceSpan = xFace ? bzMetres : bxMetres;
            float u = PortHorizontalMarginMetres + ((float)Unit(edgeSeed, 1) * MathF.Max(4f, faceSpan - (2f * PortHorizontalMarginMetres)));
            u = MathF.Round(u, 1);

            // Compute shared world Y range overlapping both boxes' vertical interiors.
            float lowWorldY = MathF.Max(boxOriginY[edge.From], boxOriginY[edge.To]) + PortVerticalMarginMetres;
            float highWorldY = MathF.Min(boxOriginY[edge.From] + byMetres, boxOriginY[edge.To] + byMetres) - PortVerticalMarginMetres;
            float worldY = highWorldY > lowWorldY
                ? lowWorldY + ((float)Unit(edgeSeed, 2) * (highWorldY - lowWorldY))
                : (boxOriginY[edge.From] + boxOriginY[edge.To] + byMetres) * 0.5f;
            worldY = MathF.Round(worldY, 1);
            float faceV = MathF.Round(Math.Clamp(worldY - boxOriginY[edge.From], PortVerticalMarginMetres * 0.6f, byMetres - (PortVerticalMarginMetres * 0.6f)), 1);

            double fromOriginX = origin0X + (gridCells[edge.From].GX * bxMetres);
            double fromOriginZ = origin0Z + (gridCells[edge.From].GZ * bzMetres);
            float worldX = edge.OutFace switch
            {
                UndergroundFace.PosX => (float)(fromOriginX + bxMetres),
                UndergroundFace.NegX => (float)fromOriginX,
                _ => (float)(fromOriginX + u),
            };
            float worldZ = edge.OutFace switch
            {
                UndergroundFace.PosZ => (float)(fromOriginZ + bzMetres),
                UndergroundFace.NegZ => (float)fromOriginZ,
                _ => (float)(fromOriginZ + u),
            };

            float waterWorldY = (boxOriginY[edge.From] + boxWaterLocalY[edge.From] + boxOriginY[edge.To] + boxWaterLocalY[edge.To]) * 0.5f;
            waterWorldY = MathF.Round(waterWorldY, 1);
            float radius = MathF.Round(MinPortRadiusMetres + ((float)Unit(edgeSeed, 3) * (MaxPortRadiusMetres - MinPortRadiusMetres)), 2);
            UndergroundLinkKind kind = worldY < waterWorldY - 1.5f
                ? UndergroundLinkKind.WetSump
                : worldY < waterWorldY + 3.0f
                    ? UndergroundLinkKind.StreamTube
                    : Unit(edgeSeed, 4) < 0.22
                        ? UndergroundLinkKind.TallRift
                        : UndergroundLinkKind.DryPassage;

            links.Add(new UndergroundLink(
                edge.From, edge.To, edge.OutFace, edge.InFace,
                u, faceV, worldX, worldY, worldZ,
                radius, waterWorldY, kind, edgeSeed));
        }

        // Surface mouths: every system has at least one primary mouth on box 0; multi-box systems may gain a second mouth (resurgence/sinkhole).
        List<UndergroundMouth> mouths = [];
        AddMouth(0, origin0X, origin0Z, gridCells[0], bxMetres, bzMetres, boxGround[0], slope, nearLake, discharge, systemSeed, 0, mouths);
        if (gridCells.Count >= 3 && Unit(systemSeed, 30) < 0.55)
        {
            int farBox = gridCells.Count - 1;
            double farCx = origin0X + ((gridCells[farBox].GX + 0.5) * bxMetres);
            double farCz = origin0Z + ((gridCells[farBox].GZ + 0.5) * bzMetres);
            double farSlope = SurfaceSlope(continent, farCx, farCz);
            AddMouth(farBox, origin0X, origin0Z, gridCells[farBox], bxMetres, bzMetres, boxGround[farBox], farSlope, nearLake, discharge, systemSeed, 1, mouths);
        }

        // Big-space markers (lakes, fog spaces, ruins inside natural caves, great halls).
        List<UndergroundBigSpace> bigSpaces = [];
        for (int i = 0; i < gridCells.Count; i++)
        {
            ulong s = Mix(boxSeeds[i], 0xB165UL);
            float lx = MathF.Round((bxMetres * 0.28f) + ((float)Unit(s, 1) * bxMetres * 0.44f), 1);
            float lz = MathF.Round((bzMetres * 0.28f) + ((float)Unit(s, 2) * bzMetres * 0.44f), 1);

            if (Unit(s, 3) < LakeChanceBase * (0.6 + anchorSample.Moisture))
            {
                float ly = MathF.Round(Math.Clamp(boxWaterLocalY[i] + 1.5f, 6f, byMetres - 10f), 1);
                float r = MathF.Round(8f + ((float)Unit(s, 4) * 10f), 1);
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.UndergroundLake, lx, ly, lz, r));
            }
            else if (boxDepth[i] >= ShallowBandLimit && Unit(s, 5) < FogSpaceChanceBase)
            {
                float ly = MathF.Round((byMetres * 0.35f) + ((float)Unit(s, 6) * byMetres * 0.25f), 1);
                float r = MathF.Round(9f + ((float)Unit(s, 7) * 9f), 1);
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.FogSpace, lx, ly, lz, r));
            }
            else if (Unit(s, 8) < RuinChanceBase)
            {
                float ly = MathF.Round(Math.Clamp(boxWaterLocalY[i] + 6f, 10f, byMetres - 10f), 1);
                float r = MathF.Round(7f + ((float)Unit(s, 9) * 6f), 1);
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.CaveRuin, lx, ly, lz, r));
            }
            else if (gridCells.Count >= 2 && solubility >= HighKarstThreshold && Unit(s, 10) < GreatHallChanceBase)
            {
                float ly = MathF.Round(byMetres * 0.5f, 1);
                float r = MathF.Round(12f + ((float)Unit(s, 11) * 10f), 1);
                bigSpaces.Add(new UndergroundBigSpace(i, UndergroundBigSpaceKind.GreatHall, lx, ly, lz, r));
            }
        }

        // Build final boxes (including upfront rooms & conduits for medium / fine / detailed levels).
        List<UndergroundBox> boxes = new(gridCells.Count);
        float maxDepth = 0f;
        for (int i = 0; i < gridCells.Count; i++)
        {
            double ox = origin0X + (gridCells[i].GX * bxMetres);
            double oz = origin0Z + (gridCells[i].GZ * bzMetres);
            UndergroundDepthBand band = ClassifyDepthBand(boxDepth[i]);
            maxDepth = MathF.Max(maxDepth, boxDepth[i]);

            (IReadOnlyList<UndergroundRoom> rooms, IReadOnlyList<UndergroundConduit> conduits) =
                coarseness.SubChambersPerBox > 0
                    ? BuildBoxInterior(i, bxMetres, byMetres, bzMetres, boxWaterLocalY[i], boxSeeds[i], coarseness.SubChambersPerBox, links, mouths, bigSpaces)
                    : (Array.Empty<UndergroundRoom>(), Array.Empty<UndergroundConduit>());

            boxes.Add(new UndergroundBox(
                i, gridCells[i].GX, gridCells[i].GZ,
                ox, boxOriginY[i], oz,
                coarseness.BoxChunksX, coarseness.BoxChunksY, coarseness.BoxChunksZ,
                (float)Math.Round(boxGround[i], 1),
                (float)Math.Round(boxDepth[i], 1),
                boxWaterLocalY[i],
                band,
                boxSeeds[i],
                rooms,
                conduits));
        }

        return new UndergroundSystem(
            id, tileX, tileZ, systemSeed,
            Math.Round(anchorX, 1), Math.Round(anchorZ, 1),
            (float)Math.Round(anchorSample.Elevation, 1),
            biome,
            (float)Math.Round(hardness, 3),
            (float)Math.Round(solubility, 3),
            (float)Math.Round(discharge, 2),
            (float)Math.Round(anchorSample.Temperature, 3),
            (float)Math.Round(anchorSample.Moisture, 3),
            (float)Math.Round(maxDepth, 1),
            boxes,
            links,
            mouths,
            bigSpaces);
    }

    private static (IReadOnlyList<UndergroundRoom> Rooms, IReadOnlyList<UndergroundConduit> Conduits) BuildBoxInterior(
        int boxIndex,
        int bx,
        int by,
        int bz,
        float waterLocalY,
        ulong boxSeed,
        int roomCount,
        IReadOnlyList<UndergroundLink> links,
        IReadOnlyList<UndergroundMouth> mouths,
        IReadOnlyList<UndergroundBigSpace> bigSpaces)
    {
        List<UndergroundRoom> rooms = new(roomCount);
        List<UndergroundConduit> conduits = [];
        ulong seed = boxSeed ^ DetailSalt;

        // First room anchors near the box's mouth or first big space if present, else central chamber.
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
                x = mouth.LocalEntryX;
                z = mouth.LocalEntryZ;
                y = MathF.Round(by * 0.72f, 1);
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
                    float dx = rooms[a].X - rooms[b].X;
                    float dy = rooms[a].Y - rooms[b].Y;
                    float dz = rooms[a].Z - rooms[b].Z;
                    float d2 = (dx * dx) + (dy * dy) + (dz * dz);
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

        // Ensure box links are aware of box rooms (the links themselves carry exact face coordinates).
        _ = links;
        return (rooms, conduits);
    }

    private static void AddMouth(
        int boxIndex,
        double origin0X,
        double origin0Z,
        (int GX, int GZ) cell,
        int bxMetres,
        int bzMetres,
        double groundElevation,
        double slope,
        bool nearLake,
        double discharge,
        ulong systemSeed,
        int mouthOrdinal,
        List<UndergroundMouth> mouths)
    {
        ulong s = Mix(systemSeed, (ulong)(0x500 + mouthOrdinal));
        float localX = MathF.Round(MouthInsetMetres + ((float)Unit(s, 1) * MathF.Max(8f, bxMetres - (2f * MouthInsetMetres))), 1);
        float localZ = MathF.Round(MouthInsetMetres + ((float)Unit(s, 2) * MathF.Max(8f, bzMetres - (2f * MouthInsetMetres))), 1);
        double wx = Math.Round(origin0X + (cell.GX * bxMetres) + localX, 1);
        double wz = Math.Round(origin0Z + (cell.GZ * bzMetres) + localZ, 1);

        UndergroundMouthKind kind = mouthOrdinal > 0 && (discharge >= HighDischargeThreshold || nearLake)
            ? UndergroundMouthKind.Resurgence
            : slope >= HillsideSlopeMin
                ? UndergroundMouthKind.Hillside
                : UndergroundMouthKind.Sinkhole;

        mouths.Add(new UndergroundMouth(boxIndex, wx, Math.Round(groundElevation, 1), wz, localX, localZ, kind));
    }

    private static double BiomeKarstMultiplier(MapBiome biome) => biome switch
    {
        MapBiome.Sea => 0.0,
        MapBiome.IceField => 0.20,
        MapBiome.Tundra => 0.45,
        MapBiome.Desert => 0.55,
        MapBiome.Grassland => 0.70,
        MapBiome.ColdSteppe => 0.75,
        MapBiome.Shrubland => 0.95,
        MapBiome.BorealForest => 1.05,
        MapBiome.TemperateForest => 1.15,
        MapBiome.Rainforest => 1.25,
        MapBiome.Alpine => 1.35,
        _ => 0.80,
    };

    private static double EstimateDepthMetres(double elevation, double peak, double rock)
    {
        double heightShare = Math.Clamp((elevation - GenerationConstants.WaterLevel) / Math.Max(1.0, peak - GenerationConstants.WaterLevel), 0.0, 1.0);
        double deep = Math.Clamp((DepthFromHeight * heightShare) + (DepthFromRock * Math.Clamp(rock, 0.0, 1.0)), 0.0, 1.0);
        return MostDepthMetres * deep;
    }

    private static UndergroundDepthBand ClassifyDepthBand(double depthMetres) =>
        depthMetres < ShallowBandLimit ? UndergroundDepthBand.Shallow
        : depthMetres < MidBandLimit ? UndergroundDepthBand.Mid
        : UndergroundDepthBand.Deep;

    private static float ComputeBoxOriginY(float groundElevation, float depthMetres, int boxHeight)
    {
        // Place the box's top below ground cover, deeper under high rocky relief, staying above bedrock floor.
        float desiredTop = groundElevation - BoxCoverMetres - (depthMetres * 0.25f);
        float originY = desiredTop - boxHeight;
        return MathF.Round(MathF.Max(MinimumBoxFloorY, originY), 1);
    }

    private static double EstimateWorldWaterTable(double elevation, double moisture, double depthMetres)
    {
        // Regional water table sits between sea level and ground, higher in wet valleys and lower under dry ridges.
        double aboveSea = Math.Max(0.0, elevation - GenerationConstants.WaterLevel);
        double factor = 0.25 + (0.45 * Math.Clamp(moisture, 0.0, 1.0));
        return GenerationConstants.WaterLevel + (aboveSea * factor) - (depthMetres * 0.15);
    }

    private static double SurfaceSlope(WorldMap continent, double x, double z)
    {
        double hx = continent.Broad(x + SlopeProbeMetres, z).Elevation - continent.Broad(x - SlopeProbeMetres, z).Elevation;
        double hz = continent.Broad(x, z + SlopeProbeMetres).Elevation - continent.Broad(x, z - SlopeProbeMetres).Elevation;
        return Math.Sqrt((hx * hx) + (hz * hz)) / (2.0 * SlopeProbeMetres);
    }

    private static bool NearLakeMargin(WorldMap continent, double x, double z)
    {
        if (continent.Lakes.Count == 0) return false;
        return double.IsFinite(continent.LakeLevelAt(x + LakeSinkSearchMetres, z))
            || double.IsFinite(continent.LakeLevelAt(x - LakeSinkSearchMetres, z))
            || double.IsFinite(continent.LakeLevelAt(x, z + LakeSinkSearchMetres))
            || double.IsFinite(continent.LakeLevelAt(x, z - LakeSinkSearchMetres));
    }

    private static int CountAdjacent(HashSet<(int GX, int GZ)> occupied, int gx, int gz)
    {
        int count = 0;
        for (int i = 0; i < Neighbours.Length; i++)
            if (occupied.Contains((gx + Neighbours[i].Dx, gz + Neighbours[i].Dz))) count++;
        return count;
    }

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
