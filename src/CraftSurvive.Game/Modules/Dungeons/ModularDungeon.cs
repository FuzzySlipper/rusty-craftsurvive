using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>A module set down in the lattice: its shape, its quarter turns, and its lowest corner cell.</summary>
internal sealed record PlacedModule(DungeonModuleShape Shape, int Turns, int X, int Y, int Z)
{
    internal int Width => Turns % 2 == 0 ? Shape.Width : Shape.Depth;

    internal int Depth => Turns % 2 == 0 ? Shape.Depth : Shape.Width;

    /// <summary>Every lattice cell the module occupies.</summary>
    internal IEnumerable<(int X, int Y, int Z)> Cells()
    {
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Shape.Height; y++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    yield return (X + x, Y + y, Z + z);
                }
            }
        }
    }

    /// <summary>The module's sockets where they now are: lattice cell and face.</summary>
    internal IEnumerable<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> Sockets() =>
        Shape.Sockets.Select(socket =>
        {
            (int x, int z) = ModularDungeon.Turn(socket.X, socket.Z, Shape.Width, Shape.Depth, Turns);
            return (X + x, Y + socket.Y, Z + z, ModularDungeon.Turn(socket.Face, Turns), socket.Kind);
        });
}

/// <summary>
/// Dungeon approach B, "socketed modules": dungeons assembled from authored 3D pieces on a coarse
/// lattice - one storey per cell layer - joined where their sockets meet, each piece turned in
/// quarter turns. The authored flow is the same as A's and C's: arrive in a cave, follow it down to
/// the building's storey, enter through a breach, wander the building's rooms, stairs, galleries and
/// fallen floors down to the vault on its deepest storey, and come back. What varies is the
/// structure itself: which pieces, in what order, at what heights. The rock is then sculpted as in
/// C. Every candidate is walk-checked, and a failing one drawn again.
/// </summary>
internal static class ModularDungeon
{
    internal const int LatticeX = 12;
    internal const int LatticeY = 13;
    internal const int LatticeZ = 12;

    /// <summary>The volume's lowest layer is solid: lattice storeys start one block up.</summary>
    internal const int BaseY = 1;

    private const int MaximumCaveModules = 14;
    private const int MaximumPlacementTries = 400;

