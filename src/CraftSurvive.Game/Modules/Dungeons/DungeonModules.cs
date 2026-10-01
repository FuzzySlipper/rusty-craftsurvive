using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>A side of a module cell: the four walls a socket can open through.</summary>
internal enum ModuleFace
{
    North,
    East,
    South,
    West,
}

/// <summary>What a socket joins to: a building doorway, or a cave opening. A breach joins the two.</summary>
internal enum SocketKind
{
    Door,
    Cave,
}

/// <summary>Whether a module is part of the building or of the cave.</summary>
internal enum ModuleKind
{
    Building,
    Cave,
}

/// <summary>A place on a module's side where a neighbour may join it: a cell of the module, a face of that cell, and what it joins to.</summary>
internal readonly record struct ModuleSocket(int X, int Y, int Z, ModuleFace Face, SocketKind Kind);

/// <summary>
/// One authored dungeon piece: its size in lattice cells, its sockets, and how its voxels are laid
/// down in a canvas of its own size. A canvas cell left untouched stays rock.
/// </summary>
internal sealed record DungeonModuleShape(
    string Name,
    ModuleKind Kind,
    int Width,
    int Height,
    int Depth,
    IReadOnlyList<ModuleSocket> Sockets,
    Action<ModuleCanvas, DungeonRandom> Build)
{
    /// <summary>A module that ends a route: it has one socket, the one it was joined by.</summary>
    internal bool DeadEnd => Sockets.Count == 1;
}

/// <summary>
/// The voxels of one module before it is placed: a box of its lattice size, every cell untouched
/// (rock) until a builder sets it. Placement copies the touched cells into the dungeon, turned.
/// </summary>
internal sealed class ModuleCanvas
{
    /// <summary>A lattice cell: its width and depth in voxels, and one storey's height.</summary>
    internal const int CellSize = 8;

    internal const int StoreyHeight = 6;

    private const byte Untouched = byte.MaxValue;
    private readonly byte[] cells;

    internal ModuleCanvas(int width, int height, int depth)
    {
        SizeX = width * CellSize;
        SizeY = height * StoreyHeight;
        SizeZ = depth * CellSize;
        cells = new byte[SizeX * SizeY * SizeZ];
        Array.Fill(cells, Untouched);
    }

    internal int SizeX { get; }

    internal int SizeY { get; }

    internal int SizeZ { get; }

    internal void Set(int x, int y, int z, BlockId block)
    {
        if (x >= 0 && y >= 0 && z >= 0 && x < SizeX && y < SizeY && z < SizeZ)
        {
            cells[(((z * SizeY) + y) * SizeX) + x] = (byte)block;
        }
    }

    /// <summary>The block a builder set at a cell, or null where the cell was left as rock.</summary>
    internal BlockId? At(int x, int y, int z)
    {
        byte value = cells[(((z * SizeY) + y) * SizeX) + x];
        return value == Untouched ? null : (BlockId)value;
    }

    internal void Fill(int x0, int y0, int z0, int x1, int y1, int z1, BlockId block)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        {
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            {
                for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
                {
                    Set(x, y, z, block);
                }
            }
        }
    }

    /// <summary>
    /// A building box over the whole canvas: brick walls, a slab floor on every storey, air inside,
    /// and a brick ceiling over the top storey, so the building is closed above wherever nothing
    /// is built on it (the rock around it never forms a room's ceiling).
    /// </summary>
    internal void Rooms(BlockId slab)
    {
        Fill(0, 0, 0, SizeX - 1, SizeY - 1, SizeZ - 1, BlockId.Brick);
        for (int storey = 0; storey * StoreyHeight < SizeY; storey++)
        {
            int y = storey * StoreyHeight;
            Fill(1, y, 1, SizeX - 2, y, SizeZ - 2, slab);
            Fill(1, y + 1, 1, SizeX - 2, Math.Min(y + StoreyHeight - 1, SizeY - 2), SizeZ - 2, BlockId.Air);
        }
    }

    /// <summary>Carves an ellipsoid of air, then puts flat floor back under it at a height.</summary>
    internal void Chamber(double cx, double cy, double cz, double rx, double ry, double rz, int floorY)
    {
        for (int x = 0; x < SizeX; x++)
        {
            for (int y = 0; y < SizeY; y++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    double dx = (x + 0.5 - cx) / rx;
                    double dy = (y + 0.5 - cy) / ry;
                    double dz = (z + 0.5 - cz) / rz;
                    if ((dx * dx) + (dy * dy) + (dz * dz) <= 1d)
                    {
                        Set(x, y, z, y < floorY ? BlockId.Stone : BlockId.Air);
                    }
                }
            }
        }
    }

    /// <summary>A flight of steps rising one block per step along +z from a foot, with headroom over each tread.</summary>
    internal void StairsNorth(int x0, int width, int y0, int z0, int rises, BlockId block)
    {
        for (int rise = 0; rise < rises; rise++)
        {
            for (int x = x0; x < x0 + width; x++)
            {
                Fill(x, y0, z0 + rise, x, y0 + rise, z0 + rise, block);
                Fill(x, y0 + rise + 1, z0 + rise, x, y0 + rise + 3, z0 + rise, BlockId.Air);
            }
        }
    }
}

