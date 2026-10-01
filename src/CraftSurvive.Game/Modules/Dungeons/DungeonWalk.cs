using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// Where a player can walk in a dungeon volume, worked out from its blocks alone: a cell is a
/// place to stand when the block below is solid and there is headroom above, and from it a player
/// walks to a neighbour on the level, steps up one block, or drops a few. Drops are one way, so
/// whether a place can be reached and whether the player can get back from it are separate
/// questions. It is the cheap route check a generator runs over every candidate; the Engine's
/// navigation is the confirmation on a loaded space.
/// </summary>
internal static class DungeonWalk
{
    /// <summary>Clear cells a standing player needs above the floor: their height, rounded up.</summary>
    internal const int Headroom = 2;

    /// <summary>The most a player steps up without climbing: one block, as the character controller allows.</summary>
    internal const int StepUp = 1;

    /// <summary>The furthest a route may drop: a walkable fall, one way.</summary>
    internal const int MaximumDrop = 3;

    private static readonly (int X, int Z)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Whether a player can stand with their feet in a cell.</summary>
    internal static bool Standable(DungeonVolume volume, int x, int y, int z)
    {
        if (!volume.Contains(x, y, z) || !Solid(volume.At(x, y - 1, z)))
        {
            return false;
        }

        for (int clear = 0; clear < Headroom; clear++)
        {
            if (Solid(volume.At(x, y + clear, z)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The cell a player standing over a column at a height comes to rest in: the first standable cell at or below it, if close.</summary>
    internal static DungeonCell? Settle(DungeonVolume volume, DungeonCell at)
    {
        for (int y = at.Y; y >= Math.Max(1, at.Y - MaximumDrop - 1); y--)
        {
            if (Standable(volume, at.X, y, at.Z))
            {
                return at with { Y = y };
            }
        }

        return null;
    }

    /// <summary>Every standable cell reachable on foot from a start, forward (as walked) or in reverse (cells from which the start can be walked to).</summary>
    internal static HashSet<DungeonCell> Reachable(DungeonVolume volume, DungeonCell start, bool reverse = false)
    {
        HashSet<DungeonCell> seen = [];
        if (!Standable(volume, start.X, start.Y, start.Z))
        {
            return seen;
        }

        Queue<DungeonCell> frontier = new();
        seen.Add(start);
        frontier.Enqueue(start);
        while (frontier.TryDequeue(out DungeonCell cell))
        {
            foreach ((int dx, int dz) in Neighbours)
            {
                int x = cell.X + dx;
                int z = cell.Z + dz;
                for (int dy = -MaximumDrop; dy <= StepUp; dy++)
                {
                    // Forward, a player steps up at most one and drops at most a few; in reverse the
                    // same moves are read backwards, so a drop becomes a climb that cannot be made.
                    int rise = reverse ? -dy : dy;
                    int y = cell.Y + dy;
                    if (rise > StepUp || rise < -MaximumDrop || !Standable(volume, x, y, z))
                    {
                        continue;
                    }

                    DungeonCell from = reverse ? new DungeonCell(x, y, z) : cell;
                    DungeonCell to = reverse ? cell : new DungeonCell(x, y, z);
                    if (!Passable(volume, from, to))
                    {
                        continue;
                    }

                    DungeonCell next = new(x, y, z);
                    if (seen.Add(next))
                    {
                        frontier.Enqueue(next);
                    }
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Whether a body can move from one standable cell to its neighbour: stepping up needs headroom
    /// over the start, and dropping needs the column above the landing clear for the fall.
    /// </summary>
    private static bool Passable(DungeonVolume volume, DungeonCell from, DungeonCell to)
    {
        int top = Math.Max(from.Y, to.Y) + Headroom - 1;
        for (int y = Math.Min(from.Y, to.Y); y <= top; y++)
        {
            if (y >= from.Y && Solid(volume.At(from.X, y, from.Z)))
            {
                return false;
            }

            if (y >= to.Y && Solid(volume.At(to.X, y, to.Z)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Solid(BlockId block) => BlockRegistry.Get(block).Collidable;
}
