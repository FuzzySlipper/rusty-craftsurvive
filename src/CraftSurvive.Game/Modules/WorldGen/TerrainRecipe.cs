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
    private const double ErodedRockThreshold = 0.65;
    private const double MinimumErosionExposure = 0.08;
    private readonly TerrainConfiguration configuration;
    private readonly ITerrainDraws draws;
    private readonly long radius;
    private readonly Dictionary<(long X, long Z), TreeShape?> featureCells = [];
    private readonly PoiPlacement pois;
    private readonly CrossingPlacement crossings;

    internal TerrainRecipe(TerrainConfiguration configuration, ITerrainDraws draws, WorldMap? map = null)
    {
        this.configuration = configuration.Validate();
        this.draws = draws ?? throw new ArgumentNullException(nameof(draws));
        Map = map ?? WorldMapGenerator.Generate(configuration);
        if (Map.Configuration != configuration) throw new ArgumentException("Terrain and map must share an identity.", nameof(map));
        radius = configuration.Size / 2;
        pois = new PoiPlacement(configuration.Contract, draws, this, radius);
        crossings = new CrossingPlacement(configuration.Contract, draws, this, radius);
    }

    /// <summary>The versioned identity every feature draw is keyed from.</summary>
    internal TerrainGeneratorContract Contract => configuration.Contract;

    /// <summary>Where this world's crossings are, for the pass that walks over water.</summary>
    internal CrossingPlacement Crossings => crossings;

    /// <summary>Where this world's places are, for the pass that records finding them.</summary>
    internal PoiPlacement Placement => pois;

    /// <summary>The recipe as the column source placement reads, so callers need no cast.</summary>
    internal ITerrainColumns Columns => this;

    internal TerrainConfiguration Configuration => configuration;

    internal WorldMap Map { get; }

    private const long MinimumMaterialYValue = -GenerationConstants.TerrainDepth;

    internal long MinimumMaterialY => MinimumMaterialYValue;

    internal long MaximumMaterialY => (long)(WorldMap.MaximumElevation + WorldMap.LocalReliefLimit) + GenerationConstants.TerrainHeadroom;

    internal ushort MaterialAt(VoxelAddress address) => MaterialAt(address, ColumnAt(address.X, address.Z));

    // Height and slope depend on x/z only. Dense chunk generation evaluates
    // them once per column, while individual voxel queries share the same rules.
    internal TerrainColumn ColumnAt(long x, long z)
    {
        if (x < -radius || x > radius || z < -radius || z > radius) return default;
        long surface = TerrainSurface(x, z);
        return new TerrainColumn(surface, CardinalSlope(x, z, surface));
    }

    /// <summary>
    /// The one thing site placement needs from the recipe: the ground and its slope.
    /// Explicit, so the recipe's own column query stays internal to the product.
    /// </summary>
    TerrainColumn ITerrainColumns.ColumnAt(long x, long z) => ColumnAt(x, z);

    internal ushort MaterialAt(VoxelAddress address, TerrainColumn column)
    {
        // Structures answer last, because they are the one pass that may take material away
        // and the only pass that may pave the course it stands on. Crossings come after sites
        // for the same reason sites come after the ground: a bridge is built over water, and
        // water is what the ground pass leaves behind.
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

        if (IsWorldFloor(address.Y) || IsWorldWall(address.X, address.Z, address.Y))
        {
            return (ushort)BlockId.Bedrock;
        }

        ushort material = NaturalMaterialAt(address, column);

        // Water fills open air at or below the world's water level, so it pools in
        // basins and along coasts. It deliberately does not flood enclosed space
        // below the ground - a cave under the sea stays a cave - because it only
        // fills where the column's own surface is below the water line.
        if (material == TerrainConstants.EmptyMaterial
            && address.Y <= GenerationConstants.WaterLevel
            && address.Y > column.Surface)
        {
            return (ushort)BlockId.Water;
        }

        // Features only fill air, so they never displace terrain: a canopy that
        // meets a slope loses to the slope rather than leaving a floating leaf.
        if (material == TerrainConstants.EmptyMaterial && !WorldMap.Arid(Map.Sample(address.X, address.Z)) && !WorldMap.Frozen(Map.Sample(address.X, address.Z)))
        {
            material = FeatureMaterialAt(address.X, address.Y, address.Z);
        }

        return material;
    }

    /// <summary>The world's floor: bedrock under everything, at the stated depth.</summary>
    private static bool IsWorldFloor(long y) => y < MinimumMaterialYValue + GenerationConstants.WorldFloorThickness;

    /// <summary>
    /// The world's border wall. It stands at this world's own extent edge on all four sides up
    /// to a stated height, so the finite world has an authored edge rather than a void the
    /// player can walk into.
    /// </summary>
    private bool IsWorldWall(long x, long z, long y)
    {
        if (y > GenerationConstants.WorldWallTop || y < MinimumMaterialYValue)
        {
            return false;
        }

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
        MapSample geography = Map.Sample(address.X, address.Z);
        // Incised shoulders expose stone; the quiet channel floor keeps regional soil/snow.
        if (geography.Erosion >= MinimumErosionExposure && geography.Rock >= ErodedRockThreshold)
            return TerrainConstants.StoneMaterial;
        if (slope <= GenerationConstants.TopsoilSlopeMaximum)
        {
            if (WorldMap.Frozen(geography)) return (ushort)BlockId.Snow;
            if (WorldMap.Arid(geography)) return (ushort)BlockId.Sand;
        }
        long depthFromSurface = top - address.Y;
        if (depthFromSurface == 0 && slope <= GenerationConstants.TopsoilSlopeMaximum)
        {
            return TerrainConstants.GrassMaterial;
        }

        return depthFromSurface <= GenerationConstants.SubsoilDepthMaximum
            && slope <= GenerationConstants.SubsoilSlopeMaximum
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

    private long TerrainSurface(long x, long z) => TerrainHeight(x, z);

    /// <summary>The generated surface height at a column, for callers that must reason about the world.</summary>
    internal long SurfaceAt(long x, long z) => TerrainSurface(x, z);

    /// <summary>The half-extent of the finite world this recipe generates.</summary>
    internal long Radius => radius;

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
        long edge = TerrainConstants.ChunkEdgeLength;
        long yMinimum = address.Y * edge;
        long yMaximum = yMinimum + edge - 1;

        // The floor and the border wall are authored everywhere they apply.
        if (yMinimum <= MinimumMaterialY)
        {
            return true;
        }

        bool waterReaches = yMinimum <= GenerationConstants.WaterLevel;
        if (yMinimum <= GenerationConstants.WorldWallTop
            && TouchesWorldEdge(address.X * edge, (address.X * edge) + edge - 1, address.Z * edge, (address.Z * edge) + edge - 1))
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

                long surface = TerrainSurface(x, z);
                if (surface >= yMinimum)
                {
                    return true;
                }

                if (waterReaches && surface < GenerationConstants.WaterLevel)
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
        long cell = GenerationConstants.FeatureCellSize;
        long reach = GenerationConstants.TreeCanopyRadius;
        long firstCellX = GridMath.FloorDivide(xStart - reach, cell);
        long lastCellX = GridMath.FloorDivide(xEnd + reach, cell);
        long firstCellZ = GridMath.FloorDivide(zStart - reach, cell);
        long lastCellZ = GridMath.FloorDivide(zEnd + reach, cell);
        for (long anchorX = firstCellX; anchorX <= lastCellX; anchorX++)
        {
            for (long anchorZ = firstCellZ; anchorZ <= lastCellZ; anchorZ++)
            {
                if (TreeAt(anchorX, anchorZ) is not TreeShape tree)
                {
                    continue;
                }

                long trunkX = (anchorX * cell) + tree.OffsetX;
                long trunkZ = (anchorZ * cell) + tree.OffsetZ;
                long crownY = TerrainSurface(trunkX, trunkZ) + 1 + tree.Height;
                long baseY = crownY - tree.Height;

                // The trunk: a column of log voxels.
                if (trunkX >= xStart && trunkX <= xEnd && trunkZ >= zStart && trunkZ <= zEnd
                    && baseY <= yMaximum && crownY - 1 >= yMinimum)
                {
                    return true;
                }

                // The canopy: a sphere centred at the top of the trunk. Tested voxel by
                // voxel over the overlap, because a bounding box would claim leaves in
                // the corners the generator leaves empty.
                long canopyXMinimum = Math.Max(xStart, trunkX - tree.CanopyRadius);
                long canopyXMaximum = Math.Min(xEnd, trunkX + tree.CanopyRadius);
                long canopyYMinimum = Math.Max(yMinimum, crownY - tree.CanopyRadius);
                long canopyYMaximum = Math.Min(yMaximum, crownY + tree.CanopyRadius);
                long canopyZMinimum = Math.Max(zStart, trunkZ - tree.CanopyRadius);
                long canopyZMaximum = Math.Min(zEnd, trunkZ + tree.CanopyRadius);
                for (long x = canopyXMinimum; x <= canopyXMaximum; x++)
                {
                    for (long y = canopyYMinimum; y <= canopyYMaximum; y++)
                    {
                        for (long z = canopyZMinimum; z <= canopyZMaximum; z++)
                        {
                            long dx = x - trunkX;
                            long dy = y - crownY;
                            long dz = z - trunkZ;
                            if ((dx * dx) + (dy * dy) + (dz * dz) <= tree.CanopyRadius * tree.CanopyRadius)
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
    private bool TouchesWorldEdge(long xMinimum, long xMaximum, long zMinimum, long zMaximum)
    {
        long limit = radius;
        long inner = limit - GenerationConstants.WorldWallThickness;
        bool xEdge = xMinimum <= -inner || xMaximum >= inner;
        bool zEdge = zMinimum <= -inner || zMaximum >= inner;
        bool inside = xMaximum >= -limit && xMinimum <= limit && zMaximum >= -limit && zMinimum <= limit;
        return (xEdge || zEdge) && inside;
    }

    /// <summary>
    /// The height field. Stable geographic noise lets map refinement preserve the underlying
    /// landforms; the full recipe version still invalidates saves and generated chunks.
    /// </summary>
    private long TerrainHeight(long x, long z) =>
        (long)Math.Round(ContinuousHeightAt(x, z), MidpointRounding.AwayFromZero);

    /// <summary>The unquantized height at a column's sample centre; DC receives this shape instead of stair steps.</summary>
    internal double ContinuousHeightAt(long x, long z)
    {
        MapSample geography = Map.Sample(x, z);
        return Math.Max(geography.Elevation + RegionalTerrain.Relief(Contract.GeographyNoiseSeed, geography, x, z),
            GenerationConstants.MinimumTerrainHeight);
    }

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

    private static double HashUnit(ulong value) => (value >> GenerationConstants.HashFractionShift)
        / (double)GenerationConstants.HashFractionMaximum;

    private static ulong CoordinateHash(ulong seed, long x, long z)
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
    /// The surface feature covering one air voxel, or empty. Every candidate anchor
    /// cell within one cell of this voxel decides for itself whether it owns a tree,
    /// using only its own coordinates and the world's contract, so two chunks that
    /// share a tree agree about it without communicating and without an order.
    /// </summary>
    private ushort FeatureMaterialAt(long x, long y, long z)
    {
        long cell = GenerationConstants.FeatureCellSize;
        return FirstInNeighbourhood<ushort>(x, z, cell, (anchorX, anchorZ) =>
        {
            if (TreeAt(anchorX, anchorZ) is not TreeShape tree)
            {
                return (ushort?)null;
            }

            long trunkX = (anchorX * cell) + tree.OffsetX;
            long trunkZ = (anchorZ * cell) + tree.OffsetZ;
            long baseY = TerrainSurface(trunkX, trunkZ) + 1;
            long crownY = baseY + tree.Height;
            if (x == trunkX && z == trunkZ && y >= baseY && y < crownY)
            {
                return (ushort)BlockId.Log;
            }

            long dx = x - trunkX;
            long dz = z - trunkZ;
            long dy = y - crownY;
            return (dx * dx) + (dy * dy) + (dz * dz) <= tree.CanopyRadius * tree.CanopyRadius
                ? (ushort)BlockId.Leaves
                : null;
        }) ?? TerrainConstants.EmptyMaterial;
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
        if (featureCells.Count >= GenerationConstants.FeatureCacheLimit)
        {
            featureCells.Clear();
        }

        featureCells[(anchorX, anchorZ)] = shape;
        return shape;
    }

    private TreeShape? DecideTree(long anchorX, long anchorZ)
    {
        // The surface is sampled at the candidate trunk column, so a cell whose
        // ground is not grass - water, sand, stone, or outside the world - owns no
        // tree. That keeps forests on soil and out of lakes without a biome pass.
        long originX = anchorX * GenerationConstants.FeatureCellSize;
        long originZ = anchorZ * GenerationConstants.FeatureCellSize;
        long offsetX = Contract.DrawLong(draws, "tree.offset.x", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            0, GenerationConstants.FeatureCellSize - 1);
        long offsetZ = Contract.DrawLong(draws, "tree.offset.z", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            0, GenerationConstants.FeatureCellSize - 1);
        long trunkX = originX + offsetX;
        long trunkZ = originZ + offsetZ;
        if (trunkX < -radius || trunkX > radius || trunkZ < -radius || trunkZ > radius)
        {
            return null;
        }

        long ground = TerrainSurface(trunkX, trunkZ);
        if (ground <= GenerationConstants.WaterLevel)
        {
            // A submerged column is not soil, so no tree stands in the water.
            return null;
        }

        if (NaturalMaterialAt(new VoxelAddress(trunkX, ground, trunkZ), ColumnAt(trunkX, trunkZ))
            != TerrainConstants.GrassMaterial)
        {
            return null;
        }

        if (!Contract.DrawUnit(draws, "tree.present", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            GenerationConstants.FeatureCellOneIn))
        {
            return null;
        }

        long height = Contract.DrawLong(draws, "tree.height", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            GenerationConstants.TreeMinimumHeight, GenerationConstants.TreeMinimumHeight + GenerationConstants.TreeHeightRange - 1);
        long canopy = Contract.DrawLong(draws, "tree.canopy", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            GenerationConstants.TreeCanopyRadius - 1, GenerationConstants.TreeCanopyRadius);
        return new TreeShape(offsetX, offsetZ, height, canopy);
    }

    private readonly record struct TreeShape(long OffsetX, long OffsetZ, long Height, long CanopyRadius);

}
