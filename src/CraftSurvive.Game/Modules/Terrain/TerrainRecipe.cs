using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Discovery;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Product-owned generation-v2 material policy. It deliberately emits only
/// material facts; Engine integration turns those facts into spatial authority.
/// </summary>
internal sealed class TerrainRecipe : ITerrainColumns
{
    private readonly TerrainConfiguration configuration;
    private readonly ITerrainDraws draws;
    private readonly long radius;
    private readonly Dictionary<(long X, long Z), TreeShape?> featureCells = [];
    private readonly PoiPlacement pois;
    private readonly CrossingPlacement crossings;

    internal TerrainRecipe(TerrainConfiguration configuration, ITerrainDraws draws)
    {
        this.configuration = configuration.Validate();
        this.draws = draws ?? throw new ArgumentNullException(nameof(draws));
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

    private const long MinimumMaterialYValue = -TerrainConstants.TerrainDepth;

    internal long MinimumMaterialY => MinimumMaterialYValue;

    internal long MaximumMaterialY => TerrainConstants.TerrainSummitHeight + TerrainConstants.TerrainHeadroom;

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
        ushort material = BaseMaterialAt(address, column);

        // Structures answer last, because they are the one pass that may take material
        // away and the only pass that may pave the course it stands on. A cut has to
        // survive whatever the ground, the water and the features put there first, so a
        // cut is final; a fill reaches only air, or the surface course it stands on, so a
        // wall that meets a slope loses to the slope rather than hollowing the hillside.
        PoiVoxel poi = PoiAt(address.X, address.Y, address.Z);
        if (poi.Kind == PoiVoxelKind.Carve)
        {
            // The cut stops one step below the local ground, which is the deepest floor a character
            // with no climb reach can step back out of. It is still a pit - a shallow, escapable
            // one - and the descent a dungeon entrance promises is the dimension slice's to add.
            return address.Y >= column.Surface - TerrainConstants.MaximumStructureStepBelowGround
                ? TerrainConstants.EmptyMaterial
                : material;
        }

        if (poi.Kind == PoiVoxelKind.Fill
            && (material == TerrainConstants.EmptyMaterial || address.Y == column.Surface))
        {
            material = poi.Material;
        }

        // Crossings come after sites for the same reason sites come after the ground: a
        // bridge is built over water, and water is what the ground pass leaves behind. It only
        // fills, so a crossing is something walked over rather than a dam across it.
        if (CrossingAt(address.X, address.Y, address.Z) is PoiVoxel span
            && span.Kind == PoiVoxelKind.Fill
            && (material == TerrainConstants.EmptyMaterial || address.Y == column.Surface))
        {
            material = span.Material;
        }

        return material;
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
            && address.Y <= TerrainConstants.WaterLevel
            && address.Y > column.Surface)
        {
            return (ushort)BlockId.Water;
        }

        // Features only fill air, so they never displace terrain: a canopy that
        // meets a slope loses to the slope rather than leaving a floating leaf.
        if (material == TerrainConstants.EmptyMaterial)
        {
            material = FeatureMaterialAt(address.X, address.Y, address.Z, column.Surface);
        }

