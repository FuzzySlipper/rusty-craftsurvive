namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The way a body with weight takes through a cave (#10908, Patch 2026-11-28: one-way drops and minimal rest ledges), on its
/// 1 m voxels: it walks, steps a cell up or down, drops off an edge (one way), and climbs a face in pitches of at most
/// <see cref="ClimbPitch"/> (about a stamina bar, which climbs some 9 m) onto a ledge to rest on. With edits allowed it may also
/// dig rock out of its way and bridge a gap with a floor, at a cost, so a way always exists and the fewest edits make it: the
/// karst generator's walker carve. Without edits it is the gravity-aware reach check.
/// </summary>
internal sealed class CaveWalker
{
    /// <summary>The highest pitch climbed in one go, in cells (metres): a rest ledge at least this often.</summary>
    internal const int ClimbPitch = 8;

    /// <summary>A standing body's height in cells.</summary>
    internal const int BodyCells = 2;

    // What each move costs: walking a cell, a step up and down, a drop (and per metre fallen), a climb per metre, and with edits
    // a cell dug or a floor cell laid (dearer than going round, so edits stay few).
    private const float WalkCost = 1f, StepUpCost = 1.6f, StepDownCost = 1.2f, DropCost = 4f, DropPerMetre = 0.05f, ClimbPerMetre = 3f;
    private const float DigCost = 7f, BridgeCost = 9f;

    /// <summary>How far down a drop is followed before it is given up (the box's height bounds it anyway).</summary>
    private const int MostDrop = 256;

    /// <summary>The search gives up past this many cells expanded.</summary>
    private const int MostExpanded = 3_000_000;

    /// <summary>How a cell was reached: walked or stepped, dropped to, climbed to.</summary>
    internal enum Move : byte
    {
        Walk = 0,
        Drop = 1,
        Climb = 2,
    }

    private readonly int sizeX, sizeY, sizeZ;
    private readonly bool[] solid;
    private readonly int editMargin;

    /// <param name="solid">Rock per cell, x fastest then y then z; edits change it in place.</param>
    /// <param name="editMargin">How near the grid's faces an edit may be (the box's rind stays whole), in cells.</param>
    internal CaveWalker(int sizeX, int sizeY, int sizeZ, bool[] solid, int editMargin = 2)
    {
        if (solid.Length != sizeX * sizeY * sizeZ)
        {
            throw new ArgumentException("One solid flag per cell.", nameof(solid));
        }

        this.sizeX = sizeX;
        this.sizeY = sizeY;
        this.sizeZ = sizeZ;
        this.solid = solid;
        this.editMargin = editMargin;
    }

    internal int SizeX => sizeX;
    internal int SizeY => sizeY;
    internal int SizeZ => sizeZ;

    private int Index(int x, int y, int z) => (((z * sizeY) + y) * sizeX) + x;

    private bool Inside(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < sizeX && y < sizeY && z < sizeZ;

    /// <summary>Whether a cell is rock (outside the grid is rock).</summary>
    internal bool Solid(int x, int y, int z) => !Inside(x, y, z) || solid[Index(x, y, z)];

    private bool Editable(int x, int y, int z) =>
        x >= editMargin && z >= editMargin && y >= editMargin && x < sizeX - editMargin && z < sizeZ - editMargin && y < sizeY - editMargin;

    /// <summary>Whether a body stands at a cell: its two cells open, rock under its feet.</summary>
    internal bool Standing(int x, int y, int z) => !Solid(x, y, z) && !Solid(x, y + 1, z) && Solid(x, y - 1, z);

    /// <summary>The standing cell nearest a point (cells), searching down a few cells and out a few; null where there is none.</summary>
    internal (int X, int Y, int Z)? Settle(int x, int y, int z, int across = 3, int down = 10, int up = 2)
    {
        for (int r = 0; r <= across; r++)
        for (int dy = up; dy >= -down; dy--)
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
            if (Standing(x + dx, y + dy, z + dz)) return (x + dx, y + dy, z + dz);
        }

        return null;
    }

    /// <summary>A way found: its cells, how each was reached, and the cells it needs dug and floored.</summary>
    internal sealed record Way(IReadOnlyList<(int X, int Y, int Z)> Cells, IReadOnlyList<Move> Moves, IReadOnlyList<(int X, int Y, int Z)> Dug, IReadOnlyList<(int X, int Y, int Z)> Floored, int Expanded)
    {
        internal int Drops => Moves.Count(m => m == Move.Drop);
        internal int Climbs => Moves.Count(m => m == Move.Climb);
    }

    /// <summary>
    /// The cheapest way from one cell to another (within a cell of it), or null. With <paramref name="edits"/> the cells it needs
    /// dug and floored are part of the way (the caller applies them, <see cref="Apply"/>); the start and the goal need not stand.
    /// </summary>
    internal Way? Find((int X, int Y, int Z) start, (int X, int Y, int Z) goal, bool edits)
    {
        // Each node: its cost, where it came from, how, and the edits its last move needed.
        Dictionary<(int, int, int), (float Cost, (int, int, int)? From, Move How, (int, int, int)[] Dig, (int, int, int)[] Floor)> best = new()
        {
            [start] = (0f, null, Move.Walk, [], []),
        };
        PriorityQueue<(int X, int Y, int Z), float> open = new();
        float Guess((int X, int Y, int Z) c) => Math.Abs(c.X - goal.X) + Math.Abs(c.Y - goal.Y) + Math.Abs(c.Z - goal.Z);
        open.Enqueue(start, Guess(start));
        int expanded = 0;
        (int X, int Y, int Z)? reached = null;
        List<(int, int, int)> dig = [], floor = [];

        // What standing at a cell would take: its two cells open, rock under it (with edits, their cost and the cells).
        bool Make((int X, int Y, int Z) c, out float cost)
        {
            cost = 0f;
            dig.Clear();
            floor.Clear();
            for (int h = 0; h < BodyCells; h++)
            {
                if (Solid(c.X, c.Y + h, c.Z))
                {
                    if (!edits || !Editable(c.X, c.Y + h, c.Z)) return false;
                    dig.Add((c.X, c.Y + h, c.Z));
                    cost += DigCost;
                }
            }

            if (!Solid(c.X, c.Y - 1, c.Z))
            {
                if (!edits || !Editable(c.X, c.Y - 1, c.Z)) return false;
                floor.Add((c.X, c.Y - 1, c.Z));
                cost += BridgeCost;
            }

            return true;
        }

        void Try((int X, int Y, int Z) at, (int X, int Y, int Z) next, float add, Move how)
        {
            float through = best[at].Cost + add;
            if (!best.TryGetValue(next, out var known) || through < known.Cost)
            {
                best[next] = (through, at, how, [.. dig], [.. floor]);
                open.Enqueue(next, through + Guess(next));
            }
        }

        (int, int)[] steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (open.TryDequeue(out (int X, int Y, int Z) at, out float priority) && expanded++ < MostExpanded)
        {
            if (priority - Guess(at) > best[at].Cost + 1e-3f) continue;
            if (Math.Abs(at.X - goal.X) <= 1 && Math.Abs(at.Z - goal.Z) <= 1 && Math.Abs(at.Y - goal.Y) <= 1)
            {
                reached = at;
                break;
            }

            foreach ((int dx, int dz) in steps)
            {
                int nx = at.X + dx, nz = at.Z + dz;
                // Walk on, a cell up (headroom over the body first) or a cell down.
                if (Make((nx, at.Y, nz), out float flat)) Try(at, (nx, at.Y, nz), WalkCost + flat, Move.Walk);
                if (!Solid(at.X, at.Y + BodyCells, at.Z) && Make((nx, at.Y + 1, nz), out float up)) Try(at, (nx, at.Y + 1, nz), StepUpCost + up, Move.Walk);
                if (Make((nx, at.Y - 1, nz), out float down)) Try(at, (nx, at.Y - 1, nz), StepDownCost + down, Move.Walk);

                // Off an edge: falls to the first floor under it (one way; no edits).
                if (!Solid(nx, at.Y, nz) && !Solid(nx, at.Y + 1, nz) && !Solid(nx, at.Y - 1, nz))
                {
                    dig.Clear();
                    floor.Clear();
                    for (int y = at.Y - 2; y > at.Y - MostDrop && y > 0; y--)
                    {
                        if (Solid(nx, y, nz)) break;
                        if (Standing(nx, y, nz))
                        {
                            Try(at, (nx, y, nz), DropCost + ((at.Y - y) * DropPerMetre), Move.Drop);
                            break;
                        }
                    }
                }

                // A face ahead: climbed in a pitch onto a ledge at its top or one dug into it, the body's own column clear.
                if (Solid(nx, at.Y, nz))
                {
                    for (int h = 2; h <= ClimbPitch; h++)
                    {
                        if (!Solid(nx, at.Y + h - 1, nz)) break;
                        // The climber's own column, up to its head at the top of the pitch.
                        bool clear = true;
                        for (int c = at.Y + BodyCells; c <= at.Y + h + 1 && clear; c++) clear = !Solid(at.X, c, at.Z);
                        if (!clear) break;
                        if (Make((nx, at.Y + h, nz), out float ledge) && dig.Count + floor.Count <= BodyCells)
                        {
                            Try(at, (nx, at.Y + h, nz), (h * ClimbPerMetre) + ledge, Move.Climb);
                        }
                    }
                }
            }
        }

        if (reached is not (int, int, int) end)
        {
            return null;
        }

        List<(int, int, int)> cells = [];
        List<Move> moves = [];
        HashSet<(int, int, int)> dug = [], floored = [];
        for ((int, int, int)? at = end; at is (int, int, int) cell; at = best[cell].From)
        {
            cells.Insert(0, cell);
            moves.Insert(0, best[cell].How);
            foreach (var d in best[cell].Dig) dug.Add(d);
            foreach (var f in best[cell].Floor) floored.Add(f);
        }

        return new Way(cells, moves, [.. dug], [.. floored], expanded);
    }

    /// <summary>Every standing cell a body reaches from one, walking, stepping, dropping and climbing, with no edits.</summary>
    internal HashSet<(int X, int Y, int Z)> Reach((int X, int Y, int Z) start, int most = MostExpanded)
    {
        HashSet<(int X, int Y, int Z)> reached = [start];
        Queue<(int X, int Y, int Z)> frontier = new([start]);
        (int, int)[] steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        void Add((int, int, int) c)
        {
            if (reached.Add(c)) frontier.Enqueue(c);
        }

        while (frontier.TryDequeue(out (int X, int Y, int Z) at) && reached.Count < most)
        {
            foreach ((int dx, int dz) in steps)
            {
                int nx = at.X + dx, nz = at.Z + dz;
                if (Standing(nx, at.Y, nz)) Add((nx, at.Y, nz));
                if (!Solid(at.X, at.Y + BodyCells, at.Z) && Standing(nx, at.Y + 1, nz)) Add((nx, at.Y + 1, nz));
                if (Standing(nx, at.Y - 1, nz)) Add((nx, at.Y - 1, nz));
                if (!Solid(nx, at.Y, nz) && !Solid(nx, at.Y + 1, nz) && !Solid(nx, at.Y - 1, nz))
                {
                    for (int y = at.Y - 2; y > at.Y - MostDrop && y > 0; y--)
                    {
                        if (Solid(nx, y, nz)) break;
                        if (Standing(nx, y, nz)) { Add((nx, y, nz)); break; }
                    }
                }

                if (Solid(nx, at.Y, nz))
                {
                    for (int h = 2; h <= ClimbPitch; h++)
                    {
                        if (!Solid(nx, at.Y + h - 1, nz)) break;
                        bool clear = true;
                        for (int c = at.Y + BodyCells; c <= at.Y + h + 1 && clear; c++) clear = !Solid(at.X, c, at.Z);
                        if (!clear) break;
                        if (Standing(nx, at.Y + h, nz)) Add((nx, at.Y + h, nz));
                    }
                }
            }
        }

        return reached;
    }

    /// <summary>Applies a way's edits to the grid (the caller carries them into its own field).</summary>
    internal void Apply(Way way)
    {
        foreach ((int x, int y, int z) in way.Dug) solid[Index(x, y, z)] = false;
        foreach ((int x, int y, int z) in way.Floored) solid[Index(x, y, z)] = true;
    }
}
