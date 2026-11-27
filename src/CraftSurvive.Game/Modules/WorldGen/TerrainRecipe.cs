using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The world generator: the material at every voxel, as a pure function of the generator
/// contract. It emits material facts and an unquantized height; TerrainDensity describes the
/// scalar field and the Engine reconstructs the admitted samples.
/// </summary>
internal sealed class TerrainRecipe : ITerrainColumns
{
    internal const double ExposedRockThreshold = 0.6;
    private readonly TerrainConfiguration configuration;
    private readonly ITerrainDraws draws;
    private readonly long radius;
    // The recipe's caches are read by the chunk workers as well as the update thread (#10907). A full one lets go of its oldest
    // quarter, not all of it (#10889 R10889-2): clearing one whole made the next plan sample the map again for every column of
    // the ring at once, a stall of tens of milliseconds wherever a walk went.
    private readonly SharedCache<(long X, long Z), TreeShape?> featureCells = new(GenerationConstants.FeatureCacheLimit);
    private readonly SharedCache<(long X, long Z), (long Minimum, long Maximum)> columnBands = new(ColumnBandCacheLimit);

    /// <summary>
    /// Whether a chunk holds generated content, remembered: the answer is a pure function of the
    /// recipe, and residency asks it of every candidate on every plan (#9578).
    /// </summary>
    private readonly SharedCache<TerrainChunkAddress, bool> chunkContent = new(ChunkContentCacheLimit);
    private const int ChunkContentCacheLimit = 65_536;
    /// <summary>Chunks kept below a column's lowest surface, so its surface always has solid footing to mesh against.</summary>
    private const long BandFootingChunks = 1;
    private const int ColumnBandCacheLimit = 4096;
    private readonly PoiPlacement pois;
    private readonly CrossingPlacement crossings;

    internal TerrainRecipe(TerrainConfiguration configuration, ITerrainDraws draws, WorldMap? map = null)
    {
        this.configuration = configuration.Validate();
        Forest = new Forest(FloraCatalog.Shared, configuration.Seed);
        this.draws = draws ?? throw new ArgumentNullException(nameof(draws));
        Map = map ?? WorldMapGenerator.Generate(configuration);
        if (Map.Configuration != configuration) throw new ArgumentException("Terrain and map must share an identity.", nameof(map));
        radius = configuration.Size / 2;
        regions = Map.Scale.Continental ? MapRegions.For(Map) : null;
        towns = new(() => [.. Settlements.For(Map).Select(settlement => settlement.Ground)], LazyThreadSafetyMode.ExecutionAndPublication);
        pois = new PoiPlacement(configuration.Contract, draws, this, radius);
        crossings = new CrossingPlacement(configuration.Contract, draws, this, radius);
        caves = new CaveUnderground(pois, radius);
    }

    private readonly CaveUnderground caves;

    /// <summary>The caves under the world (#10889): every cave mouth's tendril, and the caves generated so far, read into the chunks.</summary>
    internal CaveUnderground Caves => caves;

    /// <summary>The versioned identity every feature draw is keyed from.</summary>
    internal TerrainGeneratorContract Contract => configuration.Contract;

    /// <summary>Where this world's crossings are, for the pass that walks over water.</summary>
    internal CrossingPlacement Crossings => crossings;

    /// <summary>Where this world's places are, for the pass that records finding them.</summary>
    internal PoiPlacement Placement => pois;

    /// <summary>The recipe as the column source placement reads, so callers need no cast.</summary>
    internal ITerrainColumns Columns => this;

    internal TerrainConfiguration Configuration => configuration;

    /// <summary>Where the forests stand and what grows in them (#10032).</summary>
    internal Forest Forest { get; }

    /// <summary>Each tree owns one cell this many metres square (#10032: crowns touch in a closed forest).</summary>
    internal int TreeCellMetres => GenerationConstants.TreeCellSize;
    internal int TreeCacheCells => featureCells.Count;
    internal int TreeCacheClears => featureCells.Trims;

    internal WorldMap Map { get; }

    /// <summary>A continent's region tiles, which walking terrain samples instead of the kilometre lattice (#9551).</summary>
    internal MapRegions? Regions => regions;

    private readonly MapRegions? regions;

    /// <summary>Geography at a world point: a continent's region tiles, or a regional world's one map.</summary>
    internal MapSample Geography(double x, double z) => regions?.Sample(x, z) ?? Map.Sample(x, z);

    private const long MinimumMaterialYValue = -GenerationConstants.TerrainDepth;

    internal long MinimumMaterialY => MinimumMaterialYValue;

    internal long MaximumMaterialY => (long)(MapScale.For(configuration.Size).MaximumElevation + WorldMap.LocalReliefLimit)
        + Math.Max(GenerationConstants.TerrainHeadroom, GenerationConstants.WorldWallRise);

    internal ushort MaterialAt(VoxelAddress address) => MaterialAt(address, ColumnAt(address.X, address.Z));

    // Height and slope depend on x/z only. Dense chunk generation evaluates
    // them once per column, while individual voxel queries share the same rules.
    internal TerrainColumn ColumnAt(long x, long z)
    {
        if (x < -radius || x > radius || z < -radius || z > radius) return default;
        (long surface, double waterSurface, MapSample geography) = Ground(x, z);
        return new TerrainColumn(surface, CardinalSlope(x, z, surface), waterSurface, geography);
    }

    /// <summary>
    /// The one thing site placement needs from the recipe: the ground and its slope.
    /// Explicit, so the recipe's own column query stays internal to the product.
    /// </summary>
    TerrainColumn ITerrainColumns.ColumnAt(long x, long z) => ColumnAt(x, z);

    /// <summary>The column's ground and water alone, as <see cref="ColumnAt"/> has them, without its slope (#11126).</summary>
    (long Surface, long WaterTop) ITerrainColumns.WaterAt(long x, long z)
    {
        if (x < -radius || x > radius || z < -radius || z > radius)
        {
            TerrainColumn outside = default;
            return (outside.Surface, outside.WaterTop);
        }

        (long surface, double waterSurface, _) = Ground(x, z);
        return (surface, new TerrainColumn(surface, 0, waterSurface).WaterTop);
    }

    bool ITerrainColumns.InTown(long x, long z) => InTown(x, z);

