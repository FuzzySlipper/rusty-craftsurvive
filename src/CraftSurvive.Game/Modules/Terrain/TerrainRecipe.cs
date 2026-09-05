using System.Numerics;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Product-owned generation-v2 material policy. It deliberately emits only
/// material facts; Engine integration turns those facts into spatial authority.
/// </summary>
internal sealed class TerrainRecipe
{
    private readonly TerrainConfiguration configuration;
    private readonly long radius;

    internal TerrainRecipe(TerrainConfiguration configuration)
    {
        this.configuration = configuration.Validate();
        radius = configuration.Size / 2;
    }

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
        long x = address.X;
        long y = address.Y;
        long z = address.Z;

        if (IsInRange(x, TerrainConstants.RouteXMinimum, TerrainConstants.RouteXMaximum)
            && IsInRange(z, TerrainConstants.RouteZMinimum, TerrainConstants.RouteZMaximum)
            && y >= MinimumMaterialY)
        {
            material = RouteMaterialAt(y, TerrainConstants.RouteTop);
        }

        if (IsInRange(z, TerrainConstants.RouteTurnZMinimum, TerrainConstants.RouteTurnZMaximum)
            && y >= MinimumMaterialY)
        {
            material = RouteMaterialAt(y, TerrainConstants.RouteTop);
        }

        if (IsInRange(x, TerrainConstants.ClearingMinimum, TerrainConstants.ClearingMaximum)
            && IsInRange(z, TerrainConstants.ClearingMinimum, TerrainConstants.ClearingMaximum)
            && y >= MinimumMaterialY)
        {
            material = RouteMaterialAt(y, TerrainConstants.RouteTop);
        }

        // Keep the renderer comparison fixtures on a deliberately boring,
        // walkable pad beside the old traversal route. Their silhouettes and
        // ground contact should never depend on the procedural height field.
        if (IsInRange(x, TerrainConstants.ShowcaseXMinimum, TerrainConstants.ShowcaseXMaximum)
            && IsInRange(z, TerrainConstants.ShowcaseZMinimum, TerrainConstants.ShowcaseZMaximum)
            && y >= MinimumMaterialY)
        {
            material = RouteMaterialAt(y, TerrainConstants.ShowcaseTop);
        }

        if (IsInRange(x, TerrainConstants.RouteGapXMinimum, TerrainConstants.RouteGapXMaximum)
            && ((z == TerrainConstants.RouteFirstGapZ && y == TerrainConstants.RouteFirstGapY)
                || (z == TerrainConstants.RouteSecondGapZ && y == TerrainConstants.RouteSecondGapY)))
        {
            material = TerrainConstants.EmptyMaterial;
        }

        if (IsInRange(x, TerrainConstants.RouteXMinimum, TerrainConstants.RouteXMaximum)
            && IsInRange(z, TerrainConstants.RouteTrenchZMinimum, TerrainConstants.RouteTrenchZMaximum)
            && y >= MinimumMaterialY)
        {
            material = RouteMaterialAt(y, TerrainConstants.RouteTrenchTop);
        }

        if (IsInRange(x, TerrainConstants.RouteGapXMinimum, TerrainConstants.RouteGapXMaximum)
            && z == TerrainConstants.RouteBridgeZ
            && IsInRange(y, TerrainConstants.RouteBridgeYMinimum, TerrainConstants.RouteBridgeYMaximum))
        {
            material = TerrainConstants.StoneMaterial;
        }

        if (x == TerrainConstants.LeftPillarX && z == TerrainConstants.PillarZ
            && IsInRange(y, TerrainConstants.LeftPillarYMinimum, TerrainConstants.LeftPillarYMaximum))
        {
            material = TerrainConstants.DirtMaterial;
        }

        if (x == TerrainConstants.RightPillarX && z == TerrainConstants.PillarZ
            && IsInRange(y, TerrainConstants.RightPillarYMinimum, TerrainConstants.RightPillarYMaximum))
        {
            material = TerrainConstants.StoneMaterial;
        }

        AddLandmarks(x, y, z, column.Surface, ref material);
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

    private static ushort RouteMaterialAt(long y, long top) => y <= top
        ? y == top
            ? TerrainConstants.GrassMaterial
            : y >= TerrainConstants.RouteDirtMinimum
                ? TerrainConstants.DirtMaterial
                : TerrainConstants.StoneMaterial
        : TerrainConstants.EmptyMaterial;

    private static bool IsInRange(long value, long minimum, long maximum) => value >= minimum && value <= maximum;
}

internal readonly record struct TerrainColumn(long Surface, long Slope);
