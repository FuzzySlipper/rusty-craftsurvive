using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Product-owned generation-v2 material policy. It deliberately emits only
/// material facts; Engine integration turns those facts into spatial authority.
/// </summary>
internal sealed class TerrainRecipe
{
    private readonly TerrainConfiguration configuration;
    private readonly ITerrainDraws draws;
    private readonly long radius;
    private readonly Dictionary<(long X, long Z), TreeShape?> featureCells = [];

    internal TerrainRecipe(TerrainConfiguration configuration, ITerrainDraws draws)
    {
        this.configuration = configuration.Validate();
        this.draws = draws ?? throw new ArgumentNullException(nameof(draws));
        radius = configuration.Size / 2;
    }

    /// <summary>The versioned identity every feature draw is keyed from.</summary>
    internal TerrainGeneratorContract Contract => configuration.Contract;

    internal TerrainConfiguration Configuration => configuration;

    internal long MinimumMaterialY => -TerrainConstants.TerrainDepth;

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

    internal ushort MaterialAt(VoxelAddress address, TerrainColumn column)
    {
        if (address.X < -radius || address.X > radius || address.Z < -radius || address.Z > radius)
        {
            return TerrainConstants.EmptyMaterial;
        }

        ushort material = NaturalMaterialAt(address, column);
        AddLandmarks(address.X, address.Y, address.Z, column.Surface, ref material);

        // Features only fill air, so they never displace terrain: a canopy that
        // meets a slope loses to the slope rather than leaving a floating leaf.
        if (material == TerrainConstants.EmptyMaterial)
        {
            material = FeatureMaterialAt(address.X, address.Y, address.Z, column.Surface);
        }

        return material;
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

    private void AddLandmarks(long x, long y, long z, long surface, ref ushort material)
    {
        long distance = radius * 2 / 3;
        ApplyLandmark(-distance, 0, TerrainConstants.StoneMaterial,
            TerrainConstants.LandmarkHeightFirst, x, y, z, surface, ref material);
        ApplyLandmark(distance, 0, TerrainConstants.DirtMaterial,
            TerrainConstants.LandmarkHeightSecond, x, y, z, surface, ref material);
        ApplyLandmark(0, -distance, TerrainConstants.StoneMaterial,
            TerrainConstants.LandmarkHeightThird, x, y, z, surface, ref material);
    }

    private void ApplyLandmark(long landmarkX, long landmarkZ, ushort materialSlot, int height,
        long x, long y, long z, long surface, ref ushort material)
    {
        if (x != landmarkX || z != landmarkZ)
        {
            return;
        }

        long firstY = surface + 1;
        long lastY = surface + height;
        if (IsInRange(y, firstY, lastY))
        {
            material = materialSlot;
        }
    }

    private long TerrainSurface(long x, long z) => TerrainHeight(x, z);

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
    /// A feature voxel is only placed when its block can be bound to the scene. The
    /// decision and its draws still happen, so the contract stays exercised and the
    /// world returns unchanged the moment the Engine's material capacity admits the
    /// block; until then a tree would be a voxel whose slot has no material, which
    /// fails the scene projection instead of drawing nothing. With only grass, dirt
    /// and stone bound, this means no tree is placed today: the feature layer is
    /// implemented, drawn and proven, but dormant.
    /// </summary>
    private static ushort Placeable(BlockId id) =>
        BlockRegistry.IsBound(id) ? (ushort)id : TerrainConstants.EmptyMaterial;

    private static long FloorDivide(long value, long divisor) =>
        value >= 0 ? value / divisor : ((value - divisor + 1) / divisor);

    private readonly record struct TreeShape(long OffsetX, long OffsetZ, long Height, long CanopyRadius);

    private static bool IsInRange(long value, long minimum, long maximum) => value >= minimum && value <= maximum;
}

internal readonly record struct TerrainColumn(long Surface, long Slope);