    /// <summary>
    /// Samples every column and continuous height of a chunk in one pass (#11179): evaluates ground once per
    /// interior column and once along its four one-cell halo edges (320 evaluations instead of 1,536 for a
    /// 16x16 chunk), computing exact cardinal slopes from the shared surface buffer.
    /// </summary>
    internal (TerrainColumn[] Columns, double[] Heights) SampleChunkColumns(VoxelAddress origin, int edge)
    {
        TerrainColumn[] columns = new TerrainColumn[edge * edge];
        double[] heights = new double[edge * edge];
        int stride = edge + 2;
        Span<long> surfaces = stride * stride <= 1024 ? stackalloc long[stride * stride] : new long[stride * stride];
        for (int z = 0; z < edge; z++)
        {
            long wz = origin.Z + z;
            int row = z * edge;
            int surfaceRow = (z + 1) * stride;
            for (int x = 0; x < edge; x++)
            {
                long wx = origin.X + x;
                (double continuous, long surface, double waterSurface, MapSample geography) = GroundFull(wx, wz);
                heights[row + x] = continuous;
                surfaces[surfaceRow + x + 1] = surface;
                columns[row + x] = wx < -radius || wx > radius || wz < -radius || wz > radius
                    ? default
                    : new TerrainColumn(surface, 0, waterSurface, geography);
            }
        }

        // Four halo edges for cardinal slopes (matching CardinalSlope -> TerrainSurface, which samples Ground directly).
        for (int i = 0; i < edge; i++)
        {
            surfaces[i + 1] = GroundFull(origin.X + i, origin.Z - 1).Surface;
            surfaces[((edge + 1) * stride) + i + 1] = GroundFull(origin.X + i, origin.Z + edge).Surface;
            surfaces[(i + 1) * stride] = GroundFull(origin.X - 1, origin.Z + i).Surface;
            surfaces[((i + 1) * stride) + edge + 1] = GroundFull(origin.X + edge, origin.Z + i).Surface;
        }

        for (int z = 0; z < edge; z++)
        {
            long wz = origin.Z + z;
            if (wz < -radius || wz > radius) continue;
            int row = z * edge;
            int surfaceRow = (z + 1) * stride;
            for (int x = 0; x < edge; x++)
            {
                long wx = origin.X + x;
                if (wx < -radius || wx > radius) continue;
                int s = surfaceRow + x + 1;
                long top = surfaces[s];
                long slope = Math.Max(
                    Math.Max(Math.Abs(top - surfaces[s - 1]), Math.Abs(top - surfaces[s + 1])),
                    Math.Max(Math.Abs(top - surfaces[s - stride]), Math.Abs(top - surfaces[s + stride])));
                columns[row + x] = columns[row + x] with { Slope = slope };
            }
        }

        return (columns, heights);
    }

    /// <summary>One column's ground and continuous height in a single sampling pass (#11179), for edit patching.</summary>
    internal (TerrainColumn Column, double Height) ColumnAndHeightAt(long x, long z)
    {
        (double continuous, long surface, double waterSurface, MapSample geography) = GroundFull(x, z);
        TerrainColumn column = x < -radius || x > radius || z < -radius || z > radius
            ? default
            : new TerrainColumn(surface, CardinalSlope(x, z, surface), waterSurface, geography);
        return (column, continuous);
    }

    /// <summary>
    /// Pre-gathered feature sets whose bounds overlap one chunk (#11179), so the 16^3 voxel loop skips
    /// empty structure/crossing/cave/tree passes with zero per-voxel cache lookups.
    /// </summary>
    internal readonly struct ChunkFeatures
    {
        internal PoiSite[] Pois { get; }
        internal CrossingSite[] Crossings { get; }
        internal CavePlacement[] Caves { get; }
        private readonly TreeShape?[] trees;
        private readonly long treeOriginX;
        private readonly long treeOriginZ;
        private readonly int treeSideX;

        internal ChunkFeatures(PoiSite[] pois, CrossingSite[] crossings, CavePlacement[] caves, TreeShape?[] trees, long treeOriginX, long treeOriginZ, int treeSideX)
        {
            Pois = pois;
            Crossings = crossings;
            Caves = caves;
            this.trees = trees;
            this.treeOriginX = treeOriginX;
            this.treeOriginZ = treeOriginZ;
            this.treeSideX = treeSideX;
        }

        internal bool HasTrees => trees.Length > 0;

        internal bool TryGetTree(long anchorX, long anchorZ, out TreeShape tree)
        {
            int index = (int)(((anchorZ - treeOriginZ) * treeSideX) + (anchorX - treeOriginX));
            if ((uint)index < (uint)trees.Length && trees[index] is TreeShape found)
            {
                tree = found;
                return true;
            }

            tree = default;
            return false;
        }
    }

    internal ChunkFeatures PrepareChunkFeatures(TerrainChunkAddress address)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        long x0 = address.X * edge, y0 = address.Y * edge, z0 = address.Z * edge;
        long x1 = x0 + edge - 1, y1 = y0 + edge - 1, z1 = z0 + edge - 1;

        long cell = PoiConstants.CellSize;
        long poiReach = PoiConstants.MaximumStructureReach;
        long firstPoiX = GridMath.FloorDivide(x0 - poiReach, cell), lastPoiX = GridMath.FloorDivide(x1 + poiReach, cell);
        long firstPoiZ = GridMath.FloorDivide(z0 - poiReach, cell), lastPoiZ = GridMath.FloorDivide(z1 + poiReach, cell);
        List<PoiSite>? overlappingPois = null;
        for (long anchorX = firstPoiX; anchorX <= lastPoiX; anchorX++)
        for (long anchorZ = firstPoiZ; anchorZ <= lastPoiZ; anchorZ++)
        {
            if (pois.SiteAt(anchorX, anchorZ) is PoiSite site
                && site.X + poiReach >= x0 && site.X - poiReach <= x1
                && site.Z + poiReach >= z0 && site.Z - poiReach <= z1
                && site.Ground + PoiConstants.MaximumStructureHeight >= y0
                && site.Ground - PoiConstants.MaximumCarveDepth <= y1)
            {
                (overlappingPois ??= []).Add(site);
            }
        }

        long crossingReach = PoiConstants.CrossingMaximumSpan + PoiConstants.CrossingRampLength + 1;
        long crossingPad = PoiConstants.CrossingRampLength + PoiConstants.CrossingHalfWidth;
        long crossingDepth = Math.Max(PoiConstants.CrossingPierDepth, PoiConstants.CrossingRampLength + 1);
        long firstCrossX = GridMath.FloorDivide(x0 - crossingReach, cell), lastCrossX = GridMath.FloorDivide(x1 + crossingReach, cell);
        long firstCrossZ = GridMath.FloorDivide(z0 - crossingReach, cell), lastCrossZ = GridMath.FloorDivide(z1 + crossingReach, cell);
        List<CrossingSite>? overlappingCrossings = null;
        for (long anchorX = firstCrossX; anchorX <= lastCrossX; anchorX++)
        for (long anchorZ = firstCrossZ; anchorZ <= lastCrossZ; anchorZ++)
        {
            if (crossings.SiteAt(anchorX, anchorZ) is CrossingSite site
                && Math.Max(site.FromX, site.ToX) + crossingPad >= x0 && Math.Min(site.FromX, site.ToX) - crossingPad <= x1
                && Math.Max(site.FromZ, site.ToZ) + crossingPad >= z0 && Math.Min(site.FromZ, site.ToZ) - crossingPad <= z1
                && site.DeckY >= y0 && site.DeckY - crossingDepth <= y1)
            {
                (overlappingCrossings ??= []).Add(site);
            }
        }

