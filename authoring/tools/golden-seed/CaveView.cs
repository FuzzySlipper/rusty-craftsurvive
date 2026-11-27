using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.WorldGen;

namespace GoldenSeed;

/// <summary>
/// A cave under a mouth, simplified for the CMS (#11125): generated exactly as the walking session asks for it
/// (<see cref="UnderCaves"/>), then reduced to what shows its layout. A plan (looking down) and two side sections of its open
/// space; where a player can walk from the arrival by the generator's own walk check (<see cref="DungeonWalk"/>: a step of one,
/// drops of up to three, two of headroom), and whether they can walk back; where they can get to with climbs as well (up or down
/// a face no taller than a starting character's full stamina bar climbs, <see cref="PlayerStamina.ClimbReachMetres"/>, topping
/// out on the first ledge; overhangs and the rock's holds, which decide a real climb, are not modelled); the open space no
/// passage joins to the arrival's;
/// the main route as the generator laid it, with its profile; and the cave's features (sinks, spring, chambers, water).
/// </summary>
internal static class CaveView
{
    /// <summary>Route segments steeper than this rise per metre across are climbs; between it and the walk limit, steep.</summary>
    private const float ClimbSlope = 1.5f;
    private const float SteepSlope = 0.5f;

    /// <summary>Open pockets smaller than this many cells are noise, not a disconnected space.</summary>
    private const int LeastComponent = 64;

    // A column's state in the plan and the sections: the best of its cells.
    private const byte Rock = 0, Open = 1, Floor = 2, Climbed = 3, OneWay = 4, Walked = 5;

    /// <summary>The tallest face a starting character's full stamina bar climbs, in metres.</summary>
    internal static double ClimbReach => PlayerStamina.ClimbReachMetres(CharacterRules.Derive(CharacterAttributes.Starting, CharacterRules.MinimumLevel).MaximumStamina);

    private static readonly (int X, int Z)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    internal static string Make(string id, ulong seed, CaveSetting setting, CavePlacement placement)
    {
        Stopwatch clock = Stopwatch.StartNew();
        DungeonCandidate cave = DungeonCandidates.Generate(DungeonApproach.Karst, seed, 0, 1d, null, setting, UnderCaves.Settings());
        // As the walking session admits it: the way down from the tendril carved, the route starting with it.
        UndergroundCave prepared = UnderCaves.Prepare(cave, placement);
        double generateMs = clock.Elapsed.TotalMilliseconds;
        DungeonVolume volume = PreparedVolume(cave.Walkable, prepared.Interior);
        int sx = volume.SizeX, sy = volume.SizeY, sz = volume.SizeZ;
        bool[] open = new bool[sx * sy * sz];
        int Index(int x, int y, int z) => (((z * sy) + y) * sx) + x;
        for (int z = 0; z < sz; z++)
        for (int y = 0; y < sy; y++)
        for (int x = 0; x < sx; x++)
            open[Index(x, y, z)] = !BlockRegistry.Get(volume.At(x, y, z)).Collidable;

        DungeonLayout layout = cave.Layout with { MainRoute = prepared.Layout.MainRoute };
        Vector3[] tendril = [.. placement.Path.Select(placement.ToCanonical).Where(p => p.X >= 0f && p.X < sx && p.Z >= 0f && p.Z < sz && p.Y >= 0f && p.Y < sy)];
        Vector3 wayIn = tendril.Length > 0 ? tendril[^1] : layout.Arrival;
        DungeonCell start = Start(volume, wayIn) ?? Start(volume, layout.Arrival) ?? cave.Plan.Arrival;
        HashSet<DungeonCell> forward = DungeonWalk.Reachable(volume, start);
        HashSet<DungeonCell> back = DungeonWalk.Reachable(volume, start, reverse: true);
        int reach = (int)Math.Floor(ClimbReach);
        HashSet<DungeonCell> climbed = Climbing(volume, open, forward, reach, Index);

        // The open space's components (six-connected), and which one the arrival stands in.
        int[] component = new int[open.Length];
        List<int> sizes = [0];
        Queue<int> frontier = new();
        for (int seedCell = 0; seedCell < open.Length; seedCell++)
        {
            if (!open[seedCell] || component[seedCell] != 0) continue;
            int label = sizes.Count, count = 0;
            component[seedCell] = label;
            frontier.Enqueue(seedCell);
            while (frontier.TryDequeue(out int cell))
            {
                count++;
                int x = cell % sx, y = cell / sx % sy, z = cell / (sx * sy);
                foreach ((int dx, int dy, int dz) in (ReadOnlySpan<(int, int, int)>)[(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)])
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= sx || ny >= sy || nz >= sz) continue;
                    int next = Index(nx, ny, nz);
                    if (open[next] && component[next] == 0)
                    {
                        component[next] = label;
                        frontier.Enqueue(next);
                    }
                }
            }

