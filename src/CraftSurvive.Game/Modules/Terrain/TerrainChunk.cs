namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Dense product-owned material payload in the original z/y/x order. It is a
/// value to hand to a later Engine residency adapter, not a renderer payload.
/// </summary>
internal sealed class TerrainChunk
{
    private readonly ushort[] materials;

    internal TerrainChunk(TerrainChunkAddress address, ushort[] materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if (materials.Length != TerrainConstants.ChunkVolume)
        {
            throw new ArgumentException($"Terrain chunks require {TerrainConstants.ChunkVolume} material slots.", nameof(materials));
        }

        Address = address;
        this.materials = materials;
        SolidVoxelCount = CountSolid(materials);
    }

    internal TerrainChunkAddress Address { get; }

    internal ReadOnlyMemory<ushort> Materials => materials;

    internal int SolidVoxelCount { get; }

    private static int CountSolid(ushort[] materials)
    {
        int count = 0;
        foreach (ushort material in materials)
        {
            if (material != TerrainConstants.EmptyMaterial)
            {
                count++;
            }
        }

        return count;
    }
}

internal sealed class TerrainChunkGenerator
{
    private readonly TerrainRecipe recipe;
    private readonly TerrainChunkCache? cache;

    internal TerrainChunkGenerator(TerrainRecipe recipe, TerrainChunkCache? cache = null)
    {
        this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        this.cache = cache;
    }

    /// <summary>Chunks served from the cache rather than generated, for evidence.</summary>
    internal int CacheHits { get; private set; }

    internal TerrainChunk Generate(TerrainChunkAddress address, TerrainOverlaySnapshot overlay)
    {
        // Read-through: a chunk this world already generated comes back from the store
        // instead of being computed again. The cache is an optimisation, so a miss is
        // simply generation - and a hit is only trusted because the cache refuses any
        // payload whose shape does not match a chunk.
        if (cache is not null && !overlay.TouchesChunk(address) && cache.TryRead(address, out ushort[] cached))
        {
            CacheHits++;
            return new TerrainChunk(address, cached);
        }

        ArgumentNullException.ThrowIfNull(overlay);
        ushort[] materials = new ushort[TerrainConstants.ChunkVolume];
        VoxelAddress origin = address.Origin;
        for (int z = 0; z < TerrainConstants.ChunkEdgeLength; z++)
        {
            for (int x = 0; x < TerrainConstants.ChunkEdgeLength; x++)
            {
                TerrainColumn column = recipe.ColumnAt(origin.X + x, origin.Z + z);
                for (int y = 0; y < TerrainConstants.ChunkEdgeLength; y++)
                {
                    VoxelAddress voxel = new(origin.X + x, origin.Y + y, origin.Z + z);
                    ushort material = overlay.TryGetMaterial(voxel, out ushort overridden)
                        ? overridden
                        : recipe.MaterialAt(voxel, column);
                    materials[ToIndex(x, y, z)] = material;
                }
            }
        }

        return new TerrainChunk(address, materials);
    }

    private static int ToIndex(int x, int y, int z) => (z * TerrainConstants.ChunkPlaneLength)
        + (y * TerrainConstants.ChunkEdgeLength) + x;
}
