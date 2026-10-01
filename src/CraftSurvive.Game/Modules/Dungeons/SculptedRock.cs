using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A dungeon's rock as a smooth surface rather than cubes: a density over the volume, sampled at
/// cell centres one cell apart, negative inside rock, that the Engine meshes by dual contouring. The
/// building stays cubic voxels beside it.
/// </summary>
internal sealed record RockDensity(int Width, int Height, int Depth, float[] Values)
{
    /// <summary>The spacing between samples: one cell.</summary>
    internal const float Spacing = 1f;

    internal float At(int x, int y, int z) => Values[(((z * Height) + y) * Width) + x];
}

/// <summary>
/// Dungeon approach C, "sculpted rock": approach A's dungeon with its rock re-skinned. Rock cells
/// become a density by blurring their occupancy - flat floors stay flat, edges round - and walls and
/// ceilings away from where people walk are roughened with smooth noise, so the cave reads as a
/// cavern rather than a cut. Building blocks count half, so the rock's surface sits just behind a
/// brick face instead of fighting it. The voxels keep only the building; the rock is the mesh. The
/// result is checked again on a re-voxelised copy, since smoothing can change what can be walked.
/// </summary>
internal static class SculptedRock
{
    /// <summary>How far the noise moves a rock surface, in density units (a cell is about 0.33).</summary>
    internal const float RoughnessAmplitude = 0.38f;

    /// <summary>How weightily a building block counts toward the rock around it.</summary>
    private const float BuildingWeight = 0.3f;

    /// <summary>Within this many cells of a walkable floor, rock is left smooth so the floor stays walkable.</summary>
    private const int FloorCalmRadius = 2;

    private const float NoiseFrequency = 0.21f;

    /// <summary>How far from the surface a kept-open or kept-solid cell's density is held, in density units.</summary>
    private const float KeptMargin = 0.12f;