/// <summary>
/// The authored module library dungeons are assembled from. Every module is laid out facing north
/// (+z); placement turns it in quarter turns. Sockets sit at a cell's floor, in the middle of a face,
/// so any two compatible sockets of neighbouring cells line up.
/// </summary>
internal static class DungeonModules
{
    private const int Cell = ModuleCanvas.CellSize;
    private const int Storey = ModuleCanvas.StoreyHeight;

    /// <summary>
    /// How many cells long a cave descent runs to drop a storey: long enough that its steps have
    /// treads two or three blocks deep, which sculpted rock keeps as steps a body climbs back up.
    /// </summary>
    private const int DescentLength = 2;

    internal static DungeonModuleShape Arrival { get; } = new("arrival", ModuleKind.Cave, 1, 1, 1,
        [new(0, 0, 0, ModuleFace.South, SocketKind.Cave)],
        (canvas, random) => canvas.Chamber(4, 2.5, 4.5, 3.6, 3.2, 3.4, 1));

    internal static DungeonModuleShape CaveTunnel { get; } = new("cave-tunnel", ModuleKind.Cave, 1, 1, 1,
        [new(0, 0, 0, ModuleFace.North, SocketKind.Cave), new(0, 0, 0, ModuleFace.South, SocketKind.Cave)],
        (canvas, random) =>
        {
            double sway = (random.Unit() - 0.5) * 2.4;
            for (int z = 0; z < Cell; z++)
            {
                canvas.Chamber(4 + (sway * Math.Sin(z * Math.PI / Cell)), 2.6, z + 0.5, 2.4, 2.6, 1.6, 1);
            }
        });

    internal static DungeonModuleShape CaveBend { get; } = new("cave-bend", ModuleKind.Cave, 1, 1, 1,
        [new(0, 0, 0, ModuleFace.South, SocketKind.Cave), new(0, 0, 0, ModuleFace.East, SocketKind.Cave)],
        (canvas, random) =>
        {
            canvas.Chamber(4, 2.6, 4, 3.4, 2.8, 3.4, 1);
            canvas.Chamber(4, 2.6, 1, 2.4, 2.6, 2.4, 1);
            canvas.Chamber(7, 2.6, 4, 2.4, 2.6, 2.4, 1);
        });

    /// <summary>A stair down one storey over two cells: in at the top on the north, out at the bottom on the south.</summary>
    internal static DungeonModuleShape CaveDescent { get; } = new("cave-descent", ModuleKind.Cave, 1, 2, DescentLength,
        [new(0, 1, DescentLength - 1, ModuleFace.North, SocketKind.Cave), new(0, 0, 0, ModuleFace.South, SocketKind.Cave)],
        (canvas, random) =>
        {
            const int run = DescentLength * Cell;
            for (int z = 0; z < run; z++)
            {
                int floor = 1 + (int)Math.Round(z * (Storey / (double)(run - 1)));
                canvas.Fill(1, 0, z, Cell - 2, floor - 1, z, BlockId.Stone);
                canvas.Fill(1, floor, z, Cell - 2, floor + 3, z, BlockId.Air);
                canvas.Chamber(4, floor + 2.2, z + 0.5, 3.0, 2.6, 1.4, floor);
            }
        });

    internal static DungeonModuleShape CaveChamber { get; } = new("cave-chamber", ModuleKind.Cave, 2, 1, 2,
        [
            new(0, 0, 0, ModuleFace.South, SocketKind.Cave), new(1, 0, 1, ModuleFace.North, SocketKind.Cave),
            new(1, 0, 0, ModuleFace.East, SocketKind.Cave), new(0, 0, 1, ModuleFace.West, SocketKind.Cave),
        ],
        (canvas, random) =>
        {
            canvas.Chamber(8, 3, 8, 7.2 + random.Unit(), 3.6, 7.2 + random.Unit(), 1);
            canvas.Chamber(4, 2.6, 1, 2.4, 2.6, 2.4, 1);
            canvas.Chamber(12, 2.6, 15, 2.4, 2.6, 2.4, 1);
            canvas.Chamber(15, 2.6, 4, 2.4, 2.6, 2.4, 1);
            canvas.Chamber(1, 2.6, 12, 2.4, 2.6, 2.4, 1);
        });

