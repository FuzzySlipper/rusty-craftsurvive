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

    internal TerrainChunk Generate(TerrainChunkAddress address, TerrainOverlaySnapshot overlay) => Generate(address, overlay, true);

    /// <summary>
    /// A chunk generated afresh, never read from the cache (#10907): what a worker generates, since the cache is the Engine's
    /// persistence and the update thread's alone.
    /// </summary>
    internal TerrainChunk GenerateUncached(TerrainChunkAddress address, TerrainOverlaySnapshot overlay) => Generate(address, overlay, false);

    /// <summary>The recipe the chunks are generated from.</summary>
    internal TerrainRecipe Recipe => recipe;

    private TerrainChunk Generate(TerrainChunkAddress address, TerrainOverlaySnapshot overlay, bool useCache)
    {
        // Read-through: a chunk this world already generated comes back from the store
        // instead of being computed again. The cache is an optimisation, so a miss is
        // simply generation - and a hit is only trusted because the cache refuses any
        // payload whose shape does not match a chunk.
        ArgumentNullException.ThrowIfNull(overlay);
        ushort[]? cached = null;
        // A chunk a tendril or a cave reaches is never read from the cache (#10889): its densities are the cave's, and a
        // cave admitted since changes it.
        if (useCache && cache is not null && !overlay.TouchesChunk(address) && Cacheable(address) && cache.TryRead(address, out ushort[] found))
        {
            CacheHits++;
            cached = found;
        }

        ushort[] materials = cached ?? new ushort[TerrainConstants.ChunkVolume];
        float[] densities = new float[TerrainConstants.ChunkVolume];
        VoxelAddress origin = address.Origin;
        // The chunk column's map samples, shared by the chunks stacked in it (a cave's box is several deep, #10889).
        (TerrainColumn[] columns, double[] heights) = Columns(address.X, address.Z, origin);
        // Overlapping places, crossings, caves, and trees gathered once per chunk (#11179).
        TerrainRecipe.ChunkFeatures features = cached is null ? recipe.PrepareChunkFeatures(address) : default;
        for (int z = 0; z < TerrainConstants.ChunkEdgeLength; z++)
        {
            for (int x = 0; x < TerrainConstants.ChunkEdgeLength; x++)
            {
                int at = (z * TerrainConstants.ChunkEdgeLength) + x;
                TerrainColumn column = columns[at];
                double height = heights[at];
                for (int y = 0; y < TerrainConstants.ChunkEdgeLength; y++)
                {
                    VoxelAddress voxel = new(origin.X + x, origin.Y + y, origin.Z + z);
                    int index = ToIndex(x, y, z);
                    float? underground = null;
                    ushort generated = cached is not null ? cached[index] : recipe.GeneratedAt(voxel, column, height, in features, out underground);
                    ushort material = overlay.TryGetMaterial(voxel, out ushort overridden)
                        ? overridden
                        : generated;
                    materials[index] = material;
                    densities[index] = Density(voxel.Y, height, generated, material, underground);
                }
            }
        }

        return new TerrainChunk(address, materials, densities);
    }

    // The map samples of the chunk columns generated last: a pure function of the column, so kept a while (#10889: going down a
    // cave the chunks of one column come in a few at a time, each asked the same samples).
    // Read by the chunk workers too (#10907), so kept behind a lock: a column is sampled outside it, and two threads sampling the
    // same column agree.
    private readonly Dictionary<(long X, long Z), (TerrainColumn[] Columns, double[] Heights)> columnSamples = [];
    private readonly Queue<(long X, long Z)> columnOrder = new();
    private readonly Lock columnLock = new();
    // The surface ring a player walks in (19 x 19 columns) and a cave's (6 x 6 and its tendril), about 30 KB a column: climbing out of a
    // cave finds the ring's samples still here.
    private const int ColumnSampleLimit = 768;

    /// <summary>A chunk column's samples of the map, every column's ground and its continuous height.</summary>
    private (TerrainColumn[] Columns, double[] Heights) Columns(long chunkX, long chunkZ, VoxelAddress origin)
    {
        lock (columnLock)
        {
            if (columnSamples.TryGetValue((chunkX, chunkZ), out (TerrainColumn[] Columns, double[] Heights) known))
            {
                return known;
            }
        }

        (TerrainColumn[] columns, double[] heights) = recipe.SampleChunkColumns(origin, TerrainConstants.ChunkEdgeLength);

        lock (columnLock)
        {
            if (columnSamples.TryAdd((chunkX, chunkZ), (columns, heights)))
            {
                columnOrder.Enqueue((chunkX, chunkZ));
                if (columnSamples.Count > ColumnSampleLimit && columnOrder.TryDequeue(out (long X, long Z) oldest))
                {
                    columnSamples.Remove(oldest);
                }
            }
        }

        return (columns, heights);
    }

    /// <summary>
    /// The lowest and highest ground surface over a chunk column (#10907), from the column's samples, kept from its generation
    /// (on a worker, for the chunks round the player): what the chemistry's window asks of every column it reaches, which
    /// otherwise sampled the map again for each of the column's cells.
    /// </summary>
    internal (long Low, long High) SurfaceBand(long chunkX, long chunkZ)
    {
        (TerrainColumn[] columns, _) = Columns(chunkX, chunkZ, new TerrainChunkAddress(chunkX, 0, chunkZ).Origin);
        long low = long.MaxValue, high = long.MinValue;
        foreach (TerrainColumn column in columns)
        {
            low = Math.Min(low, column.Surface);
            high = Math.Max(high, column.Surface);
        }

        return (low, high);
    }

    /// <summary>Whether a chunk may be read from and written to the chunk cache: not one a tendril or a cave reaches (#10889).</summary>
    internal bool Cacheable(TerrainChunkAddress address) => !recipe.Caves.Touches(address);

    /// <summary>
    /// A voxel's density: underground (#10889), the cave's or the tendril's own distance, signed by the material placed
    /// there (an edit keeps the magnitude, as the height field's does); elsewhere the height field's.
    /// </summary>
    private static float Density(long y, double height, ushort generated, ushort material, float? underground) =>
        underground is float magnitude
            ? material == TerrainConstants.EmptyMaterial ? magnitude : -magnitude
            : TerrainDensity.At(y, height, generated, material);

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
        // An edit's cells share columns (a blast's sphere, a burnt surface and the ground beneath it), and a column's
        // query samples the map several times, so each is asked once (#10407).
        Dictionary<(long X, long Z), (TerrainColumn Column, double Height)> columns = [];
        foreach (VoxelAddress voxel in edited)
        {
            if (voxel.Chunk != previous.Address) continue;
            if (!columns.TryGetValue((voxel.X, voxel.Z), out (TerrainColumn Column, double Height) column))
            {
                column = columns[(voxel.X, voxel.Z)] = recipe.ColumnAndHeightAt(voxel.X, voxel.Z);
            }

            int index = ToIndex((int)(voxel.X - origin.X), (int)(voxel.Y - origin.Y), (int)(voxel.Z - origin.Z));
            ushort generated = recipe.GeneratedAt(voxel, column.Column, column.Height, out float? underground);
            ushort material = overlay.TryGetMaterial(voxel, out ushort overridden) ? overridden : generated;
            materials[index] = material;
            densities[index] = Density(voxel.Y, column.Height, generated, material, underground);
        }

        return new TerrainChunk(previous.Address, materials, densities);
    }

    private static int ToIndex(int x, int y, int z) => (z * TerrainConstants.ChunkPlaneLength)
        + (y * TerrainConstants.ChunkEdgeLength) + x;
}