    /// <summary>The rock, the voxels that remain (the building), and a voxel copy of both for the route check.</summary>
    internal static (RockDensity Rock, DungeonVolume Building, DungeonVolume Walkable) Sculpt(DungeonVolume source, DungeonCell arrival, ulong seed)
    {
        int width = source.SizeX;
        int height = source.SizeY;
        int depth = source.SizeZ;
        float[] occupancy = new float[width * height * depth];
        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    occupancy[Index(x, y, z, width, height)] = IsRock(source, x, y, z) ? 1f
                        : BlockRegistry.Get(source.At(x, y, z)).Collidable ? BuildingWeight
                        : 0f;
                }
            }
        }

        HashSet<DungeonCell> walkable0 = DungeonWalk.Reachable(source, arrival);
        bool[] calm = CalmNearFloors(walkable0, width, height, depth);
        float[] blurred = Blur(occupancy, width, height, depth);
        float[] values = new float[blurred.Length];
        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = Index(x, y, z, width, height);
                    float roughness = calm[index] ? 0f : RoughnessAmplitude * Noise(x, y, z, seed);
                    values[index] = 0.5f - blurred[index] + roughness;
                }
            }
        }

        // What can be walked stays walkable: every standing place and its headroom stay open, and
        // the rock under a standing place stays solid, whatever the blur and noise did nearby.
        foreach (DungeonCell cell in walkable0)
        {
            for (int up = 0; up < DungeonWalk.Headroom; up++)
            {
                int open = Index(cell.X, cell.Y + up, cell.Z, width, height);
                values[open] = Math.Max(values[open], KeptMargin);
            }

            if (cell.Y > 0 && IsRock(source, cell.X, cell.Y - 1, cell.Z))
            {
                int floor = Index(cell.X, cell.Y - 1, cell.Z, width, height);
                values[floor] = Math.Min(values[floor], -KeptMargin);
            }
        }

        RockDensity rock = new(width, height, depth, values);
        DungeonVolume building = new(source.ChunksX, source.ChunksY, source.ChunksZ, BlockId.Air);
        DungeonVolume walkable = new(source.ChunksX, source.ChunksY, source.ChunksZ, BlockId.Air);
        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    BlockId block = source.At(x, y, z);
                    bool rockHere = rock.At(x, y, z) < 0f;
                    if (!IsRock(source, x, y, z) && block != BlockId.Air)
                    {
                        building.Set(x, y, z, block);
                        walkable.Set(x, y, z, block);
                    }
                    else if (rockHere)
                    {
                        walkable.Set(x, y, z, BlockId.Stone);
                    }
                }
            }
        }

        return (rock, building, walkable);
    }

    /// <summary>
    /// Whether a cell is rock: stone and the strata in it. Gravel lying on a building floor is
    /// rubble, not rock: rubble is gravel resting on a slab or stair.
    /// </summary>
    internal static bool IsRock(DungeonVolume volume, int x, int y, int z) => volume.At(x, y, z) switch
    {
        BlockId.Stone or BlockId.Dirt or BlockId.Bedrock => true,
        BlockId.Gravel => volume.At(x, y - 1, z) is not (BlockId.Planks or BlockId.Cobblestone),
        _ => false,
    };

    /// <summary>Cells within a few of a walkable floor, where the rock is kept smooth.</summary>
    private static bool[] CalmNearFloors(HashSet<DungeonCell> walkable, int width, int height, int depth)
    {
        bool[] calm = new bool[width * height * depth];
        foreach (DungeonCell cell in walkable)
        {
            for (int dx = -FloorCalmRadius; dx <= FloorCalmRadius; dx++)
            {
                for (int dz = -FloorCalmRadius; dz <= FloorCalmRadius; dz++)
                {
                    for (int dy = -FloorCalmRadius; dy <= DungeonWalk.Headroom; dy++)
                    {
                        int x = cell.X + dx;
                        int y = cell.Y + dy;
                        int z = cell.Z + dz;
                        if (x >= 0 && y >= 0 && z >= 0 && x < width && y < height && z < depth)
                        {
                            calm[Index(x, y, z, width, height)] = true;
                        }
                    }
                }
            }
        }

        return calm;
    }

    /// <summary>A 3x3x3 box blur, outside the volume counting as rock.</summary>
    private static float[] Blur(float[] source, int width, int height, int depth)
    {
        float[] current = source;
        foreach (int axis in (ReadOnlySpan<int>)[0, 1, 2])
        {
            float[] next = new float[current.Length];
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float sum = 0f;
                        for (int offset = -1; offset <= 1; offset++)
                        {
                            int sx = axis == 0 ? x + offset : x;
                            int sy = axis == 1 ? y + offset : y;
                            int sz = axis == 2 ? z + offset : z;
                            sum += sx < 0 || sy < 0 || sz < 0 || sx >= width || sy >= height || sz >= depth
                                ? 1f
                                : current[Index(sx, sy, sz, width, height)];
                        }

                        next[Index(x, y, z, width, height)] = sum / 3f;
                    }
                }
            }

            current = next;
        }

        return current;
    }

    /// <summary>Smooth value noise in [-1, 1], two octaves, a pure function of the cell and the seed.</summary>
    private static float Noise(int x, int y, int z, ulong seed) =>
        (0.7f * Octave(x * NoiseFrequency, y * NoiseFrequency, z * NoiseFrequency, seed))
        + (0.3f * Octave(x * NoiseFrequency * 2.3f, y * NoiseFrequency * 2.3f, z * NoiseFrequency * 2.3f, seed ^ 0x5DEE_CE66_DUL));

    private static float Octave(float x, float y, float z, ulong seed)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int z0 = (int)MathF.Floor(z);
        float tx = Smooth(x - x0);
        float ty = Smooth(y - y0);
        float tz = Smooth(z - z0);
        float Corner(int dx, int dy, int dz) => Lattice(x0 + dx, y0 + dy, z0 + dz, seed);
        float Lerp(float a, float b, float t) => a + ((b - a) * t);
        return Lerp(
            Lerp(Lerp(Corner(0, 0, 0), Corner(1, 0, 0), tx), Lerp(Corner(0, 1, 0), Corner(1, 1, 0), tx), ty),
            Lerp(Lerp(Corner(0, 0, 1), Corner(1, 0, 1), tx), Lerp(Corner(0, 1, 1), Corner(1, 1, 1), tx), ty),
            tz);
    }

    private static float Smooth(float t) => t * t * (3f - (2f * t));

    private static float Lattice(int x, int y, int z, ulong seed)
    {
        ulong h = seed ^ ((ulong)(uint)x * 0x9E37_79B1UL) ^ ((ulong)(uint)y * 0x85EB_CA77UL << 17) ^ ((ulong)(uint)z * 0xC2B2_AE3DUL << 31);
        h ^= h >> 33;
        h *= 0xFF51_AFD7_ED55_8CCDUL;
        h ^= h >> 33;
        return ((h >> 40) / (float)(1UL << 24) * 2f) - 1f;
    }

    private static int Index(int x, int y, int z, int width, int height) => (((z * height) + y) * width) + x;
}