    /// <summary>A shaft three storeys deep with a ledge spiralling down its walls: in at the top, out at the bottom.</summary>
    internal static DungeonModuleShape CaveShaft { get; } = new("cave-shaft", ModuleKind.Cave, 2, 3, 2,
        [new(0, 2, 1, ModuleFace.North, SocketKind.Cave), new(1, 0, 0, ModuleFace.South, SocketKind.Cave)],
        (canvas, random) =>
        {
            int size = 2 * Cell;
            canvas.Chamber(size / 2.0, (3 * Storey) / 2.0, size / 2.0, 7.4, 9.5, 7.4, 1);

            // The way out at the bottom (south side, east cell), carved before the ledge is laid so
            // the ledge's last steps keep their footing.
            canvas.Fill(9, 0, 1, 14, 0, 5, BlockId.Stone);
            canvas.Fill(9, 1, 1, 14, 4, 5, BlockId.Air);

            // The ledge: a walk around the shaft wall, a step down every other cell, from the top
            // socket (north side, west cell) to the bottom one (south side, east cell).
            int y = (2 * Storey) + 1;
            (int X, int Z)[] ring = [.. Ring(size)];
            int start = Array.FindIndex(ring, p => p.Z == size - 3 && p.X == (Cell / 2));
            List<(int X, int Y, int Z)> ledge = [];
            for (int step = 0; y > 1; step++)
            {
                (int x, int z) = ring[(start + step) % ring.Length];
                ledge.Add((x, y, z));
                if (step % 2 == 1)
                {
                    y--;
                }
            }

            // Clear the whole way first, then lay the ledge under it, so a step's floor is never
            // carved away by the step after it; neighbouring steps then read as stairs.
            foreach ((int x, int ly, int z) in ledge)
            {
                canvas.Fill(x - 1, ly, z - 1, x + 1, ly + 3, z + 1, BlockId.Air);
            }

            foreach ((int x, int ly, int z) in ledge)
            {
                canvas.Fill(x - 1, ly - 1, z - 1, x + 1, ly - 1, z + 1, BlockId.Stone);
            }

        });

    internal static DungeonModuleShape Room { get; } = new("room", ModuleKind.Building, 1, 1, 1,
        [
            new(0, 0, 0, ModuleFace.North, SocketKind.Door), new(0, 0, 0, ModuleFace.East, SocketKind.Door),
            new(0, 0, 0, ModuleFace.South, SocketKind.Door), new(0, 0, 0, ModuleFace.West, SocketKind.Door),
        ],
        (canvas, random) => canvas.Rooms(BlockId.Planks));

    internal static DungeonModuleShape Corridor { get; } = new("corridor", ModuleKind.Building, 1, 1, 1,
        [new(0, 0, 0, ModuleFace.North, SocketKind.Door), new(0, 0, 0, ModuleFace.South, SocketKind.Door)],
        (canvas, random) =>
        {
            canvas.Rooms(BlockId.Cobblestone);
            canvas.Fill(1, 1, 1, 2, Storey - 1, Cell - 2, BlockId.Brick);
            canvas.Fill(Cell - 3, 1, 1, Cell - 2, Storey - 1, Cell - 2, BlockId.Brick);
        });

    internal static DungeonModuleShape Hall { get; } = new("hall", ModuleKind.Building, 2, 1, 2,
        [
            new(0, 0, 0, ModuleFace.South, SocketKind.Door), new(1, 0, 0, ModuleFace.South, SocketKind.Door),
            new(1, 0, 0, ModuleFace.East, SocketKind.Door), new(1, 0, 1, ModuleFace.North, SocketKind.Door),
            new(0, 0, 1, ModuleFace.West, SocketKind.Door),
        ],
        (canvas, random) =>
        {
            canvas.Rooms(BlockId.Cobblestone);
            for (int x = 4; x < (2 * Cell) - 2; x += 5)
            {
                for (int z = 4; z < (2 * Cell) - 2; z += 5)
                {
                    canvas.Fill(x, 1, z, x, Storey - 1, z, BlockId.Brick);
                }
            }
        });

    /// <summary>Two storeys: a stair from the south door below to the north door above.</summary>
    internal static DungeonModuleShape Stair { get; } = new("stair", ModuleKind.Building, 1, 2, 1,
        [new(0, 0, 0, ModuleFace.South, SocketKind.Door), new(0, 1, 0, ModuleFace.North, SocketKind.Door)],
        (canvas, random) =>
        {
            canvas.Rooms(BlockId.Planks);
            canvas.Fill(1, Storey, 1, Cell - 2, Storey, Cell - 3, BlockId.Air);
            canvas.StairsNorth(2, 3, 1, 1, Storey, BlockId.Cobblestone);
        });

