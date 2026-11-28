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
/// whether a body fits, not by walking: the cave's own field (sampled at half <see cref="KarstCave.FieldStep"/>, open where it is
/// not negative) gives each open sample its clearance, the distance to the rock's surface, and the space a body fits through from
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

    /// <summary>The 3D view's cells, in metres: each the best state of the samples in it.</summary>
    private const float CellMetres = 1f;

    /// <summary>Open spaces smaller than this (cubic metres) are noise, not a cut-off space.</summary>
    private const float LeastSpaceCubicMetres = 8f;

    /// <summary>How far from the arrival the fill may start, for an arrival on a floor a little inside the rock.</summary>
    private const float SeedSearchMetres = 3f;

    // A sample's state, and a column's in the projections (the greatest of its samples): rock, open beyond a pinch, open no
    // opening joins, reached only through throats within the sampling's resolution of a crawl, crawled to, stood in.
    private const byte Rock = 0, Narrow = 1, CutOff = 2, Tight = 3, Crawl = 4, Stand = 5;

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

        // The game's way in comes down the ramp preparing carves from the tendril, which joins the generator's route past its
        // first sink; the fill starts where it joins (the arrival, at the sink's foot, can lie in a pocket a pinch shuts off).
        Vector3 start = JoinOf(prepared.Layout.MainRoute, layout.MainRoute) ?? layout.Arrival;
        Reach fill = Reach.Of(field, cave.Walkable.SizeX, cave.Walkable.SizeY, cave.Walkable.SizeZ, start);
        float step = fill.Step;
        int sx = fill.SizeX, sy = fill.SizeY, sz = fill.SizeZ;
        bool[] open = fill.Open, reached = fill.Reached, within = fill.Within;
        float[] clearance = fill.Clearance;
        int seedSample = fill.Seed;
        int Index(int x, int y, int z) => (((z * sy) + y) * sx) + x;
        Vector3 Centre(int x, int y, int z) => new((x + 0.5f) * step, (y + 0.5f) * step, (z + 0.5f) * step);

        // The open space's components, and the one the arrival is in.
        int[] component = Components(open, sx, sy, sz, out List<int> sizes);
        int home = seedSample >= 0 ? component[seedSample] : 0;

        float sampleVolume = step * step * step;
        int leastSamples = (int)MathF.Ceiling(LeastSpaceCubicMetres / sampleVolume);
        // Beyond a pinch: open space joined to the arrival's only through pinches narrower than a crawl, counted where it is a
        // space of its own (a few cubic metres or more), not the thin rind a fill leaves along every wall.
        bool[] beyond = new bool[open.Length];
        for (int i = 0; i < open.Length; i++) beyond[i] = open[i] && !within[i] && component[i] == home && clearance[i] >= KarstCave.CrawlRadius;
        int[] pocket = Components(beyond, sx, sy, sz, out List<int> pocketSizes);
        // Room a body would fit in (a crawl's clearance or more): reached, reached only through tight throats, or beyond a pinch;
        // the thin rind along every wall, where no body's centre fits, counts with the rock.
        byte State(int i) => !open[i] ? Rock
            : reached[i] ? (clearance[i] >= KarstCave.StandRadius ? Stand : Crawl)
            : component[i] != home ? (sizes[component[i]] >= leastSamples ? CutOff : Rock)
            : clearance[i] < KarstCave.CrawlRadius ? Rock
            : within[i] ? Tight
            : pocketSizes[pocket[i]] >= leastSamples ? Narrow : Rock;

        byte[] planState = new byte[sx * sz], sideX = new byte[sx * sy], sideZ = new byte[sz * sy];
        short[] planTop = new short[sx * sz];
        Array.Fill(planTop, (short)-1);
        long openCount = 0, standCount = 0, crawlCount = 0, tightCount = 0, narrowCount = 0, cutCount = 0;
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
                case Tight: tightCount++; break;
                case Narrow: narrowCount++; break;
                default: cutCount++; break;
            }

            if (state != CutOff) lowestOpen = Math.Min(lowestOpen, y);
            if (state >= Tight) lowestReached = Math.Min(lowestReached, y);
            int column = (z * sx) + x;
            planState[column] = Math.Max(planState[column], state);
            if (state >= Tight) planTop[column] = (short)Math.Max(planTop[column], y);
            sideX[(y * sx) + x] = Math.Max(sideX[(y * sx) + x], state);
            sideZ[(y * sz) + z] = Math.Max(sideZ[(y * sz) + z], state);
        }

        // The 3D view's grid (#11149): each metre cell the best state of its samples, as the plan and sections show columns.
        int perCell = (int)MathF.Round(CellMetres / step);
        int cx = sx / perCell, cy = sy / perCell, cz = sz / perCell;
        byte[] cells = new byte[cx * cy * cz];
        Parallel.For(0, cz, k =>
        {
            for (int j = 0; j < cy; j++)
            for (int i = 0; i < cx; i++)
            {
                byte best = Rock;
                for (int dz = 0; dz < perCell; dz++)
                for (int dy = 0; dy < perCell; dy++)
                for (int dx = 0; dx < perCell; dx++)
                    best = Math.Max(best, State(Index((i * perCell) + dx, (j * perCell) + dy, (k * perCell) + dz)));
                cells[(((k * cy) + j) * cx) + i] = best;
            }
        });

        int[] cutOff = [.. sizes.Select((count, label) => (count, label)).Where(c => c.label != 0 && c.label != home && c.count >= leastSamples).Select(c => c.count)];

        // The main route's profile: distance along it against height, each leg a walk, steep or a climb, each point stood in,
        // crawled to or out of reach.
        IReadOnlyList<Vector3> route = prepared.Layout.MainRoute;
        float along = 0, walk = 0, steep = 0, climb = 0, climbRun = 0, longestClimb = 0, descent = 0, ascent = 0;
        int routeStand = 0, routeReached = 0, routeTight = 0, climbsOverReach = 0;
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

            // Each point: 3 stood in, 2 crawled to, 1 reached only through tight throats, 0 beyond a pinch.
            int at = Nearest(route[k], reached, clearance, sx, sy, sz, step);
            int code = at >= 0 ? (clearance[at] >= KarstCave.StandRadius ? 3 : 2) : Nearest(route[k], within, clearance, sx, sy, sz, step) >= 0 ? 1 : 0;
            if (code >= 2) routeReached++;
            if (code == 3) routeStand++;
            if (code == 1) routeTight++;
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
            ["cells"] = new JsonObject { ["size"] = new JsonArray(cx, cy, cz), ["metres"] = CellMetres, ["state"] = Convert.ToBase64String(cells) },
            ["stats"] = new JsonObject
            {
                ["openCubicMetres"] = Cubic(openCount),
                ["standCubicMetres"] = Cubic(standCount),
                ["crawlCubicMetres"] = Cubic(crawlCount),
                ["tightCubicMetres"] = Cubic(tightCount),
                ["narrowCubicMetres"] = Cubic(narrowCount),
                ["resolutionMetres"] = Math.Round(Reach.Resolution, 2),
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
                ["routeTightShare"] = route.Count == 0 ? 0 : Round(routeTight / (float)route.Count),
                ["routeStandShare"] = route.Count == 0 ? 0 : Round(routeStand / (float)route.Count),
                ["deadEnds"] = layout.FungalPockets.Count,
            },
        };
        return view.ToJsonString();
    }

    /// <summary>
    /// Where a body fits through from an arrival (#11125 R11125-2): the field sampled at half the generator's step, each open
    /// sample's clearance to the rock's surface, and the open space filled from the arrival through samples at least
    /// <see cref="KarstCave.CrawlRadius"/> clear. Clearance runs to the surface where the field crosses zero, finer than the
    /// samples, so a throat narrower than a crawl stops the fill. A body's centre is judged at sample centres, which can lie up
    /// to half a sample's diagonal (about 0.18 m) off a throat's axis: a throat that close to a crawl reads as a pinch.
    /// </summary>
    internal sealed record Reach(int SizeX, int SizeY, int SizeZ, float Step, bool[] Open, float[] Clearance, bool[] Reached, bool[] Within, int Seed)
    {
        /// <summary>The sampling step: half the generator's field step.</summary>
        internal static readonly float SampleStep = KarstCave.FieldStep / 2f;

        /// <summary>
        /// How far off a throat's axis the nearest sample centre can lie (half a sample's diagonal across the throat): a body's
        /// centre is judged at sample centres, so a throat's clearance reads up to this much short.
        /// </summary>
        internal static readonly float Resolution = SampleStep * MathF.Sqrt(2f) / 2f;

        /// <summary>The crawl radius less the resolution: a throat that passes this but not the crawl is within the sampling's error of one.</summary>
        internal static readonly float LenientCrawl = KarstCave.CrawlRadius - Resolution;

        internal static Reach Of(Func<Vector3, float> field, float metresX, float metresY, float metresZ, Vector3 arrival)
        {
            float step = SampleStep;
            int sx = (int)MathF.Round(metresX / step), sy = (int)MathF.Round(metresY / step), sz = (int)MathF.Round(metresZ / step);
            float[] values = new float[sx * sy * sz];
            Parallel.For(0, sz, z =>
            {
                for (int y = 0; y < sy; y++)
                for (int x = 0; x < sx; x++)
                    values[(((z * sy) + y) * sx) + x] = field(new Vector3((x + 0.5f) * step, (y + 0.5f) * step, (z + 0.5f) * step));
            });
            bool[] open = [.. values.Select(v => v >= 0f)];
            float[] clearance = CaveView.Clearance(values, open, sx, sy, sz, step);
            int seed = CaveView.Seed(arrival, open, clearance, sx, sy, sz, step);
            bool[] reached = Fill(seed, clearance, KarstCave.CrawlRadius, sx, sy, sz);
            bool[] within = Fill(seed, clearance, LenientCrawl, sx, sy, sz);
            return new Reach(sx, sy, sz, step, open, clearance, reached, within, seed);
        }

        /// <summary>Whether the sample at a point was reached at a crawl, and whether only through tight throats.</summary>
        internal bool ReachedAt(Vector3 p) => Reached[At(p)];

        internal bool WithinAt(Vector3 p) => Within[At(p)];

        private int At(Vector3 p) => ((((int)(p.Z / Step) * SizeY) + (int)(p.Y / Step)) * SizeX) + (int)(p.X / Step);

        /// <summary>The open space filled from a seed through samples at least <paramref name="least"/> clear.</summary>
        private static bool[] Fill(int seed, float[] clearance, float least, int sx, int sy, int sz)
        {
            bool[] reached = new bool[clearance.Length];
            if (seed < 0) return reached;
            Queue<int> frontier = new();
            reached[seed] = true;
            frontier.Enqueue(seed);
            while (frontier.TryDequeue(out int cell))
            {
                int x = cell % sx, y = cell / sx % sy, z = cell / (sx * sy);
                foreach ((int dx, int dy, int dz) in Faces)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= sx || ny >= sy || nz >= sz) continue;
                    int next = (((nz * sy) + ny) * sx) + nx;
                    if (!reached[next] && clearance[next] >= least)
                    {
                        reached[next] = true;
                        frontier.Enqueue(next);
                    }
                }
            }

            return reached;
        }
    }

    // The synthetic caves CheckThroats measures: two chambers of this radius, this far apart, in a box this size (metres), with
    // throats of each radius placed so their axis lies between sample centres (the sampling's worst case): too narrow for a
    // crawl even allowing for the resolution, within the resolution of a crawl, and wide enough for one.
    private const float ChamberRadius = 3f;
    private const float ChambersApart = 10f;
    private static readonly Vector3 CheckBox = new(20f, 10f, 10f);
    private static readonly Vector3 NearChamber = new(5f, 5f, 5f);
    private static readonly float[] TooNarrow = [0.2f, 0.3f, 0.38f];
    private static readonly float[] WithinResolution = [0.55f];
    private static readonly float[] WideEnough = [0.7f, 1f];

    private enum Throat { Stops, Tight, Passes }

    /// <summary>
    /// Two chambers joined by a throat of each radius: through one too narrow for a crawl the far chamber is beyond a pinch; through
    /// one within the sampling's resolution of a crawl it is reached only as tight; through one wider it is reached. The near
    /// chamber is always reached. Reports each case and returns whether all held.
    /// </summary>
    internal static bool CheckThroats(Action<string> report)
    {
        Vector3 far = NearChamber + new Vector3(ChambersApart, 0f, 0f);
        bool all = true;
        foreach ((float throat, Throat expected) in TooNarrow.Select(r => (r, Throat.Stops)).Concat(WithinResolution.Select(r => (r, Throat.Tight)))
            .Concat(WideEnough.Select(r => (r, Throat.Passes))))
        {
            float Field(Vector3 p)
            {
                float rooms = MathF.Max(ChamberRadius - Vector3.Distance(p, NearChamber), ChamberRadius - Vector3.Distance(p, far));
                float axis = MathF.Sqrt(((p.Y - NearChamber.Y) * (p.Y - NearChamber.Y)) + ((p.Z - NearChamber.Z) * (p.Z - NearChamber.Z)));
                float tube = p.X >= NearChamber.X && p.X <= far.X ? throat - axis : float.NegativeInfinity;
                return MathF.Max(rooms, tube);
            }

            Reach reach = Reach.Of(Field, CheckBox.X, CheckBox.Y, CheckBox.Z, NearChamber);
            Throat found = reach.ReachedAt(far) ? Throat.Passes : reach.WithinAt(far) ? Throat.Tight : Throat.Stops;
            bool held = reach.ReachedAt(NearChamber) && found == expected;
            all &= held;
            string said = found switch { Throat.Passes => "lets a body into", Throat.Tight => "is within the resolution of a crawl into", _ => "stops a body short of" };
            report(FormattableString.Invariant($"{(held ? "ok" : "FAILED")}: a {throat * 2:F1} m throat (radius {throat:F2} m; crawl {KarstCave.CrawlRadius:F2} m, resolution {Reach.Resolution:F2} m) {said} the far chamber; {reach.Reached.Count(r => r)} of {reach.Open.Count(o => o)} open samples reached"));
        }

        return all;
    }

    /// <summary>
    /// Each open sample's clearance, in metres: its distance to the rock's surface. An exact Euclidean distance transform finds
    /// each open sample's nearest rock sample (taken one axis at a time; the box's outside counts as rock), and the surface is
    /// placed where the field crosses zero on that rock sample's edge to its open neighbour, by linear interpolation of the
    /// field, so a throat's width is resolved finer than the sampling.
    /// </summary>
    internal static float[] Clearance(float[] values, bool[] open, int sx, int sy, int sz, float step)
    {
        const int Wall = -2;
        // Farther than any sample can be from rock (the box's squared diagonal, in samples), and small enough that the squared
        // offsets the envelope adds stay exact in single precision: a far larger value swallows them and misplaces the envelope.
        float far = ((float)sx * sx) + ((float)sy * sy) + ((float)sz * sz) + 1f;
        float[] d = new float[open.Length];
        int[] site = new int[open.Length];
        for (int i = 0; i < d.Length; i++)
        {
            d[i] = open[i] ? far : 0f;
            site[i] = open[i] ? -1 : i;
        }

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
                int[] sites = new int[n + 2], from = new int[n + 2];
                sites[0] = sites[n + 1] = Wall;
                for (int k = 0; k < n; k++)
                {
                    f[k + 1] = d[origin + (k * stride)];
                    sites[k + 1] = site[origin + (k * stride)];
                }

                Distance1D(f, result, from);
                for (int k = 0; k < n; k++)
                {
                    d[origin + (k * stride)] = result[k + 1];
                    site[origin + (k * stride)] = sites[from[k + 1]];
                }
            });
        }

        // How far below the surface each rock sample's centre lies: where the field crosses zero towards its nearest-crossing open
        // neighbour; half a step for the box's wall and for a rock sample with no open neighbour.
        int[] index = [1, -1, sx, -sx, sx * sy, -(sx * sy)];
        float Depth(int rock)
        {
            if (rock == Wall) return step / 2f;
            int x = rock % sx, y = rock / sx % sy, z = rock / (sx * sy);
            float depth = step / 2f;
            bool any = false;
            for (int face = 0; face < 6; face++)
            {
                int nx = x + Faces[face].X, ny = y + Faces[face].Y, nz = z + Faces[face].Z;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= sx || ny >= sy || nz >= sz) continue;
                int neighbour = rock + index[face];
                if (!open[neighbour]) continue;
                float crossing = step * (-values[rock] / (values[neighbour] - values[rock]));
                depth = any ? MathF.Min(depth, crossing) : crossing;
                any = true;
            }

            return depth;
        }

        float[] metres = new float[d.Length];
        Parallel.For(0, d.Length, i =>
        {
            if (open[i]) metres[i] = MathF.Max(0f, (MathF.Sqrt(d[i]) * step) - Depth(site[i]));
        });
        return metres;
    }

    /// <summary>
    /// The squared distance transform of a sampled function along one line (Felzenszwalb and Huttenlocher's lower envelope),
    /// with, for each place, the place whose parabola it lies under.
    /// </summary>
    private static void Distance1D(float[] f, float[] result, int[] from)
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
            from[q] = v[k];
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

    /// <summary>The first point of the prepared route that the generator's own route also has: where the way down joins it.</summary>
    internal static Vector3? JoinOf(IReadOnlyList<Vector3> prepared, IReadOnlyList<Vector3> generated)
    {
        HashSet<Vector3> original = [.. generated];
        foreach (Vector3 point in prepared)
        {
            if (original.Contains(point)) return point;
        }

        return null;
    }

    /// <summary>The sample the fill starts from: the roomiest a body fits in within a few metres of the start.</summary>
    internal static int Seed(Vector3 arrival, bool[] open, float[] clearance, int sx, int sy, int sz, float step)
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
            // The roomiest place near the start, not the nearest: a start on a floor has its pocket of rind about it.
            float room = -clearance[i];
            if (room < bestDistance)
            {
                bestDistance = room;
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
