using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>A cell of a dungeon volume, in the volume's own coordinates from its minimum corner.</summary>
internal readonly record struct DungeonCell(int X, int Y, int Z);

/// <summary>
/// A finite block of voxels a dungeon is sculpted in: every cell starts as one material and is
/// changed by boxes, carved shapes and steps. It is whole chunks on every side, so it is admitted
/// to its session chunk by chunk as it stands, all at once, with nothing streamed.
/// </summary>
internal sealed class DungeonVolume
{
    private const int Edge = TerrainConstants.ChunkEdgeLength;
    private readonly ushort[] cells;

    internal DungeonVolume(int chunksX, int chunksY, int chunksZ, BlockId fill)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunksX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunksY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunksZ);
        ChunksX = chunksX;
        ChunksY = chunksY;
        ChunksZ = chunksZ;
        cells = new ushort[SizeX * SizeY * SizeZ];
        Array.Fill(cells, (ushort)fill);
    }

    internal int ChunksX { get; }

    internal int ChunksY { get; }

    internal int ChunksZ { get; }

    internal int SizeX => ChunksX * Edge;

    internal int SizeY => ChunksY * Edge;

    internal int SizeZ => ChunksZ * Edge;

    internal bool Contains(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < SizeX && y < SizeY && z < SizeZ;

    /// <summary>The block at a cell; outside the volume is solid bedrock, so nothing leads out of it.</summary>
    internal BlockId At(int x, int y, int z) => Contains(x, y, z) ? (BlockId)cells[Index(x, y, z)] : BlockId.Bedrock;

    internal void Set(int x, int y, int z, BlockId block)
    {
        if (Contains(x, y, z))
        {
            cells[Index(x, y, z)] = (ushort)block;
        }
    }

    /// <summary>Fills a box, both corners included.</summary>
    internal void Fill(DungeonCell from, DungeonCell to, BlockId block)
    {
        for (int x = Math.Min(from.X, to.X); x <= Math.Max(from.X, to.X); x++)
        {
            for (int y = Math.Min(from.Y, to.Y); y <= Math.Max(from.Y, to.Y); y++)
            {
                for (int z = Math.Min(from.Z, to.Z); z <= Math.Max(from.Z, to.Z); z++)
                {
                    Set(x, y, z, block);
                }
            }
        }
    }

    /// <summary>A hollow box: walls, floor and ceiling of one block, air inside.</summary>
    internal void Room(DungeonCell from, DungeonCell to, BlockId walls, BlockId floor)
    {
        Fill(from, to, walls);
        Fill(new DungeonCell(Math.Min(from.X, to.X) + 1, Math.Min(from.Y, to.Y), Math.Min(from.Z, to.Z) + 1),
            new DungeonCell(Math.Max(from.X, to.X) - 1, Math.Min(from.Y, to.Y), Math.Max(from.Z, to.Z) - 1), floor);
        Fill(new DungeonCell(Math.Min(from.X, to.X) + 1, Math.Min(from.Y, to.Y) + 1, Math.Min(from.Z, to.Z) + 1),
            new DungeonCell(Math.Max(from.X, to.X) - 1, Math.Max(from.Y, to.Y) - 1, Math.Max(from.Z, to.Z) - 1), BlockId.Air);
    }

    /// <summary>Carves an ellipsoid of air: a cave chamber.</summary>
    internal void CarveEllipsoid(double centreX, double centreY, double centreZ, double radiusX, double radiusY, double radiusZ)
    {
        for (int x = (int)Math.Floor(centreX - radiusX); x <= (int)Math.Ceiling(centreX + radiusX); x++)
        {
            for (int y = (int)Math.Floor(centreY - radiusY); y <= (int)Math.Ceiling(centreY + radiusY); y++)
            {
                for (int z = (int)Math.Floor(centreZ - radiusZ); z <= (int)Math.Ceiling(centreZ + radiusZ); z++)
                {
                    double dx = (x + 0.5 - centreX) / radiusX;
                    double dy = (y + 0.5 - centreY) / radiusY;
                    double dz = (z + 0.5 - centreZ) / radiusZ;
                    if ((dx * dx) + (dy * dy) + (dz * dz) <= 1d)
                    {
                        Set(x, y, z, BlockId.Air);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A straight flight of steps: each step one block up and one forward, <paramref name="width"/>
    /// wide, with headroom cleared above every tread so the flight is walkable.
    /// </summary>
    internal void Stairs(DungeonCell foot, int stepX, int stepZ, int rises, int width, BlockId block)
    {
        const int Headroom = 3;
        int acrossX = stepZ != 0 ? 1 : 0;
        int acrossZ = stepX != 0 ? 1 : 0;
        for (int rise = 0; rise < rises; rise++)
        {
            for (int across = 0; across < width; across++)
            {
                int x = foot.X + (rise * stepX) + (across * acrossX);
                int z = foot.Z + (rise * stepZ) + (across * acrossZ);
                int y = foot.Y + rise;
                Set(x, y, z, block);
                for (int clear = 1; clear <= Headroom; clear++)
                {
                    Set(x, y + clear, z, BlockId.Air);
                }
            }
        }
    }

    /// <summary>One chunk's materials in the Engine's order (x fastest, then y, then z).</summary>
    internal ushort[] Chunk(int chunkX, int chunkY, int chunkZ)
    {
        ushort[] materials = new ushort[Edge * Edge * Edge];
        for (int z = 0; z < Edge; z++)
        {
            for (int y = 0; y < Edge; y++)
            {
                for (int x = 0; x < Edge; x++)
                {
                    materials[(z * Edge * Edge) + (y * Edge) + x] = cells[Index((chunkX * Edge) + x, (chunkY * Edge) + y, (chunkZ * Edge) + z)];
                }
            }
        }

        return materials;
    }

    /// <summary>How many cells hold a given block.</summary>
    internal int Count(BlockId block) => cells.Count(cell => cell == (ushort)block);

    private int Index(int x, int y, int z) => (((z * SizeY) + y) * SizeX) + x;
}