        CavePlacement[] columnCaves = caves.NearChunkColumn(address.X, address.Z);
        CavePlacement[] overlappingCaves = [];
        if (columnCaves.Length > 0)
        {
            List<CavePlacement>? filtered = null;
            foreach (CavePlacement cave in columnCaves)
            {
                if ((y1 >= cave.BoxMinimum.Y && y0 <= cave.BoxMaximum.Y)
                    || (y1 >= cave.TendrilMinimum.Y - 2 && y0 <= cave.TendrilMaximum.Y + 2))
                {
                    (filtered ??= []).Add(cave);
                }
            }

            if (filtered is not null) overlappingCaves = [.. filtered];
        }

        long treeCell = TreeCellMetres;
        long firstTreeX = GridMath.FloorDivide(x0, treeCell), lastTreeX = GridMath.FloorDivide(x1, treeCell);
        long firstTreeZ = GridMath.FloorDivide(z0, treeCell), lastTreeZ = GridMath.FloorDivide(z1, treeCell);
        int treeSideX = (int)(lastTreeX - firstTreeX + 1);
        int treeSideZ = (int)(lastTreeZ - firstTreeZ + 1);
        TreeShape?[]? overlappingTrees = null;
        for (long tz = firstTreeZ; tz <= lastTreeZ; tz++)
        for (long tx = firstTreeX; tx <= lastTreeX; tx++)
        {
            if (TreeAt(tx, tz) is TreeShape tree
                && tree.GroundY <= y1 && tree.GroundY + GenerationConstants.TreeCoreHeight - 1 >= y0)
            {
                overlappingTrees ??= new TreeShape?[treeSideX * treeSideZ];
                overlappingTrees[((tz - firstTreeZ) * treeSideX) + (tx - firstTreeX)] = tree;
            }
        }