        return material;
    }

    /// <summary>The world's floor: bedrock under everything, at the stated depth.</summary>
    private static bool IsWorldFloor(long y) => y < MinimumMaterialYValue + TerrainConstants.WorldFloorThickness;

    /// <summary>
    /// The world's border wall. It stands at the extent edge on all four sides up to
    /// a stated height, so the finite world has an authored edge rather than a void
    /// the player can walk into.
    /// </summary>
    private static bool IsWorldWall(long x, long z, long y)
    {
        if (y > TerrainConstants.WorldWallTop || y < MinimumMaterialYValue)
        {
            return false;
        }

        long limit = TerrainConstants.DefaultSize / 2;
        long inner = limit - TerrainConstants.WorldWallThickness;
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
        long depthFromSurface = top - address.Y;
        if (depthFromSurface == 0 && slope <= TerrainConstants.TopsoilSlopeMaximum)
        {
            return TerrainConstants.GrassMaterial;
        }

        return depthFromSurface <= TerrainConstants.SubsoilDepthMaximum
            && slope <= TerrainConstants.SubsoilSlopeMaximum
            ? TerrainConstants.DirtMaterial
            : TerrainConstants.StoneMaterial;
    }

    /// <summary>
    /// The structure material at one voxel, asked of whichever of the nine anchor cells
    /// that touch this voxel's own cell owns a site covering it. Each cell decides for
    /// itself from its own coordinates and the contract, so two chunks that share a
    /// structure agree about it without communicating and without an order - the same
    /// property the surface-feature pass has, at a far coarser lattice.
    /// </summary>
    /// <summary>
    /// The bridge voxel at one position, asked of the nine cells that touch this one. A
    /// crossing is decided by its own cell and reaches at most a span plus its abutments, so
    /// the same nine-cell neighbourhood is enough.
    /// </summary>
    private PoiVoxel? CrossingAt(long x, long y, long z)
    {
        long cell = PoiConstants.CellSize;
        long cellX = FloorDivide(x, cell);
        long cellZ = FloorDivide(z, cell);
        for (long anchorX = cellX - 1; anchorX <= cellX + 1; anchorX++)
        {
            for (long anchorZ = cellZ - 1; anchorZ <= cellZ + 1; anchorZ++)
            {
                if (crossings.SiteAt(anchorX, anchorZ) is not CrossingSite site)
                {
                    continue;
                }

                PoiVoxel voxel = CrossingStructure.MaterialAt(site, x, y, z);
                if (!voxel.IsNone)
                {
                    return voxel;
                }
            }
        }

        return null;
    }

    private PoiVoxel PoiAt(long x, long y, long z)
    {
        long cell = PoiConstants.CellSize;
        long cellX = FloorDivide(x, cell);
        long cellZ = FloorDivide(z, cell);
        for (long anchorX = cellX - 1; anchorX <= cellX + 1; anchorX++)
        {
            for (long anchorZ = cellZ - 1; anchorZ <= cellZ + 1; anchorZ++)
            {
                if (pois.SiteAt(anchorX, anchorZ) is not PoiSite site)
                {
                    continue;
                }

                PoiVoxel voxel = PoiStructures.MaterialAt(site, x, y, z);
                if (!voxel.IsNone)
                {
                    return voxel;
                }
            }
        }

        return PoiVoxel.None;
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

        bool waterReaches = yMinimum <= TerrainConstants.WaterLevel;
        if (yMinimum <= TerrainConstants.WorldWallTop
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

                if (waterReaches && surface < TerrainConstants.WaterLevel)
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
        long cell = TerrainConstants.FeatureCellSize;
        long reach = TerrainConstants.TreeCanopyRadius;
        long firstCellX = FloorDivide(xStart - reach, cell);
        long lastCellX = FloorDivide(xEnd + reach, cell);
        long firstCellZ = FloorDivide(zStart - reach, cell);
        long lastCellZ = FloorDivide(zEnd + reach, cell);
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
        long firstCellX = FloorDivide(xStart - reach, cell);
        long lastCellX = FloorDivide(xEnd + reach, cell);
        long firstCellZ = FloorDivide(zStart - reach, cell);
        long lastCellZ = FloorDivide(zEnd + reach, cell);
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
                            ushort after = poi.Kind == PoiVoxelKind.Carve
                                ? y >= column.Surface - TerrainConstants.MaximumStructureStepBelowGround
                                    ? TerrainConstants.EmptyMaterial
                                    : before
                                : before == TerrainConstants.EmptyMaterial || y == column.Surface
                                    ? poi.Material
                                    : before;
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
        long reach = PoiConstants.CrossingMaximumSpan + 2;
        long firstCellX = FloorDivide(xStart - reach, cell);
        long lastCellX = FloorDivide(xEnd + reach, cell);
        long firstCellZ = FloorDivide(zStart - reach, cell);
        long lastCellZ = FloorDivide(zEnd + reach, cell);
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
                            if (before == TerrainConstants.EmptyMaterial || y == column.Surface)
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
        long limit = TerrainConstants.DefaultSize / 2;
        long inner = limit - TerrainConstants.WorldWallThickness;
        bool xEdge = xMinimum <= -inner || xMaximum >= inner;
        bool zEdge = zMinimum <= -inner || zMaximum >= inner;
        bool inside = xMaximum >= -limit && xMinimum <= limit && zMaximum >= -limit && zMinimum <= limit;
        return (xEdge || zEdge) && inside;
    }

    private long TerrainHeight(long x, long z)
    {
        double broad = ValueNoise(configuration.Seed, x, z, TerrainConstants.BroadNoiseScale);
        double rolling = ValueNoise(configuration.Seed ^ TerrainConstants.RollingNoiseSalt, x, z, TerrainConstants.RollingNoiseScale);
        double detail = ValueNoise(configuration.Seed ^ TerrainConstants.DetailNoiseSalt, x, z, TerrainConstants.DetailNoiseScale);
        double ridge = TerrainConstants.One - Math.Abs((rolling * TerrainConstants.Two) - TerrainConstants.One);
        double height = TerrainConstants.HeightBase
            + (broad * TerrainConstants.BroadWeight)
            + ((broad - TerrainConstants.BroadCenter) * TerrainConstants.BroadDeviationWeight)
            + (ridge * TerrainConstants.RidgeWeight)
            + ((detail - TerrainConstants.BroadCenter) * TerrainConstants.DetailDeviationWeight)
            + (ValueNoise(configuration.Seed ^ TerrainConstants.LargeNoiseSalt, x, z, TerrainConstants.LargeNoiseScale)
                * TerrainConstants.LargeWeight);
        return Math.Max((long)Math.Round(height, MidpointRounding.AwayFromZero), TerrainConstants.MinimumTerrainHeight);
    }

    private long CardinalSlope(long x, long z, long top)
    {
        long west = Math.Abs(top - TerrainSurface(x - 1, z));
        long east = Math.Abs(top - TerrainSurface(x + 1, z));
        long north = Math.Abs(top - TerrainSurface(x, z - 1));
        long south = Math.Abs(top - TerrainSurface(x, z + 1));
        return Math.Max(Math.Max(west, east), Math.Max(north, south));
    }

    private static double ValueNoise(ulong seed, long x, long z, int scale)
    {
        long cellX = FloorDivide(x, scale);
        long cellZ = FloorDivide(z, scale);
        double localX = PositiveMod(x, scale) / (double)scale;
        double localZ = PositiveMod(z, scale) / (double)scale;
        double blendX = Smoothstep(localX);
        double blendZ = Smoothstep(localZ);
        double near = Lerp(HashUnit(CoordinateHash(seed, cellX, cellZ)),
            HashUnit(CoordinateHash(seed, cellX + 1, cellZ)), blendX);
        double far = Lerp(HashUnit(CoordinateHash(seed, cellX, cellZ + 1)),
            HashUnit(CoordinateHash(seed, cellX + 1, cellZ + 1)), blendX);
        return Lerp(near, far, blendZ);
    }

    private static long FloorDivide(long value, int divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    private static long PositiveMod(long value, int divisor)
    {
        long remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static double Smoothstep(double value) => value * value
        * (TerrainConstants.SmoothstepFirstFactor - (TerrainConstants.Two * value));

    private static double Lerp(double left, double right, double amount) => left + ((right - left) * amount);

    private static double HashUnit(ulong value) => (value >> TerrainConstants.HashFractionShift)
        / (double)TerrainConstants.HashFractionMaximum;

    private static ulong CoordinateHash(ulong seed, long x, long z)
    {
        unchecked
        {
            ulong value = seed ^ ((ulong)x * TerrainConstants.CoordinateXMultiplier);
            value ^= BitOperations.RotateLeft((ulong)z, TerrainConstants.CoordinateRotation)
                * TerrainConstants.CoordinateZMultiplier;
            value ^= value >> TerrainConstants.FirstHashShift;
            value *= TerrainConstants.CoordinateZMultiplier;
            value ^= value >> TerrainConstants.SecondHashShift;
            return (value * TerrainConstants.CoordinateHashMultiplier) ^ (value >> TerrainConstants.FinalHashShift);
        }
    }

    /// <summary>
    /// The surface feature covering one air voxel, or empty. Every candidate anchor
    /// cell within one cell of this voxel decides for itself whether it owns a tree,
    /// using only its own coordinates and the world's contract, so two chunks that
    /// share a tree agree about it without communicating and without an order.
    /// </summary>
    private ushort FeatureMaterialAt(long x, long y, long z, long surface)
    {
        long cell = TerrainConstants.FeatureCellSize;
        long cellX = FloorDivide(x, cell);
        long cellZ = FloorDivide(z, cell);
        for (long anchorX = cellX - 1; anchorX <= cellX + 1; anchorX++)
        {
            for (long anchorZ = cellZ - 1; anchorZ <= cellZ + 1; anchorZ++)
            {
                if (TreeAt(anchorX, anchorZ) is not TreeShape tree)
                {
                    continue;
                }

                long trunkX = (anchorX * cell) + tree.OffsetX;
                long trunkZ = (anchorZ * cell) + tree.OffsetZ;
                long ground = TerrainSurface(trunkX, trunkZ);
                long baseY = ground + 1;
                long crownY = baseY + tree.Height;

                if (x == trunkX && z == trunkZ && y >= baseY && y < crownY)
                {
                    return Placeable(BlockId.Log);
                }

                long dx = x - trunkX;
                long dz = z - trunkZ;
                long dy = y - crownY;
                if ((dx * dx) + (dy * dy) + (dz * dz) <= tree.CanopyRadius * tree.CanopyRadius)
                {
                    return Placeable(BlockId.Leaves);
                }
            }
        }

        return TerrainConstants.EmptyMaterial;
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
        if (featureCells.Count >= TerrainConstants.FeatureCacheLimit)
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
        long originX = anchorX * TerrainConstants.FeatureCellSize;
        long originZ = anchorZ * TerrainConstants.FeatureCellSize;
        long offsetX = Contract.DrawLong(draws, "tree.offset.x", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            0, TerrainConstants.FeatureCellSize - 1);
        long offsetZ = Contract.DrawLong(draws, "tree.offset.z", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            0, TerrainConstants.FeatureCellSize - 1);
        long trunkX = originX + offsetX;
        long trunkZ = originZ + offsetZ;
        if (trunkX < -radius || trunkX > radius || trunkZ < -radius || trunkZ > radius)
        {
            return null;
        }

        long ground = TerrainSurface(trunkX, trunkZ);
        if (ground <= TerrainConstants.WaterLevel)
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
            TerrainConstants.FeatureCellOneIn))
        {
            return null;
        }

        long height = Contract.DrawLong(draws, "tree.height", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            TerrainConstants.TreeMinimumHeight, TerrainConstants.TreeMinimumHeight + TerrainConstants.TreeHeightRange - 1);
        long canopy = Contract.DrawLong(draws, "tree.canopy", TerrainGeneratorContract.CoordinateKey(anchorX, anchorZ),
            TerrainConstants.TreeCanopyRadius - 1, TerrainConstants.TreeCanopyRadius);
        return new TreeShape(offsetX, offsetZ, height, canopy);
    }

    /// <summary>
    /// A feature voxel is only placed when its block can be bound to the scene, so a
    /// block with no material cannot reach the projection and fail it. Every block in the
    /// V1 floor is bound - the Engine admits sixteen authored materials per scene and
    /// this floor is exactly sixteen - so this is a guard a new block must clear rather
    /// than a restriction on the current palette.
    /// </summary>
    private static ushort Placeable(BlockId id) =>
        BlockRegistry.IsBound(id) ? (ushort)id : TerrainConstants.EmptyMaterial;

    private static long FloorDivide(long value, long divisor) =>
        value >= 0 ? value / divisor : ((value - divisor + 1) / divisor);

    private readonly record struct TreeShape(long OffsetX, long OffsetZ, long Height, long CanopyRadius);

    private static bool IsInRange(long value, long minimum, long maximum) => value >= minimum && value <= maximum;
}
