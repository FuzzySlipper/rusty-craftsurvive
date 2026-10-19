using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A study space made from a field (#10090, #10094): the field sampled at a voxel size over a span of world chunks, the
/// voxels stone where it is solid, and places set on its floors.
/// </summary>
internal static class StudyField
{
    /// <summary>The field sampled at each voxel's centre (in metres, positive open), and the voxels as stone where it is solid.</summary>
    /// <param name="chunksX">The span in one-metre chunks; a finer voxel has more chunks over the same metres.</param>
    /// <param name="rock">Which rock block a solid voxel is, by where it is; stone when not given.</param>
    internal static (DungeonVolume Volume, RockDensity Rock) Sample(Func<Vector3, float> open, int chunksX, int chunksY, int chunksZ, double voxelSize,
        Func<Vector3, BlockId>? rock = null)
    {
        int perMetre = (int)Math.Round(1d / voxelSize);
        float size = (float)voxelSize;
        DungeonVolume volume = new(chunksX * perMetre, chunksY * perMetre, chunksZ * perMetre, BlockId.Air);
        int width = volume.SizeX, height = volume.SizeY, depth = volume.SizeZ;
        float[] values = new float[width * height * depth];
        Parallel.For(0, depth, z =>
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    values[(((z * height) + y) * width) + x] = open(new Vector3((x + 0.5f) * size, (y + 0.5f) * size, (z + 0.5f) * size));
                }
        });

        for (int z = 0; z < depth; z++)
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (values[(((z * height) + y) * width) + x] < 0f)
                    {
                        volume.Set(x, y, z, rock?.Invoke(new Vector3((x + 0.5f) * size, (y + 0.5f) * size, (z + 0.5f) * size)) ?? BlockId.Stone);
                    }
                }

        return (volume, new RockDensity(width, height, depth, values));
    }

    /// <summary>The first open place above the floor found looking down from a height, or the height itself when there is no floor.</summary>
    internal static Vector3 OnFloor(Func<Vector3, float> open, float x, float z, float fromY)
    {
        for (float y = fromY; y > 1f; y -= FloorSearchStep)
        {
            if (open(new Vector3(x, y, z)) < 0f)
            {
                return new Vector3(x, y + FloorSearchStep, z);
            }
        }

        return new Vector3(x, fromY, z);
    }

    private const float FloorSearchStep = 0.1f;

    /// <summary>A place in layout metres as the cell a plan names (natural spaces are visited exactly there).</summary>
    internal static DungeonCell Cell(Vector3 p) => new((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y + 0.5f), (int)MathF.Floor(p.Z));
}
