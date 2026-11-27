using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.WorldGen;

namespace GoldenSeed;

/// <summary>
/// A cave under a mouth, simplified for the CMS (#11125): generated and prepared exactly as the walking session admits it
/// (<see cref="UnderCaves"/>), then reduced to what shows its layout. Passage is judged as the karst generator judges it - by
/// whether a body fits, not by walking: the cave's own field (<see cref="KarstCave.FieldStep"/> samples, open where it is not
/// negative) gives each open sample its clearance, the distance to the nearest rock, and the space a body fits through from
/// the arrival is filled: at <see cref="KarstCave.StandRadius"/> a body stands, down to <see cref="KarstCave.CrawlRadius"/> it
/// crawls. Open space beyond a pinch narrower than a crawl, and open space no opening joins at all, are what a poor layout
/// leaves. Climbing is judged along the main route the generator laid: each climb against a starting character's full stamina
/// bar (<see cref="PlayerStamina.ClimbReachMetres"/>). The plan looks down the box; the sections look along its two axes.
/// </summary>
internal static class CaveView
{
    /// <summary>Route legs steeper than this rise per metre across are climbs; between it and the walk limit, steep.</summary>
    private const float ClimbSlope = 1.5f;
    private const float SteepSlope = 0.5f;

    /// <summary>Open spaces smaller than this (cubic metres) are noise, not a cut-off space.</summary>
    private const float LeastSpaceCubicMetres = 8f;

    /// <summary>How far from the arrival the fill may start, for an arrival on a floor a little inside the rock.</summary>
    private const float SeedSearchMetres = 3f;

    // A sample's state, and a column's in the projections (the greatest of its samples): rock, open beyond a pinch, open no
    // opening joins, crawled to, stood in.
    private const byte Rock = 0, Narrow = 1, CutOff = 2, Crawl = 3, Stand = 4;

    private static readonly (int X, int Y, int Z)[] Faces = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>The tallest face a starting character's full stamina bar climbs, in metres.</summary>
    internal static double ClimbReach => PlayerStamina.ClimbReachMetres(CharacterRules.Derive(CharacterAttributes.Starting, CharacterRules.MinimumLevel).MaximumStamina);