            sizes.Add(count);
        }

        int home = volume.Contains(start.X, start.Y, start.Z) ? component[Index(start.X, start.Y, start.Z)] : 0;
        byte State(int x, int y, int z)
        {
            if (!open[Index(x, y, z)]) return Rock;
            DungeonCell cell = new(x, y, z);
            if (forward.Contains(cell)) return back.Contains(cell) ? Walked : OneWay;
            if (climbed.Contains(cell)) return Climbed;
            return DungeonWalk.Standable(volume, x, y, z) ? Floor : Open;
        }

        // The plan: each column's best state, the height of its highest walkable floor (else its highest floor), and whether
        // its open space is cut off from the arrival's. The sections: the same, looking along z (x by y) and along x (z by y).
        byte[] planState = new byte[sx * sz], planCut = new byte[sx * sz], sideX = new byte[sx * sy], sideZ = new byte[sz * sy];
        short[] planFloor = new short[sx * sz];
        Array.Fill(planFloor, (short)-1);
        int standable = 0, reached = 0, oneWay = 0, climbedOnly = 0, lowestFloor = int.MaxValue, lowestReached = int.MaxValue, lowestClimbed = int.MaxValue;
        for (int z = 0; z < sz; z++)
        for (int x = 0; x < sx; x++)
        {
            int column = (z * sx) + x;
            for (int y = sy - 1; y >= 0; y--)
            {
                byte state = State(x, y, z);
                if (state == Rock) continue;
                if (state >= Floor)
                {
                    standable++;
                    lowestFloor = Math.Min(lowestFloor, y);
                    if (state >= Climbed) lowestClimbed = Math.Min(lowestClimbed, y);
                    if (state == Climbed) climbedOnly++;
                    if (state >= OneWay)
                    {
                        reached++;
                        lowestReached = Math.Min(lowestReached, y);
                        if (state == OneWay) oneWay++;
                    }

                    if (planFloor[column] < 0 || (state > planState[column] && state >= Climbed)) planFloor[column] = (short)y;
                }

                planState[column] = Math.Max(planState[column], state);
                int label = component[Index(x, y, z)];
                if (label != home && sizes[label] >= LeastComponent) planCut[column] = 1;
                sideX[(y * sx) + x] = Math.Max(sideX[(y * sx) + x], state);
                sideZ[(y * sz) + z] = Math.Max(sideZ[(y * sz) + z], state);
            }
        }

        int[] cutOff = [.. sizes.Select((count, label) => (count, label)).Where(c => c.label != 0 && c.label != home && c.count >= LeastComponent).Select(c => c.count).OrderDescending()];

        // The main route's profile: distance along it against height, each segment walk, steep or a climb.
        IReadOnlyList<Vector3> route = layout.MainRoute;
        float along = 0, walk = 0, steep = 0, climb = 0, climbRun = 0, longestClimb = 0, descent = 0, ascent = 0;
        int routeWalkable = 0, routeReachable = 0;
        JsonArray profile = [];
        for (int k = 0; k < route.Count; k++)
        {
            if (k > 0)
            {
                Vector3 a = route[k - 1], b = route[k];
                float across = Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)), rise = b.Y - a.Y, length = Vector3.Distance(a, b);
                along += across;
                if (rise > 0) ascent += rise; else descent -= rise;
                float slope = across > 0.01f ? MathF.Abs(rise) / across : float.PositiveInfinity;
                if (slope > ClimbSlope)
                {
                    climb += length;
                    climbRun += MathF.Abs(rise);
                    longestClimb = MathF.Max(longestClimb, climbRun);
                }
                else
                {
                    climbRun = 0;
                    if (slope > SteepSlope) steep += length; else walk += length;
                }
            }

            DungeonCell? on = DungeonWalk.Settle(volume, new((int)MathF.Floor(route[k].X), (int)MathF.Ceiling(route[k].Y), (int)MathF.Floor(route[k].Z)));
            bool walkable = on is DungeonCell cell && forward.Contains(cell);
            bool reachable = walkable || (on is DungeonCell other && climbed.Contains(other));
            if (walkable) routeWalkable++;
            if (reachable) routeReachable++;
            profile.Add(new JsonArray(Round(along), Round(route[k].Y), walkable ? 2 : reachable ? 1 : 0));
        }

        KarstCave.Features? shape = cave.Karst?.Shape;
        JsonObject features = [];
        if (shape is not null)
        {
            features["sinks"] = Points(shape.Sinks);
            features["spring"] = Point(shape.Spring);
            features["chambers"] = Points(shape.Chambers);
            features["rifts"] = Points(shape.Rifts);
            features["beddingRooms"] = Points(shape.BeddingRooms);
            if (shape.Sponge is Vector3 sponge) features["sponge"] = Point(sponge);
            if (shape.Maze is Vector3 maze) features["maze"] = Point(maze);
            if (shape.Tiankeng is (Vector3 floor, float radius)) features["tiankeng"] = new JsonObject { ["floor"] = Point(floor), ["radius"] = Round(radius) };
            if (shape.Chasm is (Vector2 centre, float chasmRadius)) features["chasm"] = new JsonArray(Round(centre.X), Round(centre.Y), Round(chasmRadius));
            if (shape.Hall is (Vector2 hallCentre, float hallFloor, float rx, float rz)) features["hall"] = new JsonArray(Round(hallCentre.X), Round(hallFloor), Round(hallCentre.Y), Round(rx), Round(rz));
            features["passages"] = new JsonArray([.. shape.Passages.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["metres"] = Round(p.Metres), ["narrowest"] = Round(p.Before), ["widened"] = p.Widened,
            })]);
            features["skyOpen"] = shape.SkyOpen;
        }

        features["water"] = new JsonArray([.. layout.WaterZones.Select(zone => (JsonNode)new JsonObject
        {
            ["kind"] = zone.Kind.ToString(), ["surface"] = Round(zone.Surface), ["cells"] = zone.Cells.Count,
            ["at"] = zone.SurfaceColumns.Count > 0 ? new JsonArray(Round(zone.SurfaceColumns.Average(c => c.X)), Round(zone.Surface), Round(zone.SurfaceColumns.Average(c => c.Y))) : null,
        })]);
        if (layout.RuinMouth is Vector3 ruinMouth) features["ruin"] = Point(ruinMouth);

        JsonObject view = new()
        {
            ["id"] = id,
            ["seed"] = seed.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            ["setting"] = new JsonArray(Round((float)setting.Temperature), Round((float)setting.Moisture), Round(setting.DepthMetres)),
            ["size"] = new JsonArray(sx, sy, sz),
            ["generateMs"] = Math.Round(generateMs),
            ["verdict"] = cave.Verdict.Reason,
            ["arrival"] = Point(layout.Arrival),
            ["start"] = new JsonArray(start.X + 0.5, start.Y, start.Z + 0.5),
            ["exit"] = Point(layout.Exit),
            ["wayIn"] = Point(wayIn),
            ["tendril"] = Points(tendril),
            ["route"] = Points(route),
            ["profile"] = profile,
            ["features"] = features,
            ["plan"] = new JsonObject
            {
                ["state"] = Convert.ToBase64String(planState), ["floor"] = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(planFloor.AsSpan())),
                ["cut"] = Convert.ToBase64String(planCut),
            },
            ["sideX"] = Convert.ToBase64String(sideX),
            ["sideZ"] = Convert.ToBase64String(sideZ),
            ["stats"] = new JsonObject
            {
                ["openCells"] = open.Count(o => o),
                ["floorCells"] = standable,
                ["walkedCells"] = reached,
                ["oneWayCells"] = oneWay,
                ["climbedCells"] = climbedOnly,
                ["climbReach"] = Math.Round(ClimbReach, 2),
                ["unwalkedFloorShare"] = standable == 0 ? 0 : Round(1f - (reached / (float)standable)),
                ["unreachedFloorShare"] = standable == 0 ? 0 : Round(1f - ((reached + climbedOnly) / (float)standable)),
                ["lowestFloor"] = lowestFloor == int.MaxValue ? null : lowestFloor,
                ["lowestWalked"] = lowestReached == int.MaxValue ? null : lowestReached,
                ["lowestReached"] = lowestClimbed == int.MaxValue ? null : lowestClimbed,
                ["cutOffSpaces"] = cutOff.Length,
                ["cutOffCells"] = cutOff.Sum(),
                ["routeMetres"] = Round(walk + steep + climb),
                ["routeWalk"] = Round(walk),
                ["routeSteep"] = Round(steep),
                ["routeClimb"] = Round(climb),
                ["longestClimb"] = Round(longestClimb),
                ["longestClimbOverReach"] = longestClimb > ClimbReach,
                ["routeDescent"] = Round(descent),
                ["routeAscent"] = Round(ascent),
                ["routeWalkableShare"] = route.Count == 0 ? 0 : Round(routeWalkable / (float)route.Count),
                ["routeReachableShare"] = route.Count == 0 ? 0 : Round(routeReachable / (float)route.Count),
            },
        };
        return view.ToJsonString();
    }

    /// <summary>
    /// The walk check's volume for the prepared cave: the walked copy, with what preparing it changed - the way down carved open
    /// and the rock laid under its floor - read back from the interior's materials. Water and the rest stay as walked.
    /// </summary>
    private static DungeonVolume PreparedVolume(DungeonVolume walked, CaveInterior interior)
    {
        DungeonVolume volume = new(walked.ChunksX, walked.ChunksY, walked.ChunksZ, BlockId.Air);
        for (int k = 0; k < interior.SizeZ; k++)
        for (int j = 0; j < interior.SizeY; j++)
        for (int i = 0; i < interior.SizeX; i++)
        {
            BlockId block = walked.At(i, j, k);
            bool solid = BlockRegistry.Get(block).Collidable, empty = interior.Materials[interior.Index(i, j, k)] == CraftSurvive.Game.Modules.Terrain.TerrainConstants.EmptyMaterial;
            volume.Set(i, j, k, empty && solid ? BlockId.Air : !empty && !solid && block == BlockId.Air ? BlockId.CaveLimestone : block);
        }

        return volume;
    }

    /// <summary>The longest way down the first sink a start is looked for, and how far across from its line, in cells.</summary>
    private const int SinkSearchAcross = 2;

    /// <summary>
    /// Where the walk check starts: the first floor below the way in. The overworld's tendril carves the way in, so in the cave's
    /// own box the arrival can lie in rock; the player comes down the first sink to the floor under it.
    /// </summary>
    private static DungeonCell? Start(DungeonVolume volume, Vector3 arrival)
    {
        int ax = (int)MathF.Floor(arrival.X), az = (int)MathF.Floor(arrival.Z), top = Math.Min(volume.SizeY - 1, (int)MathF.Floor(arrival.Y));
        DungeonCell? best = null;
        for (int dz = -SinkSearchAcross; dz <= SinkSearchAcross; dz++)
        for (int dx = -SinkSearchAcross; dx <= SinkSearchAcross; dx++)
        {
            for (int y = top; y > 0; y--)
            {
                if (DungeonWalk.Standable(volume, ax + dx, y, az + dz))
                {
                    // The nearest column first, then the highest floor.
                    if (best is not DungeonCell found || Math.Abs(dx) + Math.Abs(dz) < Math.Abs(found.X - ax) + Math.Abs(found.Z - az)
                        || (Math.Abs(dx) + Math.Abs(dz) == Math.Abs(found.X - ax) + Math.Abs(found.Z - az) && y > found.Y))
                        best = new DungeonCell(ax + dx, y, az + dz);
                    break;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Every floor reachable from the walked set with climbs as well: up a face beside a floor to the first ledge on top of it,
    /// or down a face from a floor's edge to the first floor below, each no taller than <paramref name="reach"/>, then walking on.
    /// </summary>
    private static HashSet<DungeonCell> Climbing(DungeonVolume volume, bool[] open, HashSet<DungeonCell> walked, int reach, Func<int, int, int, int> index)
    {
        bool Air(int x, int y, int z) => volume.Contains(x, y, z) && open[index(x, y, z)];
        HashSet<DungeonCell> reached = [.. walked];
        Queue<DungeonCell> frontier = new(walked);
        void Arrive(DungeonCell cell)
        {
            if (reached.Contains(cell)) return;
            foreach (DungeonCell walkedOn in DungeonWalk.Reachable(volume, cell))
            {
                if (reached.Add(walkedOn)) frontier.Enqueue(walkedOn);
            }
        }

        while (frontier.TryDequeue(out DungeonCell cell))
        {
            foreach ((int dx, int dz) in Sides)
            {
                int nx = cell.X + dx, nz = cell.Z + dz;
                // Up: a face beside the floor; the climber rises in their own column and tops out on the first ledge.
                if (!Air(nx, cell.Y, nz))
                {
                    for (int rise = DungeonWalk.StepUp + 1; rise <= reach; rise++)
                    {
                        if (!Air(cell.X, cell.Y + rise + DungeonWalk.Headroom - 1, cell.Z)) break;
                        if (DungeonWalk.Standable(volume, nx, cell.Y + rise, nz))
                        {
                            Arrive(new DungeonCell(nx, cell.Y + rise, nz));
                            break;
                        }
                    }
                }

                // Down: over the floor's edge, down the face to the first floor below a walkable drop.
                if (Air(nx, cell.Y, nz) && Air(nx, cell.Y + 1, nz))
                {
                    for (int drop = 1; drop <= reach; drop++)
                    {
                        if (!Air(nx, cell.Y - drop, nz))
                        {
                            if (drop > DungeonWalk.MaximumDrop + 1 && DungeonWalk.Standable(volume, nx, cell.Y - drop + 1, nz)) Arrive(new DungeonCell(nx, cell.Y - drop + 1, nz));
                            break;
                        }
                    }
                }
            }
        }

        return reached;
    }

    private static double Round(float value) => Math.Round(value, 1);

    private static JsonArray Point(Vector3 p) => new(Round(p.X), Round(p.Y), Round(p.Z));

    private static JsonArray Points(IEnumerable<Vector3> points) => new([.. points.Select(p => (JsonNode)Point(p))]);
}