        return new ChunkFeatures(
            overlappingPois is null ? [] : [.. overlappingPois],
            overlappingCrossings is null ? [] : [.. overlappingCrossings],
            overlappingCaves,
            overlappingTrees ?? [],
            firstTreeX,
            firstTreeZ,
            treeSideX);
    }

    internal ushort GeneratedAt(VoxelAddress address, TerrainColumn column, double height, in ChunkFeatures features, out float? magnitude)
    {
        if (features.Caves.Length > 0 && caves.InCave(address, column.Surface, features.Caves, out ushort inside, out float within))
        {
            magnitude = within;
            return inside;
        }

        ushort material = SurfaceMaterialAt(address, column, in features);
        if (features.Caves.Length > 0 && caves.At(address, column.Surface, height, material, features.Caves) is (ushort underground, float distance))
        {
            magnitude = distance;
            return underground;
        }

        magnitude = null;
        return material;
    }

    private ushort SurfaceMaterialAt(VoxelAddress address, TerrainColumn column, in ChunkFeatures features)
    {
        ushort material = BaseMaterialAt(address, column, in features);
        if (features.Pois.Length > 0)
        {
            PoiVoxel poi = PoiVoxel.None;
            foreach (PoiSite site in features.Pois)
            {
                if (PoiStructures.MaterialAt(site, address.X, address.Y, address.Z) is { IsNone: false } voxel)
                {
                    poi = voxel;
                    break;
                }
            }

            material = StructurePasses.ApplySite(material, poi, address.Y, column);
        }

        if (features.Crossings.Length > 0)
        {
            foreach (CrossingSite site in features.Crossings)
            {
                if (CrossingStructure.MaterialAt(site, address.X, address.Y, address.Z) is { IsNone: false } span)
                {
                    return StructurePasses.ApplyCrossing(material, span, address.Y, column);
                }
            }
        }

        return material;
    }

    private ushort BaseMaterialAt(VoxelAddress address, TerrainColumn column, in ChunkFeatures features)
    {
        if (address.X < -radius || address.X > radius || address.Z < -radius || address.Z > radius)
        {
            return TerrainConstants.EmptyMaterial;
        }

        if (IsWorldFloor(address.Y) || IsWorldWall(address.X, address.Z, address.Y, column))
        {
            return (ushort)BlockId.Bedrock;
        }

        ushort material = NaturalMaterialAt(address, column);
        if (material == TerrainConstants.EmptyMaterial && features.HasTrees)
        {
            long cell = TreeCellMetres;
            long anchorX = GridMath.FloorDivide(address.X, cell);
            long anchorZ = GridMath.FloorDivide(address.Z, cell);
            if (features.TryGetTree(anchorX, anchorZ, out TreeShape tree)
                && address.X == (anchorX * cell) + tree.OffsetX && address.Z == (anchorZ * cell) + tree.OffsetZ
                && address.Y >= tree.GroundY && address.Y < tree.GroundY + GenerationConstants.TreeCoreHeight)
            {
                return (ushort)BlockId.TreeCore;
            }
        }

        return material;
    }

    internal ushort MaterialAt(VoxelAddress address, TerrainColumn column)
    {
        // Structures answer last, because they are the one pass that may take material away
        // and the only pass that may pave the course it stands on. Crossings come after sites
        // for the same reason sites come after the ground: a bridge is built over water, and
        // water is what the ground pass leaves behind.
        if (caves.InCave(address, column.Surface, out ushort inside, out _))
        {
            return inside;
        }

        ushort material = SurfaceMaterialAt(address, column);
        // Underground last (#10889): a cave mouth's tendril takes away what the passes above left, and an admitted cave's
        // box below its seal is the cave's own.
        return caves.At(address, column.Surface, column.Surface, material) is (ushort underground, _) ? underground : material;
    }

    /// <summary>
    /// A voxel's generated material and, where a tendril or a cave reaches it, its density magnitude (#10889): the
    /// chunk generator's one question, so a cave's smooth walls are not the height field's.
    /// </summary>
    internal ushort GeneratedAt(VoxelAddress address, TerrainColumn column, double height, out float? magnitude)
    {
        // Deep in an admitted cave's box the cave answers alone.
        if (caves.InCave(address, column.Surface, out ushort inside, out float within))
        {
            magnitude = within;
            return inside;
        }

        ushort material = SurfaceMaterialAt(address, column);
        if (caves.At(address, column.Surface, height, material) is (ushort underground, float distance))
        {
            magnitude = distance;
            return underground;
        }

        magnitude = null;
        return material;
    }

    /// <summary>The overworld's own voxel, before anything underground: the ground, then the places and crossings.</summary>
    private ushort SurfaceMaterialAt(VoxelAddress address, TerrainColumn column)
    {
        ushort material = BaseMaterialAt(address, column);
        material = StructurePasses.ApplySite(material, PoiAt(address.X, address.Y, address.Z), address.Y, column);
        return CrossingAt(address.X, address.Y, address.Z) is PoiVoxel span
            ? StructurePasses.ApplyCrossing(material, span, address.Y, column)
            : material;
    }

    /// <summary>
    /// The world without its structures: bounds, floor, border wall, natural material,
    /// water, and the surface features. It is split out because the chunk-content
    /// predicate has to know what the structure pass *changed*, and the only honest way
    /// to answer that is to compare this against what <see cref="MaterialAt"/> produces
    /// for the same voxel.
    /// </summary>
    private ushort BaseMaterialAt(VoxelAddress address, TerrainColumn column)
    {
        if (address.X < -radius || address.X > radius || address.Z < -radius || address.Z > radius)
        {
            return TerrainConstants.EmptyMaterial;
        }

        if (IsWorldFloor(address.Y) || IsWorldWall(address.X, address.Z, address.Y, column))
        {
            return (ushort)BlockId.Bedrock;
        }

        ushort material = NaturalMaterialAt(address, column);

        // Water is not voxels (#9877): the air over a submerged column stays air, and the water
        // surfaces, the swim volume and every view read where water stands from WaterRules.

        // Features only fill air, so they never displace terrain: a canopy that
        // meets a slope loses to the slope rather than leaving a floating leaf. Where trees grow at all is
        // the forest's decision (#10032), dry and cold country included.
        if (material == TerrainConstants.EmptyMaterial)
        {
            material = FeatureMaterialAt(address.X, address.Y, address.Z);
        }

        return material;
    }

    /// <summary>The world's floor: bedrock under everything, at the stated depth.</summary>
    private static bool IsWorldFloor(long y) => y < MinimumMaterialYValue + GenerationConstants.WorldFloorThickness;

    /// <summary>
    /// The world's border wall. It stands at this world's own extent edge on all four sides up
    /// to a stated height above the column's ground (or water), so the finite world has an
    /// authored edge rather than a void the player can walk into.
    /// </summary>
    private bool IsWorldWall(long x, long z, long y, TerrainColumn column) =>
        y >= MinimumMaterialYValue && y <= WallTop(column) && IsWallColumn(x, z);

    /// <summary>The highest cell any part of which lies under a water surface.</summary>
    private static long CellUnder(double surface) => (long)Math.Ceiling(surface) - 1;

    private static long WallTop(TerrainColumn column) =>
        Math.Max(column.Surface, column.WaterTop) + GenerationConstants.WorldWallRise;

    private bool IsWallColumn(long x, long z)
    {
        long limit = radius;
        long inner = limit - GenerationConstants.WorldWallThickness;
        bool onEdge = x <= -inner || x >= inner || z <= -inner || z >= inner;
        bool inside = x >= -limit && x <= limit && z >= -limit && z <= limit;
        return onEdge && inside;
    }

    private ushort NaturalMaterialAt(VoxelAddress address, TerrainColumn column)
    {
        long top = column.Surface;
        if (address.Y < MinimumMaterialY || address.Y > top)
        {
            return TerrainConstants.EmptyMaterial;
        }

        long slope = column.Slope;
        MapSample geography = column.Geography;
        // A town's yard (#9895) is trodden earth: the meadow's ground, where no grass, flowers or stones grow.
        if (InTownYard(address.X, address.Z)) return TerrainConstants.DirtMaterial;
        // Steep and resistant ground is bare stone; submerged ground is a sand bed.
        if (geography.Rock >= ExposedRockThreshold)
            return TerrainConstants.StoneMaterial;
        if (top <= column.WaterTop && top - address.Y <= GenerationConstants.SubsoilDepthMaximum)
            return (ushort)BlockId.Sand;
        if (slope <= GenerationConstants.TopsoilSlopeMaximum)
        {
            if (WorldMap.Frozen(geography)) return (ushort)BlockId.Snow;
            if (Soils.DeepDesert(geography)) return (ushort)BlockId.Sand;
        }
        long depthFromSurface = top - address.Y;
        if (depthFromSurface == 0 && slope <= GenerationConstants.TopsoilSlopeMaximum)
        {
            return (ushort)Topsoil(address.X, address.Z, geography);
        }

        // Frozen ground has no soil: too steep for snow, it is bare stone (#10629).
        return depthFromSurface <= GenerationConstants.SubsoilDepthMaximum
            && slope <= GenerationConstants.SubsoilSlopeMaximum
            && !WorldMap.Frozen(geography)
            ? TerrainConstants.DirtMaterial
            : TerrainConstants.StoneMaterial;
    }

    /// <summary>
    /// The bridge voxel at one position. A crossing is decided by its own cell and reaches at
    /// most a span plus its abutments, so the nine cells around this one are enough.
    /// </summary>
    private PoiVoxel? CrossingAt(long x, long y, long z) =>
        FirstInNeighbourhood<PoiVoxel>(x, z, PoiConstants.CellSize, (anchorX, anchorZ) =>
            crossings.SiteAt(anchorX, anchorZ) is CrossingSite site
            && CrossingStructure.MaterialAt(site, x, y, z) is { IsNone: false } voxel
                ? voxel
                : null);

    /// <summary>
    /// The structure voxel at one position, asked of whichever of the nine anchor cells around
    /// this one owns a site covering it. Each cell decides for itself from its own coordinates
    /// and the contract, so two chunks that share a structure agree about it without
    /// communicating and without an order.
    /// </summary>
    private PoiVoxel PoiAt(long x, long y, long z) =>
        FirstInNeighbourhood<PoiVoxel>(x, z, PoiConstants.CellSize, (anchorX, anchorZ) =>
            pois.SiteAt(anchorX, anchorZ) is PoiSite site
            && PoiStructures.MaterialAt(site, x, y, z) is { IsNone: false } voxel
                ? voxel
                : null) ?? PoiVoxel.None;

    /// <summary>How far beyond a place's own reach trees stay away (metres), so a canopy does not hide it.</summary>
    private const long PlaceTreeClearance = 4;

    /// <summary>Whether a column is within a place's footprint or its clearance (#9992).</summary>
    private bool OnPlace(long x, long z)
    {
        long cell = PoiConstants.CellSize, reach = PoiConstants.MaximumStructureReach + PlaceTreeClearance;
        long cellX = GridMath.FloorDivide(x, cell), cellZ = GridMath.FloorDivide(z, cell);
        for (long anchorX = cellX - 1; anchorX <= cellX + 1; anchorX++)
        {
            for (long anchorZ = cellZ - 1; anchorZ <= cellZ + 1; anchorZ++)
            {
                if (pois.SiteAt(anchorX, anchorZ) is PoiSite site && Math.Abs(x - site.X) <= reach && Math.Abs(z - site.Z) <= reach)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The first answer from the nine anchor cells of a lattice around a column: its own cell
    /// and the eight that touch it. Every anchored pass - trees, sites, crossings - reaches at
    /// most one cell beyond its anchor, so these nine are all that can cover the column.
    /// </summary>
    private static T? FirstInNeighbourhood<T>(long x, long z, long cellSize, Func<long, long, T?> probe)
        where T : struct
    {
        long cellX = GridMath.FloorDivide(x, cellSize);
        long cellZ = GridMath.FloorDivide(z, cellSize);
        for (long anchorX = cellX - 1; anchorX <= cellX + 1; anchorX++)
        {
            for (long anchorZ = cellZ - 1; anchorZ <= cellZ + 1; anchorZ++)
            {
                if (probe(anchorX, anchorZ) is T found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private long TerrainSurface(long x, long z) => Ground(x, z).Surface;

    /// <summary>The rounded surface, the water surface over it (<see cref="WaterRules"/>), and the geography both came from.</summary>
    private (long Surface, double WaterSurface, MapSample Geography) Ground(long x, long z)
    {
        (_, long surface, double waterSurface, MapSample geography) = GroundFull(x, z);
        return (surface, waterSurface, geography);
    }

    private (double Continuous, long Surface, double WaterSurface, MapSample Geography) GroundFull(long x, long z)
    {
        MapSample geography = Geography(x, z);
        double continuous = ContinuousHeight(geography, x, z);
        long surface = (long)Math.Round(continuous, MidpointRounding.AwayFromZero);
        return (continuous, surface, WaterRules.Surface(geography), geography);
    }

    /// <summary>The generated surface height at a column, for callers that must reason about the world.</summary>
    internal long SurfaceAt(long x, long z) => TerrainSurface(x, z);

    /// <summary>The highest cell under water over a column (<see cref="TerrainColumn.WaterTop"/>), without computing slope (#11179).</summary>
    internal long WaterTopAt(long x, long z) => ((ITerrainColumns)this).WaterAt(x, z).WaterTop;

    /// <summary>The water surface over a column, in fractional metres: the sea, a river's surface or a lake's level.</summary>
    internal double WaterSurfaceAt(long x, long z) => Ground(x, z).WaterSurface;

    /// <summary>The half-extent of the finite world this recipe generates.</summary>
    internal long Radius => radius;

    /// <summary>
    /// The vertical chunk range that can hold this chunk column's generated surface: from one
    /// chunk below its lowest ground to its highest ground, water or wall plus feature headroom.
    /// Chunks outside it are either empty air or buried solid rock with no surface to show.
    /// </summary>
    /// <summary>Whether a chunk column's band is known already (no map sampling to answer it).</summary>
    internal bool KnowsColumnBand(long chunkX, long chunkZ) => columnBands.ContainsKey((chunkX, chunkZ));

    /// <summary>Whether a chunk's content is known already (no scan to answer it).</summary>
    internal bool KnowsChunkContent(TerrainChunkAddress address) => chunkContent.ContainsKey(address);

    internal (long Minimum, long Maximum) ChunkColumnBand(long chunkX, long chunkZ)
    {
        if (columnBands.TryGetValue((chunkX, chunkZ), out (long, long) cached)) return cached;
        long edge = TerrainConstants.ChunkEdgeLength;
        long lowest = long.MaxValue, highest = long.MinValue;
        for (long x = chunkX * edge; x < chunkX * edge + edge; x++)
        for (long z = chunkZ * edge; z < chunkZ * edge + edge; z++)
        {
            if (x < -radius || x > radius || z < -radius || z > radius) continue;
            (long surface, double water, _) = Ground(x, z);
            lowest = Math.Min(lowest, surface);
            // Water is not voxels (#9877): only the wall stands to the water's height.
            highest = Math.Max(highest, IsWallColumn(x, z) ? Math.Max(surface, CellUnder(water)) + GenerationConstants.WorldWallRise : surface);
        }
        (long, long) band = lowest == long.MaxValue
            ? (1, 0)
            : (GridMath.FloorDivide(lowest, edge) - BandFootingChunks,
                GridMath.FloorDivide(highest + GenerationConstants.TerrainHeadroom, edge));
        columnBands.Set((chunkX, chunkZ), band);
        return band;
    }

    /// <summary>
    /// Whether a chunk holds any non-empty voxel, answered from the generation contract
    /// without generating it.
    ///
    /// It exists because the residency policy decides what to request and what to keep by
    /// filtering on a chunk's solid voxel count, so it currently has to generate every
    /// candidate in its retained ring - 49 chunks, about 70 ms, on the first update of a
    /// run. This predicate is the cheaper question: a chunk is empty only when nothing the
    /// generator produces reaches into its vertical span.
    ///
    /// It is deliberately conservative. Answering "empty" for a chunk that holds content
    /// would evict live chunks and thrash the stream, which is a worse failure than
    /// retaining an empty one, so anything not provably empty answers "content".
    /// </summary>
    internal bool ChunkHasContent(TerrainChunkAddress address)
    {
        if (chunkContent.TryGetValue(address, out bool known)) return known;
        bool content = ScanChunkContent(address);
        chunkContent.Set(address, content);
        return content;
    }

    private bool ScanChunkContent(TerrainChunkAddress address)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        long yMinimum = address.Y * edge;
        long yMaximum = yMinimum + edge - 1;

        // The floor and the border wall are authored everywhere they apply.
        if (yMinimum <= MinimumMaterialY)
        {
            return true;
        }

        long xStart = address.X * edge;
        long zStart = address.Z * edge;
        for (long x = xStart; x < xStart + edge; x++)
        {
            for (long z = zStart; z < zStart + edge; z++)
            {
                if (x < -radius || x > radius || z < -radius || z > radius)
                {
                    continue;
                }

                (long surface, double water, _) = Ground(x, z);
                if (surface >= yMinimum)
                {
                    return true;
                }

                if (IsWallColumn(x, z) && yMinimum <= Math.Max(surface, CellUnder(water)) + GenerationConstants.WorldWallRise)
                {
                    return true;
                }
            }
        }

        return ChunkFeaturesReach(xStart, xStart + edge - 1, yMinimum, yMaximum, zStart, zStart + edge - 1)
            || ChunkPoisChange(xStart, xStart + edge - 1, yMinimum, yMaximum, zStart, zStart + edge - 1)
            || ChunkCrossingsChange(xStart, xStart + edge - 1, yMinimum, yMaximum, zStart, zStart + edge - 1);
    }

    /// <summary>
    /// Whether any tree's voxels fall inside the chunk, using the generator's own cached
    /// per-cell decisions rather than a bound. This is exact for features - the same
    /// `TreeAt` answer the generator uses, tested against the same trunk and canopy
    /// conditions - which is what allows the residency policy to trust it for retention
    /// once it is wired in.
    /// </summary>
    private bool ChunkFeaturesReach(long xStart, long xEnd, long yMinimum, long yMaximum, long zStart, long zEnd)
    {
        long cell = TreeCellMetres;
        // A tree's voxels are its trunk core, inside its own anchor cell.
        for (long anchorX = GridMath.FloorDivide(xStart, cell); anchorX <= GridMath.FloorDivide(xEnd, cell); anchorX++)
        {
            for (long anchorZ = GridMath.FloorDivide(zStart, cell); anchorZ <= GridMath.FloorDivide(zEnd, cell); anchorZ++)
            {
                if (TreeAt(anchorX, anchorZ) is not TreeShape tree)
                {
                    continue;
                }

                long trunkX = (anchorX * cell) + tree.OffsetX;
                long trunkZ = (anchorZ * cell) + tree.OffsetZ;
                long baseY = tree.GroundY;
                if (trunkX >= xStart && trunkX <= xEnd && trunkZ >= zStart && trunkZ <= zEnd
                    && baseY <= yMaximum && baseY + GenerationConstants.TreeCoreHeight - 1 >= yMinimum)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the structure pass changes whether any voxel in the chunk is empty - by
    /// building into air, or by cutting a way in.
    ///
    /// It answers the generator's own question rather than approximating it, because
    /// residency trusts this predicate with two opposite mistakes: a chunk wrongly called
    /// empty is evicted while it holds content, and a chunk wrongly called content stays
    /// resident while it holds nothing. Both cost real work in a streamed world, so the
    /// test is exact - it evaluates the same structure voxel and the same base material
    /// that generation would.
    /// </summary>
    private bool ChunkPoisChange(long xStart, long xEnd, long yMinimum, long yMaximum, long zStart, long zEnd)
    {
        long reach = PoiConstants.MaximumStructureReach;
        long cell = PoiConstants.CellSize;
        long firstCellX = GridMath.FloorDivide(xStart - reach, cell);
        long lastCellX = GridMath.FloorDivide(xEnd + reach, cell);
        long firstCellZ = GridMath.FloorDivide(zStart - reach, cell);
        long lastCellZ = GridMath.FloorDivide(zEnd + reach, cell);
        for (long anchorX = firstCellX; anchorX <= lastCellX; anchorX++)
        {
            for (long anchorZ = firstCellZ; anchorZ <= lastCellZ; anchorZ++)
            {
                if (pois.SiteAt(anchorX, anchorZ) is not PoiSite site)
                {
                    continue;
                }

                long xMinimum = Math.Max(xStart, site.X - reach);
                long xMaximum = Math.Min(xEnd, site.X + reach);
                long zMinimum = Math.Max(zStart, site.Z - reach);
                long zMaximum = Math.Min(zEnd, site.Z + reach);
                long yTop = Math.Min(yMaximum, site.Ground + PoiConstants.MaximumStructureHeight);
                long yBottom = Math.Max(yMinimum, site.Ground - PoiConstants.MaximumCarveDepth);
                for (long y = yBottom; y <= yTop; y++)
                {
                    for (long x = xMinimum; x <= xMaximum; x++)
                    {
                        for (long z = zMinimum; z <= zMaximum; z++)
                        {
                            PoiVoxel poi = PoiStructures.MaterialAt(site, x, y, z);
                            if (poi.IsNone)
                            {
                                continue;
                            }

                            TerrainColumn column = ColumnAt(x, z);
                            ushort before = BaseMaterialAt(new VoxelAddress(x, y, z), column);
                            ushort after = StructurePasses.ApplySite(before, poi, y, column);
                            if ((before == TerrainConstants.EmptyMaterial)
                                != (after == TerrainConstants.EmptyMaterial))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a crossing changes whether any voxel in the chunk is empty. It answers exactly,
    /// the same way the site predicate does, because residency makes the same two opposite
    /// mistakes expensive: evicting a chunk that holds a deck, or keeping one that holds nothing.
    /// </summary>
    private bool ChunkCrossingsChange(long xStart, long xEnd, long yMinimum, long yMaximum, long zStart, long zEnd)
    {
        long cell = PoiConstants.CellSize;
        long reach = PoiConstants.CrossingMaximumSpan + PoiConstants.CrossingRampLength + 1;
        long firstCellX = GridMath.FloorDivide(xStart - reach, cell);
        long lastCellX = GridMath.FloorDivide(xEnd + reach, cell);
        long firstCellZ = GridMath.FloorDivide(zStart - reach, cell);
        long lastCellZ = GridMath.FloorDivide(zEnd + reach, cell);
        for (long anchorX = firstCellX; anchorX <= lastCellX; anchorX++)
        {
            for (long anchorZ = firstCellZ; anchorZ <= lastCellZ; anchorZ++)
            {
                if (crossings.SiteAt(anchorX, anchorZ) is not CrossingSite site)
                {
                    continue;
                }

                long xMinimum = Math.Max(xStart, Math.Min(site.FromX, site.ToX) - reach);
                long xMaximum = Math.Min(xEnd, Math.Max(site.FromX, site.ToX) + reach);
                long zMinimum = Math.Max(zStart, Math.Min(site.FromZ, site.ToZ) - reach);
                long zMaximum = Math.Min(zEnd, Math.Max(site.FromZ, site.ToZ) + reach);
                long yBottom = Math.Max(yMinimum, site.DeckY - PoiConstants.CrossingPierDepth);
                long yTop = Math.Min(yMaximum, site.DeckY);
                for (long y = yBottom; y <= yTop; y++)
                {
                    for (long x = xMinimum; x <= xMaximum; x++)
                    {
                        for (long z = zMinimum; z <= zMaximum; z++)
                        {
                            PoiVoxel span = CrossingStructure.MaterialAt(site, x, y, z);
                            if (span.Kind != PoiVoxelKind.Fill)
                            {
                                continue;
                            }

                            TerrainColumn column = ColumnAt(x, z);
                            ushort before = BaseMaterialAt(new VoxelAddress(x, y, z), column);
                            if (StructurePasses.ApplyCrossing(before, span, y, column) != before)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Whether the chunk overlaps the authored border wall's band.</summary>
    /// <summary>The unquantized height at a column's sample centre; DC receives this shape instead of stair steps.</summary>
    internal double ContinuousHeightAt(long x, long z) => ContinuousHeight(Geography(x, z), x, z);

    /// <summary>The ground's continuous height at a column and the geography it came from, in one sampling (#9877).</summary>
    internal (double Ground, MapSample Geography) GroundAt(long x, long z)
    {
        MapSample geography = Geography(x, z);
        return (ContinuousHeight(geography, x, z), geography);
    }

    /// <summary>How far above the water the walking ground's rising relief comes in fully (#11148).</summary>
    private const double ShoreReliefMetres = 4;

    private double ContinuousHeight(MapSample geography, long x, long z)
    {
        double relief = RegionalTerrain.Relief(Contract.GeographyNoiseSeed, geography, x, z);
        // Relief lifts no ground out of the sea (#11148): rising relief is none at the water level and below, and comes in over
        // the shore's first metres above it, as the region tiles' own relief fades at the coast. Ground the map has under water
        // stays under it, so the walking ground raises no island the map does not have, and the coastline stays where the map's is.
        if (relief > 0)
        {
            relief *= WorldMap.Smooth(Math.Clamp((geography.Elevation - GenerationConstants.WaterLevel) / ShoreReliefMetres, 0, 1));
        }

        double ground = Math.Max(geography.Elevation + relief, GenerationConstants.MinimumTerrainHeight);
        // A town's pad (#9895): flat ground beside its water, eased into the country around it.
        foreach (TownGround town in Towns) ground = town.Level(ground, x, z);
        return ground;
    }

    /// <summary>
    /// The ground every town stands on: none away from a designed continent. Read lazily, once, because
    /// the far field's workers sample heights concurrently and the settlements need the drainage.
    /// </summary>
    private TownGround[] Towns => towns.Value;

    private readonly Lazy<TownGround[]> towns;

    /// <summary>Whether a point lies on a town's pad or near enough its edge that nothing natural grows there.</summary>
    private bool InTown(long x, long z) => Towns.Any(town => town.Weight(x, z) > 0);

    /// <summary>Whether a column is a town's yard: trodden earth where nothing grows. Safe on workers (the towns are built once).</summary>
    internal bool InTownYard(long x, long z) => Towns.Any(town => town.InYard(x, z));

    private long CardinalSlope(long x, long z, long top)
    {
        long west = Math.Abs(top - TerrainSurface(x - 1, z));
        long east = Math.Abs(top - TerrainSurface(x + 1, z));
        long north = Math.Abs(top - TerrainSurface(x, z - 1));
        long south = Math.Abs(top - TerrainSurface(x, z + 1));
        return Math.Max(Math.Max(west, east), Math.Max(north, south));
    }

    internal static double ValueNoise(ulong seed, double x, double z, int scale)
    {
        long cellX = (long)Math.Floor(x / scale);
        long cellZ = (long)Math.Floor(z / scale);
        double localX = (x - cellX * scale) / scale;
        double localZ = (z - cellZ * scale) / scale;
        double blendX = Smoothstep(localX);
        double blendZ = Smoothstep(localZ);
        double near = Lerp(HashUnit(CoordinateHash(seed, cellX, cellZ)),
            HashUnit(CoordinateHash(seed, cellX + 1, cellZ)), blendX);
        double far = Lerp(HashUnit(CoordinateHash(seed, cellX, cellZ + 1)),
            HashUnit(CoordinateHash(seed, cellX + 1, cellZ + 1)), blendX);
        return Lerp(near, far, blendZ);
    }

    /// <summary>The cubic smoothstep, 3t^2 - 2t^3, which eases a blend in and out of each cell.</summary>
    private static double Smoothstep(double value) => value * value * (3d - (2d * value));

    private static double Lerp(double left, double right, double amount) => left + ((right - left) * amount);

    internal static double HashUnit(ulong value) => (value >> GenerationConstants.HashFractionShift)
        / (double)GenerationConstants.HashFractionMaximum;

    internal static ulong CoordinateHash(ulong seed, long x, long z)
    {
        unchecked
        {
            ulong value = seed ^ ((ulong)x * GenerationConstants.CoordinateXMultiplier);
            value ^= BitOperations.RotateLeft((ulong)z, GenerationConstants.CoordinateRotation)
                * GenerationConstants.CoordinateZMultiplier;
            value ^= value >> GenerationConstants.FirstHashShift;
            value *= GenerationConstants.CoordinateZMultiplier;
            value ^= value >> GenerationConstants.SecondHashShift;
            return (value * GenerationConstants.CoordinateHashMultiplier) ^ (value >> GenerationConstants.FinalHashShift);
        }
    }

    /// <summary>
    /// The surface feature covering one air voxel, or empty. A tree's voxels are its trunk core,
    /// a short column inside the anchor cell that owns it; the cell decides from its own
    /// coordinates and the world's contract, so two chunks agree about it without communicating.
    /// </summary>
    private ushort FeatureMaterialAt(long x, long y, long z)
    {
        long cell = TreeCellMetres;
        long anchorX = GridMath.FloorDivide(x, cell);
        long anchorZ = GridMath.FloorDivide(z, cell);
        if (TreeAt(anchorX, anchorZ) is not TreeShape tree
            || x != (anchorX * cell) + tree.OffsetX || z != (anchorZ * cell) + tree.OffsetZ)
        {
            return TerrainConstants.EmptyMaterial;
        }

        long baseY = tree.GroundY;
        return y >= baseY && y < baseY + GenerationConstants.TreeCoreHeight
            ? (ushort)BlockId.TreeCore
            : TerrainConstants.EmptyMaterial;
    }

    /// <summary>The tree whose trunk stands in this column, if any.</summary>
    internal TerrainTree? TreeAtColumn(long x, long z)
    {
        long cell = TreeCellMetres;
        long anchorX = GridMath.FloorDivide(x, cell);
        long anchorZ = GridMath.FloorDivide(z, cell);
        return TreeAt(anchorX, anchorZ) is TreeShape tree && x == (anchorX * cell) + tree.OffsetX && z == (anchorZ * cell) + tree.OffsetZ
            ? new TerrainTree(x, tree.GroundY, z, tree.Species, tree.Variant, tree.Scale, tree.Yaw)
            : null;
    }

    /// <summary>
    /// The trees whose trunks stand in a rectangle of columns (inclusive), for the tree
    /// presenter: where each stands, what it is and how it is turned and sized. The same
    /// decisions as the voxels, so a drawn tree always stands on its trunk core.
    /// </summary>
    internal void TreesIn(long minimumX, long minimumZ, long maximumX, long maximumZ, List<TerrainTree> into)
    {
        long cell = TreeCellMetres;
        for (long anchorX = GridMath.FloorDivide(minimumX, cell); anchorX <= GridMath.FloorDivide(maximumX, cell); anchorX++)
        {
            for (long anchorZ = GridMath.FloorDivide(minimumZ, cell); anchorZ <= GridMath.FloorDivide(maximumZ, cell); anchorZ++)
            {
                if (TreeAt(anchorX, anchorZ) is not TreeShape tree)
                {
                    continue;
                }

                long x = (anchorX * cell) + tree.OffsetX;
                long z = (anchorZ * cell) + tree.OffsetZ;
                if (x < minimumX || x > maximumX || z < minimumZ || z > maximumZ)
                {
                    continue;
                }

                into.Add(new TerrainTree(x, tree.GroundY, z, tree.Species, tree.Variant, tree.Scale, tree.Yaw));
            }
        }
    }

    /// <summary>
    /// The tree one anchor cell owns and that stands as generated, if any, decided afresh rather
    /// than through the feature cache, for a presenter that keeps its own (the distant trees,
    /// #9677): a sweep over thousands of distant cells must not evict the decisions chunk
    /// generation is using. A structure pass (a cave mouth's carve, a crossing) may take a planted
    /// tree's core or footing; such a tree is read back through the final materials and left out
    /// if it does not stand. Only those few trees touch the materials, so the sweep stays bounded.
    /// </summary>
    internal TerrainTree? TreeInCell(long anchorX, long anchorZ)
    {
        if (DecideTree(anchorX, anchorZ) is not TreeShape shape)
        {
            return null;
        }

        long x = (anchorX * TreeCellMetres) + shape.OffsetX;
        long z = (anchorZ * TreeCellMetres) + shape.OffsetZ;
        TerrainTree tree = new(x, shape.GroundY, z, shape.Species, shape.Variant, shape.Scale, shape.Yaw);
        bool structured = TreeFelling.Core(tree).Append(TreeFelling.Support(tree))
            .Any(cell => !PoiAt(cell.X, cell.Y, cell.Z).IsNone || CrossingAt(cell.X, cell.Y, cell.Z) is not null);
        return !structured || TreeFelling.Stands(tree, MaterialAt) ? tree : null;
    }

    /// <summary>
    /// Whether one anchor cell owns a tree, and its shape. Decisions are cached
    /// because every voxel in the neighbourhood asks the same nine questions;
    /// clearing the cache is always safe because a decision is a pure function of
    /// the cell and the contract.
    /// </summary>
    private TreeShape? TreeAt(long anchorX, long anchorZ)
    {
        if (featureCells.TryGetValue((anchorX, anchorZ), out TreeShape? cached))
        {
            return cached;
        }

        TreeShape? shape = DecideTree(anchorX, anchorZ);
        featureCells.Set((anchorX, anchorZ), shape);
        return shape;
    }

    /// <summary>
    /// The one keyed draw a tree cell's choices are hashed from (#11179: zero-alloc worker-callable KeyedRng).
    /// </summary>
    private ulong TreeCellDraw(long anchorX, long anchorZ) =>
        (ulong)Contract.DrawLong(draws, "tree.cell", anchorX, anchorZ, 0, GenerationConstants.TreeCellDrawMaximum, TerrainGeneratorContract.TreeSeedVersion);

    private TreeShape? DecideTree(long anchorX, long anchorZ) => DecideTree(anchorX, anchorZ, TreeCellDraw(anchorX, anchorZ));

    private TreeShape? DecideTree(long anchorX, long anchorZ, ulong cell)
    {
        // One keyed draw per cell; every choice about its tree is hashed from it. The vegetation tiles
        // decide a 128 m tile's thousand cells at once, where a keyed draw per choice cost tens of
        // milliseconds a tile (#10032); the draws are made on the update's thread and the choices on a worker (#10907).
        double Part(TreeDraw part) => HashUnit(CoordinateHash(cell, (long)part, 0));

        long offsetX = (long)(Part(TreeDraw.OffsetX) * TreeCellMetres);
        long offsetZ = (long)(Part(TreeDraw.OffsetZ) * TreeCellMetres);
        long trunkX = (anchorX * TreeCellMetres) + offsetX;
        long trunkZ = (anchorZ * TreeCellMetres) + offsetZ;
        if (trunkX < -radius || trunkX > radius || trunkZ < -radius || trunkZ > radius)
        {
            return null;
        }

        // A canopy tree with a chance from how closed the forest is here, otherwise an open-country tree
        // from what suits the open ground; the species mostly the stand's own (Forest). Decided from the
        // map alone first, so most open cells never build their column.
        MapSample geography = Geography(trunkX, trunkZ);
        double canopy = Forest.Canopy(trunkX, trunkZ, geography);
        double inCanopy = canopy * Forest.CanopyTreesPerCell;
        double present = Part(TreeDraw.Present);
        if (present >= inCanopy + ((1 - canopy) * Forest.OpenTrees(geography)))
        {
            return null;
        }

        if (InTown(trunkX, trunkZ))
        {
            // A town's own layout says what grows on its pad.
            return null;
        }

        if (OnPlace(trunkX, trunkZ))
        {
            // A place is a mesh, not voxels (#9992): nothing in the ground stops a tree growing through it,
            // so its footprint and a margin about it stay clear, and it can be seen.
            return null;
        }

        TerrainColumn column = ColumnAt(trunkX, trunkZ);
        long ground = column.Surface;
        if (!TreeGround(new VoxelAddress(trunkX, ground, trunkZ), column))
        {
            // Water, a steep bank or a built surface is no place for a tree.
            return null;
        }

        FloraSpecies? species = Forest.TreeSpecies(geography, present < inCanopy, Forest.StandDraw(trunkX, trunkZ), Part(TreeDraw.Mix), Part(TreeDraw.Kind));
        if (species is null)
        {
            return null;
        }

        double scale = GenerationConstants.TreeScaleMinimum
            + ((GenerationConstants.TreeScaleMaximum - GenerationConstants.TreeScaleMinimum) * Part(TreeDraw.Scale));
        if (Part(TreeDraw.Giant) < GenerationConstants.TreeGiantShare)
        {
            // Now and then an old giant towers over its stand.
            scale *= GenerationConstants.TreeGiantScale;
        }

        return new TreeShape(offsetX, offsetZ, ground + 1, species.Id, Part(TreeDraw.Variant), scale, Part(TreeDraw.Yaw) * Math.Tau);
    }

    /// <summary>The choices hashed from a tree cell's one draw.</summary>
    private enum TreeDraw
    {
        OffsetX = 1,
        OffsetZ,
        Present,
        Mix,
        Kind,
        Scale,
        Giant,
        Variant,
        Yaw,
    }

    /// <summary>
    /// Whether a tree may root in a column's top cell: gentle, dry natural ground (meadow, forest floor,
    /// dry sand, rock or snow) outside a town's yard. Which species suit it is the forest's question.
    /// </summary>
    private bool TreeGround(VoxelAddress top, TerrainColumn column)
    {
        if (top.Y <= column.WaterTop || column.Slope > GenerationConstants.TopsoilSlopeMaximum) return false;
        ushort material = NaturalMaterialAt(top, column);
        return material is TerrainConstants.GrassMaterial or TerrainConstants.StoneMaterial
            || (BlockId)material is BlockId.Sand or BlockId.Snow or BlockId.DryGrass
            || (material == TerrainConstants.DirtMaterial && !InTownYard(top.X, top.Z));
    }

    /// <summary>
    /// The top cell of open, gentle ground that is neither frozen nor deep desert: forest floor under a closed canopy
    /// (#10032; Dirt grows meadow grass only as the forest's shade grass), else meadow, lush or sun-dried by the climate
    /// (<see cref="Soils"/>, #10041).
    /// </summary>
    internal BlockId Topsoil(long x, long z, MapSample geography) =>
        Forest.Canopy(x, z, geography) >= Forest.FloorFromCanopy ? BlockId.Dirt
        : Soils.Dry(geography, x, z, Contract.Seed) ? BlockId.DryGrass
        : BlockId.Grass;

    /// <summary>A decided tree; <c>GroundY</c> is its trunk's lowest cell, the one above the generated ground.</summary>
    internal readonly record struct TreeShape(long OffsetX, long OffsetZ, long GroundY, ushort Species, double Variant, double Scale, double Yaw);

}
