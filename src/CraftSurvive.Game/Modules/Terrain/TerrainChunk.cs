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

    internal TerrainChunkGenerator(TerrainRecipe recipe)
    {
        this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
    }

    internal TerrainChunk Generate(TerrainChunkAddress address, TerrainOverlaySnapshot overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ushort[] materials = new ushort[TerrainConstants.ChunkVolume];
        VoxelAddress origin = address.Origin;
        for (int z = 0; z < TerrainConstants.ChunkEdgeLength; z++)
        {
            for (int y = 0; y < TerrainConstants.ChunkEdgeLength; y++)
            {
                for (int x = 0; x < TerrainConstants.ChunkEdgeLength; x++)
                {
                    VoxelAddress voxel = new(origin.X + x, origin.Y + y, origin.Z + z);
                    ushort material = overlay.TryGetMaterial(voxel, out ushort overridden)
                        ? overridden
                        : recipe.MaterialAt(voxel);
                    materials[ToIndex(x, y, z)] = material;
                }
            }
        }

        return new TerrainChunk(address, materials);
    }

    private static int ToIndex(int x, int y, int z) => (z * TerrainConstants.ChunkPlaneLength)
        + (y * TerrainConstants.ChunkEdgeLength) + x;
}