    /// <summary>Generates a walkable modular dungeon, sculpted, or the last candidate with the reason it failed.</summary>
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable, IReadOnlyList<PlacedModule> Modules) Generate(ulong seed)
    {
        (DungeonLayout, DungeonPlan, DungeonVerdict, DungeonVolume, IReadOnlyList<PlacedModule>) last = default;
        for (int attempt = 0; attempt < CarveAndStamp.MaximumAttempts; attempt++)
        {
            last = Candidate(seed, attempt);
            if (last.Item3.Walkable)
            {
                return last;
            }
        }

        return last;
    }

    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable, IReadOnlyList<PlacedModule> Modules) Candidate(ulong seed, int attempt)
    {
        DungeonRandom random = new(seed ^ ((ulong)attempt * 0xD1B5_4A32_D192_ED03UL) ^ 0xB0B0_B0B0UL);
        DungeonMix mix = (DungeonMix)random.Range(0, 2);
        Assembly assembly = new(random);
        string? failure = assembly.Grow(mix);
        if (failure is not null)
        {
            // An assembly that failed is not worth stamping or sculpting: report it as it stands.
            DungeonVolume empty = new(1, 1, 1, BlockId.Stone);
            DungeonPlan failed = new(seed, attempt, mix, 0, default, default, [], default);
            DungeonLayout nothing = new($"modular-{seed:x}", empty, Vector3.Zero, Vector3.Zero, []);
            return (nothing, failed, new DungeonVerdict(false, failure, 0), empty, assembly.Placed);
        }

        return Finish(seed, attempt, mix, assembly.Placed, assembly.Joins, assembly.BreachModule, assembly.VaultModule, random);
    }

    /// <summary>
    /// A dungeon from modules already placed: stamped, joined wherever two placed sockets face each
    /// other, sculpted and walk-checked as an assembled one is. For hand-placed sketches.
    /// </summary>
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable, IReadOnlyList<PlacedModule> Modules) Placed(
        ulong seed, IReadOnlyList<PlacedModule> placed, PlacedModule breach, PlacedModule vault)
    {
        List<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> joins = [];
        HashSet<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> sockets = [.. placed.SelectMany(module => module.Sockets())];
        foreach (var socket in sockets)
        {
            (int dx, int dz) = Step(socket.Face);
            var facing = (socket.X + dx, socket.Y, socket.Z + dz, Opposite(socket.Face), socket.Kind);
            if (socket.Face is ModuleFace.North or ModuleFace.East && sockets.Contains(facing))
            {
                joins.Add(socket);
            }
        }

        return Finish(seed, 0, DungeonMix.Balanced, placed, joins, breach, vault, new DungeonRandom(seed));
    }

    private static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable, IReadOnlyList<PlacedModule> Modules) Finish(
        ulong seed, int attempt, DungeonMix mix, IReadOnlyList<PlacedModule> placed,
        IReadOnlyList<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> joins, PlacedModule? breachModule, PlacedModule? vaultModule,
        DungeonRandom random)
    {
        DungeonVolume volume = new(CarveAndStamp.ChunksX, CarveAndStamp.ChunksY, CarveAndStamp.ChunksZ, BlockId.Stone);
        HashSet<DungeonCell> climbs = [];
        Dictionary<PlacedModule, List<Vector3>> moduleLights = [];
        foreach (PlacedModule module in placed)
        {
            moduleLights[module] = Stamp(volume, module, random, climbs);
        }

        foreach ((int x, int y, int z, ModuleFace face, SocketKind kind) in joins)
        {
            Open(volume, x, y, z, face, kind);
        }

        PlacedModule arrival = placed[0];
        DungeonCell arrivalCell = Floor(arrival, 0, 0);
        DungeonCell breach = breachModule is PlacedModule b ? Floor(b, 0, 0) : arrivalCell;
        DungeonCell loot = vaultModule is PlacedModule v ? Floor(v, 0, 0) : arrivalCell;
        List<DungeonCell> rooms = [.. placed.Where(module => module.Shape.Kind == ModuleKind.Building).Select(module => Floor(module, 0, 0))];
        int storeys = placed.Where(module => module.Shape.Kind == ModuleKind.Building).SelectMany(module => module.Cells()).Select(cell => cell.Y).Distinct().Count();
        DungeonPlan plan = new(seed, attempt, mix, storeys, arrivalCell, breach, rooms, loot);

        (RockDensity rock, DungeonVolume building, DungeonVolume walkable) = SculptedRock.Sculpt(volume, arrivalCell, seed, climbs);
        DungeonVerdict verdict = CarveAndStamp.Check(walkable, plan);
        List<Vector3> lights = [.. Lights(placed, moduleLights)];
        DungeonLayout layout = new(
            $"modular-{seed:x}",
            building,
            new Vector3(arrivalCell.X + 0.5f, arrivalCell.Y, arrivalCell.Z + 0.5f),
            new Vector3(arrivalCell.X + 0.5f, arrivalCell.Y, arrivalCell.Z - 1.5f),
            lights)
        {
            Rock = rock,
        };
        return (layout, plan, verdict, walkable, placed);
    }

    /// <summary>
    /// One module on its own, unturned, in a volume with room around it, every socket opened onto a
    /// short stub of floor: what the module self-check walks between. Returns where to stand just
    /// outside each socket.
    /// </summary>
    internal static (DungeonVolume Volume, List<(ModuleSocket Socket, DungeonCell Stand)> Sockets) Isolated(DungeonModuleShape shape)
    {
        DungeonVolume volume = new(3, 3, 3, BlockId.Stone);
        PlacedModule module = new(shape, 0, 1, 1, 1);
        Stamp(volume, module, new DungeonRandom(1UL), []);
        List<(ModuleSocket, DungeonCell)> stands = [];
        foreach ((ModuleSocket socket, var placed) in shape.Sockets.Zip(module.Sockets()))
        {
            Open(volume, placed.X, placed.Y, placed.Z, placed.Face, placed.Kind);
            (int dx, int dz) = Step(placed.Face);
            int floor = (placed.Y * ModuleCanvas.StoreyHeight) + BaseY + 1;
            int x = (placed.X * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2) + (dx * ((ModuleCanvas.CellSize / 2) + 2));
            int z = (placed.Z * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2) + (dz * ((ModuleCanvas.CellSize / 2) + 2));
            volume.Fill(new DungeonCell(x - 2, floor - 1, z - 2), new DungeonCell(x + 2, floor - 1, z + 2), BlockId.Stone);
            volume.Fill(new DungeonCell(x - 2, floor, z - 2), new DungeonCell(x + 2, floor + 2, z + 2), BlockId.Air);
            stands.Add((socket, new DungeonCell(x, floor, z)));
        }

        return (volume, stands);
    }

    /// <summary>A cell's coordinates within a module after quarter turns clockwise seen from above.</summary>
    internal static (int X, int Z) Turn(int x, int z, int width, int depth, int turns) => (turns % 4) switch
    {
        1 => (z, width - 1 - x),
        2 => (width - 1 - x, depth - 1 - z),
        3 => (depth - 1 - z, x),
        _ => (x, z),
    };

    internal static ModuleFace Turn(ModuleFace face, int turns) => (ModuleFace)(((int)face + turns) % 4);

    internal static ModuleFace Opposite(ModuleFace face) => (ModuleFace)(((int)face + 2) % 4);

    internal static (int X, int Z) Step(ModuleFace face) => face switch
    {
        ModuleFace.North => (0, 1),
        ModuleFace.East => (1, 0),
        ModuleFace.South => (0, -1),
        _ => (-1, 0),
    };

    /// <summary>A standing place in a module's cell: the cell's middle, on the floor of a storey.</summary>
    private static DungeonCell Floor(PlacedModule module, int cellX, int storey) =>
        new((module.X + cellX) * ModuleCanvas.CellSize + (ModuleCanvas.CellSize / 2),
            ((module.Y + storey) * ModuleCanvas.StoreyHeight) + BaseY + 1,
            (module.Z * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2));

    /// <summary>
    /// Lays a module's voxels into the dungeon, turned, leaving rock where the module left it, and
    /// adds its climb lanes to <paramref name="climbs"/>. Returns the lights it hangs, in the dungeon.
    /// </summary>
    private static List<Vector3> Stamp(DungeonVolume volume, PlacedModule module, DungeonRandom random, HashSet<DungeonCell> climbs)
    {
        DungeonModuleShape shape = module.Shape;
        ModuleCanvas canvas = new(shape.Width, shape.Height, shape.Depth);
        shape.Build(canvas, random);
        if (shape.SetPiece)
        {
            foreach (ModuleSocket socket in shape.Sockets.Where(socket => socket.Kind == SocketKind.Cave))
            {
                canvas.Landing(socket);
            }
        }
        int ox = module.X * ModuleCanvas.CellSize;
        int oy = (module.Y * ModuleCanvas.StoreyHeight) + BaseY;
        int oz = module.Z * ModuleCanvas.CellSize;
        for (int x = 0; x < canvas.SizeX; x++)
        {
            for (int y = 0; y < canvas.SizeY; y++)
            {
                for (int z = 0; z < canvas.SizeZ; z++)
                {
                    if (canvas.At(x, y, z) is BlockId block)
                    {
                        (int tx, int tz) = Turn(x, z, canvas.SizeX, canvas.SizeZ, module.Turns);
                        volume.Set(ox + tx, oy + y, oz + tz, block);
                    }
                }
            }
        }

        foreach ((int x, int y, int z) in canvas.Climbs)
        {
            (int tx, int tz) = Turn(x, z, canvas.SizeX, canvas.SizeZ, module.Turns);
            climbs.Add(new DungeonCell(ox + tx, oy + y, oz + tz));
        }

        List<Vector3> lights = [];
        foreach ((double x, double y, double z) in canvas.Lights)
        {
            // Turned as a cell's corner is: the light's cell, then its offset within the cell.
            (int tx, int tz) = Turn((int)x, (int)z, canvas.SizeX, canvas.SizeZ, module.Turns);
            lights.Add(new Vector3(ox + tx + 0.5f, oy + (float)y, oz + tz + 0.5f));
        }

        return lights;
    }

    /// <summary>
    /// Opens a join between two modules: a doorway two wide and three tall, or a cave mouth four wide
    /// and four tall, through both modules' walls, with floor under it.
    /// </summary>
    private static void Open(DungeonVolume volume, int cellX, int cellY, int cellZ, ModuleFace face, SocketKind kind)
    {
        int half = kind == SocketKind.Door ? 1 : 2;
        int height = kind == SocketKind.Door ? 3 : 4;
        int floor = (cellY * ModuleCanvas.StoreyHeight) + BaseY + 1;
        int centreX = (cellX * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2);
        int centreZ = (cellZ * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2);
        (int dx, int dz) = Step(face);
        int boundaryX = centreX + (dx * ModuleCanvas.CellSize / 2);
        int boundaryZ = centreZ + (dz * ModuleCanvas.CellSize / 2);
        for (int across = -half; across < half; across++)
        {
            for (int through = -3; through <= 2; through++)
            {
                int x = dx != 0 ? boundaryX + through : centreX + across;
                int z = dz != 0 ? boundaryZ + through : centreZ + across;
                if (volume.At(x, floor - 1, z) == BlockId.Air)
                {
                    volume.Set(x, floor - 1, z, kind == SocketKind.Door ? BlockId.Cobblestone : BlockId.Stone);
                }

                for (int y = floor; y < floor + height; y++)
                {
                    volume.Set(x, y, z, BlockId.Air);
                }
            }
        }
    }

    /// <summary>
    /// The lights each module hangs, its own or one over its first cell, up to the dungeon's light
    /// budget, spread along the assembly.
    /// </summary>
    private static IEnumerable<Vector3> Lights(IReadOnlyList<PlacedModule> placed, Dictionary<PlacedModule, List<Vector3>> own)
    {
        List<Vector3> all = [.. placed.SelectMany(module => own.TryGetValue(module, out List<Vector3>? mine) && mine.Count > 0
            ? mine
            : [DefaultLight(module)])];
        int stride = Math.Max(1, (int)Math.Ceiling(all.Count / (double)DungeonLayout.MaximumLights));
        for (int index = 0; index < all.Count; index += stride)
        {
            yield return all[index];
        }
    }

    private static Vector3 DefaultLight(PlacedModule module)
    {
        DungeonCell floor = Floor(module, 0, 0);
        return new Vector3(floor.X + 0.5f, floor.Y + DefaultLightHeight, floor.Z + 0.5f);
    }

    /// <summary>How high over a module's floor its default light hangs.</summary>
    private const float DefaultLightHeight = 3.2f;

    /// <summary>
    /// The growing assembly: which lattice cells are taken, the placed modules in order, and every
    /// socket join. It grows the cave from the arrival down to the building's storey, sets the
    /// breach, grows the building, and sets the vault on the building's deepest storey.
    /// </summary>
    private sealed class Assembly(DungeonRandom random)
    {
        private readonly bool[] taken = new bool[LatticeX * LatticeY * LatticeZ];
        private readonly List<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> open = [];

        internal List<PlacedModule> Placed { get; } = [];

        internal List<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> Joins { get; } = [];

        internal PlacedModule? BreachModule { get; private set; }

        internal PlacedModule? VaultModule { get; private set; }

        /// <summary>Grows the whole dungeon; returns why it could not, or null.</summary>
        internal string? Grow(DungeonMix mix)
        {
            int breachStorey = random.Range(2, 4);
            int startStorey = Math.Min(LatticeY - 2, breachStorey + mix switch
            {
                DungeonMix.MostlyCave => random.Range(6, 8),
                DungeonMix.Balanced => random.Range(4, 6),
                _ => random.Range(2, 4),
            });
            PlacedModule arrival = new(DungeonModules.Arrival, random.Range(0, 3), random.Range(3, LatticeX - 4), startStorey, random.Range(3, LatticeZ - 4));
            Place(arrival);

            // The cave: always on from the newest cave socket, down until the building's storey.
            (int X, int Y, int Z, ModuleFace Face, SocketKind Kind) end = open.Single();
            for (int count = 0; count < MaximumCaveModules && end.Y > breachStorey; count++)
            {
                bool deep = end.Y - breachStorey >= 3;
                DungeonModuleShape[] choices = deep
                    ? [DungeonModules.CaveShaft, DungeonModules.CaveShaft, DungeonModules.CaveDescent, DungeonModules.CaveDescent, DungeonModules.CaveTunnel, DungeonModules.CaveBend, DungeonModules.CaveChamber,
                        DungeonModules.Abyss, DungeonModules.Abyss, DungeonModules.ChasmStair, DungeonModules.Cavern]
                    : [DungeonModules.CaveDescent, DungeonModules.CaveDescent, DungeonModules.CaveTunnel, DungeonModules.CaveBend, DungeonModules.CaveChamber,
                        DungeonModules.ChasmStair, DungeonModules.Cavern];
                if (Attach(end, choices, mustNotRise: true) is not PlacedModule next)
                {
                    return "the cave ran into itself or the edge";
                }

                end = Newest(next, SocketKind.Cave) ?? end;
            }

            if (end.Y != breachStorey)
            {
                return "the cave did not reach the building's storey";
            }

            if (Attach(end, [DungeonModules.Breach]) is not PlacedModule breach)
            {
                return "there was no room for the breach";
            }

            BreachModule = breach;

            // The building: grow from open doors, more of it where the building has the upper hand,
            // always heading down until a storey at least two below the breach is reached.
            int rooms = mix switch
            {
                DungeonMix.MostlyBuilding => random.Range(12, 16),
                DungeonMix.Balanced => random.Range(8, 11),
                _ => random.Range(5, 7),
            };
            int vaultStorey = Math.Max(0, breachStorey - 2);
            for (int tries = 0; tries < MaximumPlacementTries && (Building() < rooms || LowestBuilding() > vaultStorey); tries++)
            {
                List<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> doors = [.. open.Where(socket => socket.Kind == SocketKind.Door)];
                if (doors.Count == 0)
                {
                    return "the building closed itself off";
                }

                bool descend = LowestBuilding() > vaultStorey;
                var from = descend
                    ? doors.OrderBy(socket => socket.Y).ThenBy(_ => random.Next()).First()
                    : doors[random.Range(0, doors.Count - 1)];
                DungeonModuleShape[] choices = descend
                    ? [DungeonModules.Stair, DungeonModules.Stair, DungeonModules.Collapse, DungeonModules.Room, DungeonModules.Corridor]
                    : [DungeonModules.Room, DungeonModules.Room, DungeonModules.Corridor, DungeonModules.Hall, DungeonModules.Hall, DungeonModules.Gallery, DungeonModules.Gallery, DungeonModules.Gallery, DungeonModules.Stair, DungeonModules.Collapse,
                        DungeonModules.GreatHall, DungeonModules.GreatHall, DungeonModules.GreatHall];
                Attach(from, choices);
            }

            if (LowestBuilding() > vaultStorey)
            {
                return "the building did not reach the vault's storey";
            }

            foreach (var door in open.Where(socket => socket.Kind == SocketKind.Door).OrderBy(socket => socket.Y).ToList())
            {
                if (Attach(door, [DungeonModules.Vault]) is PlacedModule vault)
                {
                    VaultModule = vault;
                    return null;
                }
            }

            return "there was no room for the vault";
        }

        private int Building() => Placed.Count(module => module.Shape.Kind == ModuleKind.Building);

        private int LowestBuilding() => Placed.Where(module => module.Shape.Kind == ModuleKind.Building).Select(module => module.Y).DefaultIfEmpty(int.MaxValue).Min();

        /// <summary>The newly placed module's open socket of a kind, the way on.</summary>
        private (int X, int Y, int Z, ModuleFace Face, SocketKind Kind)? Newest(PlacedModule module, SocketKind kind)
        {
            var ways = module.Sockets().Where(socket => socket.Kind == kind && open.Contains(socket)).ToList();
            return ways.Count == 0 ? null : ways[random.Range(0, ways.Count - 1)];
        }

        /// <summary>
        /// Joins one of the choices to an open socket: some turn and some socket of the choice that
        /// lands on the neighbouring cell facing back, with every cell it needs free. Every such
        /// placement is weighed - a cave going down must not rise, and a placement whose ways on
        /// open onto free cells is preferred - and the best, ties broken at random, is placed.
        /// </summary>
        internal PlacedModule? Attach((int X, int Y, int Z, ModuleFace Face, SocketKind Kind) socket, DungeonModuleShape[] choices,
            bool mustNotRise = false)
        {
            (int dx, int dz) = Step(socket.Face);
            (int X, int Y, int Z) target = (socket.X + dx, socket.Y, socket.Z + dz);
            ModuleFace facing = Opposite(socket.Face);
            PlacedModule? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (DungeonModuleShape shape in choices.Distinct().Where(shape => !shape.SetPiece || !Placed.Any(module => module.Shape == shape)))
            {
                double weight = choices.Count(choice => choice == shape);
                for (int turns = 0; turns < 4; turns++)
                {
                    PlacedModule probe = new(shape, turns, 0, 0, 0);
                    foreach (var mine in probe.Sockets().Where(s => s.Kind == socket.Kind && s.Face == facing))
                    {
                        PlacedModule candidate = probe with { X = target.X - mine.X, Y = target.Y - mine.Y, Z = target.Z - mine.Z };
                        if (!candidate.Cells().All(Free) || (mustNotRise && candidate.Y + shape.Height - 1 > socket.Y))
                        {
                            continue;
                        }

                        double score = weight + (2 * Openness(candidate, target, facing)) + (random.Unit() * 3)
                            + (shape.Kind == ModuleKind.Building ? StackingBonus * Stacked(candidate) : 0);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }
            }

            if (best is null)
            {
                return null;
            }

            Place(best);
            open.Remove(socket);
            open.Remove((target.X, target.Y, target.Z, facing, socket.Kind));
            Joins.Add(socket);
            return best;
        }

        /// <summary>How many of a building placement's cells sit directly over or under building already placed: rooms above rooms.</summary>
        private int Stacked(PlacedModule candidate) => candidate.Cells().Count(cell =>
            buildingCells.Contains((cell.X, cell.Y + 1, cell.Z)) || buildingCells.Contains((cell.X, cell.Y - 1, cell.Z)));

        /// <summary>How many lattice cells the building occupies, by cell, for the stacking preference.</summary>
        private readonly HashSet<(int X, int Y, int Z)> buildingCells = [];

        /// <summary>How much a cell sitting over or under the building weighs in a placement's favour.</summary>
        private const double StackingBonus = 2.5;

        /// <summary>How much free lattice lies beyond a placement's other ways on: room for the dungeon to keep growing.</summary>
        private double Openness(PlacedModule candidate, (int X, int Y, int Z) joinedCell, ModuleFace joinedFace)
        {
            double best = 0;
            HashSet<(int, int, int)> cells = [.. candidate.Cells()];
            foreach (var way in candidate.Sockets())
            {
                if ((way.X, way.Y, way.Z) == joinedCell && way.Face == joinedFace)
                {
                    continue;
                }

                (int dx, int dz) = Step(way.Face);
                int free = 0;
                for (int ahead = 1; ahead <= 3; ahead++)
                {
                    (int X, int Y, int Z) cell = (way.X + (dx * ahead), way.Y, way.Z + (dz * ahead));
                    if (!Free(cell) || cells.Contains(cell))
                    {
                        break;
                    }

                    free++;
                }

                best = Math.Max(best, free);
            }

            return best;
        }

        private void Place(PlacedModule module)
        {
            foreach ((int x, int y, int z) in module.Cells())
            {
                taken[Index(x, y, z)] = true;
                if (module.Shape.Kind == ModuleKind.Building)
                {
                    buildingCells.Add((x, y, z));
                }
            }

            Placed.Add(module);
            foreach (var socket in module.Sockets())
            {
                (int dx, int dz) = Step(socket.Face);
                if (Inside(socket.X + dx, socket.Y, socket.Z + dz))
                {
                    open.Add(socket);
                }
            }
        }

        private bool Free((int X, int Y, int Z) cell) => Inside(cell.X, cell.Y, cell.Z) && !taken[Index(cell.X, cell.Y, cell.Z)];

        private static bool Inside(int x, int y, int z) =>
            x >= 1 && y >= 0 && z >= 1 && x < LatticeX - 1 && y < LatticeY && z < LatticeZ - 1;

        private static int Index(int x, int y, int z) => (((z * LatticeY) + y) * LatticeX) + x;
    }
}
