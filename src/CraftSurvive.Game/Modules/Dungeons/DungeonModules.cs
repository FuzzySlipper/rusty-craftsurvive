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

    /// <summary>A landmark piece a dungeon has at most one of, so it stays a moment rather than a pattern.</summary>
    internal bool SetPiece { get; init; }
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

    /// <summary>
    /// Cells a body climbs through, in front of a climbable face: sculpting holds them open like a
    /// standing body's, so the face stays on its cell boundary where the climb rail runs.
    /// </summary>
    internal List<(int X, int Y, int Z)> Climbs { get; } = [];

    /// <summary>Where the module hangs its own lights, in canvas voxels; a module without any gets one by default.</summary>
    internal List<(double X, double Y, double Z)> Lights { get; } = [];

    internal void Light(double x, double y, double z) => Lights.Add((x, y, z));

    /// <summary>How far a landing reaches in from its socket's face, how wide it is, and its headroom.</summary>
    internal const int LandingDepth = 3;

    internal const int LandingHalfWidth = 3;

    internal const int LandingHeadroom = 4;

    /// <summary>
    /// A flat landing just inside a socket: floor under it, air over it, and its cells held open
    /// like a climb lane, so a reconstructed surface cannot crowd the way in. Set pieces get one at
    /// every cave socket, where their rock meets a neighbour's mouth.
    /// </summary>
    internal void Landing(ModuleSocket socket)
    {
        int floor = (socket.Y * StoreyHeight) + 1;
        int centreX = (socket.X * CellSize) + (CellSize / 2);
        int centreZ = (socket.Z * CellSize) + (CellSize / 2);
        (int x0, int x1, int z0, int z1) = socket.Face switch
        {
            ModuleFace.North => (centreX - LandingHalfWidth, centreX + LandingHalfWidth - 1, ((socket.Z + 1) * CellSize) - LandingDepth, ((socket.Z + 1) * CellSize) - 1),
            ModuleFace.South => (centreX - LandingHalfWidth, centreX + LandingHalfWidth - 1, socket.Z * CellSize, (socket.Z * CellSize) + LandingDepth - 1),
            ModuleFace.East => (((socket.X + 1) * CellSize) - LandingDepth, ((socket.X + 1) * CellSize) - 1, centreZ - LandingHalfWidth, centreZ + LandingHalfWidth - 1),
            _ => (socket.X * CellSize, (socket.X * CellSize) + LandingDepth - 1, centreZ - LandingHalfWidth, centreZ + LandingHalfWidth - 1),
        };
        Fill(x0, floor - 1, z0, x1, floor - 1, z1, BlockId.Stone);
        Fill(x0, floor, z0, x1, floor + LandingHeadroom - 1, z1, BlockId.Air);
        Climb(x0, floor, z0, x1, floor + LandingHeadroom - 1, z1);
    }

    /// <summary>Marks a box of cells as a climb lane (<see cref="Climbs"/>).</summary>
    internal void Climb(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        {
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            {
                for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
                {
                    Climbs.Add((x, y, z));
                }
            }
        }
    }

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

    /// <summary>
    /// A ledge three cells wide walked along a path, each step at its own feet height: rock under the
    /// whole ledge first (a corbel <paramref name="corbelDepth"/> deep), then headroom over every
    /// step, then every step's tread, so each tread's front is the riser down to the next.
    /// </summary>
    internal void Ledge(IReadOnlyList<(int X, int Feet, int Z)> steps, int headroom, int corbelDepth)
    {
        foreach ((int x, int feet, int z) in steps)
        {
            Fill(x - 1, Math.Max(0, feet - 1 - corbelDepth), z - 1, x + 1, feet - 1, z + 1, BlockId.Stone);
        }

        foreach ((int x, int feet, int z) in steps)
        {
            Fill(x - 1, feet, z - 1, x + 1, feet + headroom - 1, z + 1, BlockId.Air);
        }

        foreach ((int x, int feet, int z) in steps)
        {
            Fill(x - 1, feet - 1, z - 1, x + 1, feet - 1, z + 1, DungeonModules.LedgeTread);
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
    /// What a ledge's treads are cut from: masonry, an ancient stair on the rock, which keeps to the
    /// grid where reconstructed rock would round each step past what a body steps up.
    /// </summary>
    internal const BlockId LedgeTread = BlockId.Cobblestone;

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
                canvas.Fill(x - 1, ly - 1, z - 1, x + 1, ly - 1, z + 1, LedgeTread);
            }

        });

    /// <summary>
    /// A chasm two storeys deep and three cells across, crossed by a brick bridge on the upper
    /// storey: the bridge's ways on are north and south, and a cave runs east to west along the chasm
    /// floor beneath it, so the two routes cross a storey apart. The ledges' rock faces are climbable
    /// beside the bridge's footings. The two routes meet only by a drop and a climb.
    /// </summary>
    internal static DungeonModuleShape Chasm { get; } = new("chasm", ModuleKind.Cave, 3, 2, 3, ChasmSockets,
        (canvas, random) => BuildChasm(canvas, withStair: false));

    /// <summary>
    /// The chasm with a ledge stair down its south-west wall from the bridge's south ledge to the
    /// floor, so all four ways on are walked between and a fall is never a trap: the chasm as
    /// assembled dungeons use it.
    /// </summary>
    internal static DungeonModuleShape ChasmStair { get; } = new("chasm-stair", ModuleKind.Cave, 3, 2, 3, ChasmSockets,
        (canvas, random) => BuildChasm(canvas, withStair: true))
    {
        SetPiece = true,
    };

    private static ModuleSocket[] ChasmSockets =>
    [
        new(1, 1, 2, ModuleFace.North, SocketKind.Cave), new(1, 1, 0, ModuleFace.South, SocketKind.Cave),
        new(2, 0, 1, ModuleFace.East, SocketKind.Cave), new(0, 0, 1, ModuleFace.West, SocketKind.Cave),
    ];

    private static void BuildChasm(ModuleCanvas canvas, bool withStair)
    {
        const int upper = Storey + 1;
        const int ledgeHeadroom = 4;
        const int ledgeWest = Cell - 2;
        const int ledgeEast = (2 * Cell) + 1;
        const int bridgeWest = Cell + 2;
        const int bridgeEast = Cell + 5;
        const int footingDepth = 2;
        const int stairCorbelDepth = 2;
        int across = canvas.SizeX;

        // The ledges the bridge leaves from, at either end, on the upper storey.
        canvas.Fill(ledgeWest, upper, 1, ledgeEast, upper + ledgeHeadroom - 1, Cell - 1, BlockId.Air);
        canvas.Fill(ledgeWest, upper, 2 * Cell, ledgeEast, upper + ledgeHeadroom - 1, (3 * Cell) - 2, BlockId.Air);

        // The chasm: open from the lower storey's floor to under the roof, the whole module across.
        canvas.Fill(1, 1, Cell, across - 2, canvas.SizeY - 2, (2 * Cell) - 1, BlockId.Air);

        // The cave along the chasm floor, wall to wall, under the bridge.
        canvas.Chamber(across / 2.0, 2.6, Cell * 1.5, across / 2.0, 2.6, 2.6, 1);

        if (withStair)
        {
            // West from the south ledge along the chasm's south wall, then north a few cells in from
            // its west wall (clear of the west way on's mouth) down to the floor: a storey in eleven
            // steps.
            const int stairLeg = 4;
            List<(int X, int Z)> path = [];
            for (int x = ledgeWest + 2; x >= stairLeg; x--)
            {
                path.Add((x, Cell + 1));
            }

            for (int z = Cell + 2; z <= (2 * Cell) - 1; z++)
            {
                path.Add((stairLeg, z));
            }

            canvas.Ledge([.. path.Select((cell, step) => (cell.X, upper - (int)Math.Round(step * Storey / (double)(path.Count - 1)), cell.Z))],
                ledgeHeadroom, stairCorbelDepth);
        }
        else
        {
            canvas.Climb(ledgeWest, 1, Cell, bridgeWest - 2, upper + 1, Cell);
            canvas.Climb(ledgeWest, 1, (2 * Cell) - 1, bridgeWest - 2, upper + 1, (2 * Cell) - 1);
        }

        // Climb lanes up the ledge faces east of the bridge, from the chasm floor to the ledge.
        canvas.Climb(bridgeEast + 2, 1, Cell, ledgeEast, upper + 1, Cell);
        canvas.Climb(bridgeEast + 2, 1, (2 * Cell) - 1, ledgeEast, upper + 1, (2 * Cell) - 1);

        // The bridge, and a footing of brick where it meets each ledge.
        canvas.Fill(bridgeWest, Storey, Cell, bridgeEast, Storey, (2 * Cell) - 1, BlockId.Brick);
        canvas.Fill(bridgeWest - 1, Storey - footingDepth, Cell - 1, bridgeEast + 1, Storey, Cell - 1, BlockId.Brick);
        canvas.Fill(bridgeWest - 1, Storey - footingDepth, 2 * Cell, bridgeEast + 1, Storey, 2 * Cell, BlockId.Brick);

        // Light down in the chasm either side of the bridge, so its depth reads from above.
        canvas.Light(Cell / 2, 3, Cell * 1.5);
        canvas.Light(across - (Cell / 2), 3, Cell * 1.5);
    }

    /// <summary>
    /// A cavern three cells across and two storeys high: one rounded dome with a shelf along its
    /// north side, a rough ramp up to it, ways on at the floor to the south, east and west and one on
    /// the shelf to the north.
    /// </summary>
    internal static DungeonModuleShape Cavern { get; } = new("cavern", ModuleKind.Cave, 3, 2, 3,
        [
            new(1, 0, 0, ModuleFace.South, SocketKind.Cave), new(2, 0, 1, ModuleFace.East, SocketKind.Cave),
            new(0, 0, 1, ModuleFace.West, SocketKind.Cave), new(1, 1, 2, ModuleFace.North, SocketKind.Cave),
        ],
        (canvas, random) =>
        {
            const int shelfSouth = (2 * Cell) - 1;
            const int rampWest = Cell + 3;
            const int rampEast = Cell + 5;
            const int treadDepth = 2;
            const int shelfHeadroom = 4;
            int across = canvas.SizeX;
            double middle = across / 2.0;

            canvas.Chamber(middle, (canvas.SizeY / 2.0) + 0.5, middle, middle - 0.6 + (random.Unit() * 0.6), (canvas.SizeY / 2.0) + 0.4,
                middle - 0.6 + (random.Unit() * 0.6), 1);

            // The shelf, the full storey up, along the north side.
            canvas.Fill(1, 0, shelfSouth, across - 2, Storey, across - 2, BlockId.Stone);
            canvas.Fill(Cell, Storey + 1, shelfSouth, (2 * Cell) - 1, Storey + shelfHeadroom, across - 2, BlockId.Air);

            // The ramp: up a block every two cells, its top tread level with the shelf.
            for (int rise = 1; rise <= Storey; rise++)
            {
                int z = shelfSouth - ((Storey - rise + 1) * treadDepth);
                canvas.Fill(rampWest, 0, z, rampEast, rise, z + treadDepth - 1, BlockId.Stone);
            }

            canvas.Light(middle, 4, middle);
            canvas.Light(middle, Storey + 3, across - 4);
        })
    {
        SetPiece = true,
    };

    /// <summary>
    /// A great hall three cells across and two storeys high, pillared, doors in the middle of each
    /// side, and its north-east corner fallen into a rounded cave that leaves the hall by a cave way
    /// on: the building and the cave in one room.
    /// </summary>
    internal static DungeonModuleShape GreatHall { get; } = new("great-hall", ModuleKind.Building, 3, 2, 3,
        [
            new(1, 0, 0, ModuleFace.South, SocketKind.Door), new(2, 0, 1, ModuleFace.East, SocketKind.Door),
            new(0, 0, 1, ModuleFace.West, SocketKind.Door), new(1, 0, 2, ModuleFace.North, SocketKind.Door),
            new(2, 0, 2, ModuleFace.East, SocketKind.Cave),
        ],
        (canvas, random) =>
        {
            const int pillarNear = 6;
            const int pillarFar = 16;
            const int pillarWidth = 2;
            const int rubbleHeaps = 4;
            int across = canvas.SizeX;
            int roof = canvas.SizeY - 2;

            canvas.Rooms(BlockId.Cobblestone);
            canvas.Fill(1, Storey, 1, across - 2, Storey, across - 2, BlockId.Air);
            foreach (int x in (int[])[pillarNear, pillarFar])
            {
                foreach (int z in (int[])[pillarNear, pillarFar])
                {
                    canvas.Fill(x, 1, z, x + pillarWidth - 1, roof, z + pillarWidth - 1, BlockId.Brick);
                }
            }

            // The fallen corner: rock where the hall's corner stood, and a rounded cave through it.
            int corner = 2 * Cell;
            canvas.Fill(corner, 1, corner, across - 1, roof, across - 1, BlockId.Stone);
            canvas.Chamber(corner + 3.5, 4.2, corner + 3.5, 6.4, 4.2, 6.4, 1);

            // Rubble where the corner came down.
            for (int heap = 0; heap < rubbleHeaps; heap++)
            {
                int x = corner - 4 + random.Range(0, 4);
                int z = corner - 4 + random.Range(0, 4);
                canvas.Fill(x, 1, z, x + 1, 1, z, BlockId.Gravel);
            }

            canvas.Light(Cell * 1.5, Storey + 2, Cell * 0.75);
            canvas.Light(Cell * 0.75, Storey + 2, Cell * 2);
            canvas.Light(corner + 3.5, 3, corner + 3.5);
        })
    {
        SetPiece = true,
    };

    /// <summary>
    /// A shaft five storeys deep and three cells across, open down the middle, with a ledge on its
    /// walls that winds one full turn from the top socket (north) to the floor. Halfway down the ledge
    /// passes the south socket, the way into whatever lies beside the shaft. Stepping off the ledge
    /// drops into the depths; the ledge's lower turns lead back up.
    /// </summary>
    internal static DungeonModuleShape Abyss { get; } = new("abyss", ModuleKind.Cave, 3, 5, 3,
        [new(1, 4, 2, ModuleFace.North, SocketKind.Cave), new(1, 2, 0, ModuleFace.South, SocketKind.Cave)],
        (canvas, random) =>
        {
            const int ledgeHeadroom = 4;
            const int corbelDepth = 3;
            int size = canvas.SizeX;
            int near = 2;
            int far = size - 3;
            int middle = size / 2;
            int top = (4 * Storey) + 1;

            // The walk: from the north socket east along the north wall, down the east wall, west
            // along the south wall past the south socket, up the west wall, and east along the north
            // wall again to under where it began.
            List<(int X, int Z)> path = [];
            for (int x = middle; x < far; x++)
            {
                path.Add((x, far));
            }

            for (int z = far; z > near; z--)
            {
                path.Add((far, z));
            }

            for (int x = far; x > near; x--)
            {
                path.Add((x, near));
            }

            for (int z = near; z < far; z++)
            {
                path.Add((near, z));
            }

            for (int x = near; x < middle - 2; x++)
            {
                path.Add((x, far));
            }

            // Down a storey every quarter of the way, so the ledge is level with the south socket
            // when it passes it and reaches the floor at the end.
            int halfway = path.IndexOf((middle, near));
            int Feet(int step) => step <= halfway
                ? top - (int)Math.Round(step * ((top - (2 * Storey) - 1) / (double)halfway))
                : (2 * Storey) + 1 - (int)Math.Round((step - halfway) * ((2 * Storey) / (double)(path.Count - 1 - halfway)));

            canvas.Fill(1, 1, 1, size - 2, canvas.SizeY - 2, size - 2, BlockId.Air);
            canvas.Ledge([.. path.Select((cell, step) => (cell.X, Feet(step), cell.Z))], ledgeHeadroom, corbelDepth);

            // A glow at the bottom, so the depth reads from the ledge above, and one by the way in.
            canvas.Light(middle, 2, middle);
            canvas.Light(middle, top + 3, far);
        })
    {
        SetPiece = true,
    };

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

    internal static IReadOnlyList<DungeonModuleShape> CaveRoute { get; } = [CaveTunnel, CaveBend, CaveDescent, CaveChamber, CaveShaft, ChasmStair, Abyss, Cavern];

    internal static IReadOnlyList<DungeonModuleShape> BuildingRooms { get; } = [Room, Corridor, Hall, Stair, Gallery, Collapse, GreatHall];

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