    /// <summary>A two-storey hall with a balcony on one side: rooms above rooms, seen from below.</summary>
    internal static DungeonModuleShape Gallery { get; } = new("gallery", ModuleKind.Building, 2, 2, 2,
        [
            new(0, 0, 0, ModuleFace.South, SocketKind.Door), new(1, 0, 1, ModuleFace.East, SocketKind.Door),
            new(0, 1, 1, ModuleFace.North, SocketKind.Door), new(1, 1, 1, ModuleFace.North, SocketKind.Door),
            new(0, 1, 0, ModuleFace.West, SocketKind.Door),
        ],
        (canvas, random) =>
        {
            canvas.Rooms(BlockId.Cobblestone);
            int size = 2 * Cell;

            // The upper floor is only a balcony along the north and west walls, and a stair to it.
            canvas.Fill(5, Storey, 1, size - 2, Storey, size - 6, BlockId.Air);
            // The stair climbs north and lands on the north balcony.
            canvas.StairsNorth(size - 4, 3, 1, size - 5 - Storey, Storey, BlockId.Cobblestone);
        });

    /// <summary>
    /// Two storeys whose upper floor has given way: the fallen floor lies as a rubble slope that can
    /// be climbed, from the south door below to the north door above.
    /// </summary>
    internal static DungeonModuleShape Collapse { get; } = new("collapse", ModuleKind.Building, 1, 2, 1,
        [new(0, 1, 0, ModuleFace.North, SocketKind.Door), new(0, 0, 0, ModuleFace.South, SocketKind.Door)],
        (canvas, random) =>
        {
            canvas.Rooms(BlockId.Planks);
            canvas.Fill(1, Storey, 1, Cell - 2, Storey, Cell - 4, BlockId.Air);

            // The slope: a heap one block higher each step east, its top level with the floor above.
            for (int rise = 0; rise < Storey; rise++)
            {
                canvas.Fill(1 + rise, 1, 3, 1 + rise, 1 + rise, 4, BlockId.Gravel);
            }
        });

    /// <summary>A small vault with one door: where the loot lies.</summary>
    internal static DungeonModuleShape Vault { get; } = new("vault", ModuleKind.Building, 1, 1, 1,
        [new(0, 0, 0, ModuleFace.South, SocketKind.Door)],
        (canvas, random) =>
        {
            canvas.Rooms(BlockId.Cobblestone);
            canvas.Fill(3, 1, 5, 4, 1, 5, BlockId.Planks);
        });

    /// <summary>
    /// The breach: a building room whose south wall has fallen into the cave. A cave socket on the
    /// broken side, doors on the others.
    /// </summary>
    internal static DungeonModuleShape Breach { get; } = new("breach", ModuleKind.Building, 1, 1, 1,
        [
            new(0, 0, 0, ModuleFace.South, SocketKind.Cave), new(0, 0, 0, ModuleFace.North, SocketKind.Door),
            new(0, 0, 0, ModuleFace.East, SocketKind.Door), new(0, 0, 0, ModuleFace.West, SocketKind.Door),
        ],
        (canvas, random) =>
        {
            canvas.Rooms(BlockId.Planks);
            for (int x = 0; x < Cell; x++)
            {
                int top = 2 + random.Range(1, 3);
                canvas.Fill(x, 1, 0, x, top, 1, BlockId.Air);
            }

            canvas.Fill(1, 1, 2, 2, 1, 3, BlockId.Gravel);
        });

    internal static IReadOnlyList<DungeonModuleShape> CaveRoute { get; } = [CaveTunnel, CaveBend, CaveDescent, CaveChamber, CaveShaft];

    internal static IReadOnlyList<DungeonModuleShape> BuildingRooms { get; } = [Room, Corridor, Hall, Stair, Gallery, Collapse];

    /// <summary>The cells of a square ring two in from the walls, walked anticlockwise from above.</summary>
    private static IEnumerable<(int X, int Z)> Ring(int size)
    {
        int low = 2;
        int high = size - 3;
        for (int x = low; x <= high; x++)
        {
            yield return (x, high);
        }

        for (int z = high - 1; z >= low; z--)
        {
            yield return (high, z);
        }

        for (int x = high - 1; x >= low; x--)
        {
            yield return (x, low);
        }

        for (int z = low + 1; z < high; z++)
        {
            yield return (low, z);
        }
    }
}
