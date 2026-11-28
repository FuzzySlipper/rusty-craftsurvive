using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A planned cave system in the walking session (#11219, the first runtime milestone of the underground plan; design: wiki
/// Combat and delving, "Caves form a planned underground"). When the player comes into a planned system's region or the next
/// one, the system is built from the plan in the background (its region's skeleton on the continent's smooth ground, then the
/// system on the walked ground) and its boxes are held by the terrain: their mouths' tendrils carve the overworld, and a
/// landmark cave in their way gives way. From then on its boxes are asked for, admitted, dressed and released as the caves
/// under landmark mouths are, nearest first, so a seam or a shaft is crossed into a box already there.
/// </summary>
internal sealed partial class DungeonModule
{
    /// <summary>
    /// The systems this milestone takes from the plan (#11219): elsewhere the landmark table still places the caves (#11220
    /// moves every natural cave to the plan). The golden seed's five-level system with a mouth on each level.
    /// </summary>
    internal static readonly IReadOnlyList<string> PlannedSystemIds = ["ug:fine:-2:-8:3"];

    /// <summary>The plan level the walking session builds from: the standard (#11209: Patch took fine and its 12-box budget).</summary>
    private const string PlannedLevel = "fine";

    /// <summary>A planned system is built once the player is this many region tiles from its own (its region or the next).</summary>
    private const int PlannedTileReach = 1;

    private sealed record PlannedJob(UndergroundSystem? System, IReadOnlyList<CavePlacement> Boxes, double SkeletonMs, double SystemMs, int RegionTilesBuilt);

    private sealed record PlannedHeld(string Id, UndergroundSystem? System, IReadOnlyList<CavePlacement> Boxes, PlannedJob Job, double QueuedMs);

    private readonly Dictionary<string, PlannedHeld> plannedHeld = [];
    private readonly HashSet<string> plannedAsked = [];
    private UndergroundPlanner? planner;

    /// <summary>The plan, built on the walked ground the boxes are buried under (the recipe's continuous height).</summary>
    private UndergroundPlanner Planner => planner ??= new UndergroundPlanner(terrain.Recipe.Map, UndergroundPlan.Preset(PlannedLevel),
        (x, z) => terrain.Recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z)));

    private static string PlanKey(string id) => "plan-" + id;

    /// <summary>Asks for the planned systems near the player, and holds those the background has built.</summary>
    private void FollowPlannedSystems()
    {
        if (!terrain.Recipe.Map.Scale.Continental)
        {
            return;
        }

        Vector3 feet = player.WorldFeetPosition;
        (int tx, int tz) = MapRegions.TileAt(feet.X, feet.Z);
        foreach (string id in PlannedSystemIds)
        {
            if (plannedAsked.Contains(id) || !TileOf(id, out int sx, out int sz) || Math.Abs(sx - tx) > PlannedTileReach || Math.Abs(sz - tz) > PlannedTileReach)
            {
                continue;
            }

            UndergroundPlanner plan = Planner;
            MapRegions? regions = terrain.Recipe.Regions;
            Generator.Request(PlanKey(id), () =>
            {
                long tilesBefore = regions?.Built ?? 0;
                long began = Stopwatch.GetTimestamp();
                UndergroundSite? site = plan.Site(id);
                double skeletonMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                long building = Stopwatch.GetTimestamp();
                UndergroundSystem? system = site is null ? null : plan.SystemAt(site);
                double systemMs = Stopwatch.GetElapsedTime(building).TotalMilliseconds;
                IReadOnlyList<CavePlacement> boxes = system is null ? [] : PlannedCaves.Placements(system);
                return new PlannedJob(system, boxes, skeletonMs, systemMs, (int)((regions?.Built ?? 0) - tilesBefore));
            });
            plannedAsked.Add(id);
        }

        foreach (string id in plannedAsked)
        {
            if (plannedHeld.ContainsKey(id))
            {
                continue;
            }

            PlannedJob? job;
            try
            {
                job = Generator.Finished<PlannedJob>(PlanKey(id));
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                plannedHeld[id] = new PlannedHeld(id, null, [], new PlannedJob(null, [], 0, 0, 0), 0);
                lastCave = $"the planned system {id} could not be built: {failure.Message}";
                continue;
            }

            if (job is null)
            {
                continue;
            }

            (_, double queuedMs) = Generator.Timing(PlanKey(id)) ?? (0d, 0d);
            if (job.System is null || job.Boxes.Count == 0)
            {
                plannedHeld[id] = new PlannedHeld(id, null, [], job, queuedMs);
                lastCave = $"the plan has no system {id} on this world";
                continue;
            }

            // The landmark caves in the system's way give way: one already asked for is let go.
            foreach (CavePlacement gone in terrain.HoldPlanned(job.Boxes))
            {
                if (underCaves.TryGetValue(gone.Key, out UnderCave? landmark))
                {
                    Undress(landmark);
                    if (landmark.Ready is not null)
                    {
                        terrain.ReleaseCave(landmark.Cave);
                        cavesReleased++;
                    }

                    underCaves.Remove(gone.Key);
                }
            }

            plannedHeld[id] = new PlannedHeld(id, job.System, job.Boxes, job, queuedMs);
            lastCave = string.Create(CultureInfo.InvariantCulture,
                $"holding the planned system {id}: {job.Boxes.Count} boxes, {job.System.Mouths.Count} mouths (skeleton {job.SkeletonMs:F0} ms, system {job.SystemMs:F0} ms, {job.RegionTilesBuilt} region tiles built, queued {queuedMs:F0} ms)");
        }
    }

    /// <summary>The held planned boxes within reach of the player that have not been asked for are asked for, nearest first.</summary>
    private void AskForPlannedBoxes()
    {
        Vector3 feet = player.WorldFeetPosition;
        foreach (CavePlacement box in plannedHeld.Values.SelectMany(h => h.Boxes)
            .Where(b => !underCaves.ContainsKey(b.Key) && Distance(b, feet) <= PrefetchMetres)
            .OrderBy(b => DistanceIn(b, feet)))
        {
            Ask(box);
        }
    }

    /// <summary>What a planned box is generated with: the plan's seed, setting and ports (the same the authoring tools use).</summary>
    private static (ulong Seed, CaveSetting Setting, KarstCave.Settings Settings) PlannedGeneration(CavePlacement box) =>
        PlannedCaveSettings.For(box.Planned!.System, box.Planned.Box.Index, UndergroundSettings());

    private static bool TileOf(string id, out int tileX, out int tileZ)
    {
        string[] parts = id.Split(':');
        tileX = tileZ = 0;
        return parts.Length == 5 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out tileX)
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out tileZ);
    }

    /// <summary>The planned systems: those asked for, held, their boxes asked for, ready and admitted, and what building them cost.</summary>
    internal string PlannedReadout()
    {
        if (plannedAsked.Count == 0)
        {
            return "planned: none near";
        }

        System.Text.StringBuilder text = new("planned:");
        foreach (string id in plannedAsked)
        {
            if (!plannedHeld.TryGetValue(id, out PlannedHeld? held))
            {
                text.Append(CultureInfo.InvariantCulture, $" {id} building");
                continue;
            }

            int asked = held.Boxes.Count(b => underCaves.ContainsKey(b.Key));
            int ready = held.Boxes.Count(b => underCaves.TryGetValue(b.Key, out UnderCave? c) && c.Ready is not null);
            int admitted = held.Boxes.Count(terrain.Caves.IsAdmitted);
            int dressed = held.Boxes.Count(b => dressings.ContainsKey(b.Key));
            text.Append(CultureInfo.InvariantCulture,
                $" {id} boxes={held.Boxes.Count} asked={asked} ready={ready} admitted={admitted} dressed={dressed} skeletonMs={held.Job.SkeletonMs:F0} systemMs={held.Job.SystemMs:F0} regionTiles={held.Job.RegionTilesBuilt} queuedMs={held.QueuedMs:F0}");
            foreach (CavePlacement b in held.Boxes)
            {
                if (underCaves.TryGetValue(b.Key, out UnderCave? c) && Generator.Timing(c.Key) is (double g, double q))
                {
                    text.Append(CultureInfo.InvariantCulture, $" b{b.Planned!.Box.Index}:{g:F0}+{q:F0}ms");
                }
            }
        }

        (int skeletons, int uncut, int built) = planner?.Counts ?? (0, 0, 0);
        return text.Append(CultureInfo.InvariantCulture, $" planner skeletons={skeletons} uncut={uncut} built={built}").ToString();
    }

    // The walk through a planned system (#11219): its way is laid by PlanWay, not by EnsureWay's tendril-and-main-route.
    private bool plannedWay;

    /// <summary>
    /// Assisted, for the walk (#11219): "status" reports the planned systems; "goto M" stands the player outside mouth M of the
    /// first planned system (teleporting to its region first when it is not held yet, so it is built); "way A B" lays the way
    /// from mouth A to mouth B through the system's boxes (each box's own walkable routes between its ports, across the seams
    /// and down the shafts between), which craft.cave.steer then follows on held input.
    /// </summary>
    internal string Plan(string command)
    {
        // One argument (live-debug splits on spaces): status, goto:M, way:A:B.
        string[] words = command.Split([' ', ':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string id = PlannedSystemIds[0];
        if (words.Length == 0 || words[0] == "status")
        {
            return $"{PlannedReadout()}; {CavesReadout()}";
        }

        if (words[0] == "goto" && words.Length == 2 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mouthIndex))
        {
            if (!plannedHeld.TryGetValue(id, out PlannedHeld? held) || held.System is null)
            {
                if (Planner.Site(id) is not UndergroundSite site)
                {
                    return $"the plan has no system {id}";
                }

                float ground = (float)terrain.GroundAt(site.X, site.Z);
                player.Teleport((float)site.X, ground + PlayerStandAbove, (float)site.Z);
                return string.Create(CultureInfo.InvariantCulture, $"moved to {id}'s anchor at {site.X:F0},{site.Z:F0} so it is built; goto again once held. {PlannedReadout()}");
            }

            if (mouthIndex < 0 || mouthIndex >= held.System.Mouths.Count)
            {
                return $"{id} has mouths 0..{held.System.Mouths.Count - 1}";
            }

            UndergroundMouth mouth = held.System.Mouths[mouthIndex];
            if (mouth.Face == UndergroundFace.PosY)
            {
                return $"M{mouthIndex} is a sinkhole: the walk enters by a side mouth";
            }

            (int nx, int nz) = PlannedCaves.Outward(mouth.Face);
            Vector3 outside = new((float)mouth.WorldX + (nx * OutsideMouthMetres), 0f, (float)mouth.WorldZ + (nz * OutsideMouthMetres));
            float standY = (float)terrain.GroundAt(outside.X, outside.Z);
            if (player.Teleport(outside.X, standY + PlayerStandAbove, outside.Z) is null)
            {
                return "refused: no standing room outside the mouth";
            }

            Face(new Vector3(-nx, 0f, -nz), 0d);
            walkCave = held.Boxes[mouth.BoxIndex].Key;
            walkWay = null;
            plannedWay = false;
            return string.Create(CultureInfo.InvariantCulture,
                $"outside {id} M{mouthIndex} ({mouth.Kind}, level {mouth.Level}, box {mouth.BoxIndex}, tendril {mouth.TendrilMetres:F0} m) at {outside.X:F1},{standY:F1},{outside.Z:F1}; {PlannedReadout()}");
        }

        if (words[0] == "routes" && words.Length == 2 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int routeBox)
            && plannedHeld.TryGetValue(id, out PlannedHeld? routesHeld) && routeBox >= 0 && routeBox < routesHeld.Boxes.Count)
        {
            // A box's required routes in world metres: each one's ends and its path every few points (for traversal proof).
            CavePlacement box = routesHeld.Boxes[routeBox];
            if (!underCaves.TryGetValue(box.Key, out UnderCave? routeCave) || routeCave.Ready is not UndergroundCave routeReady)
            {
                return $"box {routeBox} is not ready";
            }

            System.Text.StringBuilder text = new();
            foreach ((string name, (Vector3 routeFrom, Vector3 routeTo, IReadOnlyList<Vector3> path)) in routeReady.Layout.PortRoutes)
            {
                Vector3 f = box.ToWorld(routeFrom), t = box.ToWorld(routeTo);
                text.Append(CultureInfo.InvariantCulture, $"{name}: {f.X:F0},{f.Y:F0},{f.Z:F0} -> {t.X:F0},{t.Y:F0},{t.Z:F0} ({Length(path):F0} m):");
                for (int i = 0; i < path.Count; i += Math.Max(1, path.Count / 12))
                {
                    Vector3 w = box.ToWorld(path[i]);
                    text.Append(CultureInfo.InvariantCulture, $" {w.X:F0},{w.Y:F0},{w.Z:F0}");
                }

                text.Append('\n');
            }

            return text.ToString();
        }

        if (words[0] == "way" && words.Length == 3 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)
            && int.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int to))
        {
            return PlanWay(id, from, to);
        }

        return "craft.cave.plan status | goto:<mouth> | way:<entry mouth>:<exit mouth>";
    }

    /// <summary>Lays the way from one side mouth of a held system to another, once every box on the way is ready.</summary>
    private string PlanWay(string id, int entry, int exit)
    {
        if (!plannedHeld.TryGetValue(id, out PlannedHeld? held) || held.System is not UndergroundSystem system)
        {
            return $"{id} is not held yet: craft.cave.plan goto first";
        }

        if (entry < 0 || exit < 0 || entry >= system.Mouths.Count || exit >= system.Mouths.Count
            || system.Mouths[entry].Face == UndergroundFace.PosY || system.Mouths[exit].Face == UndergroundFace.PosY)
        {
            return "the way runs between two side mouths";
        }

        // The boxes from the entry's to the exit's, fewest first, and the link crossed into each.
        UndergroundMouth inMouth = system.Mouths[entry], outMouth = system.Mouths[exit];
        Dictionary<int, (int From, int Link)> back = new() { [inMouth.BoxIndex] = (-1, -1) };
        Queue<int> frontier = new([inMouth.BoxIndex]);
        while (frontier.TryDequeue(out int at))
        {
            for (int i = 0; i < system.Links.Count; i++)
            {
                UndergroundLink l = system.Links[i];
                int other = l.FromBox == at ? l.ToBox : l.ToBox == at ? l.FromBox : -1;
                if (other >= 0 && back.TryAdd(other, (at, i))) frontier.Enqueue(other);
            }
        }

        if (!back.ContainsKey(outMouth.BoxIndex))
        {
            return "the exit's box is not joined to the entry's";
        }

        List<(int Box, string Arrive, string Leave)> legs = [];
        string leave = PlannedCaveSettings.MouthLabel(exit);
        for (int box = outMouth.BoxIndex; box >= 0;)
        {
            (int previous, int link) = back[box];
            string arrive = previous < 0 ? PlannedCaveSettings.MouthLabel(entry) : PlannedCaveSettings.LinkLabel(link, box, previous);
            legs.Insert(0, (box, arrive, leave));
            if (previous >= 0) leave = PlannedCaveSettings.LinkLabel(link, previous, box);
            box = previous;
        }

        List<Vector3> way = [];
        CavePlacement entryBox = held.Boxes[inMouth.BoxIndex];
        way.AddRange(entryBox.Tendrils.First(t => Vector3.Distance(t.Path[0], new Vector3((float)inMouth.WorldX, (float)inMouth.WorldY + 1.4f, (float)inMouth.WorldZ)) < 12f).Path);
        foreach ((int box, string arrive, string leaveBy) in legs)
        {
            CavePlacement placement = held.Boxes[box];
            if (!underCaves.TryGetValue(placement.Key, out UnderCave? cave) || cave.Ready is not UndergroundCave ready)
            {
                return $"box {box} on the way is not ready yet; {PlannedReadout()}";
            }

            if (BoxWay(ready.Layout.PortRoutes, "port:" + arrive, "port:" + leaveBy) is not List<Vector3> inside)
            {
                return $"box {box} has no walkable way from {arrive} to {leaveBy}";
            }

            way.AddRange(inside.Select(placement.ToWorld));
        }

        CavePlacement exitBox = held.Boxes[outMouth.BoxIndex];
        CaveTendril outTendril = exitBox.Tendrils.First(t => Vector3.Distance(t.Path[0], new Vector3((float)outMouth.WorldX, (float)outMouth.WorldY + 1.4f, (float)outMouth.WorldZ)) < 12f);
        CaveTendril inTendril = entryBox.Tendrils.First(t => Vector3.Distance(t.Path[0], new Vector3((float)inMouth.WorldX, (float)inMouth.WorldY + 1.4f, (float)inMouth.WorldZ)) < 12f);
        // The generator's routes are walkable for a body that ignores gravity: the way a walker takes is found on the voxels,
        // walking, stepping, dropping and climbing (#11219).
        string gravity = "";
        (List<Vector3>? walked, string summary) = GravityWay(held, inTendril.Path[^1], outTendril.Path[^1]);
        if (walked is not null)
        {
            way = [.. inTendril.Path, .. walked];
            gravity = summary;
        }
        else
        {
            gravity = $"no way a walker can take: {summary}; the generator's route is laid instead";
        }

        way.AddRange(outTendril.Path.Reverse());
        (int nx, int nz) = PlannedCaves.Outward(outMouth.Face);
        Vector3 last = outTendril.Path[0];
        way.Add(last + new Vector3(nx * OutsideMouthMetres * 2f, 0f, nz * OutsideMouthMetres * 2f));
        walkWay = way;
        walkCave = entryBox.Key;
        plannedWay = true;
        return string.Create(CultureInfo.InvariantCulture,
            $"way {Length(way):F0} m from M{entry} (level {inMouth.Level}) to M{exit} (level {outMouth.Level}); the generator's routes go through boxes {string.Join(">", legs.Select(l => l.Box))}: {string.Join(" ", legs.Select(l => $"[{l.Arrive}..{l.Leave}]"))}; walker's way: {gravity}");
    }

    /// <summary>
    /// A box's way between two of its ports' anchors along the routes its generator made walkable (each from its start to its
    /// end): the shortest over the routes joined end to end, in the box's metres.
    /// </summary>
    private static List<Vector3>? BoxWay(IReadOnlyDictionary<string, (Vector3 From, Vector3 To, IReadOnlyList<Vector3> Path)> routes, string arrive, string leave)
    {
        if (!routes.TryGetValue(arrive, out var a) || !routes.TryGetValue(leave, out var b))
        {
            return null;
        }

        static (int, int, int) Node(Vector3 p) => ((int)MathF.Round(p.X * 2f), (int)MathF.Round(p.Y * 2f), (int)MathF.Round(p.Z * 2f));
        Dictionary<(int, int, int), List<(IReadOnlyList<Vector3> Path, (int, int, int) To, float Length)>> edges = [];
        foreach ((Vector3 from, Vector3 to, IReadOnlyList<Vector3> path) in routes.Values)
        {
            if (path.Count == 0) continue;
            float length = Length(path);
            (int, int, int) f = Node(from), t = Node(to);
            if (!edges.TryGetValue(f, out var fromEdges)) edges[f] = fromEdges = [];
            if (!edges.TryGetValue(t, out var toEdges)) edges[t] = toEdges = [];
            fromEdges.Add((path, t, length));
            toEdges.Add(([.. path.Reverse()], f, length));
        }

        (int, int, int) start = Node(a.To), goal = Node(b.To);
        Dictionary<(int, int, int), (float Cost, (int, int, int)? From, IReadOnlyList<Vector3>? Path)> best = new() { [start] = (0f, null, null) };
        PriorityQueue<(int, int, int), float> open = new();
        open.Enqueue(start, 0f);
        while (open.TryDequeue(out (int, int, int) node, out float cost))
        {
            if (node == goal) break;
            if (cost > best[node].Cost) continue;
            foreach ((IReadOnlyList<Vector3> path, (int, int, int) next, float length) in edges.GetValueOrDefault(node) ?? [])
            {
                float through = cost + length;
                if (!best.TryGetValue(next, out var known) || through < known.Cost)
                {
                    best[next] = (through, node, path);
                    open.Enqueue(next, through);
                }
            }
        }

        if (!best.ContainsKey(goal))
        {
            return null;
        }

        List<IReadOnlyList<Vector3>> pieces = [];
        for ((int, int, int)? at = goal; at is (int, int, int) node && best[node].From is (int, int, int) previous; at = previous)
        {
            pieces.Insert(0, best[node].Path!);
        }

        List<Vector3> joined = [a.To];
        foreach (IReadOnlyList<Vector3> piece in pieces) joined.AddRange(piece);
        joined.Add(b.To);
        return joined;
    }

    /// <summary>
    /// The way a walker takes between two points inside a held system (#11219, #10908's <see cref="CaveWalker"/>): on the voxels
    /// the terrain holds (the admitted boxes' interiors and the tendrils' air, rock elsewhere), walking, stepping, dropping one
    /// way and climbing pitches onto ledges, with no edits. Its points stand a walker's height over the floor.
    /// </summary>
    private (List<Vector3>? Way, string Summary) GravityWay(PlannedHeld held, Vector3 from, Vector3 to)
    {
        (CavePlacement Box, CaveInterior Interior)[] boxes = [.. held.Boxes
            .Select(b => underCaves.TryGetValue(b.Key, out UnderCave? c) && c.Ready is UndergroundCave r ? (b, r.Interior) : (b, null!))
            .Where(p => p.Interior is not null)];
        if (boxes.Length == 0)
        {
            return (null, "no box is ready");
        }

        // The grid: every held box and both ends, a few cells round.
        const int Margin = 4;
        long x0 = Math.Min((long)Math.Floor(Math.Min(from.X, to.X)), boxes.Min(b => b.Box.BoxMinimum.X)) - Margin;
        long y0 = Math.Min((long)Math.Floor(Math.Min(from.Y, to.Y)), boxes.Min(b => b.Box.BoxMinimum.Y)) - Margin;
        long z0 = Math.Min((long)Math.Floor(Math.Min(from.Z, to.Z)), boxes.Min(b => b.Box.BoxMinimum.Z)) - Margin;
        long x1 = Math.Max((long)Math.Ceiling(Math.Max(from.X, to.X)), boxes.Max(b => b.Box.BoxMaximum.X)) + Margin;
        long y1 = Math.Max((long)Math.Ceiling(Math.Max(from.Y, to.Y)), boxes.Max(b => b.Box.BoxMaximum.Y)) + Margin;
        long z1 = Math.Max((long)Math.Ceiling(Math.Max(from.Z, to.Z)), boxes.Max(b => b.Box.BoxMaximum.Z)) + Margin;
        int sx = (int)(x1 - x0 + 1), sy = (int)(y1 - y0 + 1), sz = (int)(z1 - z0 + 1);
        bool[] solid = new bool[sx * sy * sz];
        Parallel.For(0, sz, k =>
        {
            for (int j = 0; j < sy; j++)
            for (int i = 0; i < sx; i++)
            {
                long x = x0 + i, y = y0 + j, z = z0 + k;
                bool rock = true;
                foreach ((CavePlacement box, CaveInterior interior) in boxes)
                {
                    if (box.InBox(x, y, z, out int bi, out int bj, out int bk))
                    {
                        rock = interior.Materials[interior.Index(bi, bj, bk)] != CraftSurvive.Game.Modules.Terrain.TerrainConstants.EmptyMaterial;
                        break;
                    }
                }

                if (rock)
                {
                    Vector3 centre = new(x + 0.5f, y + 0.5f, z + 0.5f);
                    rock = !held.Boxes.Any(b => b.TendrilOpen(centre) > 0f);
                }

                solid[(((k * sy) + j) * sx) + i] = rock;
            }
        });

        CaveWalker walker = new(sx, sy, sz, solid);
        (int, int, int) Cell(Vector3 p) => ((int)(MathF.Floor(p.X) - x0), (int)(MathF.Floor(p.Y) - y0), (int)(MathF.Floor(p.Z) - z0));
        (int fx, int fy, int fz) = Cell(from);
        (int tx, int ty, int tz) = Cell(to);
        if (walker.Settle(fx, fy, fz, across: 3, down: 6) is not (int, int, int) start || walker.Settle(tx, ty, tz, across: 3, down: 6) is not (int, int, int) goal)
        {
            return (null, "no floor to stand on near an end");
        }

        if (walker.Find(start, goal, edits: false) is not CaveWalker.Way way)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"no way from {start} to {goal} (reached {walker.Reach(start).Count} standing cells)"));
        }

        List<Vector3> points = [.. way.Cells.Select(c => new Vector3(c.X + x0 + 0.5f, c.Y + y0 + WalkAboveFloor, c.Z + z0 + 0.5f))];
        return (points, string.Create(CultureInfo.InvariantCulture,
            $"{Length(points):F0} m, {way.Drops} drops, {way.Climbs} climbs (pitches of at most {CaveWalker.ClimbPitch} m), {way.Expanded} cells searched"));
    }
}
