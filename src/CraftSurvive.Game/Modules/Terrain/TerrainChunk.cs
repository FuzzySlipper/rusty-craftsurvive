using CraftSurvive.Game.Modules.WorldGen;
namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Dense product-owned material and density payload in the original z/y/x order. It is a
/// value to hand to a later Engine residency adapter, not a renderer payload.
/// </summary>
internal sealed class TerrainChunk
{
    private readonly ushort[] materials;
    private readonly float[] densities;

    internal TerrainChunk(TerrainChunkAddress address, ushort[] materials, float[] densities)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(densities);
        if (materials.Length != TerrainConstants.ChunkVolume)
        {
            throw new ArgumentException($"Terrain chunks require {TerrainConstants.ChunkVolume} material slots.", nameof(materials));
        }

        Address = address;
        this.materials = materials;
        this.densities = densities;
        if (this.densities.Length != materials.Length)
        {
            throw new ArgumentException("Terrain density and material payloads must have equal lengths.", nameof(densities));
        }
        SolidVoxelCount = CountSolid(materials);
    }

    internal TerrainChunkAddress Address { get; }

    internal ReadOnlyMemory<ushort> Materials => materials;
    internal ReadOnlyMemory<float> Densities => densities;

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
        ArgumentNullException.ThrowIfNull(overlay);
        ushort[]? cached = null;
        if (cache is not null && !overlay.TouchesChunk(address) && cache.TryRead(address, out ushort[] found))
        {
            CacheHits++;
            cached = found;
        }

        ushort[] materials = cached ?? new ushort[TerrainConstants.ChunkVolume];
        float[] densities = new float[TerrainConstants.ChunkVolume];
        VoxelAddress origin = address.Origin;
        for (int z = 0; z < TerrainConstants.ChunkEdgeLength; z++)
        {
            for (int x = 0; x < TerrainConstants.ChunkEdgeLength; x++)
            {
                TerrainColumn column = cached is null ? recipe.ColumnAt(origin.X + x, origin.Z + z) : default;
                double height = recipe.ContinuousHeightAt(origin.X + x, origin.Z + z);
                for (int y = 0; y < TerrainConstants.ChunkEdgeLength; y++)
                {
                    VoxelAddress voxel = new(origin.X + x, origin.Y + y, origin.Z + z);
                    int index = ToIndex(x, y, z);
                    ushort generated = cached is not null ? cached[index] : recipe.MaterialAt(voxel, column);
                    ushort material = overlay.TryGetMaterial(voxel, out ushort overridden)
                        ? overridden
                        : generated;
                    materials[index] = material;
                    densities[index] = TerrainDensity.At(voxel.Y, height, generated, material);
                }
            }
        }

        return new TerrainChunk(address, materials, densities);
    }

    /// <summary>
    /// An edited chunk without generating it again (#9578): the previous payload with only the
    /// edited voxels recomputed, each from its generated material, its column's height and the
    /// overlay - exactly what <see cref="Generate"/> would produce for them.
    /// </summary>
    internal TerrainChunk Patch(TerrainChunk previous, IEnumerable<VoxelAddress> edited, TerrainOverlaySnapshot overlay)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(overlay);
        ushort[] materials = previous.Materials.ToArray();
        float[] densities = previous.Densities.ToArray();
        VoxelAddress origin = previous.Address.Origin;
        foreach (VoxelAddress voxel in edited)
        {
            if (voxel.Chunk != previous.Address) continue;
            int index = ToIndex((int)(voxel.X - origin.X), (int)(voxel.Y - origin.Y), (int)(voxel.Z - origin.Z));
            ushort generated = recipe.MaterialAt(voxel, recipe.ColumnAt(voxel.X, voxel.Z));
            ushort material = overlay.TryGetMaterial(voxel, out ushort overridden) ? overridden : generated;
            materials[index] = material;
            densities[index] = TerrainDensity.At(voxel.Y, recipe.ContinuousHeightAt(voxel.X, voxel.Z), generated, material);
        }

        return new TerrainChunk(previous.Address, materials, densities);
    }

    private static int ToIndex(int x, int y, int z) => (z * TerrainConstants.ChunkPlaneLength)
        + (y * TerrainConstants.ChunkEdgeLength) + x;
}
