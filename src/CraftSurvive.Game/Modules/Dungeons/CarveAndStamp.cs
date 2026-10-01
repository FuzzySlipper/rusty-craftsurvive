using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>How much of a dungeon is cave and how much is building.</summary>
internal enum DungeonMix
{
    MostlyBuilding,
    Balanced,
    MostlyCave,
}

/// <summary>A small deterministic generator: a dungeon's draws are a pure function of its seed.</summary>
internal sealed class DungeonRandom(ulong seed)
{
    private ulong state = seed;

    internal ulong Next()
    {
        ulong z = state += 0x9E37_79B9_7F4A_7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A whole number from minimum to maximum, both included.</summary>
    internal int Range(int minimum, int maximum) => minimum + (int)(Next() % (ulong)(maximum - minimum + 1));

    internal double Unit() => (Next() >> 11) * (1d / (1UL << 53));

    internal bool Chance(double probability) => Unit() < probability;
}

/// <summary>
/// What a generated dungeon is, beyond its blocks: the authored flow it was built to and the places
/// the route check needs - where the player arrives, where the cave breaks into the building, one
/// place on every floor, and the loot room.
/// </summary>
internal sealed record DungeonPlan(
    ulong Seed,
    int Attempt,
    DungeonMix Mix,
    int Floors,
    DungeonCell Arrival,
    DungeonCell Breach,
    IReadOnlyList<DungeonCell> FloorAnchors,
    DungeonCell Loot);

/// <summary>Whether a generated dungeon can be walked as its flow intends, and if not, the first reason why.</summary>
internal sealed record DungeonVerdict(bool Walkable, string Reason, int ReachableCells);

/// <summary>
/// Dungeon approach A, "carve and stamp": one voxel volume, a building stamped into it and a chasm
/// and its ledges carved through it. The authored flow is fixed - arrive at the top of the chasm,
/// descend a ledge cut into its wall, enter the building where the chasm has torn it open partway
/// up, take the stairs, find the loot on the lowest floor, and come back the same way - and every
/// draw only varies how that flow is realised: the mix of cave and building, the building's size and
/// floors, the rooms, the chasm's line and width, the alcoves. A candidate that cannot be walked as the flow intends is drawn again with the
/// next attempt's seed, so a seed always yields a walkable dungeon or an honest failure.
/// </summary>
internal static class CarveAndStamp
{
    internal const int ChunksX = 6;
    internal const int ChunksY = 5;
    internal const int ChunksZ = 6;

    /// <summary>How many candidates one seed may draw before it is reported as failing.</summary>
    internal const int MaximumAttempts = 16;

    /// <summary>A storey: one slab and five clear cells.</summary>
    internal const int StoreyHeight = 6;

    private const int BuildingBaseY = 8;
    private const int StairWidth = 3;

    /// <summary>How far into the building, from its back wall, a flight of stairs ends.</summary>
    private const int StairTopZ = 7;
    private const int ArrivalAboveRoof = 12;
    private const int CaveClearHeight = 4;
    private const int LightEveryCaveSteps = 14;

    /// <summary>A cave that has not reached its breach in this many steps gives up; the route check then fails it.</summary>
    private const int MaximumCaveSteps = 4000;

    /// <summary>Generates a walkable dungeon for a seed, or the last candidate with the reason it failed.</summary>
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict) Generate(ulong seed)
    {
        (DungeonLayout, DungeonPlan, DungeonVerdict) last = default;
        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            last = Candidate(seed, attempt);
            if (last.Item3.Walkable)
            {
                return last;
            }
        }