    internal static string Make(string id, ulong seed, CaveSetting setting, CavePlacement placement)
    {
        Stopwatch clock = Stopwatch.StartNew();
        DungeonCandidate cave = DungeonCandidates.Generate(DungeonApproach.Karst, seed, 0, 1d, null, setting, UnderCaves.Settings());
        // As the walking session admits it: the way down from the tendril carved, the route starting with it.
        UndergroundCave prepared = UnderCaves.Prepare(cave, placement);
        double generateMs = clock.Elapsed.TotalMilliseconds;
        DungeonLayout layout = cave.Layout;
        Func<Vector3, float> field = layout.Shape ?? throw new InvalidOperationException("A karst cave carries its field.");

        float step = KarstCave.FieldStep;
        int sx = (int)(cave.Walkable.SizeX / step), sy = (int)(cave.Walkable.SizeY / step), sz = (int)(cave.Walkable.SizeZ / step);
        int Index(int x, int y, int z) => (((z * sy) + y) * sx) + x;
        Vector3 Centre(int x, int y, int z) => new((x + 0.5f) * step, (y + 0.5f) * step, (z + 0.5f) * step);
        bool[] open = new bool[sx * sy * sz];
        Parallel.For(0, sz, z =>
        {
            for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
                open[Index(x, y, z)] = field(Centre(x, y, z)) >= 0f;
        });
        float[] clearance = Clearance(open, sx, sy, sz, step);

        // The open space's components, and the one the arrival is in.
        int[] component = Components(open, sx, sy, sz, out List<int> sizes);
        int seedSample = Seed(layout.Arrival, open, clearance, sx, sy, sz, step);
        int home = seedSample >= 0 ? component[seedSample] : 0;

        // The space a body fits through from the arrival.
        bool[] reached = new bool[open.Length];
        if (seedSample >= 0)
        {
            Queue<int> frontier = new();
            reached[seedSample] = true;
            frontier.Enqueue(seedSample);
            while (frontier.TryDequeue(out int cell))
            {
                int x = cell % sx, y = cell / sx % sy, z = cell / (sx * sy);
                foreach ((int dx, int dy, int dz) in Faces)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= sx || ny >= sy || nz >= sz) continue;
                    int next = Index(nx, ny, nz);
                    if (!reached[next] && clearance[next] >= KarstCave.CrawlRadius)
                    {
                        reached[next] = true;
                        frontier.Enqueue(next);
                    }
                }
            }
        }

        float sampleVolume = step * step * step;
        int leastSamples = (int)MathF.Ceiling(LeastSpaceCubicMetres / sampleVolume);
        // Beyond a pinch: open space joined to the arrival's only through pinches narrower than a crawl, counted where it is a
        // space of its own (a few cubic metres or more), not the thin rind a fill leaves along every wall.
        bool[] beyond = new bool[open.Length];
        for (int i = 0; i < open.Length; i++) beyond[i] = open[i] && !reached[i] && component[i] == home;
        int[] pocket = Components(beyond, sx, sy, sz, out List<int> pocketSizes);
        byte State(int i) => !open[i] ? Rock
            : reached[i] ? (clearance[i] >= KarstCave.StandRadius ? Stand : Crawl)
            : component[i] == home ? (pocketSizes[pocket[i]] >= leastSamples ? Narrow : Crawl)
            : sizes[component[i]] >= leastSamples ? CutOff : Rock;

        byte[] planState = new byte[sx * sz], sideX = new byte[sx * sy], sideZ = new byte[sz * sy];
        short[] planTop = new short[sx * sz];
        Array.Fill(planTop, (short)-1);
        long openCount = 0, standCount = 0, crawlCount = 0, narrowCount = 0, cutCount = 0;
        int lowestOpen = int.MaxValue, lowestReached = int.MaxValue;
        for (int z = 0; z < sz; z++)
        for (int y = 0; y < sy; y++)
        for (int x = 0; x < sx; x++)
        {
            byte state = State(Index(x, y, z));
            if (state == Rock) continue;
            openCount++;
            switch (state)
            {
                case Stand: standCount++; break;
                case Crawl: crawlCount++; break;
                case Narrow: narrowCount++; break;
                default: cutCount++; break;
            }

            if (state != CutOff) lowestOpen = Math.Min(lowestOpen, y);
            if (state >= Crawl) lowestReached = Math.Min(lowestReached, y);
            int column = (z * sx) + x;
            planState[column] = Math.Max(planState[column], state);
            if (state >= Crawl) planTop[column] = (short)Math.Max(planTop[column], y);
            sideX[(y * sx) + x] = Math.Max(sideX[(y * sx) + x], state);
            sideZ[(y * sz) + z] = Math.Max(sideZ[(y * sz) + z], state);
        }

        int[] cutOff = [.. sizes.Select((count, label) => (count, label)).Where(c => c.label != 0 && c.label != home && c.count >= leastSamples).Select(c => c.count)];

        // The main route's profile: distance along it against height, each leg a walk, steep or a climb, each point stood in,
        // crawled to or out of reach.
        IReadOnlyList<Vector3> route = prepared.Layout.MainRoute;
        float along = 0, walk = 0, steep = 0, climb = 0, climbRun = 0, longestClimb = 0, descent = 0, ascent = 0;
        int routeStand = 0, routeReached = 0, climbsOverReach = 0;
        double reach = ClimbReach;
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
                }
                else
                {
                    if (climbRun > reach) climbsOverReach++;
                    longestClimb = MathF.Max(longestClimb, climbRun);
                    climbRun = 0;
                    if (slope > SteepSlope) steep += length; else walk += length;
                }
            }

            int at = Nearest(route[k], reached, clearance, sx, sy, sz, step);
            int code = at < 0 ? 0 : clearance[at] >= KarstCave.StandRadius ? 2 : 1;
            if (code > 0) routeReached++;
            if (code == 2) routeStand++;
            profile.Add(new JsonArray(Round(along), Round(route[k].Y), code));
        }

        if (climbRun > reach) climbsOverReach++;
        longestClimb = MathF.Max(longestClimb, climbRun);

        JsonObject features = [];
        if (cave.Karst?.Shape is KarstCave.Features shape)
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

        // The generator's own dead ends: the damp pockets fungi grow in (#10305).
        features["deadEnds"] = Points(layout.FungalPockets);
        features["water"] = new JsonArray([.. layout.WaterZones.Select(zone => (JsonNode)new JsonObject
        {
            ["kind"] = zone.Kind.ToString(), ["surface"] = Round(zone.Surface), ["cells"] = zone.Cells.Count,
            ["at"] = zone.SurfaceColumns.Count > 0 ? new JsonArray(Round(zone.SurfaceColumns.Average(c => c.X)), Round(zone.Surface), Round(zone.SurfaceColumns.Average(c => c.Y))) : null,
        })]);
        if (layout.RuinMouth is Vector3 ruinMouth) features["ruin"] = Point(ruinMouth);

        Vector3[] tendril = [.. placement.Path.Select(placement.ToCanonical)
            .Where(p => p.X >= 0f && p.X < cave.Walkable.SizeX && p.Z >= 0f && p.Z < cave.Walkable.SizeZ && p.Y >= 0f && p.Y < cave.Walkable.SizeY)];
        double Cubic(long samples) => Math.Round(samples * sampleVolume);
        JsonObject view = new()
        {
            ["id"] = id,
            ["seed"] = seed.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            ["setting"] = new JsonArray(Round((float)setting.Temperature), Round((float)setting.Moisture), Round(setting.DepthMetres)),
            ["size"] = new JsonArray(cave.Walkable.SizeX, cave.Walkable.SizeY, cave.Walkable.SizeZ),
            ["grid"] = new JsonArray(sx, sy, sz),
            ["step"] = step,
            ["generateMs"] = Math.Round(generateMs),
            ["viewMs"] = Math.Round(clock.Elapsed.TotalMilliseconds - generateMs),
            ["verdict"] = cave.Verdict.Reason,
            ["arrival"] = Point(layout.Arrival),
            ["start"] = seedSample >= 0 ? Point(Centre(seedSample % sx, seedSample / sx % sy, seedSample / (sx * sy))) : null,
            ["tendril"] = Points(tendril),
            ["route"] = Points(route),
            ["profile"] = profile,
            ["features"] = features,
            ["plan"] = new JsonObject
            {
                ["state"] = Convert.ToBase64String(planState),
                ["top"] = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(planTop.AsSpan())),
            },
            ["sideX"] = Convert.ToBase64String(sideX),
            ["sideZ"] = Convert.ToBase64String(sideZ),
            ["stats"] = new JsonObject
            {
                ["openCubicMetres"] = Cubic(openCount),
                ["standCubicMetres"] = Cubic(standCount),
                ["crawlCubicMetres"] = Cubic(crawlCount),
                ["narrowCubicMetres"] = Cubic(narrowCount),
                ["cutOffSpaces"] = cutOff.Length,
                ["cutOffCubicMetres"] = Cubic(cutCount),
                ["unreachedShare"] = openCount == 0 ? 0 : Round((narrowCount + cutCount) / (float)openCount),
                ["lowestOpen"] = lowestOpen == int.MaxValue ? null : Round(lowestOpen * step),
                ["lowestReached"] = lowestReached == int.MaxValue ? null : Round(lowestReached * step),
                ["standRadius"] = KarstCave.StandRadius,
                ["crawlRadius"] = KarstCave.CrawlRadius,
                ["climbReach"] = Math.Round(reach, 2),
                ["routeMetres"] = Round(walk + steep + climb),
                ["routeWalk"] = Round(walk),
                ["routeSteep"] = Round(steep),
                ["routeClimb"] = Round(climb),
                ["longestClimb"] = Round(longestClimb),
                ["climbsOverReach"] = climbsOverReach,
                ["routeDescent"] = Round(descent),
                ["routeAscent"] = Round(ascent),
                ["routeReachedShare"] = route.Count == 0 ? 0 : Round(routeReached / (float)route.Count),
                ["routeStandShare"] = route.Count == 0 ? 0 : Round(routeStand / (float)route.Count),
                ["deadEnds"] = layout.FungalPockets.Count,
            },
        };
        return view.ToJsonString();
    }

    /// <summary>
    /// Each open sample's clearance, in metres: the distance to the nearest rock sample (the box's outside counts as rock), an
    /// exact Euclidean distance transform taken one axis at a time.
    /// </summary>
    private static float[] Clearance(bool[] open, int sx, int sy, int sz, float step)
    {
        const float Far = 1e12f;
        float[] d = new float[open.Length];
        for (int i = 0; i < d.Length; i++) d[i] = open[i] ? Far : 0f;
        int[] sizes = [sx, sy, sz];
        int[] strides = [1, sx, sx * sy];
        for (int axis = 0; axis < 3; axis++)
        {
            int n = sizes[axis], stride = strides[axis];
            int[] others = [.. Enumerable.Range(0, 3).Where(a => a != axis)];
            int count = sizes[others[0]] * sizes[others[1]];
            Parallel.For(0, count, line =>
            {
                int origin = ((line % sizes[others[0]]) * strides[others[0]]) + ((line / sizes[others[0]]) * strides[others[1]]);
                // The line padded with rock beyond each end, so the box's walls bound the clearance.
                float[] f = new float[n + 2], result = new float[n + 2];
                for (int k = 0; k < n; k++) f[k + 1] = d[origin + (k * stride)];
                Distance1D(f, result);
                for (int k = 0; k < n; k++) d[origin + (k * stride)] = result[k + 1];
            });
        }

        float[] metres = new float[d.Length];
        for (int i = 0; i < d.Length; i++) metres[i] = open[i] ? MathF.Sqrt(d[i]) * step : 0f;
        return metres;
    }

    /// <summary>The squared distance transform of a sampled function along one line (Felzenszwalb and Huttenlocher's lower envelope).</summary>
    private static void Distance1D(float[] f, float[] result)
    {
        int n = f.Length;
        int[] v = new int[n];
        float[] z = new float[n + 1];
        int k = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s = ((f[q] + (q * q)) - (f[v[k]] + (v[k] * v[k]))) / (2f * (q - v[k]));
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + (q * q)) - (f[v[k]] + (v[k] * v[k]))) / (2f * (q - v[k]));
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            result[q] = ((q - v[k]) * (q - v[k])) + f[v[k]];
        }
    }

    /// <summary>The open samples' six-connected components (label 0 is rock), with each one's size.</summary>
    private static int[] Components(bool[] open, int sx, int sy, int sz, out List<int> sizes)
    {
        int[] component = new int[open.Length];
        sizes = [0];
        Queue<int> frontier = new();
        for (int first = 0; first < open.Length; first++)
        {
            if (!open[first] || component[first] != 0) continue;
            int label = sizes.Count, count = 0;
            component[first] = label;
            frontier.Enqueue(first);
            while (frontier.TryDequeue(out int cell))
            {
                count++;
                int x = cell % sx, y = cell / sx % sy, z = cell / (sx * sy);
                foreach ((int dx, int dy, int dz) in Faces)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= sx || ny >= sy || nz >= sz) continue;
                    int next = (((nz * sy) + ny) * sx) + nx;
                    if (open[next] && component[next] == 0)
                    {
                        component[next] = label;
                        frontier.Enqueue(next);
                    }
                }
            }

            sizes.Add(count);
        }

        return component;
    }

    /// <summary>The sample the fill starts from: the one a body fits in nearest the arrival, within a few metres.</summary>
    private static int Seed(Vector3 arrival, bool[] open, float[] clearance, int sx, int sy, int sz, float step)
    {
        int reach = (int)MathF.Ceiling(SeedSearchMetres / step);
        int ax = (int)(arrival.X / step), ay = (int)(arrival.Y / step), az = (int)(arrival.Z / step);
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int z = Math.Max(0, az - reach); z <= Math.Min(sz - 1, az + reach); z++)
        for (int y = Math.Max(0, ay - reach); y <= Math.Min(sy - 1, ay + reach); y++)
        for (int x = Math.Max(0, ax - reach); x <= Math.Min(sx - 1, ax + reach); x++)
        {
            int i = (((z * sy) + y) * sx) + x;
            if (!open[i] || clearance[i] < KarstCave.CrawlRadius) continue;
            float distance = ((x - ax) * (x - ax)) + ((y - ay) * (y - ay)) + ((z - az) * (z - az));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The roomiest reached sample within a metre of a route point, or -1: the route runs near the floor, where a sample's own
    /// clearance is the floor's distance, so the place is judged by the largest body that fits about it.
    /// </summary>
    private static int Nearest(Vector3 p, bool[] reached, float[] clearance, int sx, int sy, int sz, float step)
    {
        int reach = (int)MathF.Ceiling(1f / step);
        int px = (int)(p.X / step), py = (int)(p.Y / step), pz = (int)(p.Z / step);
        int best = -1;
        for (int z = Math.Max(0, pz - reach); z <= Math.Min(sz - 1, pz + reach); z++)
        for (int y = Math.Max(0, py - reach); y <= Math.Min(sy - 1, py + reach); y++)
        for (int x = Math.Max(0, px - reach); x <= Math.Min(sx - 1, px + reach); x++)
        {
            int i = (((z * sy) + y) * sx) + x;
            if (reached[i] && (best < 0 || clearance[i] > clearance[best])) best = i;
        }

        return best;
    }

    private static double Round(float value) => Math.Round(value, 1);

    private static JsonArray Point(Vector3 p) => new(Round(p.X), Round(p.Y), Round(p.Z));

    private static JsonArray Points(IEnumerable<Vector3> points) => new([.. points.Select(p => (JsonNode)Point(p))]);
}
