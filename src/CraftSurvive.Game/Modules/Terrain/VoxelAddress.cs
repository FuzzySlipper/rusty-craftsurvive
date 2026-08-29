namespace CraftSurvive.Game.Modules.Terrain;

internal readonly record struct VoxelAddress(long X, long Y, long Z) : IComparable<VoxelAddress>
{
    internal TerrainChunkAddress Chunk => new(
        FloorDivide(X, TerrainConstants.ChunkEdgeLength),
        FloorDivide(Y, TerrainConstants.ChunkEdgeLength),
        FloorDivide(Z, TerrainConstants.ChunkEdgeLength));

    public int CompareTo(VoxelAddress other)
    {
        int x = X.CompareTo(other.X);
        if (x != 0)
        {
            return x;
        }

        int y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }

    internal bool IsWithinWorldBounds => X >= -TerrainConstants.MaximumCoordinateMagnitude
        && X <= TerrainConstants.MaximumCoordinateMagnitude
        && Y >= -TerrainConstants.MaximumCoordinateMagnitude
        && Y <= TerrainConstants.MaximumCoordinateMagnitude
        && Z >= -TerrainConstants.MaximumCoordinateMagnitude
        && Z <= TerrainConstants.MaximumCoordinateMagnitude;

    internal void Validate()
    {
        if (!IsWithinWorldBounds)
        {
            throw new ArgumentOutOfRangeException(nameof(VoxelAddress), this,
                $"Voxel coordinates must remain within ±{TerrainConstants.MaximumCoordinateMagnitude}.");
        }
    }

    private static long FloorDivide(long value, int divisor)
    {
        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder < 0 ? quotient - 1L : quotient;
    }
}

internal readonly record struct TerrainChunkAddress(long X, long Y, long Z) : IComparable<TerrainChunkAddress>
{
    internal VoxelAddress Origin => new(
        checked(X * TerrainConstants.ChunkEdgeLength),
        checked(Y * TerrainConstants.ChunkEdgeLength),
        checked(Z * TerrainConstants.ChunkEdgeLength));

    public int CompareTo(TerrainChunkAddress other)
    {
        int x = X.CompareTo(other.X);
        if (x != 0)
        {
            return x;
        }

        int y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }
}