        return last;
    }

    /// <summary>One candidate: the realised flow and its route check.</summary>
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict) Candidate(ulong seed, int attempt)
    {
        DungeonRandom random = new(seed ^ ((ulong)attempt * 0xA24B_AED4_963E_E407UL));
        DungeonVolume volume = new(ChunksX, ChunksY, ChunksZ, BlockId.Stone);
        List<Vector3> lights = [];
        Strata(volume, random);

        DungeonMix mix = (DungeonMix)random.Range(0, 2);
        int floors = mix switch
        {
            DungeonMix.MostlyBuilding => random.Range(4, 5),
            DungeonMix.Balanced => random.Range(3, 4),
            _ => random.Range(2, 3),
        };

        // The chasm is the dungeon's spine: a deep crack the length of the volume. The building
        // stands across its west edge, so the chasm has torn its east side away.
        Chasm chasm = new(
            CentreX: random.Range(46, 56),
            HalfWidth: mix == DungeonMix.MostlyBuilding ? random.Range(3, 4) : random.Range(4, 6),
            Phase: random.Unit() * Math.PI * 2);
        // The building is laid out from the chasm: an intact west part, where its stairs and doors
        // are, and an east part the chasm has taken.
        int intact = random.Range(16, 22);
        int width = intact + random.Range(4, 10);
        int depth = random.Range(16, 22);
        int x0 = chasm.NearestWest - intact;
        int z0 = random.Range(12, 20);
        Building building = new(x0, BuildingBaseY, z0, width, depth, floors) { Intact = intact };
        Stamp(volume, building, random, lights, mix);

        int breachFloor = random.Range(1, floors - 1);
        int breachY = building.FloorY(breachFloor) + 1;
        DungeonCell arrival = new(0, Math.Min(volume.SizeY - 8, building.RoofY + ArrivalAboveRoof + random.Range(0, 10)), volume.SizeZ - 8);
        Carve(volume, chasm, arrival.Y + 4);
        (DungeonCell landing, DungeonCell breach) = Ledges(volume, chasm, random, arrival, breachY, building, lights, mix);
        arrival = landing;
        Breach(volume, breach, random);
        Decay(volume, building, random, mix);

        // The landing where the player arrives: ground under it and room around it, whatever the
        // cave did to the rock beneath on its way down.
        volume.Fill(new DungeonCell(arrival.X - 2, arrival.Y - 1, arrival.Z - 2), new DungeonCell(arrival.X + 2, arrival.Y - 1, arrival.Z + 4), BlockId.Stone);
        volume.Fill(new DungeonCell(arrival.X - 2, arrival.Y, arrival.Z - 2), new DungeonCell(arrival.X + 2, arrival.Y + 3, arrival.Z + 4), BlockId.Air);

        DungeonCell loot = building.LootRoom;
        volume.Fill(loot with { Y = loot.Y - 1 }, loot with { Y = loot.Y - 1 }, BlockId.Planks);
        List<DungeonCell> anchors = [.. Enumerable.Range(0, floors).Select(floor => building.Anchor(floor))];
        DungeonPlan plan = new(seed, attempt, mix, floors, arrival, breach with { Z = breach.Z - 2 }, anchors, loot);
        _ = breachFloor;
        DungeonVerdict verdict = Check(volume, plan);
        DungeonLayout layout = new(
            $"carve-and-stamp-{seed:x}",
            volume,
            new Vector3(arrival.X + 0.5f, arrival.Y, arrival.Z + 0.5f),
            new Vector3(arrival.X + 0.5f, arrival.Y, arrival.Z + 2.5f),
            lights.Take(DungeonLayout.MaximumLights).ToArray());
        return (layout, plan, verdict);
    }

    /// <summary>
    /// The route check: from the arrival a player must reach the breach, every floor and the loot,
    /// and from the loot they must be able to walk back to the arrival.
    /// </summary>
    internal static DungeonVerdict Check(DungeonVolume volume, DungeonPlan plan)
    {
        HashSet<DungeonCell> forward = DungeonWalk.Reachable(volume, plan.Arrival);
        if (forward.Count == 0)
        {
            return new DungeonVerdict(false, "the arrival is not a place to stand", 0);
        }

        if (!Near(forward, plan.Breach))
        {
            return new DungeonVerdict(false, "the cave does not reach the breach", forward.Count);
        }

        for (int floor = 0; floor < plan.FloorAnchors.Count; floor++)
        {
            if (!Near(forward, plan.FloorAnchors[floor]))
            {
                return new DungeonVerdict(false, $"floor {floor} cannot be reached", forward.Count);
            }
        }

        if (!Near(forward, plan.Loot))
        {
            return new DungeonVerdict(false, "the loot room cannot be reached", forward.Count);
        }

        HashSet<DungeonCell> back = DungeonWalk.Reachable(volume, plan.Arrival, reverse: true);
        return Near(back, plan.Loot)
            ? new DungeonVerdict(true, "walkable", forward.Count)
            : new DungeonVerdict(false, "there is no way back from the loot room", forward.Count);
    }

    /// <summary>Whether a set of standing cells holds one within two cells of a place, on its level.</summary>
    private static bool Near(HashSet<DungeonCell> cells, DungeonCell place)
    {
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (cells.Contains(new DungeonCell(place.X + dx, place.Y + dy, place.Z + dz)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Bands of dirt and gravel in the stone, so the rock reads as layers laid down.</summary>
    private static void Strata(DungeonVolume volume, DungeonRandom random)
    {
        int bands = random.Range(3, 5);
        for (int band = 0; band < bands; band++)
        {
            int y = random.Range(4, volume.SizeY - 6);
            int thickness = random.Range(1, 3);
            BlockId block = random.Chance(0.5) ? BlockId.Dirt : BlockId.Gravel;
            double phase = random.Unit() * Math.PI * 2;
            for (int x = 0; x < volume.SizeX; x++)
            {
                for (int z = 0; z < volume.SizeZ; z++)
                {
                    int wave = (int)Math.Round(2 * Math.Sin((x * 0.11) + phase) + (1.5 * Math.Sin((z * 0.13) + phase)));
                    for (int t = 0; t < thickness; t++)
                    {
                        volume.Set(x, y + wave + t, z, block);
                    }
                }
            }
        }
    }

    /// <summary>The building: its shell and slabs, the rooms on every floor, and the stairs between them.</summary>
    private static void Stamp(DungeonVolume volume, Building b, DungeonRandom random, List<Vector3> lights, DungeonMix mix)
    {
        BlockId slab = random.Chance(0.5) ? BlockId.Cobblestone : BlockId.Planks;
        volume.Fill(new DungeonCell(b.X0, b.Y0, b.Z0), new DungeonCell(b.X1, b.RoofY, b.Z1), BlockId.Brick);
        for (int floor = 0; floor < b.Floors; floor++)
        {
            int y = b.FloorY(floor);
            volume.Fill(new DungeonCell(b.X0 + 1, y, b.Z0 + 1), new DungeonCell(b.X1 - 1, y, b.Z1 - 1), slab);
            volume.Fill(new DungeonCell(b.X0 + 1, y + 1, b.Z0 + 1), new DungeonCell(b.X1 - 1, y + StoreyHeight - 1, b.Z1 - 1), BlockId.Air);

            // A wall across the floor, with a doorway, makes two rooms of it.
            int wallZ = b.Z0 + Math.Max(StairTopZ + 2, (b.Depth / 2) + random.Range(-1, 1));
            volume.Fill(new DungeonCell(b.X0 + 1, y + 1, wallZ), new DungeonCell(b.X1 - 1, y + StoreyHeight - 1, wallZ), BlockId.Brick);
            int door = random.Range(b.X0 + 8, b.X0 + b.Intact - 3);
            volume.Fill(new DungeonCell(door, y + 1, wallZ), new DungeonCell(door + 1, y + 3, wallZ), BlockId.Air);
            if (mix != DungeonMix.MostlyCave && b.Intact >= 18)
            {
                int wallX = b.X0 + (b.Intact / 2) + random.Range(-1, 1);
                volume.Fill(new DungeonCell(wallX, y + 1, wallZ + 1), new DungeonCell(wallX, y + StoreyHeight - 1, b.Z1 - 1), BlockId.Brick);
                int side = random.Range(wallZ + 2, b.Z1 - 3);
                volume.Fill(new DungeonCell(wallX, y + 1, side), new DungeonCell(wallX, y + 3, side + 1), BlockId.Air);
            }

            lights.Add(new Vector3(b.X0 + (b.Width / 2f), y + 4.5f, b.Z0 + (b.Depth / 4f)));
        }

        // A switchback stair in the corner: each flight climbs one storey along +z, the flights
        // side by side, with the slab above each flight opened for headroom.
        for (int floor = 0; floor + 1 < b.Floors; floor++)
        {
            int y = b.FloorY(floor);
            int column = b.X0 + 1 + ((floor % 2) * StairWidth);
            volume.Stairs(new DungeonCell(column, y + 1, b.Z0 + 2), 0, 1, StoreyHeight, StairWidth, BlockId.Cobblestone);
            volume.Fill(new DungeonCell(column, y + StoreyHeight, b.Z0 + 2), new DungeonCell(column + StairWidth - 1, y + StoreyHeight, b.Z0 + 6), BlockId.Air);
        }
    }

    /// <summary>The chasm's shape: a centre line, a half width, and the phase its walls wander by.</summary>
    private sealed record Chasm(int CentreX, int HalfWidth, double Phase)
    {
        /// <summary>The furthest west the chasm's west wall ever wanders.</summary>
        internal int NearestWest => CentreX - HalfWidth - 5;

        /// <summary>The first open cell of the chasm on its west side, at a height and depth.</summary>
        internal int WestEdge(int y, int z) =>
            CentreX - HalfWidth + (int)Math.Round((2.5 * Math.Sin((z * 0.17) + Phase)) + (1.5 * Math.Sin((y * 0.13) + (Phase * 0.6))));

        internal int EastEdge(int y, int z) =>
            CentreX + HalfWidth + (int)Math.Round((2 * Math.Sin((z * 0.21) + (Phase * 1.3))) + (1.5 * Math.Sin((y * 0.11) + Phase)));
    }

    /// <summary>Opens the chasm from near the bottom of the volume to above the arrival, its whole length.</summary>
    private static void Carve(DungeonVolume volume, Chasm chasm, int top)
    {
        for (int z = 3; z < volume.SizeZ - 3; z++)
        {
            for (int y = 3; y <= top; y++)
            {
                for (int x = chasm.WestEdge(y, z); x <= chasm.EastEdge(y, z); x++)
                {
                    volume.Set(x, y, z, BlockId.Air);
                }
            }
        }
    }

    /// <summary>
    /// The way down: a ledge cut into the chasm's west wall, the chasm open beside it. It runs along
    /// the wall north of the building, dropping a block every other step, turns back at each end one
    /// leg lower, and when it is level with the breach floor it runs on south into the building's
    /// torn north face. Alcoves open off it, more of them where the cave has the upper hand. Returns
    /// where the player arrives (the top of the ledge) and where the ledge enters the building.
    /// </summary>
    private static (DungeonCell Arrival, DungeonCell Breach) Ledges(DungeonVolume volume, Chasm chasm, DungeonRandom random,
        DungeonCell top, int breachY, Building b, List<Vector3> lights, DungeonMix mix)
    {
        int north = volume.SizeZ - 8;
        int south = b.Z1 + 4;
        int floor = top.Y;
        int z = north;
        int direction = -1;
        int step = 0;
        DungeonCell arrival = new(chasm.WestEdge(floor, z) - 2, floor, z);
        double alcoveChance = mix == DungeonMix.MostlyCave ? 0.05 : mix == DungeonMix.Balanced ? 0.025 : 0.01;
        while (step < MaximumCaveSteps)
        {
            step++;
            Ledge(volume, chasm, floor, z);
            if (step % LightEveryCaveSteps == 0)
            {
                lights.Add(new Vector3(chasm.WestEdge(floor, z) - 1.5f, floor + 3f, z + 0.5f));
            }

            if (random.Chance(alcoveChance))
            {
                int wall = chasm.WestEdge(floor, z);
                int reach = random.Range(4, 7);
                volume.CarveEllipsoid(wall - reach, floor + 2.5, z, reach, random.Range(3, 4), random.Range(3, 5));
                volume.Fill(new DungeonCell(wall - (2 * reach), floor - 1, z - 5), new DungeonCell(wall - 1, floor - 1, z + 5), BlockId.Stone);
            }

            if (floor <= breachY && direction < 0 && z <= south)
            {
                break;
            }

            int next = z + direction;
            if (next < south || next > north)
            {
                // The end of a leg: a landing, then back the other way one leg lower.
                direction = -direction;
                continue;
            }

            z = next;
            if (floor > breachY && step % 2 == 0)
            {
                floor--;
            }
        }

        // The last stretch, level with the breach floor, south into the building.
        int x = chasm.WestEdge(floor, z) - 2;
        for (int zz = z; zz >= b.Z1 - 1; zz--)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = 0; dy < CaveClearHeight; dy++)
                {
                    volume.Set(x + dx, floor + dy, zz, BlockId.Air);
                }

                if (volume.At(x + dx, floor - 1, zz) == BlockId.Air)
                {
                    volume.Set(x + dx, floor - 1, zz, BlockId.Stone);
                }
            }
        }

        return (arrival, new DungeonCell(x, floor, b.Z1));
    }

    /// <summary>One step of ledge: three cells cut back into the wall, solid underfoot, headroom above.</summary>
    private static void Ledge(DungeonVolume volume, Chasm chasm, int floor, int z)
    {
        int wall = chasm.WestEdge(floor, z);
        for (int x = wall - 3; x <= wall; x++)
        {
            for (int dy = 0; dy < CaveClearHeight; dy++)
            {
                volume.Set(x, floor + dy, z, BlockId.Air);
            }

            if (x < wall)
            {
                volume.Set(x, floor - 1, z, BlockId.Stone);
            }
        }
    }

    /// <summary>The ruined wall the cave breaks in by: a ragged hole through the building's face.</summary>
    private static void Breach(DungeonVolume volume, DungeonCell at, DungeonRandom random)
    {
        int half = random.Range(2, 3);
        for (int dx = -half; dx <= half; dx++)
        {
            int height = 3 + random.Range(0, 2);
            for (int dy = 0; dy < height; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    volume.Set(at.X + dx, at.Y + dy, at.Z + dz, BlockId.Air);
                }
            }
        }
    }

    /// <summary>Age: holes in the walls and rubble on the floors, more of both where the cave has the upper hand.</summary>
    private static void Decay(DungeonVolume volume, Building b, DungeonRandom random, DungeonMix mix)
    {
        double holes = mix == DungeonMix.MostlyCave ? 0.08 : 0.025;
        for (int x = b.X0; x <= b.X1; x++)
        {
            for (int y = b.Y0 + 1; y < b.RoofY; y++)
            {
                for (int z = b.Z0; z <= b.Z1; z++)
                {
                    if (volume.At(x, y, z) == BlockId.Brick && random.Chance(holes))
                    {
                        volume.Set(x, y, z, BlockId.Air);
                    }
                }
            }
        }

        for (int floor = 0; floor < b.Floors; floor++)
        {
            int piles = random.Range(2, 5);
            for (int pile = 0; pile < piles; pile++)
            {
                int px = random.Range(b.X0 + 8, b.X1 - 2);
                int pz = random.Range(b.Z0 + 2, b.Z1 - 2);
                int y = b.FloorY(floor) + 1;
                if (volume.At(px, y, pz) == BlockId.Air && volume.At(px, y - 1, pz) != BlockId.Air)
                {
                    volume.Set(px, y, pz, BlockId.Gravel);
                }
            }
        }
    }

    /// <summary>The stamped building's box.</summary>
    private sealed record Building(int X0, int Y0, int Z0, int Width, int Depth, int Floors)
    {
        /// <summary>How much of the building, from its west wall, the chasm has left standing.</summary>
        internal int Intact { get; init; } = Width;

        internal int X1 => X0 + Width - 1;

        internal int Z1 => Z0 + Depth - 1;

        internal int RoofY => Y0 + (Floors * StoreyHeight);

        internal int FloorY(int floor) => Y0 + (floor * StoreyHeight);

        /// <summary>A place to stand on a floor: the middle of its near room, clear of the stairs.</summary>
        internal DungeonCell Anchor(int floor) => new(X0 + (Intact / 4) + 1, FloorY(floor) + 1, Z1 - 3);

        /// <summary>The loot lies on the lowest floor, in the back room, away from the stairs.</summary>
        internal DungeonCell LootRoom => new(X0 + (Intact / 2) + 1, FloorY(0) + 1, Z0 + 3);
    }
}
