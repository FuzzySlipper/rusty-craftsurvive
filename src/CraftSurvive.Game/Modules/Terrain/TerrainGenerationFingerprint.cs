using System.Buffers.Binary;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// A fingerprint of what the generator produces over a fixed probe of the world: a lattice of
/// surface heights spanning the whole extent, the site and crossing decisions of the anchor
/// cells around the origin, and every voxel of a set of chunks that includes the origin, a
/// structure and the border wall.
///
/// It answers "is this the same world" by looking at output rather than at the version number,
/// so a tuning change that forgot its version bump shows up, and so does a change in the keyed
/// draws underneath. The chunk cache keys on it, and a managed check pins it per version.
/// </summary>
internal static class TerrainGenerationFingerprint
{
    /// <summary>How much of the world a fingerprint looks at.</summary>
    internal sealed record ProbeScale(int HeightSamplesPerSide, long SiteCellRadius, bool OriginChunkBlock);

    /// <summary>Cheap enough to take at every start: a few hundred draws and five chunks.</summary>
    internal static ProbeScale Startup { get; } = new(HeightSamplesPerSide: 65, SiteCellRadius: 2, OriginChunkBlock: false);

    /// <summary>The managed check's probe: every site in a 41-cell square and a block of 63 chunks.</summary>
    internal static ProbeScale Golden { get; } = new(HeightSamplesPerSide: 257, SiteCellRadius: 20, OriginChunkBlock: true);

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>The block of chunks around the origin the golden probe generates whole.</summary>
    private static readonly long[] BlockX = [-3, -2, -1, 0, 1, 2, 3];
    private static readonly long[] BlockZ = [-2, 0, 2];
    private static readonly long[] BlockY = [-1, 0, 1];

    internal static ulong Compute(TerrainRecipe recipe, ProbeScale scale)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(scale);
        ulong hash = FnvOffsetBasis;
        TerrainGeneratorContract contract = recipe.Contract;
        hash = Mix(hash, contract.Version);
        hash = Mix(hash, (ulong)contract.Extent);

        // Heights over the whole world, edge to edge.
        long radius = recipe.Radius;
        int samples = scale.HeightSamplesPerSide;
        for (int i = 0; i < samples; i++)
        {
            for (int j = 0; j < samples; j++)
            {
                long x = -radius + (2 * radius * i / (samples - 1));
                long z = -radius + (2 * radius * j / (samples - 1));
                hash = Mix(hash, (ulong)recipe.SurfaceAt(x, z));
            }
        }

        // Every site and crossing decision around the origin.
        PoiSite? nearest = null;
        for (long cellX = -scale.SiteCellRadius; cellX <= scale.SiteCellRadius; cellX++)
        {
            for (long cellZ = -scale.SiteCellRadius; cellZ <= scale.SiteCellRadius; cellZ++)
            {
                if (recipe.Placement.SiteAt(cellX, cellZ) is PoiSite site)
                {
                    hash = MixSite(hash, site);
                    if (nearest is null || Math.Abs(site.X) + Math.Abs(site.Z) < Math.Abs(nearest.Value.X) + Math.Abs(nearest.Value.Z))
                    {
                        nearest = site;
                    }
                }
                else
                {
                    hash = Mix(hash, 0);
                }

                hash = recipe.Crossings.SiteAt(cellX, cellZ) is CrossingSite crossing
                    ? MixCrossing(hash, crossing)
                    : Mix(hash, 0);
            }
        }

        // Whole chunks: the origin's surface, the nearest structure, and the border wall.
        TerrainChunkGenerator generator = new(recipe);
        TerrainOverlaySnapshot pristine = new(contract.Seed, []);
        foreach (TerrainChunkAddress address in ProbeChunks(recipe, scale, nearest))
        {
            foreach (ushort material in generator.Generate(address, pristine).Materials.Span)
            {
                hash = Mix(hash, material);
            }
        }

        return hash;
    }

    private static IEnumerable<TerrainChunkAddress> ProbeChunks(TerrainRecipe recipe, ProbeScale scale, PoiSite? nearest)
    {
        long surfaceChunk = GridMath.FloorDivide(recipe.SurfaceAt(0, 0), TerrainConstants.ChunkEdgeLength);
        yield return new TerrainChunkAddress(0, surfaceChunk, 0);
        yield return new TerrainChunkAddress(0, surfaceChunk - 1, 0);
        if (nearest is PoiSite site)
        {
            yield return new VoxelAddress(site.X, site.Ground, site.Z).Chunk;
        }

        long edge = recipe.Radius - 1;
        yield return new VoxelAddress(edge, GenerationConstants.WaterLevel, 0).Chunk;
        yield return new VoxelAddress(0, GenerationConstants.WaterLevel, -edge).Chunk;
        if (!scale.OriginChunkBlock)
        {
            yield break;
        }

        foreach (long x in BlockX)
        {
            foreach (long z in BlockZ)
            {
                foreach (long y in BlockY)
                {
                    yield return new TerrainChunkAddress(x, y, z);
                }
            }
        }
    }

    private static ulong MixSite(ulong hash, PoiSite site)
    {
        foreach (long value in (ReadOnlySpan<long>)[site.CellX, site.CellZ, (long)site.Kind, site.X, site.Z, site.Ground, site.Height, site.Variant, site.Aspect])
        {
            hash = Mix(hash, (ulong)value);
        }

        return hash;
    }

    private static ulong MixCrossing(ulong hash, CrossingSite site)
    {
        foreach (long value in (ReadOnlySpan<long>)[site.CellX, site.CellZ, site.FromX, site.FromZ, site.ToX, site.ToZ, site.DeckY, site.AlongX ? 1 : 0])
        {
            hash = Mix(hash, (ulong)value);
        }

        return hash;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        foreach (byte b in bytes)
        {
            hash = unchecked((hash ^ b) * FnvPrime);
        }

        return hash;
    }
}
