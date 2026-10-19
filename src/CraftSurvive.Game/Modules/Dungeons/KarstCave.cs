using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The cave generator study (#10094): a cave made by water rather than carved shapes. Rock is laid down in dipping beds
/// whose partings dissolve readily, crossed by vertical joints. Water enters at sinks near the top and leaves at a spring
/// at the water table; each round it takes the cheapest way through the rock, and the ways it took get cheaper, so the
/// flow gathers into a branching conduit network. The conduits become the cave: round, sediment-floored tubes below the
/// water table, canyons cut down by falling water and shafts where it drops above it, and breakdown chambers with flat
/// bedding-plane ceilings where large flows meet. The water's flow, drip and standing water are kept for later passes.
/// Passage is checked by whether a body fits, not by walking: a clearance field (the distance to the nearest rock) is
/// searched for the widest way along each route, and pinches narrower than a body are widened.
/// </summary>
internal static class KarstCave
{
    internal const int ChunksX = 6, ChunksY = 4, ChunksZ = 6;

    // The structural grid water runs through, and the finer field the cave is shaped in.
    private const float GridCell = 2f;
    private const int GridX = ChunksX * 16 / 2, GridY = ChunksY * 16 / 2, GridZ = ChunksZ * 16 / 2;
    internal const float FieldStep = 0.5f;
    internal const int FieldX = (int)(ChunksX * 16 / FieldStep), FieldY = (int)(ChunksY * 16 / FieldStep), FieldZ = (int)(ChunksZ * 16 / FieldStep);
    private const float FieldLimit = 2f;

    // The rock: bed spacing, how much more readily partings and joints dissolve, and the beds' dip.
    private const float BedSpacingLeast = 3.5f, BedSpacingMost = 7f, PartingSolubility = 3f, JointSolubility = 2.2f;
    private const int Joints = 7;
    private const float JointLongest = 40f;
    private static readonly Vector2 BedDip = new(0.05f, 0.025f);
    private const float SolubilityRoll = 0.3f;

    // The water: the water table falls in stages as the valley cuts down, each stage leaving a level of the cave behind;
    // below where it stands now the cave is still flooded.
    private static readonly float[] WaterTables = [44f, 30f, 17f];
    private const float DrainedTable = 10f;
    private const int Sinks = 6;
    private const int RoundsPerStage = 10;
    /// <summary>Moving through plain rock rather than along a parting (sideways) or a joint (up or down) costs this much more.</summary>
    private const float AcrossBeds = 2.2f, AcrossJoints = 7f;
    /// <summary>How much cheaper a conduit gets per unit of water through it, and how much each round's costs are jostled.</summary>
    private const float ConduitGain = 0.6f, RoundJitter = 0.25f;
    /// <summary>Above the water table water will hardly climb and falls readily; below it, under pressure, it loops.</summary>
    private const float VadoseClimb = 30f, VadoseFall = 0.35f, PhreaticClimb = 1.3f;

    // The shapes: conduit radii, canyon depth, shaft widening, tube flattening, chamber size and sediment.
    private const float LeastFlowCarved = 2.5f, NarrowestRadius = 0.55f, WidestRadius = 3.4f;
    private const float CanyonStretch = 2.2f, ShaftWiden = 1.4f, TubeWiden = 1.35f, TubeFlatten = 0.8f, PartingWiden = 2.1f, PartingFlatten = 0.55f;
    private const float ChamberWiden = 2.3f, ChamberHeight = 1.5f, ChamberFlow = 0.25f;
    private const float SedimentFill = 0.45f, ShapeBlend = 0.8f;

    // The rock's own unevenness: swells and a grain, scalloped harder where water fell freely.
    private const float SwellAmplitude = 0.5f, SwellFrequency = 0.13f, GrainAmplitude = 0.22f, GrainFrequency = 0.5f, VadoseGrain = 1.6f;

    // Passage: a crawling body's radius, a standing one's, and how far past a body a pinch is widened.
    internal const float CrawlRadius = 0.45f, StandRadius = 0.85f;
    private const float WidenMargin = 0.2f;

    /// <summary>The rock kept between the cave and the space's sides.</summary>
    private const float RindMetres = 1.5f;

    private const int MostLights = DungeonLayout.MaximumLights;
    private const float LightAboveFloor = 1.2f;

    /// <summary>Water moves level (with diagonals across the floor) or straight up and down: down joints, along partings, not on slants.</summary>
    private static readonly (int X, int Y, int Z)[] AroundWater = [(1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (1, 0, 1), (1, 0, -1), (-1, 0, 1), (-1, 0, -1), (0, 1, 0), (0, -1, 0)];
    private static readonly (int X, int Y, int Z)[] Around6 = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>A passage route's widest way: its narrowest clearance before and after widening, and how much was widened.</summary>
    internal readonly record struct Passage(string Name, float Before, float After, int Widened, float Metres);

    /// <summary>A generated cave: its field (0.5 m), the water's fields (2 m grid), its places and its passages.</summary>
    internal sealed class Result
    {
        internal required float[] Open { get; init; }
        internal required float[] Clearance { get; init; }
        internal required float[] Flow { get; init; }
        internal required float[] Drip { get; init; }
        internal required float[] Standing { get; init; }
        internal required Vector3[] SinkPoints { get; init; }
        internal required Vector3 Spring { get; init; }
        internal required Vector3[] Chambers { get; init; }
        internal required List<Vector3> MainRoute { get; init; }
        internal required Passage[] Passages { get; init; }
        internal required int Conduits { get; init; }
        internal required double Milliseconds { get; init; }

        /// <summary>The field anywhere in the layout, trilinear between its samples.</summary>
        internal float At(Vector3 p) => Trilinear(Open, p);
    }

    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable) Generate(ulong seed, double voxelSize = 1d)
    {
        Result cave = Build(seed);
        Func<Vector3, float> shape = cave.At;
        (DungeonVolume volume, RockDensity rock) = StudyField.Sample(shape, ChunksX, ChunksY, ChunksZ, voxelSize);
        Vector3 arrive = StudyField.OnFloor(shape, cave.SinkPoints[0].X, cave.SinkPoints[0].Z, cave.SinkPoints[0].Y);
        Vector3 spring = StudyField.OnFloor(shape, cave.Spring.X, cave.Spring.Z, cave.Spring.Y + 1f);
        List<Vector3> lights = [];
        int step = Math.Max(1, cave.MainRoute.Count / (MostLights - 2));
        for (int i = step / 2; i < cave.MainRoute.Count && lights.Count < MostLights - 2; i += step)
        {
            Vector3 on = cave.MainRoute[i];
            lights.Add(StudyField.OnFloor(shape, on.X, on.Z, on.Y) + (Vector3.UnitY * LightAboveFloor));
        }

        lights.Add(arrive + (Vector3.UnitY * LightAboveFloor));
        lights.Add(spring + (Vector3.UnitY * LightAboveFloor));
        DungeonCell arrival = StudyField.Cell(arrive);
        DungeonCell springCell = StudyField.Cell(spring);
        DungeonPlan plan = new(seed, 0, DungeonMix.MostlyCave, 1, arrival, springCell,
            [arrival, .. cave.SinkPoints.Skip(1).Select(s => StudyField.Cell(StudyField.OnFloor(shape, s.X, s.Z, s.Y))),
                .. cave.Chambers.Select(c => StudyField.Cell(StudyField.OnFloor(shape, c.X, c.Z, c.Y))), springCell], springCell);
        DungeonLayout layout = new($"karst-cave-{seed:x}", new DungeonVolume(volume.ChunksX, volume.ChunksY, volume.ChunksZ, BlockId.Air),
            arrive, arrive + new Vector3(0.5f, 0f, 0f), lights)
        {
            Rock = rock,
            Natural = true,
            Shape = shape,
            VoxelSize = voxelSize,
        };
        return (layout, plan, new DungeonVerdict(true, Readout(cave), 0), volume);
    }

    /// <summary>What a generated cave came to, in one line.</summary>
    internal static string Readout(Result cave) => string.Create(CultureInfo.InvariantCulture,
        $"karst cave: {cave.Conduits} conduits, {cave.Chambers.Length} chambers, generated in {cave.Milliseconds:F0} ms; passages ")
        + string.Join(", ", cave.Passages.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Name} {p.Metres:F0} m narrowest {p.Before:F2}->{p.After:F2} m widened {p.Widened}")));

    /// <summary>Generates the cave: the rock, the water's rounds through it, the shapes it leaves, and the passages checked.</summary>
    internal static Result Build(ulong seed)
    {
        long started = Stopwatch.GetTimestamp();
        DungeonRandom random = new(seed);
        float[] solubility = Rock(random, seed, out bool[] joint, out bool[] parting, out float[] beds);

        // Where water enters and leaves.
        Vector3[] sinks = new Vector3[Sinks];
        for (int i = 0; i < Sinks; i++)
        {
            sinks[i] = new Vector3(Between(random, 8f, (GridX * GridCell) - 30f), (GridY - 2.5f) * GridCell, Between(random, 8f, (GridZ * GridCell) - 8f));
        }

        // Each stage's spring rises where the valley's floor then was, at the cave's far side.
        Vector3[] springs = [.. WaterTables.Select(table => new Vector3((GridX - 2.5f) * GridCell, table - 2f, Between(random, 16f, (GridZ * GridCell) - 16f)))];
        Vector3 spring = springs[^1];
        float[] discharge = [.. Enumerable.Range(0, Sinks).Select(_ => Between(random, 0.5f, 1.5f))];

        // The rounds: each sink's water takes the cheapest way to the stage's spring, and the ways taken grow. A conduit
        // keeps the stage it was made in, which says whether it was flooded (a tube) or open to the air (a canyon or shaft).
        float[] conduit = new float[GridX * GridY * GridZ];
        Dictionary<(int Stage, int A, int B), float> edgeFlow = [];
        for (int stage = 0; stage < WaterTables.Length; stage++)
        {
            int springCell = GridIndex(springs[stage]);
            for (int round = 0; round < RoundsPerStage; round++)
            {
                int[] towardSpring = Drain(solubility, joint, parting, conduit, springCell, WaterTables[stage], seed, (stage * RoundsPerStage) + round);
                for (int s = 0; s < Sinks; s++)
                {
                    int at = GridIndex(sinks[s]);
                    for (int guard = 0; at != springCell && towardSpring[at] >= 0 && guard < conduit.Length; guard++)
                    {
                        int next = towardSpring[at];
                        (int, int, int) edge = (stage, at, next);
                        edgeFlow[edge] = edgeFlow.GetValueOrDefault(edge) + discharge[s];
                        conduit[next] += discharge[s] * ConduitGain;
                        conduit[at] += discharge[s] * ConduitGain;
                        at = next;
                    }
                }
            }
        }

        // The shapes the water left.
        float[] open = new float[FieldX * FieldY * FieldZ];
        Array.Fill(open, -FieldLimit);
        float mostFlow = edgeFlow.Values.DefaultIfEmpty(1f).Max();
        Dictionary<int, (int Degree, float Flow)> nodes = [];
        int carved = 0;
        foreach (((int stage, int a, int b), float flow) in edgeFlow)
        {
            if (flow < LeastFlowCarved)
            {
                continue;
            }

            carved++;
            float share = MathF.Sqrt(flow / mostFlow);
            float radius = NarrowestRadius + ((WidestRadius - NarrowestRadius) * share);
            Vector3 from = GridCentre(a), to = GridCentre(b);
            Conduit(open, from, to, radius, share, WaterTables[stage], parting[a] && parting[b]);
            foreach (int end in (ReadOnlySpan<int>)[a, b])
            {
                (int degree, float most) = nodes.GetValueOrDefault(end);
                nodes[end] = (degree + 1, MathF.Max(most, flow));
            }
        }

        List<Vector3> chambers = [];
        foreach ((int node, (int degree, float flow)) in nodes)
        {
            if (degree >= 3 && flow >= ChamberFlow * mostFlow)
            {
                Vector3 centre = GridCentre(node);
                if (chambers.All(c => Vector3.Distance(c, centre) > WidestRadius * ChamberWiden * 2f))
                {
                    chambers.Add(centre);
                    Chamber(open, centre, NarrowestRadius + ((WidestRadius - NarrowestRadius) * MathF.Sqrt(flow / mostFlow)), beds);
                }
            }
        }

        Roughen(open, seed);
        Enclose(open);

        // The water's fields for later passes.
        float[] flowField = new float[conduit.Length];
        foreach (((_, int a, int b), float flow) in edgeFlow)
        {
            flowField[a] = MathF.Max(flowField[a], flow / mostFlow);
            flowField[b] = MathF.Max(flowField[b], flow / mostFlow);
        }

        float[] drip = new float[conduit.Length];
        float[] standing = new float[conduit.Length];
        for (int i = 0; i < conduit.Length; i++)
        {
            Vector3 c = GridCentre(i);
            bool openHere = Trilinear(open, c) > 0f;
            bool ceilingAbove = Trilinear(open, c + new Vector3(0f, GridCell, 0f)) < 0f;
            drip[i] = openHere && ceilingAbove && joint[i] && c.Y > DrainedTable ? 1f : 0f;
            standing[i] = openHere && c.Y < DrainedTable ? 1f : 0f;
        }

        // Passage: the widest way from the first sink to the spring and to every other sink, widened where a body would not fit.
        float[] clearance = Clearance(open);
        Vector3 start = sinks[0];
        List<(string Name, Vector3 To)> routes = [("spring", spring), .. sinks.Skip(1).Select((s, i) => ($"sink{i + 1}", s))];
        List<Passage> passages = [];
        List<Vector3> mainRoute = [];
        foreach ((string name, Vector3 to) in routes)
        {
            (Passage passage, List<int> path) = EnsurePassage(open, ref clearance, name, start, to);
            passages.Add(passage);
            if (name == "spring")
            {
                mainRoute = [.. path.Where((_, i) => i % 4 == 0).Select(FieldCentre)];
            }
        }

        return new Result
        {
            Open = open,
            Clearance = clearance,
            Flow = flowField,
            Drip = drip,
            Standing = standing,
            SinkPoints = sinks,
            Spring = spring,
            Chambers = [.. chambers],
            MainRoute = mainRoute,
            Passages = [.. passages],
            Conduits = carved,
            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }

    /// <summary>How readily each grid cell dissolves: the host rock, more along bed partings and joints. Also the joints and bed heights.</summary>
    private static float[] Rock(DungeonRandom random, ulong seed, out bool[] joint, out bool[] parting, out float[] beds)
    {
        List<float> partings = [];
        for (float y = Between(random, 1f, BedSpacingMost); y < GridY * GridCell; y += Between(random, BedSpacingLeast, BedSpacingMost))
        {
            partings.Add(y);
        }

        beds = [.. partings];
        (Vector2 Point, Vector2 Normal, float Low, float High, float Reach)[] joints = [.. Enumerable.Range(0, Joints).Select(_ =>
        {
            float angle = Between(random, 0f, MathF.PI);
            float low = Between(random, 0f, GridY * GridCell * 0.6f);
            return (new Vector2(Between(random, 0f, GridX * GridCell), Between(random, 0f, GridZ * GridCell)),
                new Vector2(MathF.Cos(angle), MathF.Sin(angle)), low, low + Between(random, 15f, GridY * GridCell), Between(random, 15f, JointLongest));
        })];
        float[] solubility = new float[GridX * GridY * GridZ];
        joint = new bool[solubility.Length];
        parting = new bool[solubility.Length];
        for (int i = 0; i < solubility.Length; i++)
        {
            Vector3 c = GridCentre(i);
            float s = 1f + (SolubilityRoll * NaturalCavern.Signed(NaturalCavern.Noise(c * 0.05f, seed ^ 0x501UL)));
            float bedY = c.Y - (BedDip.X * c.X) - (BedDip.Y * c.Z);
            if (partings.Any(p => MathF.Abs(bedY - p) < GridCell * 0.5f))
            {
                s *= PartingSolubility;
                parting[i] = true;
            }

            foreach ((Vector2 point, Vector2 normal, float low, float high, float reach) in joints)
            {
                Vector2 d = new Vector2(c.X, c.Z) - point;
                Vector2 along = new(-normal.Y, normal.X);
                if (MathF.Abs(Vector2.Dot(d, normal)) < GridCell * 0.5f && MathF.Abs(Vector2.Dot(d, along)) < reach && c.Y >= low && c.Y <= high)
                {
                    s *= JointSolubility;
                    joint[i] = true;
                }
            }

            solubility[i] = s;
        }

        return solubility;
    }

    /// <summary>One round's cheapest ways to the spring: for every cell, the next cell its water flows to (Dijkstra from the spring, edges reversed).</summary>
    private static int[] Drain(float[] solubility, bool[] joint, bool[] parting, float[] conduit, int spring, float waterTable, ulong seed, int round)
    {
        float[] cost = new float[solubility.Length];
        Array.Fill(cost, float.MaxValue);
        int[] next = new int[solubility.Length];
        Array.Fill(next, -1);
        PriorityQueue<int, float> frontier = new();
        cost[spring] = 0f;
        frontier.Enqueue(spring, 0f);
        while (frontier.TryDequeue(out int c, out float known))
        {
            if (known > cost[c])
            {
                continue;
            }

            (int cx, int cy, int cz) = GridCell3(c);
            float resistance = 1f / (solubility[c] * (1f + conduit[c]));
            float jitter = 1f + (RoundJitter * NaturalCavern.Signed(NaturalCavern.Hash(seed ^ (ulong)(round * 7919), cx, cy, cz)));
            foreach ((int dx, int dy, int dz) in AroundWater)
            {
                int nx = cx + dx, ny = cy + dy, nz = cz + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= GridX || ny >= GridY || nz >= GridZ)
                {
                    continue;
                }

                // Water flows from n to c: climbing when c is above n.
                bool vadose = ((ny + 0.5f) * GridCell) > waterTable;
                float lean = -dy > 0 ? (vadose ? VadoseClimb : PhreaticClimb) : -dy < 0 ? (vadose ? VadoseFall : 1f) : 1f;
                float length = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) * GridCell;
                int n = GridIndex(nx, ny, nz);
                float grain = dy != 0 ? (joint[c] && joint[n] ? 1f : AcrossJoints) : (parting[c] && parting[n] ? 1f : AcrossBeds);
                float through = known + (length * resistance * lean * grain * jitter);
                if (through < cost[n])
                {
                    cost[n] = through;
                    next[n] = c;
                    frontier.Enqueue(n, through);
                }
            }
        }

        return next;
    }

    /// <summary>Opens one conduit between two grid cells, shaped by where it lies and which way it runs.</summary>
    private static void Conduit(float[] open, Vector3 from, Vector3 to, float radius, float share, float waterTable, bool alongParting)
    {
        Vector3 mid = (from + to) * 0.5f;
        Vector3 run = to - from;
        bool vadose = mid.Y > waterTable;
        bool falls = MathF.Abs(run.Y) > 0.7f * run.Length();
        if (vadose && falls)
        {
            StampCapsule(open, from, to, radius * ShaftWiden, Vector3.One, 0f);
        }
        else if (vadose)
        {
            // A canyon: the falling water cut down below its first tube, deeper the more of it there was.
            float stretch = 1f + (CanyonStretch * share);
            StampCapsule(open, from, to, radius * 0.75f, new Vector3(1f, stretch, 1f), radius * 0.75f * (stretch - 1f));
        }
        else
        {
            // Along a parting the tube spreads into a wide, low bedding-plane passage.
            Vector3 scale = alongParting ? new Vector3(PartingWiden, PartingFlatten, PartingWiden) : new Vector3(TubeWiden, TubeFlatten, TubeWiden);
            StampCapsule(open, from, to, radius, scale, 0f, sediment: true);
        }
    }

    /// <summary>Opens a breakdown chamber: wide and low, its ceiling fallen back to the bedding plane above.</summary>
    private static void Chamber(float[] open, Vector3 centre, float radius, float[] beds)
    {
        float ceiling = beds.Where(b => b > centre.Y + radius).DefaultIfEmpty(centre.Y + (radius * ChamberHeight)).Min();
        Vector3 radii = new(radius * ChamberWiden, radius * ChamberHeight, radius * ChamberWiden);
        ForBox(centre, radii + Vector3.One, (index, p) =>
        {
            Vector3 q = (p - centre) / radii;
            float shape = (1f - q.Length()) * radii.Y;
            shape = NaturalCavern.SmoothMin(shape, ceiling - p.Y, 0.6f);
            shape = NaturalCavern.SmoothMin(shape, p.Y - (centre.Y - (radius * 0.6f)), 0.6f);
            open[index] = NaturalCavern.SmoothMax(open[index], shape, ShapeBlend);
        });
    }

    /// <summary>Opens a capsule from a to b, scaled per axis, its middle offset downward; with sediment, its lower part filled flat.</summary>
    private static void StampCapsule(float[] open, Vector3 a, Vector3 b, float radius, Vector3 scale, float down, bool sediment = false)
    {
        Vector3 offset = new(0f, -down, 0f);
        Vector3 reach = (scale * radius) + new Vector3(0f, down, 0f) + Vector3.One;
        Vector3 low = Vector3.Min(a, b) - reach, high = Vector3.Max(a, b) + reach;
        float least = MathF.Min(scale.X, MathF.Min(scale.Y, scale.Z));
        ForBox((low + high) * 0.5f, (high - low) * 0.5f, (index, p) =>
        {
            Vector3 q = p - offset;
            Vector3 ab = (b - a) / scale, aq = (q - a) / scale;
            float t = Math.Clamp(Vector3.Dot(aq, ab) / MathF.Max(1e-4f, ab.LengthSquared()), 0f, 1f);
            float shape = (radius - (aq - (ab * t)).Length()) * least;
            if (sediment)
            {
                float axisY = a.Y + ((b.Y - a.Y) * t);
                shape = NaturalCavern.SmoothMin(shape, p.Y - (axisY - (radius * scale.Y * (1f - SedimentFill))), 0.4f);
            }

            open[index] = NaturalCavern.SmoothMax(open[index], shape, ShapeBlend);
        });
    }

    /// <summary>Opens a plain sphere where a pinch is too narrow for a body.</summary>
    private static void Widen(float[] open, Vector3 centre, float radius)
    {
        ForBox(centre, new Vector3(radius + 1f), (index, p) =>
            open[index] = MathF.Max(open[index], radius - Vector3.Distance(p, centre)));
    }

    /// <summary>Unevenness on every open surface: broad swells and a grain, harder where water fell freely.</summary>
    private static void Roughen(float[] open, ulong seed)
    {
        Parallel.For(0, FieldZ, z =>
        {
            for (int y = 0; y < FieldY; y++)
                for (int x = 0; x < FieldX; x++)
                {
                    int index = FieldIndex(x, y, z);
                    if (open[index] <= -FieldLimit)
                    {
                        continue;
                    }

                    Vector3 p = FieldCentre(index);
                    float grain = p.Y > WaterTables[^1] ? GrainAmplitude * VadoseGrain : GrainAmplitude;
                    open[index] = Math.Clamp(open[index]
                        + (SwellAmplitude * NaturalCavern.Signed(NaturalCavern.Noise(p * SwellFrequency, seed ^ 0x601UL)))
                        + (grain * NaturalCavern.Signed(NaturalCavern.Noise(p * GrainFrequency, seed ^ 0x602UL))), -FieldLimit, FieldLimit);
                }
        });
    }

    /// <summary>Keeps a rind of rock inside the volume's sides, so the cave never opens onto the edge of the space.</summary>
    private static void Enclose(float[] open)
    {
        Vector3 size = new(FieldX * FieldStep, FieldY * FieldStep, FieldZ * FieldStep);
        Parallel.For(0, FieldZ, z =>
        {
            for (int y = 0; y < FieldY; y++)
                for (int x = 0; x < FieldX; x++)
                {
                    int index = FieldIndex(x, y, z);
                    Vector3 p = FieldCentre(index);
                    Vector3 far = size - p;
                    float edge = MathF.Min(MathF.Min(MathF.Min(p.X, far.X), MathF.Min(p.Y, far.Y)), MathF.Min(p.Z, far.Z));
                    open[index] = MathF.Min(open[index], edge - RindMetres);
                }
        });
    }

    /// <summary>Each open sample's distance to the nearest rock, in metres (zero in rock): an exact distance transform.</summary>
    internal static float[] Clearance(float[] open)
    {
        const float Far = 1e9f;
        float[] d = new float[open.Length];
        for (int i = 0; i < open.Length; i++)
        {
            d[i] = open[i] > 0f ? Far : 0f;
        }

        // Squared distances along x, then y, then z (Felzenszwalb and Huttenlocher), in samples.
        Parallel.For(0, FieldZ, z =>
        {
            float[] line = new float[FieldX];
            for (int y = 0; y < FieldY; y++)
            {
                for (int x = 0; x < FieldX; x++) line[x] = d[FieldIndex(x, y, z)];
                float[] done = Transform(line);
                for (int x = 0; x < FieldX; x++) d[FieldIndex(x, y, z)] = done[x];
            }
        });
        Parallel.For(0, FieldZ, z =>
        {
            float[] line = new float[FieldY];
            for (int x = 0; x < FieldX; x++)
            {
                for (int y = 0; y < FieldY; y++) line[y] = d[FieldIndex(x, y, z)];
                float[] done = Transform(line);
                for (int y = 0; y < FieldY; y++) d[FieldIndex(x, y, z)] = done[y];
            }
        });
        Parallel.For(0, FieldY, y =>
        {
            float[] line = new float[FieldZ];
            for (int x = 0; x < FieldX; x++)
            {
                for (int z = 0; z < FieldZ; z++) line[z] = d[FieldIndex(x, y, z)];
                float[] done = Transform(line);
                for (int z = 0; z < FieldZ; z++) d[FieldIndex(x, y, z)] = done[z];
            }
        });
        for (int i = 0; i < d.Length; i++)
        {
            d[i] = MathF.Sqrt(d[i]) * FieldStep;
        }

        return d;
    }

    /// <summary>One line of the squared distance transform: the lower envelope of parabolas.</summary>
    private static float[] Transform(float[] f)
    {
        int n = f.Length;
        float[] result = new float[n];
        int[] v = new int[n];
        float[] z = new float[n + 1];
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s = Meet(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Meet(f, q, v[k]);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            result[q] = ((q - v[k]) * (q - v[k])) + f[v[k]];
        }

        return result;
    }

    /// <summary>
    /// Makes sure a body fits from one place to another. The way taken is the shortest one as wide as a standing body (or,
    /// where nothing is, as wide as the widest way allows); where that way is narrower than a crawling body, it is widened
    /// at each pinch, and the clearance is measured again.
    /// </summary>
    internal static (Passage Passage, List<int> Path) EnsurePassage(float[] open, ref float[] clearance, string name, Vector3 from, Vector3 to)
    {
        (float before, _) = Widest(clearance, from, to);
        List<int> path = Shortest(clearance, from, to, MathF.Min(before, StandRadius));
        int widened = 0;
        foreach (int sample in path)
        {
            if (clearance[sample] < CrawlRadius)
            {
                Widen(open, FieldCentre(sample), CrawlRadius + WidenMargin);
                widened++;
            }
        }

        if (widened > 0)
        {
            clearance = Clearance(open);
        }

        (float after, _) = Widest(clearance, from, to);
        List<int> final = Shortest(clearance, from, to, MathF.Min(after, StandRadius));
        return (new Passage(name, before, after, widened, final.Count * FieldStep), final);
    }

    /// <summary>
    /// The shortest way between two places through samples at least <paramref name="least"/> clear. With nothing that clear,
    /// the way through every sample (rock included) that is cheapest by how much rock it crosses: what widening would open.
    /// </summary>
    private static List<int> Shortest(float[] clearance, Vector3 from, Vector3 to, float least)
    {
        int start = NearestOpen(clearance, from), goal = NearestOpen(clearance, to);
        if (start < 0 || goal < 0)
        {
            return [];
        }

        float[] cost = new float[clearance.Length];
        Array.Fill(cost, float.MaxValue);
        int[] back = new int[clearance.Length];
        Array.Fill(back, -1);
        PriorityQueue<int, float> frontier = new();
        cost[start] = 0f;
        frontier.Enqueue(start, 0f);
        while (frontier.TryDequeue(out int c, out float known))
        {
            if (known > cost[c])
            {
                continue;
            }

            if (c == goal)
            {
                break;
            }

            (int cx, int cy, int cz) = FieldCell3(c);
            foreach ((int dx, int dy, int dz) in Around6)
            {
                int nx = cx + dx, ny = cy + dy, nz = cz + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                {
                    continue;
                }

                int n = FieldIndex(nx, ny, nz);
                // Wide enough samples cost their length; narrower ones cost much more the narrower they are, so a way
                // through a pinch is taken only where there is no other.
                float step = clearance[n] >= least ? 1f : 1f + (PinchCost * (least - clearance[n]));
                float through = known + step;
                if (through < cost[n])
                {
                    cost[n] = through;
                    back[n] = c;
                    frontier.Enqueue(n, through);
                }
            }
        }

        List<int> path = [];
        for (int at = goal; at >= 0; at = back[at])
        {
            path.Add(at);
        }

        path.Reverse();
        return path;
    }

    /// <summary>How much a sample narrower than wanted adds to a way, per metre it is too narrow.</summary>
    private const float PinchCost = 400f;

    /// <summary>Where the parabolas rooted at samples q and r meet.</summary>
    private static float Meet(float[] f, int q, int r) => ((f[q] + (q * q)) - (f[r] + (r * r))) / ((2f * q) - (2f * r));

    /// <summary>
    /// The widest way between two places through the open samples: the path whose narrowest clearance is greatest (a
    /// sphere of that radius passes all along it), and that narrowest clearance. Zero when there is no way at all.
    /// </summary>
    internal static (float Narrowest, List<int> Path) Widest(float[] clearance, Vector3 from, Vector3 to)
    {
        int start = NearestOpen(clearance, from), goal = NearestOpen(clearance, to);
        if (start < 0 || goal < 0)
        {
            return (0f, []);
        }

        float[] best = new float[clearance.Length];
        int[] back = new int[clearance.Length];
        Array.Fill(back, -1);
        PriorityQueue<int, float> frontier = new();
        best[start] = clearance[start];
        frontier.Enqueue(start, -best[start]);
        while (frontier.TryDequeue(out int c, out float negative))
        {
            if (-negative < best[c])
            {
                continue;
            }

            if (c == goal)
            {
                break;
            }

            (int cx, int cy, int cz) = FieldCell3(c);
            foreach ((int dx, int dy, int dz) in Around6)
            {
                int nx = cx + dx, ny = cy + dy, nz = cz + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                {
                    continue;
                }

                int n = FieldIndex(nx, ny, nz);
                float width = MathF.Min(best[c], clearance[n]);
                if (clearance[n] > 0f && width > best[n])
                {
                    best[n] = width;
                    back[n] = c;
                    frontier.Enqueue(n, -width);
                }
            }
        }

        if (best[goal] <= 0f)
        {
            return (0f, []);
        }

        List<int> path = [];
        for (int at = goal; at >= 0; at = back[at])
        {
            path.Add(at);
        }

        path.Reverse();
        return (best[goal], path);
    }

    /// <summary>The open sample with the most clearance within a few metres of a place.</summary>
    private static int NearestOpen(float[] clearance, Vector3 p)
    {
        const float Reach = 4f;
        int found = -1;
        float most = 0f;
        ForBox(p, new Vector3(Reach), (index, _) =>
        {
            if (clearance[index] > most)
            {
                most = clearance[index];
                found = index;
            }
        });
        return found;
    }

    private static void ForBox(Vector3 centre, Vector3 half, Action<int, Vector3> visit)
    {
        int x0 = Math.Max(0, (int)MathF.Floor((centre.X - half.X) / FieldStep)), x1 = Math.Min(FieldX - 1, (int)MathF.Ceiling((centre.X + half.X) / FieldStep));
        int y0 = Math.Max(0, (int)MathF.Floor((centre.Y - half.Y) / FieldStep)), y1 = Math.Min(FieldY - 1, (int)MathF.Ceiling((centre.Y + half.Y) / FieldStep));
        int z0 = Math.Max(0, (int)MathF.Floor((centre.Z - half.Z) / FieldStep)), z1 = Math.Min(FieldZ - 1, (int)MathF.Ceiling((centre.Z + half.Z) / FieldStep));
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int index = FieldIndex(x, y, z);
                    visit(index, FieldCentre(index));
                }
    }

    private static float Trilinear(float[] field, Vector3 p)
    {
        float fx = Math.Clamp((p.X / FieldStep) - 0.5f, 0f, FieldX - 1.001f);
        float fy = Math.Clamp((p.Y / FieldStep) - 0.5f, 0f, FieldY - 1.001f);
        float fz = Math.Clamp((p.Z / FieldStep) - 0.5f, 0f, FieldZ - 1.001f);
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
        float L(int dx, int dy, int dz) => field[FieldIndex(x0 + dx, y0 + dy, z0 + dz)];
        static float Lerp(float a, float b, float t) => a + ((b - a) * t);
        return Lerp(Lerp(Lerp(L(0, 0, 0), L(1, 0, 0), tx), Lerp(L(0, 1, 0), L(1, 1, 0), tx), ty),
            Lerp(Lerp(L(0, 0, 1), L(1, 0, 1), tx), Lerp(L(0, 1, 1), L(1, 1, 1), tx), ty), tz);
    }

    private static float Between(DungeonRandom random, float low, float high) => low + ((high - low) * ((random.Next() >> 40) / (float)(1UL << 24)));

    private static int GridIndex(int x, int y, int z) => (((z * GridY) + y) * GridX) + x;

    private static int GridIndex(Vector3 p) => GridIndex(
        Math.Clamp((int)(p.X / GridCell), 0, GridX - 1), Math.Clamp((int)(p.Y / GridCell), 0, GridY - 1), Math.Clamp((int)(p.Z / GridCell), 0, GridZ - 1));

    private static (int X, int Y, int Z) GridCell3(int index) => (index % GridX, index / GridX % GridY, index / (GridX * GridY));

    private static Vector3 GridCentre(int index)
    {
        (int x, int y, int z) = GridCell3(index);
        return new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * GridCell;
    }

    internal static int FieldIndex(int x, int y, int z) => (((z * FieldY) + y) * FieldX) + x;

    private static (int X, int Y, int Z) FieldCell3(int index) => (index % FieldX, index / FieldX % FieldY, index / (FieldX * FieldY));

    private static Vector3 FieldCentre(int index)
    {
        (int x, int y, int z) = FieldCell3(index);
        return new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * FieldStep;
    }
}
