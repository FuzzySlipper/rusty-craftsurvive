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
    internal static readonly Vector2 BedDip = new(0.05f, 0.025f);
    private const float SolubilityRoll = 0.3f;

    // The water: a cave lives through a few eras drawn from the kinds below, the water table falling from era to era as
    // the valley cuts down; below where it stands now the cave is still flooded.
    private const float HighestTable = 46f, LowestTable = 15f, DrainedBelowTable = 6f;
    private const int FewestEras = 3, MostEras = 5;
    private const int Sinks = 6;
    private const int RoundsPerStage = 10;
    /// <summary>The share of the sinks feeding any one era: sinks are captured and abandoned between eras.</summary>
    private const float SinkActiveChance = 0.7f;
    private const int FewestActiveSinks = 2;
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

    /// <summary>How much a conduit's radius swells and pinches along its length, and over what distance.</summary>
    private const float WidthRoll = 0.55f, WidthFrequency = 0.11f;

    // Breakdown: how many fallen blocks a chamber holds, their sizes, and the chance a large open-air passage has fallen in.
    private const int FewestBlocks = 6, MostBlocks = 15;
    private const float SmallestBlock = 0.5f, LargestBlock = 1.9f, BlockRounding = 0.25f, CollapseChance = 0.3f, CollapseFlow = 0.3f;

    // Bedding rooms: broad, low rooms spread along a flooded parting, held up by pillars of what did not dissolve.
    private const float BeddingRoomFlow = 0.15f, BeddingRoomChance = 0.5f, BeddingRoomLeast = 6f, BeddingRoomMost = 12f;
    private const float BeddingRoomLow = 0.8f, BeddingRoomHigh = 1.25f, PillarFrequency = 0.3f, PillarShare = 0.62f, BeddingRoomApart = 18f;

    // A joint maze: a block of rock whose close joints all dissolved into a grid of narrow, tall fissures.
    private const float MazeHalf = 12f, MazeSpacing = 3.2f, MazeTop = 4.5f, MazeFissureFoot = 0.55f, MazeFissureTop = 0.32f, MazeOpenShare = 0.72f;

    // Formations where water drips: from open joints under a ceiling above the water table, a stalactite hangs and often a
    // stalagmite rises under it; in a low gap the two have met in a column.
    private const float FormationChance = 0.4f, StalagmiteChance = 0.65f, ColumnGap = 4f, ColumnChance = 0.35f, FormationApart = 1.6f;
    private const int MostFormations = 90;
    /// <summary>A passage opened this many eras ago is fully decorated; one opened in the last era gets this share of formations.</summary>
    private const int DecoratedAfterEras = 2;
    private const float YoungDecoration = 0.25f;
    private const float LeastGap = 1.6f, LongestReach = 14f, FormationBlend = 0.3f, FormationWobble = 0.18f;

    // Detail finer than the field's samples, added wherever the field is read: a micro term only voxels finer than the
    // field (or the mesh) show.
    private const float MicroAmplitude = 0.12f, MicroFrequency = 1.1f, FinerAmplitude = 0.05f, FinerFrequency = 2.4f;

    // Fine forms (#10094), below the field's half-metre samples: scallops (dishes in flow-worn rock, their packing and
    // depth), laminae ledges (their spacing and how far they step), pits, and flowstone ripples where water drips.
    private const float FlowFrom = 0.02f, FlowFull = 0.2f, DripFrom = 0.1f, DripFull = 0.5f;
    private static readonly Vector3 ScallopSize = new(0.6f, 0.4f, 0.6f);
    private const float ScallopReach = 0.55f, ScallopDepth = 0.09f;
    private const float LaminaSpacing = 0.45f, LedgeDepth = 0.06f, LaminaPatchFrequency = 0.15f;
    private const float PitSpacing = 0.3f, PitShare = 0.25f, PitReach = 0.45f, PitDepth = 0.08f;
    private const float RippleFrequency = 12f, RippleHeight = 0.06f;

    // Above the water table: sharp ridged grain, and soft partings eaten back into notches between harder beds.
    private const float JagAmplitude = 0.55f, JagFrequency = 0.33f, NotchDepth = 0.5f, NotchHalfHeight = 0.45f;

    // The rock's own unevenness: swells and a grain, scalloped harder where water fell freely.
    private const float SwellAmplitude = 0.5f, SwellFrequency = 0.13f, GrainAmplitude = 0.22f, GrainFrequency = 0.5f, VadoseGrain = 1.6f;

    // Passage: a crawling body's radius, a standing one's, and how far past a body a pinch is widened.
    internal const float CrawlRadius = 0.45f, StandRadius = 0.85f;
    private const float WidenMargin = 0.2f;

    /// <summary>The rock kept between the cave and the space's sides.</summary>
    private const float RindMetres = 1.5f;

    private const int MostLights = DungeonLayout.MaximumLights;
    private const float LightAboveFloor = 1.2f;

    /// <summary>How many of the lights go to features off the main route (the maze, bedding rooms, chambers).</summary>
    private const int MostFeatureLights = 6;

    /// <summary>Water moves level (with diagonals across the floor) or straight up and down: down joints, along partings, not on slants.</summary>
    private static readonly (int X, int Y, int Z)[] AroundWater = [(1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (1, 0, 1), (1, 0, -1), (-1, 0, 1), (-1, 0, -1), (0, 1, 0), (0, -1, 0)];
    private static readonly (int X, int Y, int Z)[] Around6 = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>
    /// The kinds of era a cave may have lived through, in the order they happen when drawn together: each a different
    /// water regime, leaving its own kind of passage (#10094; the owner's note: passages are the surviving records of
    /// different water regimes at different times).
    /// </summary>
    internal enum Era
    {
        /// <summary>Water rising from below: spongework of joined bubbles and domed cupolas, with no sinks.</summary>
        Hypogene,

        /// <summary>Water filling the rock: rounded tubes and wide bedding passages that loop and branch.</summary>
        Phreatic,

        /// <summary>Floods backing up: a grid of narrow fissures along close joints.</summary>
        FloodMaze,

        /// <summary>Sediment settling: older passages' floors filled flat, some passages choked.</summary>
        Infill,

        /// <summary>Water working the fractures: straight, tall, angular rifts along joints.</summary>
        JointRifts,

        /// <summary>A stream above a falling water table: canyons and shafts, and keyholes cut down into older tubes.</summary>
        VadoseCanyons,

        /// <summary>Collapse: great rooms where ceilings fell, with boulder slopes.</summary>
        Breakdown,
    }

    /// <summary>The eras that carry water from the sinks to a spring; a cave always has one.</summary>
    private static bool Flows(Era era) => era is Era.Phreatic or Era.VadoseCanyons;

    // Keyholes: the slot a later stream cuts below an older tube, as a share of the tube's radius, and how deep.
    private const float KeyholeWidth = 0.4f, KeyholeDepth = 2.5f, KeyholeDepthPerFlow = 3f;

    // Rifts: how many, how far past the touched part of a joint they run, how far they rise, their half-width and jag.
    private const int MostRifts = 4;
    private const float RiftReach = 6f, RiftRise = 7f, RiftNarrowest = 0.35f, RiftWidest = 0.7f, RiftJag = 1.5f, RiftEdge = 0.15f;

    // Hypogene: the spongework's extent, bubble spacing and radii, and the cupolas rising from it.
    private static readonly Vector3 SpongeRadii = new(13f, 6f, 13f);
    private const float SpongeCell = 3.2f, SmallestBubble = 1.7f, LargestBubble = 2.5f, SpongeLowest = 8f, SpongeHighest = 20f;
    private const int Cupolas = 3;
    private static readonly Vector3 CupolaRadii = new(2.5f, 6f, 2.5f);

    // Flood mazes: how many, and how near the era's water table they lie.
    private const int MostMazes = 2;
    private const float MazeNearTable = 6f;

    // Infill: how deep sediment settles on older floors.
    private const float LeastInfill = 0.6f, MostInfill = 2.2f, InfillFrequency = 0.08f, InfillShareOfSpan = 0.4f;

    // Great collapses: how many, their size, and the boulders piled on one side.
    private const int MostCollapses = 3;
    private const float CollapseLeast = 4f, CollapseMost = 6f, CollapseApart = 20f;
    private const int FewestBoulders = 20, MostBoulders = 35;
    private const float LargestBoulder = 3f, BoulderSlopeShare = 0.7f;

    /// <summary>A joint plane: a point on it, its normal across the floor, the heights it spans and how far it runs.</summary>
    private readonly record struct JointPlane(Vector2 Point, Vector2 Normal, float Low, float High, float Reach);

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
        internal required Vector3? MazeAt { get; init; }
        internal required Era[] Eras { get; init; }
        internal required Vector3[] Rifts { get; init; }
        internal required Vector3? Sponge { get; init; }
        /// <summary>Where the water table stands now: above it the cave is open to the air.</summary>
        internal required float FinalTable { get; init; }
        internal required Vector3[] BeddingRooms { get; init; }
        internal required Passage[] Passages { get; init; }
        internal required int Conduits { get; init; }
        internal required double Milliseconds { get; init; }

        /// <summary>The field anywhere in the layout, trilinear between its samples.</summary>
        internal float At(Vector3 p)
        {
            float open = Trilinear(Open, p);
            if (MathF.Abs(open) > 1f)
            {
                return open;
            }

            open += (MicroAmplitude * NaturalCavern.Signed(NaturalCavern.Noise(p * MicroFrequency, Seed ^ 0xA01UL)))
                + (FinerAmplitude * NaturalCavern.Signed(NaturalCavern.Noise(p * FinerFrequency, Seed ^ 0xA02UL)));
            return open + FineForms(p);
        }

        /// <summary>
        /// The forms finer than the field's samples, from what the water did where: scallops where it flowed, little
        /// ledges along the laminae and pits where the rock stood in air, flowstone ripples where it dripped (metres,
        /// positive opening the rock).
        /// </summary>
        private float FineForms(Vector3 p)
        {
            float flowed = SmoothStep(FlowFrom, FlowFull, GridTrilinear(Flow, p));
            float dripped = SmoothStep(DripFrom, DripFull, MathF.Max(GridTrilinear(Drip, p),
                MathF.Max(GridTrilinear(Drip, p + new Vector3(0f, GridCell, 0f)), GridTrilinear(Drip, p + new Vector3(0f, 2f * GridCell, 0f)))));
            bool aired = p.Y > FinalTable;
            float forms = 0f;

            // Scallops: shallow dishes packed over the walls, longer along the flow (level) than across it.
            if (flowed > 0f)
            {
                float f1 = Worley(p / ScallopSize, Seed ^ 0xB01UL, out _);
                forms += ScallopDepth * flowed * MathF.Max(0f, 1f - (f1 * f1 / (ScallopReach * ScallopReach)));
            }

            if (aired)
            {
                // Laminae: thin beds eaten back unevenly, a sawtooth of little ledges along the dip.
                float bedY = p.Y - (BedDip.X * p.X) - (BedDip.Y * p.Z);
                float saw = (bedY / LaminaSpacing) - MathF.Floor(bedY / LaminaSpacing);
                float laminated = SmoothStep(0.45f, 0.7f, NaturalCavern.Noise(p * LaminaPatchFrequency, Seed ^ 0xB02UL));
                forms += LedgeDepth * laminated * ((saw * 2f) - 1f);

                // Pits: a few small cups where the rock weathered in air.
                float f1 = Worley(p / PitSpacing, Seed ^ 0xB03UL, out float which);
                if (which < PitShare)
                {
                    forms += PitDepth * MathF.Max(0f, 1f - (f1 / PitReach));
                }
            }

            // Flowstone: deposited rock in ripples down the walls where the water dripped.
            if (dripped > 0f)
            {
                float wave = 0.5f + (0.5f * MathF.Sin((p.Y * RippleFrequency) + (3f * NaturalCavern.Noise(p * 0.6f, Seed ^ 0xB04UL))));
                forms -= RippleHeight * dripped * wave;
            }

            return forms;
        }

        /// <summary>The cave's seed, for its micro term.</summary>
        internal required ulong Seed { get; init; }

        /// <summary>How many stalactites, stalagmites and columns the drip left.</summary>
        internal required int Formations { get; init; }
    }

    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable) Generate(ulong seed, double voxelSize = 1d)
    {
        Result cave = Build(seed);
        Func<Vector3, float> shape = cave.At;
        (DungeonVolume volume, RockDensity rock) = StudyField.Sample(shape, ChunksX, ChunksY, ChunksZ, voxelSize);
        Vector3 arrive = StudyField.OnFloor(shape, cave.SinkPoints[0].X, cave.SinkPoints[0].Z, cave.SinkPoints[0].Y);
        Vector3 spring = StudyField.OnFloor(shape, cave.Spring.X, cave.Spring.Z, cave.Spring.Y + 1f);
        // The lights: the arrival and the spring, a light at each feature off the way (the maze, bedding rooms, then
        // chambers), and the rest spread along the main route.
        List<Vector3> lights = [arrive + (Vector3.UnitY * LightAboveFloor), spring + (Vector3.UnitY * LightAboveFloor)];
        Vector3[] features = [.. (cave.Sponge is Vector3 sponge ? [sponge] : Array.Empty<Vector3>()), .. (cave.MazeAt is Vector3 maze ? [maze] : Array.Empty<Vector3>()),
            .. cave.Rifts, .. cave.BeddingRooms, .. cave.Chambers];
        foreach (Vector3 feature in features.Take(MostFeatureLights))
        {
            lights.Add(StudyField.OnFloor(shape, feature.X, feature.Z, feature.Y + LightAboveFloor) + (Vector3.UnitY * LightAboveFloor));
        }

        int routeLights = MostLights - lights.Count;
        int step = Math.Max(1, cave.MainRoute.Count / Math.Max(1, routeLights));
        for (int i = step / 2; i < cave.MainRoute.Count && lights.Count < MostLights; i += step)
        {
            Vector3 on = cave.MainRoute[i];
            lights.Add(StudyField.OnFloor(shape, on.X, on.Z, on.Y) + (Vector3.UnitY * LightAboveFloor));
        }
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
        $"karst cave: eras {string.Join("+", cave.Eras)}, {cave.Conduits} conduits, {cave.Chambers.Length} chambers, {cave.Rifts.Length} rifts, {cave.Formations} formations, generated in {cave.Milliseconds:F0} ms; passages ")
        + string.Join(", ", cave.Passages.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Name} {p.Metres:F0} m narrowest {p.Before:F2}->{p.After:F2} m widened {p.Widened}")));

    /// <summary>Generates the cave: the rock, the water's rounds through it, the shapes it leaves, and the passages checked.</summary>
    internal static Result Build(ulong seed)
    {
        long started = Stopwatch.GetTimestamp();
        DungeonRandom random = new(seed);
        float[] solubility = Rock(random, seed, out bool[] joint, out bool[] parting, out float[] beds, out JointPlane[] planes);

        // Where water enters.
        Vector3[] sinks = new Vector3[Sinks];
        for (int i = 0; i < Sinks; i++)
        {
            sinks[i] = new Vector3(Between(random, 8f, (GridX * GridCell) - 30f), (GridY - 2.5f) * GridCell, Between(random, 8f, (GridZ * GridCell) - 8f));
        }

        float[] discharge = [.. Enumerable.Range(0, Sinks).Select(_ => Between(random, 0.5f, 1.5f))];

        // The cave's history: a few eras drawn without repeats, in the order they happen, the water table falling from
        // one to the next. A cave always has an era of flowing water.
        Era[] eras = DrawEras(random);
        float[] tables = [.. eras.Select((_, i) => eras.Length == 1 ? LowestTable : HighestTable + ((LowestTable - HighestTable) * i / (eras.Length - 1)))];
        float finalTable = tables[^1];

        float[] open = new float[FieldX * FieldY * FieldZ];
        Array.Fill(open, -FieldLimit);
        // The era each open sample was first opened in, for what age does to it later.
        byte[] formed = new byte[open.Length];
        Array.Fill(formed, Unformed);
        float[] conduit = new float[GridX * GridY * GridZ];
        float[] activeFlow = new float[conduit.Length];
        List<Vector3> chambers = [], beddingRooms = [], mazes = [], rifts = [];
        List<(Vector3 At, float Share, int Era)> passages0 = [];
        Vector3? sponge = null;
        Vector3 spring = new((GridX - 2.5f) * GridCell, finalTable - 2f, GridZ * GridCell * 0.5f);
        int carved = 0;
        for (int index = 0; index < eras.Length; index++)
        {
            float[] before = (float[])open.Clone();
            float table = tables[index];
            switch (eras[index])
            {
                case Era.Phreatic:
                case Era.VadoseCanyons:
                    spring = new Vector3((GridX - 2.5f) * GridCell, table - 2f, Between(random, 16f, (GridZ * GridCell) - 16f));
                    carved += Flow(eras[index], index, open, solubility, joint, parting, beds, conduit, activeFlow, sinks, discharge, spring, table,
                        seed, random, chambers, beddingRooms, passages0);
                    break;
                case Era.JointRifts:
                    Rifts(open, planes, conduit, table, seed, rifts);
                    break;
                case Era.FloodMaze:
                    FloodMazes(open, passages0, sinks[0], table, seed, random, mazes);
                    break;
                case Era.Hypogene:
                    sponge = Hypogene(open, seed, random);
                    break;
                case Era.Infill:
                    Infill(open, formed, (byte)index, seed);
                    break;
                case Era.Breakdown:
                    Collapses(open, beds, passages0, index, random, chambers);
                    break;
            }

            for (int i = 0; i < open.Length; i++)
            {
                if (before[i] <= 0f && open[i] > 0f)
                {
                    formed[i] = (byte)index;
                }
            }
        }

        foreach (Vector3 chamber in chambers)
        {
            Breakdown(open, chamber, random);
        }

        int formations = Drip(open, joint, formed, eras.Length, finalTable, random);

        Roughen(open, seed, beds, finalTable);
        Enclose(open);

        // The water's fields for later passes: the flow is the last flowing era's, the water still at work.
        float drainedTable = finalTable - DrainedBelowTable;
        float[] drip = new float[conduit.Length];
        float[] standing = new float[conduit.Length];
        for (int i = 0; i < conduit.Length; i++)
        {
            Vector3 c = GridCentre(i);
            bool openHere = Trilinear(open, c) > 0f;
            bool ceilingAbove = Trilinear(open, c + new Vector3(0f, GridCell, 0f)) < 0f;
            drip[i] = openHere && ceilingAbove && joint[i] && c.Y > drainedTable ? 1f : 0f;
            standing[i] = openHere && c.Y < drainedTable ? 1f : 0f;
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
            Flow = activeFlow,
            Drip = drip,
            Standing = standing,
            SinkPoints = sinks,
            Spring = spring,
            Chambers = [.. chambers],
            MainRoute = mainRoute,
            MazeAt = mazes.Count > 0 ? mazes[0] : null,
            BeddingRooms = [.. beddingRooms],
            Passages = [.. passages],
            Conduits = carved,
            Seed = seed,
            Formations = formations,
            Eras = eras,
            Rifts = [.. rifts],
            Sponge = sponge,
            FinalTable = finalTable,
            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }

    private const byte Unformed = byte.MaxValue;

    /// <summary>A cave's eras: between the fewest and most, no kind twice, in the order they happen, at least one flowing.</summary>
    private static Era[] DrawEras(DungeonRandom random)
    {
        List<Era> pool = [.. Enum.GetValues<Era>()];
        int count = FewestEras + (int)(random.Next() % (ulong)(MostEras - FewestEras + 1));
        List<Era> drawn = [];
        for (int i = 0; i < count; i++)
        {
            int pick = (int)(random.Next() % (ulong)pool.Count);
            drawn.Add(pool[pick]);
            pool.RemoveAt(pick);
        }

        if (!drawn.Any(Flows))
        {
            drawn[0] = Chance(random, 0.5f) ? Era.Phreatic : Era.VadoseCanyons;
        }

        return [.. drawn.Distinct().OrderBy(era => era)];
    }

    /// <summary>
    /// A flowing era: the active sinks' water takes the cheapest way to the era's spring, round after round, the ways
    /// taken growing; then the shapes it leaves. A phreatic era leaves tubes and bedding rooms; a vadose era canyons and
    /// shafts, and where its stream follows an older tube it cuts a slot down through that tube's floor - a keyhole.
    /// Returns how many conduits it opened.
    /// </summary>
    private static int Flow(Era era, int index, float[] open, float[] solubility, bool[] joint, bool[] parting, float[] beds, float[] conduit,
        float[] activeFlow, Vector3[] sinks, float[] discharge, Vector3 spring, float table, ulong seed, DungeonRandom random,
        List<Vector3> chambers, List<Vector3> beddingRooms, List<(Vector3 At, float Share, int Era)> passages)
    {
        float[] conduitBefore = (float[])conduit.Clone();
        bool[] active = [.. sinks.Select(_ => Chance(random, SinkActiveChance))];
        for (int i = 0; i < FewestActiveSinks; i++)
        {
            active[i] = true;
        }

        int springCell = GridIndex(spring);
        Dictionary<(int A, int B), float> edgeFlow = [];
        for (int round = 0; round < RoundsPerStage; round++)
        {
            int[] towardSpring = Drain(solubility, joint, parting, conduit, springCell, table, seed, (index * RoundsPerStage) + round);
            for (int s = 0; s < sinks.Length; s++)
            {
                if (!active[s])
                {
                    continue;
                }

                int at = GridIndex(sinks[s]);
                for (int guard = 0; at != springCell && towardSpring[at] >= 0 && guard < conduit.Length; guard++)
                {
                    int next = towardSpring[at];
                    edgeFlow[(at, next)] = edgeFlow.GetValueOrDefault((at, next)) + discharge[s];
                    conduit[next] += discharge[s] * ConduitGain;
                    conduit[at] += discharge[s] * ConduitGain;
                    at = next;
                }
            }
        }

        float mostFlow = edgeFlow.Values.DefaultIfEmpty(1f).Max();
        Array.Clear(activeFlow);
        Dictionary<int, (int Degree, float Flow)> nodes = [];
        int carved = 0;
        foreach (((int a, int b), float flow) in edgeFlow)
        {
            activeFlow[a] = MathF.Max(activeFlow[a], flow / mostFlow);
            activeFlow[b] = MathF.Max(activeFlow[b], flow / mostFlow);
            if (flow < LeastFlowCarved)
            {
                continue;
            }

            carved++;
            float share = MathF.Sqrt(flow / mostFlow);
            float radius = NarrowestRadius + ((WidestRadius - NarrowestRadius) * share);
            Vector3 from = GridCentre(a), to = GridCentre(b);
            if (era == Era.VadoseCanyons && conduitBefore[a] > 0f && conduitBefore[b] > 0f)
            {
                Keyhole(open, from, to, radius * Swell(from, seed), share);
            }
            else
            {
                Conduit(open, from, to, radius * Swell(from, seed), radius * Swell(to, seed), share, era == Era.Phreatic ? table + 8f : table, parting[a] && parting[b]);
            }

            foreach (int end in (ReadOnlySpan<int>)[a, b])
            {
                (int degree, float most) = nodes.GetValueOrDefault(end);
                nodes[end] = (degree + 1, MathF.Max(most, flow));
            }
        }

        foreach ((int node, (int degree, float flow)) in nodes.OrderBy(n => n.Key))
        {
            Vector3 at = GridCentre(node);
            passages.Add((at, MathF.Sqrt(flow / mostFlow), index));
            if (era == Era.Phreatic && parting[node] && Inside(at, BeddingRoomMost) && flow >= BeddingRoomFlow * mostFlow && Chance(random, BeddingRoomChance)
                && beddingRooms.All(r => Vector3.Distance(r, at) > BeddingRoomApart))
            {
                beddingRooms.Add(at);
                BeddingRoom(open, at, Between(random, BeddingRoomLeast, BeddingRoomMost), Between(random, BeddingRoomLow, BeddingRoomHigh), seed);
            }

            if (degree >= 3 && flow >= ChamberFlow * mostFlow && chambers.All(c => Vector3.Distance(c, at) > WidestRadius * ChamberWiden * 2f))
            {
                chambers.Add(at);
                Chamber(open, at, NarrowestRadius + ((WidestRadius - NarrowestRadius) * MathF.Sqrt(flow / mostFlow)), beds);
            }
        }

        return carved;
    }

    /// <summary>The slot a later stream cuts down through an older tube's floor: narrow, tall, deeper the more water it carried.</summary>
    private static void Keyhole(float[] open, Vector3 from, Vector3 to, float radius, float share)
    {
        float slot = radius * KeyholeWidth;
        float depth = KeyholeDepth + (KeyholeDepthPerFlow * share);
        float stretch = depth / slot;
        StampCapsule(open, from, to, slot, 1f, new Vector3(1f, stretch, 1f), (radius * 0.5f) + (depth * 0.5f));
    }

    /// <summary>
    /// Rifts: where water reached a joint, it opened the joint itself - straight, tall and narrow, angular at its edges -
    /// along the joint past where it was touched and up toward the joint's top. The joints most touched are opened.
    /// </summary>
    private static void Rifts(float[] open, JointPlane[] planes, float[] conduit, float table, ulong seed, List<Vector3> rifts)
    {
        List<(JointPlane Plane, float Least, float Most, float Low, float High, int Touched)> touched = [];
        foreach (JointPlane plane in planes)
        {
            Vector2 along = new(-plane.Normal.Y, plane.Normal.X);
            float least = float.MaxValue, most = float.MinValue, low = float.MaxValue, high = float.MinValue;
            int count = 0;
            for (int i = 0; i < conduit.Length; i++)
            {
                Vector3 c = GridCentre(i);
                Vector2 d = new Vector2(c.X, c.Z) - plane.Point;
                if (MathF.Abs(Vector2.Dot(d, plane.Normal)) >= GridCell * 0.5f || c.Y < plane.Low || c.Y > plane.High
                    || MathF.Abs(Vector2.Dot(d, along)) > plane.Reach || (conduit[i] <= 0f && Trilinear(open, c) <= 0f))
                {
                    continue;
                }

                float t = Vector2.Dot(d, along);
                least = MathF.Min(least, t);
                most = MathF.Max(most, t);
                low = MathF.Min(low, c.Y);
                high = MathF.Max(high, c.Y);
                count++;
            }

            if (count > 0)
            {
                touched.Add((plane, least, most, low, high, count));
            }
        }

        foreach ((JointPlane plane, float least, float most, float low, float high, int _) in touched.OrderByDescending(t => t.Touched).Take(MostRifts))
        {
            Vector2 along = new(-plane.Normal.Y, plane.Normal.X);
            float from = MathF.Max(-plane.Reach, least - RiftReach), to = MathF.Min(plane.Reach, most + RiftReach);
            float bottom = MathF.Max(plane.Low, low - 1f), top = MathF.Min(plane.High, MathF.Max(high, table) + RiftRise);
            Vector2 mid2 = plane.Point + (along * ((from + to) * 0.5f));
            Vector3 middle = new(mid2.X, (bottom + top) * 0.5f, mid2.Y);
            rifts.Add(new Vector3(mid2.X, bottom + 1f, mid2.Y));
            float halfLength = (to - from) * 0.5f;
            ForBox(middle, new Vector3(halfLength + 2f, ((top - bottom) * 0.5f) + 2f, halfLength + 2f), (index, p) =>
            {
                Vector2 d = new Vector2(p.X, p.Z) - plane.Point;
                float across = MathF.Abs(Vector2.Dot(d, plane.Normal));
                float t = Vector2.Dot(d, along) - ((from + to) * 0.5f);
                float half = RiftNarrowest + ((RiftWidest - RiftNarrowest) * NaturalCavern.Noise(new Vector3(t * 0.15f, p.Y * 0.2f, 0f), seed ^ 0xC01UL));
                float roof = top - (RiftJag * NaturalCavern.Noise(new Vector3(t * 0.3f, 0f, 0f), seed ^ 0xC02UL));
                float shape = MathF.Min(MathF.Min(half - across, halfLength - MathF.Abs(t)), MathF.Min(p.Y - bottom, roof - p.Y));
                open[index] = NaturalCavern.SmoothMax(open[index], shape, RiftEdge);
            });
        }
    }

    /// <summary>Flood mazes: grids of narrow fissures at conduits near the era's water table, away from where the player comes in.</summary>
    private static void FloodMazes(float[] open, List<(Vector3 At, float Share, int Era)> passages, Vector3 arrival, float table, ulong seed,
        DungeonRandom random, List<Vector3> mazes)
    {
        Vector3[] near = [.. passages.Select(p => p.At).Where(c => MathF.Abs(c.Y - table) < MazeNearTable && Inside(c, MazeHalf) && Vector3.Distance(c, arrival) > MazeHalf * 2f)];
        for (int i = 0; i < MostMazes; i++)
        {
            Vector3 centre = near.Length > 0 ? near[(int)(random.Next() % (ulong)near.Length)]
                : new Vector3(Between(random, MazeHalf + 4f, (GridX * GridCell) - MazeHalf - 4f), table, Between(random, MazeHalf + 4f, (GridZ * GridCell) - MazeHalf - 4f));
            if (mazes.Any(m => Vector3.Distance(m, centre) < MazeHalf * 2f))
            {
                continue;
            }

            mazes.Add(centre);
            Maze(open, centre, Between(random, 0f, MathF.PI), seed ^ (ulong)(i * 0x9E37));
        }
    }

    /// <summary>
    /// Hypogene spongework: water rising from below dissolved a cluster of joined bubbles low in the rock, domed cupolas
    /// rising from its top, and a riser carried it up to the rest of the cave. Returns the cluster's centre.
    /// </summary>
    private static Vector3 Hypogene(float[] open, ulong seed, DungeonRandom random)
    {
        float margin = SpongeRadii.X + RindMetres + 2f;
        Vector3 centre = new(Between(random, margin, (GridX * GridCell) - margin), Between(random, SpongeLowest, SpongeHighest), Between(random, margin, (GridZ * GridCell) - margin));
        ForBox(centre, SpongeRadii + new Vector3(2f), (index, p) =>
        {
            float f1 = Worley(p / SpongeCell, seed ^ 0xD01UL, out float which);
            float bubble = SmallestBubble + ((LargestBubble - SmallestBubble) * which) - (f1 * SpongeCell);
            float inside = (1f - ((p - centre) / SpongeRadii).Length()) * SpongeRadii.Y;
            open[index] = NaturalCavern.SmoothMax(open[index], MathF.Min(bubble, inside), 0.5f);
        });
        for (int i = 0; i < Cupolas; i++)
        {
            Vector3 dome = centre + new Vector3(Between(random, -SpongeRadii.X * 0.6f, SpongeRadii.X * 0.6f), SpongeRadii.Y * 0.7f, Between(random, -SpongeRadii.Z * 0.6f, SpongeRadii.Z * 0.6f));
            ForBox(dome, CupolaRadii + Vector3.One, (index, p) =>
                open[index] = NaturalCavern.SmoothMax(open[index], (1f - ((p - dome) / CupolaRadii).Length()) * CupolaRadii.X, 0.6f));
        }

        // The riser: up from the cluster to the nearest open space above it.
        Vector3 top = centre + new Vector3(0f, SpongeRadii.Y, 0f);
        float reach = top.Y;
        while (reach < (GridY * GridCell) - RindMetres - 2f && Trilinear(open, new Vector3(top.X, reach, top.Z)) <= 0f)
        {
            reach += FieldStep;
        }

        StampCapsule(open, top, new Vector3(top.X, reach + 1f, top.Z), NarrowestRadius * 1.6f, 1f, Vector3.One, 0f);
        return centre;
    }

    /// <summary>Sediment: the floors of passages older than this era filled flat, deeper in some places than others.</summary>
    private static void Infill(float[] open, byte[] formed, byte era, ulong seed)
    {
        Parallel.For(0, FieldZ, z =>
        {
            for (int x = 0; x < FieldX; x++)
            {
                for (int y = 1; y < FieldY; y++)
                {
                    int index = FieldIndex(x, y, z);
                    // A floor: an older open sample on rock.
                    if (open[index] <= 0f || open[FieldIndex(x, y - 1, z)] > 0f || formed[index] >= era)
                    {
                        continue;
                    }

                    Vector3 p = FieldCentre(index);
                    // Sediment fills at most a share of the opening above the floor, so small voids keep their shape.
                    int span = 0;
                    while (y + span < FieldY && open[FieldIndex(x, y + span, z)] > 0f)
                    {
                        span++;
                    }

                    float depth = MathF.Min(InfillShareOfSpan * span * FieldStep,
                        LeastInfill + ((MostInfill - LeastInfill) * NaturalCavern.Noise(new Vector3(p.X, 0f, p.Z) * InfillFrequency, seed ^ 0xE01UL)));
                    float surface = p.Y - (FieldStep * 0.5f) + depth;
                    for (int up = y; up < FieldY && FieldCentre(FieldIndex(x, up, z)).Y < surface + 1f; up++)
                    {
                        int above = FieldIndex(x, up, z);
                        if (open[above] <= 0f && up > y)
                        {
                            break;
                        }

                        open[above] = MathF.Min(open[above], FieldCentre(above).Y - surface);
                    }
                }
            }
        });
    }

    /// <summary>
    /// Great collapses: the largest older passages' ceilings fell back to the beds above, leaving vast rooms with boulder
    /// slopes piled toward one side.
    /// </summary>
    private static void Collapses(float[] open, float[] beds, List<(Vector3 At, float Share, int Era)> passages, int era, DungeonRandom random, List<Vector3> chambers)
    {
        List<Vector3> fallen = [];
        foreach ((Vector3 at, float share, int _) in passages.Where(p => p.Era < era && Inside(p.At, CollapseMost * ChamberWiden)).OrderByDescending(p => p.Share))
        {
            if (fallen.Count >= MostCollapses)
            {
                break;
            }

            if (fallen.Any(f => Vector3.Distance(f, at) < CollapseApart))
            {
                continue;
            }

            fallen.Add(at);
            chambers.Add(at);
            float radius = Between(random, CollapseLeast, CollapseMost) * (0.7f + (0.3f * share));
            Chamber(open, at, radius, beds);
            // The boulder slope: blocks thrown toward one side, each landing on what fell before it.
            float side = Between(random, 0f, MathF.Tau);
            int boulders = FewestBoulders + (int)(random.Next() % (ulong)(MostBoulders - FewestBoulders + 1));
            for (int i = 0; i < boulders; i++)
            {
                float toward = Chance(random, BoulderSlopeShare) ? side + Between(random, -0.6f, 0.6f) : Between(random, 0f, MathF.Tau);
                Vector3 drop = at + new Vector3(MathF.Cos(toward), 0f, MathF.Sin(toward)) * Between(random, 0f, radius * ChamberWiden * 0.8f) + new Vector3(0f, radius, 0f);
                Block(open, drop, random, LargestBoulder);
            }
        }
    }

    /// <summary>How readily each grid cell dissolves: the host rock, more along bed partings and joints. Also the joints and bed heights.</summary>
    private static float[] Rock(DungeonRandom random, ulong seed, out bool[] joint, out bool[] parting, out float[] beds, out JointPlane[] planes)
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
        planes = [.. joints.Select(j => new JointPlane(j.Point, j.Normal, j.Low, j.High, j.Reach))];
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
    private static void Conduit(float[] open, Vector3 from, Vector3 to, float radius, float radiusTo, float share, float waterTable, bool alongParting)
    {
        float taper = radiusTo / radius;
        Vector3 mid = (from + to) * 0.5f;
        Vector3 run = to - from;
        bool vadose = mid.Y > waterTable;
        bool falls = MathF.Abs(run.Y) > 0.7f * run.Length();
        if (vadose && falls)
        {
            StampCapsule(open, from, to, radius * ShaftWiden, taper, Vector3.One, 0f);
        }
        else if (vadose)
        {
            // A canyon: the falling water cut down below its first tube, deeper the more of it there was.
            float stretch = 1f + (CanyonStretch * share);
            StampCapsule(open, from, to, radius * 0.75f, taper, new Vector3(1f, stretch, 1f), radius * 0.75f * (stretch - 1f));
        }
        else
        {
            // Along a parting the tube spreads into a wide, low bedding-plane passage.
            Vector3 scale = alongParting ? new Vector3(PartingWiden, PartingFlatten, PartingWiden) : new Vector3(TubeWiden, TubeFlatten, TubeWiden);
            StampCapsule(open, from, to, radius, taper, scale, 0f, sediment: true);
        }
    }

    /// <summary>How much a conduit swells or pinches at a place: the same for every conduit meeting there, so they join.</summary>
    private static float Swell(Vector3 at, ulong seed) =>
        1f + (WidthRoll * NaturalCavern.Signed(NaturalCavern.Noise(at * WidthFrequency, seed ^ 0x701UL)));

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

    /// <summary>
    /// Opens a bedding room: a broad, low space along a flooded parting, its outline irregular, its floor flat with
    /// sediment, and pillars left standing where the rock did not dissolve.
    /// </summary>
    private static void BeddingRoom(float[] open, Vector3 centre, float radius, float halfHeight, ulong seed)
    {
        ForBox(centre, new Vector3(radius * 1.4f, halfHeight + 1f, radius * 1.4f), (index, p) =>
        {
            Vector2 d = new(p.X - centre.X, p.Z - centre.Z);
            float outline = radius * (1f + (0.35f * NaturalCavern.Signed(NaturalCavern.Noise(new Vector3(p.X, 0f, p.Z) * 0.12f, seed ^ 0x801UL))));
            float shape = NaturalCavern.SmoothMin(outline - d.Length(), halfHeight - MathF.Abs(p.Y - centre.Y), 0.5f);
            float pillar = NaturalCavern.Noise(new Vector3(p.X, 0f, p.Z) * PillarFrequency, seed ^ 0x802UL);
            shape = MathF.Min(shape, (PillarShare - pillar) * 6f);
            open[index] = NaturalCavern.SmoothMax(open[index], shape, ShapeBlend);
        });
    }

    /// <summary>
    /// Opens a joint maze: two crossing sets of close joints, turned to the rock's grain, dissolved into narrow fissures
    /// along most of their segments, wider at the foot where water ran than at the top.
    /// </summary>
    private static void Maze(float[] open, Vector3 centre, float angle, ulong seed)
    {
        Vector2 u = new(MathF.Cos(angle), MathF.Sin(angle)), v = new(-u.Y, u.X);
        float floor = centre.Y - 1f;
        ForBox(centre + new Vector3(0f, MazeTop * 0.5f, 0f), new Vector3(MazeHalf * 1.5f, (MazeTop * 0.5f) + 2f, MazeHalf * 1.5f), (index, p) =>
        {
            Vector2 d = new(p.X - centre.X, p.Z - centre.Z);
            float a = Vector2.Dot(d, u) / MazeSpacing, b = Vector2.Dot(d, v) / MazeSpacing;
            float rise = Math.Clamp((p.Y - floor) / MazeTop, 0f, 1f);
            float half = (MazeFissureFoot + ((MazeFissureTop - MazeFissureFoot) * rise)) / MazeSpacing;
            // Half a spacing is as far as a sample can be from a joint: with neither segment dissolved, the fissure is shut.
            float best = 0.5f;
            // A joint along a (fixed a) and one along b (fixed b); each segment between crossings dissolved or not by its hash.
            int ka = (int)MathF.Round(a), kb = (int)MathF.Round(b);
            if (NaturalCavern.Hash(seed ^ 0x901UL, ka, (int)MathF.Floor(b), 0) < MazeOpenShare)
            {
                best = MathF.Min(best, MathF.Abs(a - ka));
            }

            if (NaturalCavern.Hash(seed ^ 0x902UL, kb, (int)MathF.Floor(a), 1) < MazeOpenShare)
            {
                best = MathF.Min(best, MathF.Abs(b - kb));
            }

            float shape = (half - best) * MazeSpacing;
            shape = MathF.Min(shape, MathF.Min(p.Y - floor, floor + MazeTop - p.Y));
            shape = MathF.Min(shape, MazeHalf - MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y)));
            open[index] = NaturalCavern.SmoothMax(open[index], shape, 0.3f);
        });
    }

    /// <summary>
    /// Grows formations where water drips: at open joints under a ceiling, above the water table, a stalactite hanging a
    /// share of the gap and often a stalagmite under it; in a low gap a column. Returns how many were grown.
    /// </summary>
    private static int Drip(float[] open, bool[] joint, byte[] formed, int eras, float finalTable, DungeonRandom random)
    {
        List<Vector3> grown = [];
        int count = 0;
        for (int i = 0; i < joint.Length && grown.Count < MostFormations; i++)
        {
            Vector3 c = GridCentre(i);
            // Older, drier passages are more decorated: the chance grows with the eras since the passage was opened.
            int sample = FieldIndex(Math.Clamp((int)(c.X / FieldStep), 0, FieldX - 1), Math.Clamp((int)(c.Y / FieldStep), 0, FieldY - 1), Math.Clamp((int)(c.Z / FieldStep), 0, FieldZ - 1));
            float dry = formed[sample] == Unformed ? 0f : Math.Clamp((eras - 1 - formed[sample]) / (float)DecoratedAfterEras, 0f, 1f);
            if (!joint[i] || c.Y <= finalTable || Trilinear(open, c) <= 0f || !Chance(random, FormationChance * (YoungDecoration + ((1f - YoungDecoration) * dry))))
            {
                continue;
            }

            Vector3 at = c + new Vector3(Between(random, -0.8f, 0.8f), 0f, Between(random, -0.8f, 0.8f));
            float? ceiling = Reach(open, at, 1f), floor = Reach(open, at, -1f);
            if (ceiling is not float top || floor is not float foot || top - foot < LeastGap || grown.Any(g => Vector2.Distance(new(g.X, g.Z), new(at.X, at.Z)) < FormationApart))
            {
                continue;
            }

            grown.Add(at);
            float gap = top - foot;
            if (gap < ColumnGap && Chance(random, ColumnChance))
            {
                Formation(open, at, foot - 0.4f, top + 0.4f, Between(random, 0.35f, 0.6f), Between(random, 0.9f, 1.5f), column: true);
                count++;
                continue;
            }

            float hang = gap * Between(random, 0.15f, 0.45f);
            Formation(open, at, top - hang, top + 0.4f, 0f, Between(random, 0.25f, 0.5f) + (hang * 0.12f), column: false);
            count++;
            if (Chance(random, StalagmiteChance))
            {
                float rise = MathF.Min(gap - hang - 0.6f, gap * Between(random, 0.1f, 0.35f));
                if (rise > 0.3f)
                {
                    Formation(open, at, foot - 0.4f, foot + rise, Between(random, 0.4f, 0.8f) + (rise * 0.15f), 0f, column: false);
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>How far the rock is straight up (1) or down (-1) from an open place, or null beyond reach.</summary>
    private static float? Reach(float[] open, Vector3 from, float way)
    {
        for (float d = 0f; d < LongestReach; d += FieldStep * 0.5f)
        {
            if (Trilinear(open, from + new Vector3(0f, way * d, 0f)) <= 0f)
            {
                return from.Y + (way * d);
            }
        }

        return null;
    }

    /// <summary>
    /// Grows one formation between two heights: its radius goes from <paramref name="lowRadius"/> at the bottom to
    /// <paramref name="highRadius"/> at the top (a stalactite narrows to nothing below, a stalagmite above); a column is
    /// wide at both ends and waisted where its two halves met. Its outline wobbles a little, like flowstone.
    /// </summary>
    private static void Formation(float[] open, Vector3 at, float low, float high, float lowRadius, float highRadius, bool column)
    {
        float widest = MathF.Max(lowRadius, highRadius) + 0.5f;
        ForBox(new Vector3(at.X, (low + high) * 0.5f, at.Z), new Vector3(widest + 0.5f, ((high - low) * 0.5f) + 0.5f, widest + 0.5f), (index, p) =>
        {
            float t = Math.Clamp((p.Y - low) / MathF.Max(0.1f, high - low), 0f, 1f);
            float radius = column
                ? lowRadius + ((highRadius - lowRadius) * MathF.Pow(MathF.Abs((2f * t) - 1f), 1.4f))
                : lowRadius + ((highRadius - lowRadius) * MathF.Pow(t, lowRadius > highRadius ? 0.7f : 1.6f));
            float angle = MathF.Atan2(p.Z - at.Z, p.X - at.X);
            radius *= 1f + (FormationWobble * MathF.Sin((angle * 5f) + (p.Y * 1.7f)));
            float inside = radius - Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(at.X, at.Z));
            inside = MathF.Min(inside, MathF.Min(p.Y - low, high - p.Y) + 0.2f);
            open[index] = NaturalCavern.SmoothMin(open[index], -inside, FormationBlend);
        });
    }

    /// <summary>Drops fallen blocks on a chamber's floor: rounded boxes of rock, turned and sized at random, lying where they landed.</summary>
    private static void Breakdown(float[] open, Vector3 chamber, DungeonRandom random)
    {
        int blocks = FewestBlocks + (int)(random.Next() % (ulong)(MostBlocks - FewestBlocks + 1));
        for (int i = 0; i < blocks; i++)
        {
            float angle = Between(random, 0f, MathF.Tau), reach = Between(random, 0f, WidestRadius * ChamberWiden * 0.8f);
            Block(open, chamber + new Vector3(MathF.Cos(angle) * reach, 0f, MathF.Sin(angle) * reach), random, LargestBlock);
        }
    }

    /// <summary>One fallen block: a rounded box of rock, turned and sized at random, dropped from a place onto whatever is below it.</summary>
    private static void Block(float[] open, Vector3 at, DungeonRandom random, float largest)
    {
        if (Trilinear(open, at) <= 0f)
        {
            return;
        }

        // Fall to the floor: the first rock below.
        float y = at.Y;
        while (y > 1f && Trilinear(open, new Vector3(at.X, y, at.Z)) > 0f)
        {
            y -= FieldStep;
        }

        Vector3 half = new(Between(random, SmallestBlock, largest), Between(random, SmallestBlock * 0.7f, largest * 0.6f), Between(random, SmallestBlock, largest));
        Vector3 centre = new(at.X, y + (half.Y * 0.7f), at.Z);
        float turn = Between(random, 0f, MathF.PI), cos = MathF.Cos(turn), sin = MathF.Sin(turn);
        float tilt = Between(random, -0.25f, 0.25f);
        ForBox(centre, new Vector3(MathF.Max(half.X, half.Z) * 1.5f, half.Y * 1.6f, MathF.Max(half.X, half.Z) * 1.5f), (index, p) =>
        {
            Vector3 q = p - centre;
            q = new Vector3((q.X * cos) - (q.Z * sin), q.Y + (tilt * q.X), (q.X * sin) + (q.Z * cos));
            Vector3 outside = Vector3.Max(Vector3.Abs(q) - half, Vector3.Zero);
            float inside = MathF.Min(MathF.Max(MathF.Abs(q.X) - half.X, MathF.Max(MathF.Abs(q.Y) - half.Y, MathF.Abs(q.Z) - half.Z)), 0f);
            float distance = outside.Length() + inside - BlockRounding;
            open[index] = NaturalCavern.SmoothMin(open[index], distance, 0.25f);
        });
    }

    /// <summary>
    /// Opens a capsule from a to b, its radius going from <paramref name="radius"/> to <paramref name="taper"/> times it, scaled
    /// per axis, its middle offset downward; with sediment, its lower part filled flat.
    /// </summary>
    private static void StampCapsule(float[] open, Vector3 a, Vector3 b, float radius, float taper, Vector3 scale, float down, bool sediment = false)
    {
        Vector3 offset = new(0f, -down, 0f);
        Vector3 reach = (scale * radius * MathF.Max(1f, taper)) + new Vector3(0f, down, 0f) + Vector3.One;
        Vector3 low = Vector3.Min(a, b) - reach, high = Vector3.Max(a, b) + reach;
        float least = MathF.Min(scale.X, MathF.Min(scale.Y, scale.Z));
        ForBox((low + high) * 0.5f, (high - low) * 0.5f, (index, p) =>
        {
            Vector3 q = p - offset;
            Vector3 ab = (b - a) / scale, aq = (q - a) / scale;
            float t = Math.Clamp(Vector3.Dot(aq, ab) / MathF.Max(1e-4f, ab.LengthSquared()), 0f, 1f);
            float here = radius * (1f + ((taper - 1f) * t));
            float shape = (here - (aq - (ab * t)).Length()) * least;
            if (sediment)
            {
                float axisY = a.Y + ((b.Y - a.Y) * t);
                shape = NaturalCavern.SmoothMin(shape, p.Y - (axisY - (here * scale.Y * (1f - SedimentFill))), 0.4f);
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
    private static void Roughen(float[] open, ulong seed, float[] beds, float finalTable)
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
                    bool vadose = p.Y > finalTable;
                    float grain = vadose ? GrainAmplitude * VadoseGrain : GrainAmplitude;
                    float rough = (SwellAmplitude * NaturalCavern.Signed(NaturalCavern.Noise(p * SwellFrequency, seed ^ 0x601UL)))
                        + (grain * NaturalCavern.Signed(NaturalCavern.Noise(p * GrainFrequency, seed ^ 0x602UL)));
                    if (vadose)
                    {
                        // Ridged noise: sharp crests and creases rather than swells.
                        float ridge = 1f - MathF.Abs(NaturalCavern.Signed(NaturalCavern.Noise(p * JagFrequency, seed ^ 0x603UL)));
                        rough += JagAmplitude * ((ridge * 2f) - 1f);
                        float bedY = p.Y - (BedDip.X * p.X) - (BedDip.Y * p.Z);
                        float nearest = float.MaxValue;
                        foreach (float bed in beds)
                        {
                            nearest = MathF.Min(nearest, MathF.Abs(bedY - bed));
                        }

                        rough += NotchDepth * MathF.Max(0f, 1f - (nearest / NotchHalfHeight));
                    }

                    open[index] = Math.Clamp(open[index] + rough, -FieldLimit, FieldLimit);
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

    /// <summary>A field on the 2 m structural grid, read anywhere (trilinear between cell centres).</summary>
    private static float GridTrilinear(float[] grid, Vector3 p)
    {
        float fx = Math.Clamp((p.X / GridCell) - 0.5f, 0f, GridX - 1.001f);
        float fy = Math.Clamp((p.Y / GridCell) - 0.5f, 0f, GridY - 1.001f);
        float fz = Math.Clamp((p.Z / GridCell) - 0.5f, 0f, GridZ - 1.001f);
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
        float L(int dx, int dy, int dz) => grid[GridIndex(x0 + dx, y0 + dy, z0 + dz)];
        static float Lerp(float a, float b, float t) => a + ((b - a) * t);
        return Lerp(Lerp(Lerp(L(0, 0, 0), L(1, 0, 0), tx), Lerp(L(0, 1, 0), L(1, 1, 0), tx), ty),
            Lerp(Lerp(L(0, 0, 1), L(1, 0, 1), tx), Lerp(L(0, 1, 1), L(1, 1, 1), tx), ty), tz);
    }

    /// <summary>
    /// Cellular (Worley) noise: the distance from a point to the nearest of one jittered feature point per unit cell, and
    /// that nearest point's own hash in [0, 1) (so a share of the cells can be picked out).
    /// </summary>
    private static float Worley(Vector3 p, ulong seed, out float which)
    {
        int cx = (int)MathF.Floor(p.X), cy = (int)MathF.Floor(p.Y), cz = (int)MathF.Floor(p.Z);
        float best = float.MaxValue;
        which = 0f;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, y = cy + dy, z = cz + dz;
                    Vector3 feature = new(x + NaturalCavern.Hash(seed, x, y, z), y + NaturalCavern.Hash(seed ^ 0x11UL, x, y, z), z + NaturalCavern.Hash(seed ^ 0x22UL, x, y, z));
                    float distance = Vector3.Distance(p, feature);
                    if (distance < best)
                    {
                        best = distance;
                        which = NaturalCavern.Hash(seed ^ 0x33UL, x, y, z);
                    }
                }

        return best;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
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

    /// <summary>Whether a feature this far across fits at a place without reaching the rind at the space's sides.</summary>
    private static bool Inside(Vector3 at, float reach)
    {
        float margin = reach + RindMetres + GridCell;
        return at.X > margin && at.Z > margin && at.X < (GridX * GridCell) - margin && at.Z < (GridZ * GridCell) - margin;
    }

    private static bool Chance(DungeonRandom random, float chance) => Between(random, 0f, 1f) < chance;

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
