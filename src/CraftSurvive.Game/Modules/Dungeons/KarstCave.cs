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
    /// <summary>
    /// The cave's box in chunks (#10304 size prototype): 6 x 4 x 6 by default; the size study may set larger ones, for the
    /// next caves generated.
    /// </summary>
    internal static int ChunksX = DefaultChunksX, ChunksY = DefaultChunksY, ChunksZ = DefaultChunksZ;

    internal const int DefaultChunksX = 6, DefaultChunksY = 4, DefaultChunksZ = 6;

    /// <summary>
    /// A count tuned to the default box, scaled with the box's floor area (#10304 size prototype), so a larger box holds
    /// proportionally more cave rather than the same features spread thinner.
    /// </summary>
    private static int Area(int count) => Math.Max(count == 0 ? 0 : 1, (int)Math.Round(count * ChunksX * ChunksZ / (double)(DefaultChunksX * DefaultChunksZ)));

    /// <summary>A height tuned to the default box's top, kept the same distance below a taller box's top.</summary>
    private static float FromTop(float height) => height + ((ChunksY - DefaultChunksY) * 8f);

    // The structural grid water runs through, and the finer field the cave is shaped in.
    private const float GridCell = 2f;
    private static int GridX => ChunksX * 16 / 2;

    private static int GridY => ChunksY * 16 / 2;

    private static int GridZ => ChunksZ * 16 / 2;
    internal const float FieldStep = 0.5f;
    internal static int FieldX => (int)(ChunksX * 16 / FieldStep);

    internal static int FieldY => (int)(ChunksY * 16 / FieldStep);

    internal static int FieldZ => (int)(ChunksZ * 16 / FieldStep);
    private const float FieldLimit = 2f;

    // The rock: bed spacing, how much more readily partings and joints dissolve, and the beds' dip.
    private const float BedSpacingLeast = 3.5f, BedSpacingMost = 7f, PartingSolubility = 3f, JointSolubility = 2.2f;
    private static int Joints => Area(7);
    private const float JointLongest = 40f;
    internal static readonly Vector2 BedDip = new(0.05f, 0.025f);
    private const float SolubilityRoll = 0.3f;

    // The water: a cave lives through a few eras drawn from the kinds below, the water table falling from era to era as
    // the valley cuts down; below where it stands now the cave is still flooded.
    private const float HighestTable = 46f, LowestTable = 15f, DrainedBelowTable = 6f;
    private const int FewestEras = 3, MostEras = 5;
    private static int Sinks => Area(6);
    private const int RoundsPerStage = 10;
    /// <summary>The share of the sinks feeding any one era: sinks are captured and abandoned between eras.</summary>
    private const float SinkActiveChance = 0.7f;
    private static int FewestActiveSinks => Area(2);
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
    private static int FewestBlocks => Area(6);
    private static int MostBlocks => Area(15);
    private const float SmallestBlock = 0.5f, LargestBlock = 1.9f, BlockRounding = 0.25f, CollapseChance = 0.3f, CollapseFlow = 0.3f;

    // Bedding rooms: broad, low rooms spread along a flooded parting, held up by pillars of what did not dissolve.
    private const float BeddingRoomFlow = 0.15f, BeddingRoomChance = 0.5f, BeddingRoomLeast = 6f, BeddingRoomMost = 12f;
    private const float BeddingRoomLow = 0.8f, BeddingRoomHigh = 1.25f, PillarFrequency = 0.3f, PillarShare = 0.62f, BeddingRoomApart = 18f;

    // A joint maze: a block of rock whose close joints all dissolved into a grid of narrow, tall fissures.
    private const float MazeHalf = 12f, MazeSpacing = 3.2f, MazeTop = 4.5f, MazeFissureFoot = 0.55f, MazeFissureTop = 0.32f, MazeOpenShare = 0.72f;

    // Formations where water drips: from open joints under a ceiling above the water table, a stalactite hangs and often a
    // stalagmite rises under it; in a low gap the two have met in a column.
    private const float FormationChance = 0.4f, StalagmiteChance = 0.65f, ColumnGap = 4f, ColumnChance = 0.35f, FormationApart = 1.6f;
    private static int MostFormations => Area(90);
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
    private static int MostFeatureLights => Area(6);

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

        /// <summary>
        /// A chasm, rare: a great pit at the middle of the system, open to the sky and falling past the bottom of the space,
        /// which cuts through what the water opened before it - older passages end at windows in its walls or cross it on
        /// natural bridges, and ledges step down its walls where the harder beds stand out (owner, 2026-10-20).
        /// </summary>
        Chasm,

        /// <summary>
        /// A great hall, rare: a long age of slow water under a thick cap opened one vast vaulted room, its floor heaped and
        /// terraced, its roof held up by remnant pillars - big enough to have weather of its own light (owner, 2026-10-19:
        /// a rare larger cavern lit by large, bright, distant sources, like an underground sky).
        /// </summary>
        GreatHall,

        /// <summary>A collapse through to the surface: a great shaft open to the sky, its floor a boulder slope under daylight.</summary>
        Tiankeng,
    }

    /// <summary>The eras that carry water from the sinks to a spring; a cave always has one.</summary>
    private static bool Flows(Era era) => era is Era.Phreatic or Era.VadoseCanyons;

    // Keyholes: the slot a later stream cuts below an older tube, as a share of the tube's radius, and how deep.
    private const float KeyholeWidth = 0.4f, KeyholeDepth = 2.5f, KeyholeDepthPerFlow = 3f;

    // Rifts: how many, how far past the touched part of a joint they run, how far they rise, their half-width and jag.
    private static int MostRifts => Area(4);
    private const float RiftReach = 6f, RiftRise = 7f, RiftNarrowest = 0.35f, RiftWidest = 0.7f, RiftJag = 1.5f, RiftEdge = 0.15f;

    // Hypogene: the spongework's extent, bubble spacing and radii, and the cupolas rising from it.
    private static readonly Vector3 SpongeRadii = new(13f, 6f, 13f);
    private const float SpongeCell = 3.2f, SmallestBubble = 1.7f, LargestBubble = 2.5f, SpongeLowest = 8f, SpongeHighest = 20f;
    private static int Cupolas => Area(3);
    private static readonly Vector3 CupolaRadii = new(2.5f, 6f, 2.5f);

    // Flood mazes: how many, and how near the era's water table they lie.
    private static int MostMazes => Area(2);
    private const float MazeNearTable = 6f;

    // Infill: how deep sediment settles on older floors.
    private const float LeastInfill = 0.6f, MostInfill = 2.2f, InfillFrequency = 0.08f, InfillShareOfSpan = 0.4f;

    // Great collapses: how many, their size, and the boulders piled on one side.
    private static int MostCollapses => Area(3);
    private const float CollapseLeast = 4f, CollapseMost = 6f, CollapseApart = 20f;
    private static int FewestBoulders => Area(20);
    private static int MostBoulders => Area(35);
    private const float LargestBoulder = 3f, BoulderSlopeShare = 0.7f;

    // A tiankeng: the ground surface it opens through (open air above, the plateau's level and roll), its radius and how
    // deep under the surface its floor may lie, the bell of its walls, the bands at the beds, and the boulders on its floor.
    private const float SurfaceRoll = 2.5f, SurfaceFrequency = 0.04f, SurfaceBlend = 1f;

    private static float SurfaceLevel => FromTop(57f);
    private const float TiankengLeast = 9f, TiankengMost = 14f, TiankengShallowest = 8f, TiankengDeepest = 45f;
    private const float TiankengBell = 0.25f, TiankengBand = 0.6f, TiankengWobble = 0.15f;
    private const int TiankengFewestBoulders = 40, TiankengMostBoulders = 60;
    private const float TiankengBoulder = 3.5f;
    /// <summary>How far down from the surface the plateau's ground and the tiankeng's floor are soil rather than rock.</summary>
    private const float SoilDepth = 1.5f;

    /// <summary>How far under an open surface the arrival is sought, at the foot of the first sink's shaft.</summary>
    private const float SinkDepthBelowSurface = 4f;

    /// <summary>How high above its floor centre the tiankeng's floor counts as ground (its boulder heap and soil slope).</summary>
    private const float TiankengFloorSoil = 6f;

    /// <summary>The plateau's ground height over a place, for a cave with a tiankeng.</summary>
    internal static float SurfaceAt(float x, float z, ulong seed) =>
        SurfaceLevel + (SurfaceRoll * NaturalCavern.Signed(NaturalCavern.Noise(new Vector3(x * SurfaceFrequency, 0f, z * SurfaceFrequency), seed ^ 0xF01UL)));

    /// <summary>The ground surface over a cave open to the sky: open air above the plateau's rolling ground.</summary>
    private static void LaySurface(float[] open, ulong seed) => Parallel.For(0, FieldZ, z =>
    {
        for (int y = 0; y < FieldY; y++)
            for (int x = 0; x < FieldX; x++)
            {
                int index = FieldIndex(x, y, z);
                Vector3 p = FieldCentre(index);
                open[index] = NaturalCavern.SmoothMax(open[index], p.Y - SurfaceAt(p.X, p.Z, seed), SurfaceBlend);
            }
    });

    /// <summary>
    /// The tiankeng: the ground surface is laid with open air above it, and the largest older passage at a depth the
    /// shaft can span collapses through to it - a wide shaft, its walls banded at the beds and a little belled below, its
    /// floor heaped with what fell. Returns its floor centre and radius, or null when no passage lies where one could open.
    /// </summary>
    private static (Vector3 Floor, float Radius)? Tiankeng(float[] open, float[] beds, List<(Vector3 At, float Share, int Era)> passages, ulong seed, DungeonRandom random)
    {
        LaySurface(open, seed);
        Mark("surface");
        float radius = Between(random, TiankengLeast, TiankengMost);
        foreach ((Vector3 at, float _, int _) in passages.Where(p => Inside(p.At, radius + 4f)).OrderByDescending(p => p.Share))
        {
            float surface = SurfaceAt(at.X, at.Z, seed);
            if (surface - at.Y < TiankengShallowest || surface - at.Y > TiankengDeepest)
            {
                continue;
            }

            float bottom = at.Y - 2f, top = surface + 3f;
            ForBox(new Vector3(at.X, (bottom + top) * 0.5f, at.Z), new Vector3(radius * (1f + TiankengBell) + 3f, ((top - bottom) * 0.5f) + 2f, radius * (1f + TiankengBell) + 3f), (index, p) =>
            {
                float t = Math.Clamp((p.Y - bottom) / (top - bottom), 0f, 1f);
                float angle = MathF.Atan2(p.Z - at.Z, p.X - at.X);
                float bedY = p.Y - (BedDip.X * p.X) - (BedDip.Y * p.Z);
                float nearest = beds.Select(b => MathF.Abs(bedY - b)).DefaultIfEmpty(9f).Min();
                float wall = radius * (1f + (TiankengBell * (1f - t)) + (TiankengWobble * NaturalCavern.Signed(NaturalCavern.Noise(new Vector3(angle * 2f, p.Y * 0.08f, 0f), seed ^ 0xF02UL))))
                    + (TiankengBand * MathF.Max(0f, 1f - nearest));
                float shape = MathF.Min(wall - Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(at.X, at.Z)), p.Y - bottom);
                open[index] = NaturalCavern.SmoothMax(open[index], shape, 1f);
            });
            int boulders = TiankengFewestBoulders + (int)(random.Next() % (ulong)(TiankengMostBoulders - TiankengFewestBoulders + 1));
            for (int i = 0; i < boulders; i++)
            {
                float a = Between(random, 0f, MathF.Tau), r = radius * MathF.Sqrt(Between(random, 0f, 0.8f));
                Block(open, new Vector3(at.X + (MathF.Cos(a) * r), surface - 1f, at.Z + (MathF.Sin(a) * r)), random, TiankengBoulder);
            }

            return (new Vector3(at.X, bottom, at.Z), radius);
        }

        return null;
    }

    /// <summary>What a cave is built round, if anything: a great hall or a chasm (each rare), for studies to ask for.</summary>
    internal enum Centrepiece { None, GreatHall, Chasm }

    // A chasm (owner, 2026-10-20: a very deep chasm at the middle of the system, the water's passages crossing it or making
    // their way down its sides): how often a cave has one, its radius and how far its middle wanders, how its wall wobbles
    // and is notched at the beds, how far the sinks keep from it, and how far below the space a body is lost to it.
    private const float ChasmChance = 0.08f, ChasmLeast = 11f, ChasmMost = 16f, ChasmWander = 6f, ChasmWobble = 0.12f, ChasmWobbleFrequency = 0.06f;
    private const float ChasmBand = 0.8f, ChasmBlend = 1f, SinkClearOfChasm = 6f;
    // Its ledges: at which beds (the chance each bed stands out), how far out and how thick, over how much of the round,
    // and how they taper at their ends.
    private const float LedgeChance = 0.75f, LedgeLeast = 1.2f, LedgeMost = 2.6f, LedgeThickness = 1.4f, LedgeArcLeast = 1.6f, LedgeArcMost = 3.6f, LedgeTaper = 0.35f;
    // Its natural bridges: how many, how wide and thick, how much thicker at their ends (their arch), how far apart in
    // height their ends may be, and how far round the chasm one must reach to cross it.
    private static int MostBridges => Area(3);
    private const float BridgeHalfWidth = 1.4f, BridgeThickness = 1.3f, BridgeArch = 2f, BridgeLevel = 4f, BridgeAcross = 2.1f, BridgeBelowPassage = 0.9f;

    /// <summary>A chasm: its middle across the floor and radius, the ledges on its walls (their top, reach and the arc of the round they span), and its bridges.</summary>
    internal sealed record ChasmShape(Vector2 Centre, float Radius, (float Top, float Reach, float From, float To)[] Ledges, (Vector3 From, Vector3 To)[] Bridges);

    /// <summary>
    /// The chasm: a great round pit at the middle, from the surface down past the bottom of the space, its wall wobbling and
    /// notched back at the partings. Where a harder bed stands out, a ledge runs part way round its wall, the ledges of
    /// successive beds overlapping round the chasm so a way down its sides may step from one to the next. Older passages it
    /// cut through end at windows in its walls; where two such windows face each other across it at nearly one level, the
    /// rock under the passage held as a natural bridge, arched thicker at its ends.
    /// </summary>
    private static ChasmShape Chasm(float[] open, float[] beds, List<(Vector3 At, float Share, int Era)> passages, Vector2 centre, float radius, ulong seed, DungeonRandom random)
    {
        float surface = SurfaceAt(centre.X, centre.Y, seed);
        List<(float Top, float Reach, float From, float To)> ledges = [];
        float turn = Between(random, 0f, MathF.Tau);
        foreach (float bed in beds.OrderByDescending(b => b))
        {
            float top = bed + (BedDip.X * centre.X) + (BedDip.Y * centre.Y);
            if (top > surface - 3f || top < 2f || !Chance(random, LedgeChance))
            {
                continue;
            }

            // Each ledge starts round from where the one above ended, so they overlap into a way down.
            float arc = Between(random, LedgeArcLeast, LedgeArcMost);
            float from = turn - Between(random, 0.3f, 0.9f);
            ledges.Add((top, Between(random, LedgeLeast, LedgeMost), from, from + arc));
            turn = from + arc;
        }

        Parallel.For(0, FieldZ, z =>
        {
            for (int y = 0; y < FieldY; y++)
                for (int x = 0; x < FieldX; x++)
                {
                    int index = FieldIndex(x, y, z);
                    Vector3 p = FieldCentre(index);
                    Vector2 d = new Vector2(p.X, p.Z) - centre;
                    float across = d.Length();
                    if (across > radius * 1.6f)
                    {
                        continue;
                    }

                    float angle = MathF.Atan2(d.Y, d.X);
                    float bedY = p.Y - (BedDip.X * p.X) - (BedDip.Y * p.Z);
                    float nearest = 9f;
                    foreach (float bed in beds)
                    {
                        nearest = MathF.Min(nearest, MathF.Abs(bedY - bed));
                    }

                    float wall = radius * (1f + (ChasmWobble * NaturalCavern.Signed(NaturalCavern.Noise(new Vector3(MathF.Cos(angle) * 4f, p.Y * ChasmWobbleFrequency, MathF.Sin(angle) * 4f), seed ^ 0xC401UL))))
                        + (ChasmBand * MathF.Max(0f, 1f - nearest));
                    foreach ((float top, float reach, float from, float to) in ledges)
                    {
                        if (p.Y > top || p.Y < top - LedgeThickness)
                        {
                            continue;
                        }

                        // How far into the ledge's arc this angle lies (the round wraps), tapering at both ends.
                        float into = Wrap(angle - from);
                        if (into > to - from)
                        {
                            continue;
                        }

                        float taper = MathF.Min(NaturalCavern.SmoothStep(0f, LedgeTaper, into), NaturalCavern.SmoothStep(0f, LedgeTaper, (to - from) - into));
                        // Thinner toward its lip, as rock undercut below a harder bed.
                        float undercut = (top - p.Y) / LedgeThickness;
                        wall -= reach * taper * (1f - (0.5f * undercut));
                    }

                    open[index] = Math.Clamp(NaturalCavern.SmoothMax(open[index], wall - across, ChasmBlend), -FieldLimit, FieldLimit);
                }
        });

        // Bridges: windows of older passages facing each other across the chasm at nearly one level.
        List<(Vector3 From, Vector3 To)> bridges = [];
        Vector3[] windows = [.. passages.Select(p => p.At).Where(a =>
        {
            float r = Vector2.Distance(new Vector2(a.X, a.Z), centre);
            return r > radius * 0.8f && r < radius + 4f && a.Y < surface - 4f;
        })];
        foreach (Vector3 a in windows.OrderBy(_ => random.Next()))
        {
            if (bridges.Count >= MostBridges)
            {
                break;
            }

            float aAngle = MathF.Atan2(a.Z - centre.Y, a.X - centre.X);
            Vector3? partner = windows.Where(b => MathF.Abs(b.Y - a.Y) < BridgeLevel && MathF.Abs(Wrap(MathF.Atan2(b.Z - centre.Y, b.X - centre.X) - aAngle + MathF.PI) - MathF.PI) > BridgeAcross)
                .Select(b => (Vector3?)b).FirstOrDefault();
            if (partner is not Vector3 b || bridges.Any(x => MathF.Abs(x.From.Y - a.Y) < LedgeThickness * 2f))
            {
                continue;
            }

            // Ends set into the wall beyond each window, a passage's floor below its middle.
            Vector3 from = Rim(a, centre, radius + 2.5f) - new Vector3(0f, BridgeBelowPassage, 0f);
            Vector3 to = Rim(b, centre, radius + 2.5f) - new Vector3(0f, BridgeBelowPassage, 0f);
            bridges.Add((from, to));
            Bridge(open, from, to);
        }

        return new ChasmShape(centre, radius, [.. ledges], [.. bridges]);
    }

    // Waterfalls (owner, 2026-10-20): how many streams may fall into a chasm, how much water a window's passage must still
    // carry to pour, how far apart their lips must be, and how fast the sky's light falls off below the surface.
    private static int MostWaterfalls => Area(3);
    private const float WaterfallLeastFlow = 0.05f, WaterfallsApart = 8f, SkyLightFalloff = 25f, WaterfallClear = 1.2f, LeastDrop = 4f;

    /// <summary>
    /// The streams falling into a chasm: windows of cut passages that still carry water (the last flowing era's), each
    /// pouring over the lip of its passage's floor and falling until it lands on a ledge or the chasm's foot, or on into the
    /// abyss. The wettest windows first; a chasm always has at least one, from its wettest window.
    /// </summary>
    private static Waterfall[] Waterfalls(float[] open, float[] flow, List<(Vector3 At, float Share, int Era)> passages, ChasmShape chasm, ulong seed)
    {
        float outer = (chasm.Radius * (1f + ChasmWobble)) + ChasmBand + 2f;
        List<(Waterfall Fall, float Wet)> found = [];
        foreach ((Vector3 at, float _, int _) in passages)
        {
            Vector2 radial = new Vector2(at.X, at.Z) - chasm.Centre;
            float r = radial.Length();
            if (r < chasm.Radius * 0.6f || r > outer + 4f)
            {
                continue;
            }

            Vector2 u = radial / r;
            Vector3 behind = new(chasm.Centre.X + (u.X * outer), at.Y, chasm.Centre.Y + (u.Y * outer));
            if (Trilinear(open, behind) <= 0f)
            {
                continue;
            }

            Vector3 floor = Floor(open, behind);
            float wet = GridTrilinear(flow, behind + new Vector3(0f, 0.5f, 0f));
            // The lip: inward along the passage's floor until there is nothing under it.
            Vector3? lip = null;
            for (float step = 0f; step < outer; step += 0.25f)
            {
                Vector3 p = floor + new Vector3(-u.X * step, 0.3f, -u.Y * step);
                if (Trilinear(open, p) <= 0f)
                {
                    break;
                }

                // A lip only where the water would fall clear: open air below, a little out from it, for a real drop.
                Vector3 beyond = p + new Vector3(-u.X * WaterfallClear, 0f, -u.Y * WaterfallClear);
                if (Trilinear(open, p - new Vector3(0f, 1.3f, 0f)) > 0f && Enumerable.Range(1, (int)(LeastDrop / 0.5f)).All(k => Trilinear(open, beyond - new Vector3(0f, k * 0.5f, 0f)) > 0f))
                {
                    lip = p - new Vector3(0f, 0.3f, 0f);
                    break;
                }
            }

            if (lip is not Vector3 edge || found.Any(f => Vector3.Distance(f.Fall.Lip, edge) < WaterfallsApart))
            {
                continue;
            }

            Vector3 pour = new(-u.X, 0f, -u.Y);
            float y = edge.Y - 1f;
            Vector3 falling = edge + (pour * WaterfallClear);
            while (y > FieldStep && Trilinear(open, new Vector3(falling.X, y, falling.Z)) > 0f)
            {
                y -= 0.25f;
            }

            bool lands = y > FieldStep;
            float sky = MathF.Exp(-MathF.Max(0f, SurfaceAt(edge.X, edge.Z, seed) - edge.Y) / SkyLightFalloff);
            found.Add((new Waterfall(edge, pour, edge.Y - (lands ? y : 0f), lands, Math.Clamp(wet, 0f, 1f), sky), wet));
        }

        // Wettest first; the wettest always pours, so a chasm has at least one stream.
        Waterfall[] wettest = [.. found.OrderByDescending(f => f.Wet).Where((f, i) => i == 0 || f.Wet >= WaterfallLeastFlow).Take(MostWaterfalls).Select(f => f.Fall)];
        return wettest;
    }

    // Water zones (owner, 2026-10-20): how wide a still surface must be to be a lake rather than a pool, how wide a mouth
    // must be to count (smaller is a bubble of trapped air), how much air must open above a mouth for it to open onto
    // the cave (less is an air bell), and how far apart a sump's path points are.
    private const float LakeArea = 30f, LeastMouth = 3f, PathStep = 1.5f;
    private const int OpenAirReach = 4000;

    /// <summary>
    /// A sump: the way through the water between two mouths must be at least this long and dive at least this far below
    /// the surface, and the air above the two mouths must not join within this many times that length (else it is one
    /// body of water with islands or a rock post in it).
    /// </summary>
    private const float LeastSumpLength = 6f, LeastSumpDive = 1.5f, AirDetour = 3f;

    private static readonly (int X, int Z)[] Around8 = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>
    /// The cave's flooded places (#10094): every open place below the water table where it stands now is still full of
    /// water. Each connected body is a zone: with one open surface it is still water, a lake or a pool by its size; with
    /// its surface broken into two or more mouths it is a flooded passage joining parts of the cave, which the cave holds
    /// as a mist sump. Bodies open to the abyss (draining through the bottom of the space) and ones sealed off with no
    /// mouth are left out; a mouth onto a mere bubble of air does not count.
    /// </summary>
    private static List<WaterZone> WaterZones(float[] open, float table)
    {
        bool Flooded(int x, int y, int z) => (y + 0.5f) * FieldStep < table && open[FieldIndex(x, y, z)] > 0f;
        int[] body = new int[open.Length];
        Array.Fill(body, -1);
        List<WaterZone> zones = [];
        int bodies = 0;
        int top = (int)MathF.Floor((table / FieldStep) - 0.5f);
        for (int z0 = 0; z0 < FieldZ; z0++)
            for (int y0 = 0; y0 <= top && y0 < FieldY; y0++)
                for (int x0 = 0; x0 < FieldX; x0++)
                {
                    int start = FieldIndex(x0, y0, z0);
                    if (body[start] >= 0 || !Flooded(x0, y0, z0))
                    {
                        continue;
                    }

                    // The body: every flooded sample joined to this one.
                    int id = bodies++;
                    List<int> samples = [start];
                    body[start] = id;
                    bool drains = false;
                    for (int k = 0; k < samples.Count; k++)
                    {
                        (int x, int y, int z) = FieldCell3(samples[k]);
                        drains |= y == 0;
                        foreach ((int dx, int dy, int dz) in Around6)
                        {
                            int nx = x + dx, ny = y + dy, nz = z + dz;
                            if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                            {
                                continue;
                            }

                            int n = FieldIndex(nx, ny, nz);
                            if (body[n] < 0 && Flooded(nx, ny, nz))
                            {
                                body[n] = id;
                                samples.Add(n);
                            }
                        }
                    }

                    if (drains)
                    {
                        continue;
                    }

                    // Its open surface, in mouths: the top samples with air above, joined across the surface.
                    HashSet<int> surface = [.. samples.Where(i => FieldCell3(i).Y == top && top + 1 < FieldY && open[i + FieldX] > 0f)];
                    List<List<int>> mouths = [];
                    HashSet<int> seen = [];
                    foreach (int s0 in surface)
                    {
                        if (!seen.Add(s0))
                        {
                            continue;
                        }

                        List<int> mouth = [s0];
                        for (int k = 0; k < mouth.Count; k++)
                        {
                            (int x, int _, int z) = FieldCell3(mouth[k]);
                            foreach ((int dx, int dz) in Around8)
                            {
                                int nx = x + dx, nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= FieldX || nz >= FieldZ)
                                {
                                    continue;
                                }

                                int n = FieldIndex(nx, top, nz);
                                if (surface.Contains(n) && seen.Add(n))
                                {
                                    mouth.Add(n);
                                }
                            }
                        }

                        if (mouth.Count * FieldStep * FieldStep >= LeastMouth && OpensOntoCave(open, mouth[0] + FieldX))
                        {
                            mouths.Add(mouth);
                        }
                    }

                    if (mouths.Count == 0)
                    {
                        continue;
                    }

                    mouths.Sort((a, b) => b.Count.CompareTo(a.Count));
                    HashSet<int> cells = [.. samples.Select(i => WaterZone.CellKey(FieldCentre(i)))];
                    Vector2[] columns = [.. mouths.SelectMany(m => m).Select(i => { Vector3 c = FieldCentre(i); return new Vector2(c.X, c.Z); })];
                    Vector3[] openings = [.. mouths.Select(m => Centre(m) with { Y = table })];
                    // A sump: two mouths joined only through the water, by a way that dives.
                    List<Vector3>? sump = null;
                    for (int b = 1; b < mouths.Count && sump is null; b++)
                    {
                        List<Vector3> way = SumpPath(body, id, mouths[0], mouths[b]);
                        float length = way.Zip(way.Skip(1), Vector3.Distance).Sum();
                        float dive = table - way.DefaultIfEmpty(new Vector3(0f, table, 0f)).Min(p => p.Y);
                        if (length >= LeastSumpLength && dive >= LeastSumpDive && !AirJoins(open, mouths[0][0] + FieldX, mouths[b], AirDetour * length))
                        {
                            sump = way;
                        }
                    }

                    if (sump is not null)
                    {
                        zones.Add(new WaterZone(WaterZoneKind.MistSump, table, cells, columns, openings, sump));
                    }
                    else
                    {
                        float area = mouths.Sum(m => m.Count) * FieldStep * FieldStep;
                        zones.Add(new WaterZone(area >= LakeArea ? WaterZoneKind.Lake : WaterZoneKind.Pool, table, cells, columns, openings, []));
                    }
                }

        return zones;

        Vector3 Centre(List<int> samples)
        {
            Vector3 sum = Vector3.Zero;
            foreach (int i in samples)
            {
                sum += FieldCentre(i);
            }

            return sum / samples.Count;
        }
    }

    /// <summary>Whether the air above a mouth opens onto the cave rather than being a bubble trapped under the rock.</summary>
    private static bool OpensOntoCave(float[] open, int above)
    {
        HashSet<int> reached = [above];
        Queue<int> frontier = new([above]);
        while (frontier.TryDequeue(out int i) && reached.Count < OpenAirReach)
        {
            (int x, int y, int z) = FieldCell3(i);
            foreach ((int dx, int dy, int dz) in Around6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                {
                    continue;
                }

                int n = FieldIndex(nx, ny, nz);
                if (open[n] > 0f && FieldCentre(n).Y >= FieldCentre(above).Y && reached.Add(n))
                {
                    frontier.Enqueue(n);
                }
            }
        }

        return reached.Count >= OpenAirReach;
    }

    // Perched water (#10094): how deep a dip along a route must be to hold water, how much higher than the water a
    // passage's ceiling may stand and still be drowned (a sump), and how many samples a perched body may fill before it
    // is not a trap but a lowland the water would run out of.
    private const float LeastTrap = 0.4f;
    private const int MostPerched = 40000;

    /// <summary>
    /// A dip whose water would leave less headroom than this under the passage's roof was drowned in a wetter age: a sump,
    /// held as mist (the carving keeps some headroom everywhere, so a sump is a nearly drowned dip, not a fully drowned one).
    /// </summary>
    private const float SumpHeadroom = 2.2f, LeastSumpRun = 8f;

    /// <summary>A sump is its way through: the body's water within this reach of the way; the rest of the body is still water beside it.</summary>
    private const float SumpReach = 3f;

    /// <summary>Still water held above the table no bigger than this many samples (about 1000 m3), so water stays a feature, not the cave.</summary>
    private const int MostPerchedStill = 8000;

    /// <summary>
    /// Water held above the water table in the dips of the cave's routes (#10094): where a way between parts of the cave
    /// goes down and comes up again, the dip keeps the water of an older table (the classic sump of a cave diver). Along
    /// each route the floor is read every half metre; each dip deeper than a little holds water up to the lower of its
    /// two lips, and that water fills every open place below that level joined to it - unless it reaches the water table
    /// or the bottom of the space, where it would run out. A dip whose water reaches the passage's ceiling is a sump,
    /// held as mist; a dip with air above its water is a pool or a lake.
    /// </summary>
    private static List<WaterZone> PerchedWater(float[] open, List<List<int>> routes, float table, List<WaterZone> found)
    {
        List<WaterZone> zones = [];
        bool[] taken = new bool[open.Length];
        foreach (List<int> route in routes)
        {
            Mark("perched-other");
            Vector3[] at = [.. route.Select(FieldCentre)];
            float[] floor = [.. at.Select(p => Floor(open, p).Y)];
            float[] ceiling = [.. at.Select(p => Ceiling(open, p).Y)];
            Mark("perched-scan");
            int n = at.Length;
            float[] left = new float[n], right = new float[n];
            for (int i = 0; i < n; i++) left[i] = Math.Max(i > 0 ? left[i - 1] : float.MinValue, floor[i]);
            for (int i = n - 1; i >= 0; i--) right[i] = Math.Max(i < n - 1 ? right[i + 1] : float.MinValue, floor[i]);
            for (int i = 0; i < n; i++)
            {
                float level = Math.Min(left[i], right[i]);
                if (level - floor[i] < LeastTrap)
                {
                    continue;
                }

                // The dip: the run of the route below this level.
                int a = i, b = i;
                while (a > 0 && Math.Min(left[a - 1], right[a - 1]) - floor[a - 1] > 0.05f) a--;
                while (b < n - 1 && Math.Min(left[b + 1], right[b + 1]) - floor[b + 1] > 0.05f) b++;
                i = b;
                if (level <= table)
                {
                    continue;
                }

                int lowest = Enumerable.Range(a, b - a + 1).MinBy(k => floor[k]);
                int seed = SampleAt(Floor(open, at[lowest]) + new Vector3(0f, FieldStep * 0.5f, 0f));
                Mark("perched-other");
                bool known = seed >= 0 && (taken[seed] || found.Any(z => z.Contains(FieldCentre(seed))));
                Mark("perched-known");
                if (seed < 0 || known || open[seed] <= 0f)
                {
                    continue;
                }

                Mark("perched-other");
                List<int>? body = FillBelow(open, seed, level, table);
                Mark("perched-fill");
                if (body is null)
                {
                    continue;
                }

                foreach (int k in body) taken[k] = true;
                HashSet<int> cells = [.. body.Select(k => WaterZone.CellKey(FieldCentre(k)))];
                int topRow = (int)MathF.Floor((level / FieldStep) - 0.5f);
                Vector2[] columns = [.. body.Where(k => FieldCell3(k).Y == topRow && open[k + FieldX] > 0f)
                    .Select(k => { Vector3 c = FieldCentre(k); return new Vector2(c.X, c.Z); })];
                Vector3[] mouths = [at[a] with { Y = level }, at[b] with { Y = level }];
                bool drowned = Enumerable.Range(a, b - a + 1).Min(k => ceiling[k] - level) < SumpHeadroom && Vector3.Distance(at[a], at[b]) >= LeastSumpRun;
                if (!drowned && body.Count > MostPerchedStill)
                {
                    continue;
                }

                List<Vector3> path = [];
                for (int k = a; k <= b; k++)
                {
                    if (path.Count == 0 || Vector3.Distance(path[^1], at[k]) >= PathStep) path.Add(at[k]);
                }

                float length = path.Zip(path.Skip(1), Vector3.Distance).Sum();
                if (drowned && length >= LeastSumpLength)
                {
                    // The sump: the water along its way through; what the dip's water fills beyond reach of it is still water.
                    // The path's points bucketed by SumpReach, so each sample looks only at the points in the buckets round it
                    // (#10304: testing every sample against every point grew with a large cave's water squared).
                    Dictionary<(int, int, int), List<Vector3>> near = [];
                    foreach (Vector3 q in path)
                    {
                        (int, int, int) bucket = ((int)MathF.Floor(q.X / SumpReach), (int)MathF.Floor(q.Y / SumpReach), (int)MathF.Floor(q.Z / SumpReach));
                        (near.TryGetValue(bucket, out List<Vector3>? list) ? list : near[bucket] = []).Add(q);
                    }

                    bool InReach(Vector3 c)
                    {
                        int bx = (int)MathF.Floor(c.X / SumpReach), by = (int)MathF.Floor(c.Y / SumpReach), bz = (int)MathF.Floor(c.Z / SumpReach);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dz = -1; dz <= 1; dz++)
                                {
                                    if (near.TryGetValue((bx + dx, by + dy, bz + dz), out List<Vector3>? list) && list.Any(q => Vector3.DistanceSquared(q, c) < SumpReach * SumpReach))
                                    {
                                        return true;
                                    }
                                }

                        return false;
                    }

                    List<int> sump = [.. body.Where(k => InReach(FieldCentre(k)))];
                    HashSet<int> inSump = [.. sump];
                    List<int> rest = [.. body.Where(k => !inSump.Contains(k))];
                    zones.Add(new WaterZone(WaterZoneKind.MistSump, level, Cells(sump), Columns(sump), mouths, path));
                    if (rest.Count <= MostPerchedStill && Columns(rest) is { Length: > 0 } still)
                    {
                        zones.Add(new WaterZone(still.Length * FieldStep * FieldStep >= LakeArea ? WaterZoneKind.Lake : WaterZoneKind.Pool, level, Cells(rest), still, mouths, []));
                    }
                }
                else if (columns.Length > 0)
                {
                    zones.Add(new WaterZone(columns.Length * FieldStep * FieldStep >= LakeArea ? WaterZoneKind.Lake : WaterZoneKind.Pool, level, cells, columns, mouths, []));
                }

                HashSet<int> Cells(List<int> samples) => [.. samples.Select(k => WaterZone.CellKey(FieldCentre(k)))];
                Vector2[] Columns(List<int> samples) => [.. samples.Where(k => FieldCell3(k).Y == topRow && open[k + FieldX] > 0f)
                    .Select(k => { Vector3 c = FieldCentre(k); return new Vector2(c.X, c.Z); })];
            }
        }

        return zones;
    }

    // Luminous pools (#10129): the share of still water deep in the dark that glows, the most a cave has, how far from a
    // sink (and the light about it) and how far below an open sky they must lie, and the light's height over the water.
    private const float LuminousChance = 0.45f, LuminousFromSink = 12f, LuminousBelowSky = 25f;
    private static int MostLuminous => Area(3);

    /// <summary>
    /// Marks some of the cave's still water as luminous (#10129): lakes and pools away from the entrances and the sky's
    /// light, chosen by the seed, a few at most; each gets a growth placement for its light, at the middle of its surface.
    /// </summary>
    private static void Luminous(List<WaterZone> zones, List<EcologyPlacement> ecology, Vector3[] sinks, bool skyOpen, ulong seed)
    {
        int glowing = 0;
        for (int i = 0; i < zones.Count && glowing < MostLuminous; i++)
        {
            WaterZone zone = zones[i];
            if (zone.Kind is not (WaterZoneKind.Lake or WaterZoneKind.Pool) || zone.SurfaceColumns.Count == 0)
            {
                continue;
            }

            Vector2 middle = zone.SurfaceColumns.Aggregate(Vector2.Zero, (a, c) => a + c) / zone.SurfaceColumns.Count;
            Vector3 at = new(middle.X, zone.Surface, middle.Y);
            bool dark = sinks.All(s => Vector3.Distance(s, at) > LuminousFromSink) && (!skyOpen || SurfaceAt(at.X, at.Z, seed) - at.Y > LuminousBelowSky);
            float roll = NaturalCavern.Hash(seed ^ 0x1E05UL, (int)at.X, (int)at.Y, (int)at.Z);
            if (!dark || roll > LuminousChance)
            {
                continue;
            }

            zones[i] = zone with { Luminous = true };
            float area = zone.SurfaceColumns.Count * FieldStep * FieldStep;
            ecology.Add(new EcologyPlacement(EcologyKind.LuminousPool, at, Vector3.UnitY, MathF.Min(2f, 0.6f + (MathF.Sqrt(area) * 0.1f)), (uint)(roll * uint.MaxValue)));
            glowing++;
        }
    }

    // A sinkhole forest (#10129; owner's reference: the Vietnamese and Chinese tiankeng with forests growing into them):
    // the flora of a damp, sheltered hollow - trees, understory, ground plants - each on its own jittered grid (its
    // spacing and the share of its places it takes), on the turf under an opening and on the plateau round its rim, out
    // this far past it; trees want this much clear air above them.
    private static readonly (string[] Species, float Spacing, float Share, float Least, float Most)[] ForestLayers =
    [
        (["alder", "beech", "laurel", "willow"], 5.5f, 0.6f, 0.7f, 1.1f),
        (["hazel", "holly", "bush"], 3f, 0.4f, 0.8f, 1.2f),
        (["fern", "woodland-flowers"], 1.4f, 0.55f, 0.7f, 1.3f),
    ];
    private const float ForestBeyondRim = 12f, TreeClearAbove = 5f;
    private static int MostForest => Area(400);

    /// <summary>
    /// The forest growing into a cave's openings to the sky (#10129): on every turf column under or round a tiankeng's or
    /// a chasm's opening, each layer of the hollow's flora on its own jittered grid, the turf found looking down from the
    /// plateau; trees only where the air above them is clear. The surface's voxels say what is turf.
    /// </summary>
    private static List<ForestPlacement> SinkholeForest(Result cave)
    {
        List<(Vector2 Centre, float Radius)> openings = Openings(cave);
        List<ForestPlacement> forest = [];
        for (int layer = 0; layer < ForestLayers.Length && forest.Count < MostForest; layer++)
        {
            (string[] species, float spacing, float share, float least, float most) = ForestLayers[layer];
            float span = FieldX * FieldStep;
            for (float gz = spacing * 0.5f; gz < span && forest.Count < MostForest; gz += spacing)
                for (float gx = spacing * 0.5f; gx < span && forest.Count < MostForest; gx += spacing)
                {
                    float roll = NaturalCavern.Hash(cave.Seed ^ (0xF0A1UL + (ulong)layer), (int)(gx * 4f), layer, (int)(gz * 4f));
                    if (roll > share)
                    {
                        continue;
                    }

                    float jx = NaturalCavern.Hash(cave.Seed ^ 0xF0B1UL, (int)(gx * 4f), layer, (int)(gz * 4f)) - 0.5f;
                    float jz = NaturalCavern.Hash(cave.Seed ^ 0xF0B2UL, (int)(gx * 4f), layer, (int)(gz * 4f)) - 0.5f;
                    Vector2 at = new(gx + (jx * spacing * 0.8f), gz + (jz * spacing * 0.8f));
                    if (!openings.Any(o => Vector2.Distance(at, o.Centre) < o.Radius + ForestBeyondRim))
                    {
                        continue;
                    }

                    // The turf: the first rock looking down from above the plateau, if it is turf.
                    Vector3 ground = Floor(cave.Open, new Vector3(at.X, MathF.Min(FieldY * FieldStep - 1f, SurfaceAt(at.X, at.Y, cave.Seed) + 2f), at.Y));
                    if (ground.Y <= 1f || cave.RockAt(ground - new Vector3(0f, FieldStep, 0f)) != BlockId.Grass)
                    {
                        continue;
                    }

                    if (layer == 0 && Trilinear(cave.Open, ground + new Vector3(0f, TreeClearAbove, 0f)) <= 0f)
                    {
                        continue;
                    }

                    float pick = NaturalCavern.Hash(cave.Seed ^ 0xF0C1UL, (int)(gx * 4f), layer, (int)(gz * 4f));
                    forest.Add(new ForestPlacement(species[(int)(pick * species.Length) % species.Length], (int)(pick * 977f) % 4, ground - new Vector3(0f, 0.1f, 0f),
                        roll * 360f / share, least + ((most - least) * jx + (most - least) * 0.5f)));
                }
        }

        return forest;
    }

    /// <summary>A cave's openings to the sky, in plan: a tiankeng's, a chasm's.</summary>
    private static List<(Vector2 Centre, float Radius)> Openings(Result cave)
    {
        List<(Vector2 Centre, float Radius)> openings = [];
        if (cave.Tiankeng is (Vector3 floor, float radius))
        {
            openings.Add((new Vector2(floor.X, floor.Z), radius));
        }

        if (cave.Chasm is ChasmShape chasm)
        {
            openings.Add((chasm.Centre, chasm.Radius * (1f + ChasmWobble) + ChasmBand));
        }

        return openings;
    }

    // Moss and ivy cards (#10092; #10044's painted cards): grown where the cave's moss is, more of them the wetter the
    // rock - the generator's drip and standing water are the cave's moisture volume, and light is the moss's other need
    // (Result.Lit) - and ivy down an opening's walls from its rim, over whatever rock. The surface is looked for on a
    // lattice this fine, a card's anchor at most one in each cell this wide; each kind takes its share of the anchors
    // (scaled by wetness from a dry share to 1), and a cave grows at most so many. A wall takes no card of its own: the
    // moss rock is the wall's moss, small wall cards read as specks on it, and a curtain's straight top on a bare wall as a box.
    private const float CardStep = 0.5f, CardApart = 0.55f;

    // The underground forest's overgrowth on cave rock (#10096), where the moss grows by the light: vines draped from lips
    // (an opening's cliff always, else this share times the damp), ivy mounds at mossy walls' feet, ivy clumps on mossy
    // walls, grass tufts on mossy floors; each kind at least so far apart, and at most so many in a cave.
    private const float DrapeShare = 0.5f, MoundShare = 0.35f, ClumpShare = 0.25f, GrassShare = 0.35f, GrowthApart = 1.4f;
    private static int MostOvergrowth => Area(1500);

    // Plain mushrooms on damp, still floors anywhere in the dark (beside the glow fungi), and root tangles under the
    // surface beside the root curtains: so far apart, and at most so many.
    private const float MushroomsApart = 2f, RootTanglesApart = 4f;
    private static int MostMushrooms => Area(90);
    private static int MostRootTangles => Area(10);
    private const float MatShare = 0.7f, CushionShare = 0.6f, CurtainShare = 0.8f;
    private const float DryCardShare = 0.45f, WetCardDamp = 0.3f;
    // Ivy hangs from an opening's rim this far down its walls, and this far out past the opening's edge.
    private const float IvyBelowRim = 6f, IvyBeyondOpening = 1.5f;
    // Ivy from a rim's lip hangs in a chain of at most this many curtains, each this far below the last (less than the
    // shortest curtain's hang, so they overlap).
    private const int IvyChain = 4;
    private const float IvyLink = 1f;
    // An opening's cliff: the rock this far behind a wall is solid up to this far under the plateau, at so many heights.
    private const float IvyCliffBehind = 0.6f, IvyCliffTop = 0.8f;
    private const int IvyCliffSamples = 3;
    // A floor faces up this much, a wall is this near the horizontal; a lip has air over the rock this near above its
    // anchor and a clear drop this deep in front; a foot has a floor this near below.
    private const float CardTopRise = 0.6f, CardSideRise = 0.35f, LipAbove = 0.5f, LipDrop = 1f, FootBelow = 0.5f;
    private static int MostCards => Area(1200);
    private const float MossPatchReach = 0.6f;

    /// <summary>
    /// The moss and ivy cards on a cave's rock (#10092): on the lattice, every place just inside the open air near the
    /// rock is moved onto the surface; there, under the light the moss needs or on an opening's cliff, a mossy floor takes
    /// a mat, a mossy wall a cushion at its foot and a curtain over its lip, and a cliff's lip ivy hanging down it in a
    /// chain of curtains, whatever the rock.
    /// </summary>
    private static List<MossCard> MossCards(Result cave, List<OvergrowthPlacement> overgrowth)
    {
        List<(Vector2 Centre, float Radius)> openings = cave.SkyOpen ? Openings(cave) : [];
        float top = FieldY * FieldStep;
        List<(Vector3 Min, Vector3 Max)> boxes = [.. cave.SinkPoints.Select(s => (s - new Vector3(MossNearSink), s + new Vector3(MossNearSink)))];
        foreach ((Vector2 centre, float radius) in openings)
        {
            float reach = radius + MathF.Max(MossBeyondOpening, IvyBeyondOpening);
            // Down only as far as the light reaches the moss from the lowest of the plateau round the opening.
            float lowest = new[] { Vector2.Zero, Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY }
                .Min(d => SurfaceAt(centre.X + (d.X * reach), centre.Y + (d.Y * reach), cave.Seed));
            float floor = MathF.Max(0f, lowest - MathF.Max(MossBelowSky, IvyBelowRim) - CardStep);
            boxes.Add((new Vector3(centre.X - reach, floor, centre.Y - reach), new Vector3(centre.X + reach, top, centre.Y + reach)));
        }

        bool InIvy(Vector3 p)
        {
            float below = SurfaceAt(p.X, p.Z, cave.Seed) - p.Y;
            return below >= 0f && below < IvyBelowRim
                && openings.Any(o => Vector2.Distance(new Vector2(p.X, p.Z), o.Centre) < o.Radius + IvyBeyondOpening);
        }

        float Open(Vector3 p) => Trilinear(cave.Open, p);

        // A cliff of the opening, not a boulder on its floor: the rock behind a wall stands solid up to near the plateau.
        bool Cliff(Vector3 p, Vector3 normal)
        {
            Vector3 behind = new Vector3(-normal.X, 0f, -normal.Z) * IvyCliffBehind;
            float rise = SurfaceAt(p.X, p.Z, cave.Seed) - IvyCliffTop - p.Y;
            for (int k = 1; k <= IvyCliffSamples; k++)
            {
                if (Open(p + behind + (Vector3.UnitY * (rise * k / IvyCliffSamples))) > 0f)
                {
                    return false;
                }
            }

            return true;
        }

        // Moss thick enough for a card: the rock under the anchor and round it, this far out across the surface, is all moss,
        // so a card sits in a patch rather than alone on bare rock.
        bool MossPatch(Vector3 p, Vector3 normal)
        {
            Vector3 under = p - (normal * (FieldStep * 0.5f));
            Vector3 u = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal)), v = Vector3.Cross(normal, u);
            return cave.RockAt(under) == MossRock && cave.RockAt(under + (u * MossPatchReach)) == MossRock && cave.RockAt(under - (u * MossPatchReach)) == MossRock
                && cave.RockAt(under + (v * MossPatchReach)) == MossRock && cave.RockAt(under - (v * MossPatchReach)) == MossRock;
        }

        HashSet<(int, int, int)> taken = [];
        HashSet<(OvergrowthKind, int, int, int)> grownAt = [];
        // An overgrowth of a kind at a place, unless one of its kind already grows within GrowthApart.
        void Grow(OvergrowthKind kind, Vector3 at, Vector3 outward, Vector3 up, float pick)
        {
            if (overgrowth.Count < MostOvergrowth
                && grownAt.Add((kind, (int)MathF.Floor(at.X / GrowthApart), (int)MathF.Floor(at.Y / GrowthApart), (int)MathF.Floor(at.Z / GrowthApart))))
            {
                overgrowth.Add(new OvergrowthPlacement(kind, at, outward, 0.85f + (0.3f * pick), pick, up));
            }
        }

        List<MossCard> cards = [];
        foreach ((Vector3 min, Vector3 max) in boxes)
        {
            for (float z = MathF.Max(1f, min.Z); z < MathF.Min(FieldZ * FieldStep - 1f, max.Z) && cards.Count < MostCards; z += CardStep)
                for (float y = MathF.Max(1f, min.Y); y < MathF.Min(top - 1f, max.Y) && cards.Count < MostCards; y += CardStep)
                    for (float x = MathF.Max(1f, min.X); x < MathF.Min(FieldX * FieldStep - 1f, max.X) && cards.Count < MostCards; x += CardStep)
                    {
                        Vector3 p = new(x, y, z);
                        float o = Open(p);
                        if (o <= 0f || o > CardStep)
                        {
                            continue;
                        }

                        Vector3 g = new(Open(p + Vector3.UnitX * 0.1f) - Open(p - Vector3.UnitX * 0.1f), Open(p + Vector3.UnitY * 0.1f) - Open(p - Vector3.UnitY * 0.1f),
                            Open(p + Vector3.UnitZ * 0.1f) - Open(p - Vector3.UnitZ * 0.1f));
                        if (g.LengthSquared() < 1e-8f)
                        {
                            continue;
                        }

                        // Onto the surface the rock is drawn at (the field with its finer forms), a few steps along the normal.
                        Vector3 n = Vector3.Normalize(g), s = p;
                        for (int step = 0; step < 3; step++)
                        {
                            s -= n * cave.At(s) * 0.8f;
                        }

                        if (!taken.Add(((int)MathF.Floor(s.X / CardApart), (int)MathF.Floor(s.Y / CardApart), (int)MathF.Floor(s.Z / CardApart))))
                        {
                            continue;
                        }

                        bool ivy = openings.Count > 0 && InIvy(s) && Cliff(s, n);
                        bool mossy = MossPatch(s, n);
                        if (!ivy && !mossy)
                        {
                            continue;
                        }

                        int cx = (int)(s.X * 4f), cy = (int)(s.Y * 4f), cz = (int)(s.Z * 4f);
                        float roll = NaturalCavern.Hash(cave.Seed ^ 0xC4D1UL, cx, cy, cz), pick = NaturalCavern.Hash(cave.Seed ^ 0xC4D2UL, cx, cy, cz);
                        float damp = MathF.Max(GridTrilinear(cave.Drip, s), GridTrilinear(cave.Standing, s));
                        float wet = DryCardShare + ((1f - DryCardShare) * Math.Clamp(damp / WetCardDamp, 0f, 1f));
                        float growRoll = NaturalCavern.Hash(cave.Seed ^ 0x0F61UL, cx, cy, cz);
                        if (n.Y > CardTopRise)
                        {
                            if (mossy && growRoll < GrassShare * wet)
                            {
                                float a = 2f * MathF.PI * pick;
                                Grow(OvergrowthKind.GrassTuft, s, new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), n, pick);
                            }

                            if (mossy && roll < MatShare * wet)
                            {
                                cards.Add(new MossCard(MossCardKind.Mat, s, Vector3.Zero, n, pick));
                            }

                            continue;
                        }

                        if (MathF.Abs(n.Y) > CardSideRise)
                        {
                            continue;
                        }

                        Vector3 outward = Vector3.Normalize(new Vector3(n.X, 0f, n.Z));
                        // A lip: air over the rock behind the wall, just above, and a clear drop in front. Its curtain hangs from the edge.
                        float edge = 0f;
                        while (edge < LipAbove && Open(s - (outward * 0.3f) + (Vector3.UnitY * edge)) <= 0f)
                        {
                            edge += 0.1f;
                        }

                        bool lip = edge < LipAbove && Open(s + (outward * 0.4f) - (Vector3.UnitY * LipDrop)) > 0f;
                        bool foot = Open(s + (outward * 0.3f) - (Vector3.UnitY * FootBelow)) <= 0f;
                        if (lip && growRoll < (ivy ? 1f : DrapeShare * wet))
                        {
                            // Long vines down an opening's cliff, shorter ones from a lip in the dark.
                            Grow(ivy || growRoll < 0.3f ? OvergrowthKind.VineDrape : OvergrowthKind.VineDrapeShort, s + (Vector3.UnitY * edge), outward, Vector3.UnitY, pick);
                        }
                        else if (foot && mossy && growRoll < MoundShare * wet)
                        {
                            Grow(OvergrowthKind.IvyMound, Floor(cave.Open, s + (outward * 0.05f)) - (Vector3.UnitY * 0.08f), outward, Vector3.UnitY, pick);
                        }
                        else if ((mossy || ivy) && growRoll < ClumpShare * wet)
                        {
                            Grow(OvergrowthKind.IvyClump, s, n, Vector3.UnitY, pick);
                        }

                        if (lip && roll < (ivy ? 1f : CurtainShare * wet))
                        {
                            Vector3 lipAt = s + (Vector3.UnitY * edge);
                            cards.Add(new MossCard(MossCardKind.Curtain, lipAt, outward, n, pick));
                            // Ivy hangs on down the cliff in a chain, each curtain's ragged end over the next one's top, while
                            // the wall goes on below (rock behind, air in front).
                            for (int link = 1; ivy && link < IvyChain; link++)
                            {
                                Vector3 below = lipAt - (Vector3.UnitY * (IvyLink * link));
                                if (Open(below - (outward * 0.3f)) > 0f || Open(below + (outward * 0.4f)) <= 0f)
                                {
                                    break;
                                }

                                cards.Add(new MossCard(MossCardKind.Curtain, below, outward, n, NaturalCavern.Hash(cave.Seed ^ 0xC4D3UL, cx, cy - link, cz)));
                            }
                        }
                        else if (foot && mossy && roll < CushionShare * wet)
                        {
                            cards.Add(new MossCard(MossCardKind.Cushion, Floor(cave.Open, s + (outward * 0.35f)), outward, Vector3.UnitY, pick));
                        }
                    }
        }

        return cards;
    }

    /// <summary>The field sample a place lies in, or -1 outside the field.</summary>
    private static int SampleAt(Vector3 p)
    {
        int x = (int)(p.X / FieldStep), y = (int)(p.Y / FieldStep), z = (int)(p.Z / FieldStep);
        return x < 0 || y < 0 || z < 0 || x >= FieldX || y >= FieldY || z >= FieldZ ? -1 : FieldIndex(x, y, z);
    }

    /// <summary>
    /// Every open sample below a level joined to a seed (the water a dip holds), or null when that water would run out:
    /// reaching the water table, the bottom of the space, or more than a trap holds.
    /// </summary>
    private static List<int>? FillBelow(float[] open, int seed, float level, float table)
    {
        List<int> body = [seed];
        HashSet<int> seen = [seed];
        for (int k = 0; k < body.Count; k++)
        {
            (int x, int y, int z) = FieldCell3(body[k]);
            if (y == 0 || FieldCentre(body[k]).Y < table || body.Count > MostPerched)
            {
                return null;
            }

            foreach ((int dx, int dy, int dz) in Around6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                {
                    continue;
                }

                int i = FieldIndex(nx, ny, nz);
                if (open[i] > 0f && (ny + 0.5f) * FieldStep < level && seen.Add(i))
                {
                    body.Add(i);
                }
            }
        }

        return body;
    }

    /// <summary>Whether the air above one mouth reaches the air above another within some metres of air (above the water).</summary>
    private static bool AirJoins(float[] open, int above, List<int> other, float metres)
    {
        HashSet<int> goal = [.. other.Select(i => i + FieldX)];
        int most = (int)(metres / FieldStep);
        Dictionary<int, int> steps = new() { [above] = 0 };
        Queue<int> frontier = new([above]);
        float floor = FieldCentre(above).Y;
        while (frontier.TryDequeue(out int i))
        {
            if (goal.Contains(i))
            {
                return true;
            }

            int here = steps[i];
            if (here >= most)
            {
                continue;
            }

            (int x, int y, int z) = FieldCell3(i);
            foreach ((int dx, int dy, int dz) in Around6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                {
                    continue;
                }

                int n = FieldIndex(nx, ny, nz);
                if (open[n] > 0f && FieldCentre(n).Y >= floor && !steps.ContainsKey(n))
                {
                    steps[n] = here + 1;
                    frontier.Enqueue(n);
                }
            }
        }

        return false;
    }

    /// <summary>A way through a sump's body from one mouth to another (the shortest through its samples), every few metres.</summary>
    private static List<Vector3> SumpPath(int[] body, int zone, List<int> from, List<int> to)
    {
        Dictionary<int, int> came = [];
        Queue<int> frontier = new();
        foreach (int s in from)
        {
            came[s] = -1;
            frontier.Enqueue(s);
        }

        HashSet<int> goal = [.. to];
        int end = -1;
        while (frontier.TryDequeue(out int i))
        {
            if (goal.Contains(i))
            {
                end = i;
                break;
            }

            (int x, int y, int z) = FieldCell3(i);
            foreach ((int dx, int dy, int dz) in Around6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                {
                    continue;
                }

                int n = FieldIndex(nx, ny, nz);
                if (body[n] == zone && !came.ContainsKey(n))
                {
                    came[n] = i;
                    frontier.Enqueue(n);
                }
            }
        }

        List<Vector3> path = [];
        for (int i = end; i >= 0; i = came[i])
        {
            Vector3 p = FieldCentre(i);
            if (path.Count == 0 || Vector3.Distance(path[^1], p) >= PathStep)
            {
                path.Add(p);
            }
        }

        path.Reverse();
        return path;
    }

    /// <summary>An angle wrapped into 0 to a full turn.</summary>
    private static float Wrap(float angle) => ((angle % MathF.Tau) + MathF.Tau) % MathF.Tau;

    /// <summary>A point carried out across the floor to a distance from a middle, at its own height.</summary>
    private static Vector3 Rim(Vector3 at, Vector2 centre, float distance)
    {
        Vector2 d = new Vector2(at.X, at.Z) - centre;
        Vector2 r = centre + (Vector2.Normalize(d) * distance);
        return new Vector3(r.X, at.Y, r.Y);
    }

    /// <summary>A natural bridge: a slab of rock between two ends, its top along them, its underside arched up between thick ends.</summary>
    private static void Bridge(float[] open, Vector3 from, Vector3 to)
    {
        Vector3 span = to - from;
        Vector2 flat = new(span.X, span.Z);
        float length = flat.Length();
        Vector2 along = flat / length;
        ForBox((from + to) * 0.5f, new Vector3((MathF.Abs(span.X) * 0.5f) + 4f, (MathF.Abs(span.Y) * 0.5f) + BridgeThickness + BridgeArch + 2f, (MathF.Abs(span.Z) * 0.5f) + 4f), (index, p) =>
        {
            Vector2 q = new Vector2(p.X, p.Z) - new Vector2(from.X, from.Z);
            float t = Math.Clamp(Vector2.Dot(q, along) / length, 0f, 1f);
            float side = MathF.Abs((q.X * along.Y) - (q.Y * along.X));
            float top = from.Y + (span.Y * t);
            float bottom = top - BridgeThickness - (BridgeArch * (1f - (4f * t * (1f - t))));
            float solid = MathF.Max(side - BridgeHalfWidth, MathF.Max(p.Y - top, bottom - p.Y));
            open[index] = MathF.Min(open[index], solid);
        });
    }

    // A great hall (owner, 2026-10-19: rare, vast, lit like an underground sky): how often a cave has one, its reach
    // across the floor and height, how far its middle wanders from the space's, the floor's level, roll and terraces,
    // the walls' wobble and bands at the beds, where the vault springs from the walls, its pillars, and its boulders.
    private const float GreatHallChance = 0.1f;
    private const float HallLeast = 30f, HallMost = 38f, HallWander = 6f, HallHighest = 46f, HallLowest = 36f;
    private const float HallFloorLeast = 5f, HallFloorMost = 9f, HallFloorRoll = 2.2f, HallFloorFrequency = 0.05f, HallTerrace = 0.9f, HallTerraceShare = 0.6f;
    private const float HallWobble = 0.06f, HallWobbleFrequency = 0.07f, HallBand = 1.2f, HallSpring = 0.3f, HallBlend = 2f;
    private const int FewestPillars = 2, MostPillars = 4;
    private const float PillarLeast = 2f, PillarMost = 4.2f, PillarFlare = 0.8f, PillarFootFlare = 4f, PillarHeadFlare = 6f, PillarApart = 16f, PillarBlend = 1.5f;
    private const int HallFewestBoulders = 30, HallMostBoulders = 50;
    private const float HallBoulder = 3.2f;

    /// <summary>
    /// A great hall: an elliptical room filling much of the space, its walls standing up from a rolling, terraced floor
    /// and curving over into a vault banded at the beds, held up by a few remnant pillars flared at foot and head, with
    /// boulders heaped against its walls. Older passages that crossed its place open into it.
    /// </summary>
    internal sealed record Hall(Vector2 Centre, float RadiusX, float RadiusZ, float Turn, float Floor, float Height, (Vector2 At, float Radius)[] Pillars)
    {
        /// <summary>How far out a place lies across the floor: 0 at the middle, 1 at the foot of the walls.</summary>
        internal float Reach(float x, float z)
        {
            Vector2 d = new Vector2(x, z) - Centre;
            float cos = MathF.Cos(Turn), sin = MathF.Sin(Turn);
            Vector2 q = new(((d.X * cos) - (d.Y * sin)) / RadiusX, ((d.X * sin) + (d.Y * cos)) / RadiusZ);
            return q.Length();
        }

        /// <summary>The vault's height over a place, before its bands and wobble.</summary>
        internal float RoofAt(float reach) => Floor + (Height * (HallSpring + ((1f - HallSpring) * MathF.Sqrt(MathF.Max(0f, 1f - (reach * reach))))));
    }

    private static Hall GreatHall(float[] open, float[] beds, ulong seed, DungeonRandom random)
    {
        float size = Between(random, HallLeast, HallMost);
        float aspect = Between(random, 0.8f, 1f);
        Vector2 centre = new((FieldX * FieldStep * 0.5f) + Between(random, -HallWander, HallWander), (FieldZ * FieldStep * 0.5f) + Between(random, -HallWander, HallWander));
        // Kept off the space's sides, so the walls are the hall's and not the edge's.
        float room = MathF.Min(MathF.Min(centre.X, (FieldX * FieldStep) - centre.X), MathF.Min(centre.Y, (FieldZ * FieldStep) - centre.Y)) - RindMetres - 3f;
        float radiusX = MathF.Min(size, room), radiusZ = MathF.Min(size * aspect, room);
        float floor = Between(random, HallFloorLeast, HallFloorMost);
        float height = MathF.Min(Between(random, HallLowest, HallHighest), (FieldY * FieldStep) - RindMetres - 4f - floor);
        float turn = Between(random, 0f, MathF.PI);
        List<(Vector2 At, float Radius)> pillars = [];
        int wanted = FewestPillars + (int)(random.Next() % (ulong)(MostPillars - FewestPillars + 1));
        for (int attempt = 0; attempt < wanted * 8 && pillars.Count < wanted; attempt++)
        {
            float a = Between(random, 0f, MathF.Tau), r = Between(random, 0.25f, 0.7f);
            Vector2 at = centre + new Vector2(MathF.Cos(a) * r * radiusX, MathF.Sin(a) * r * radiusZ);
            if (pillars.All(p => Vector2.Distance(p.At, at) >= PillarApart))
            {
                pillars.Add((at, Between(random, PillarLeast, PillarMost)));
            }
        }

        Hall hall = new(centre, radiusX, radiusZ, turn, floor, height, [.. pillars]);
        Parallel.For(0, FieldZ, z =>
        {
            for (int y = 0; y < FieldY; y++)
                for (int x = 0; x < FieldX; x++)
                {
                    int index = FieldIndex(x, y, z);
                    Vector3 p = FieldCentre(index);
                    float reach = hall.Reach(p.X, p.Z);
                    if (reach > 1.3f || p.Y > floor + height + 4f)
                    {
                        continue;
                    }

                    float bedY = p.Y - (BedDip.X * p.X) - (BedDip.Y * p.Z);
                    float nearest = 9f;
                    foreach (float bed in beds)
                    {
                        nearest = MathF.Min(nearest, MathF.Abs(bedY - bed));
                    }

                    // The walls: upright to where the vault springs, then curving over; wobbling, and stepped back at the beds.
                    float t = Math.Clamp((p.Y - floor) / height, 0f, 1f);
                    float wall = t <= HallSpring ? 1f : MathF.Sqrt(MathF.Max(0f, 1f - MathF.Pow((t - HallSpring) / (1f - HallSpring), 2f)));
                    wall += HallWobble * NaturalCavern.Signed(NaturalCavern.Noise(p * HallWobbleFrequency, seed ^ 0x4A01UL));
                    float across = MathF.Min(radiusX, radiusZ);
                    float walls = ((wall - reach) * across) + (HallBand * MathF.Max(0f, 1f - nearest));
                    // The vault itself, read upward, so its crown is as round as its sides.
                    float roof = hall.RoofAt(reach) + (HallBand * MathF.Max(0f, 1f - nearest)) - p.Y;
                    // The floor: rolling, and stepped in rimstone terraces over part of it.
                    float ground = floor + (HallFloorRoll * NaturalCavern.Signed(NaturalCavern.Noise(new Vector3(p.X, 0f, p.Z) * HallFloorFrequency, seed ^ 0x4A02UL)));
                    float terraced = MathF.Round(ground / HallTerrace) * HallTerrace;
                    float terraces = NaturalCavern.SmoothStep(0.45f, 0.6f, NaturalCavern.Noise(new Vector3(p.X, 7f, p.Z) * HallFloorFrequency, seed ^ 0x4A03UL)) * HallTerraceShare;
                    ground += (terraced - ground) * terraces;
                    // The walls and the vault meet in one surface; the vault read upward grades the crown, the walls read across grade the sides.
                    float shape = MathF.Min(MathF.Min(walls, roof), p.Y - ground);
                    float value = NaturalCavern.SmoothMax(open[index], shape, HallBlend);
                    // The pillars: what did not dissolve, flared where it meets the floor and the vault.
                    foreach ((Vector2 at, float radius) in pillars)
                    {
                        float top = hall.RoofAt(hall.Reach(at.X, at.Y));
                        float flare = 1f + (PillarFlare * (MathF.Exp(-(p.Y - floor) / PillarFootFlare) + MathF.Exp(-(top - p.Y) / PillarHeadFlare)));
                        float girth = radius * flare * (1f + (0.15f * NaturalCavern.Signed(NaturalCavern.Noise(p * 0.2f, seed ^ 0x4A04UL))));
                        value = NaturalCavern.SmoothMin(value, Vector2.Distance(new Vector2(p.X, p.Z), at) - girth, PillarBlend);
                    }

                    open[index] = Math.Clamp(value, -FieldLimit, FieldLimit);
                }
        });

        // Boulders heaped against the walls, where the vault shed its slabs.
        int boulders = HallFewestBoulders + (int)(random.Next() % (ulong)(HallMostBoulders - HallFewestBoulders + 1));
        for (int i = 0; i < boulders; i++)
        {
            float a = Between(random, 0f, MathF.Tau), r = Between(random, 0.65f, 0.92f);
            float cos = MathF.Cos(-turn), sin = MathF.Sin(-turn);
            Vector2 local = new(MathF.Cos(a) * r * radiusX, MathF.Sin(a) * r * radiusZ);
            Vector2 at = centre + new Vector2((local.X * cos) - (local.Y * sin), (local.X * sin) + (local.Y * cos));
            Block(open, new Vector3(at.X, floor + (height * HallSpring * 0.5f), at.Y), random, HallBoulder);
        }

        return hall;
    }

    // The mist lying on a hall's floor: how far over the floor it spreads, how deep it lies, and how high its middle sits.
    private const float MistSpread = 0.95f, MistDepth = 4f, MistAboveFloor = 1f;

    /// <summary>The mist on a hall's floor: a broad, shallow ellipsoid filling the hall's lower metres.</summary>
    private static MistVolume HallMist(Hall hall) => new(new Vector3(hall.Centre.X, hall.Floor + MistAboveFloor, hall.Centre.Y),
        new Vector3(hall.RadiusX * MistSpread, MistDepth, hall.RadiusZ * MistSpread), -hall.Turn * 180f / MathF.PI, HallMistDensity);

    /// <summary>The most mist volumes a space places: the Engine draws at most 64 fog volumes in a view.</summary>
    private static int MostMistVolumes => Area(64);

    /// <summary>How thick a hall's floor mist is at its heart.</summary>
    private const float HallMistDensity = 0.12f;

    // A sump's mist (owner, 2026-10-20: a heavy mist where a cave diver would dive): how thick (thick enough that a few
    // metres in, the far side is gone; the lantern glows back from it), how far apart its volumes are along the way
    // through, their size, and how far its mouths' mist rises over the water's level so it shows from outside.
    private const float SumpMistDensity = 0.25f, SumpMistStep = 2.5f, MouthMistRise = 1.2f;

    /// <summary>
    /// The faint light a sump's mist gives of itself (owner, 2026-10-20: a faint teal glow; white was harsh): unlit, a mist
    /// shows only where a light reaches it, so its mouth is dark from a few metres off; with this glow the mouth reads as a
    /// pale wall of haze.
    /// </summary>
    private static readonly Vector3 SumpMistGlow = new(0f, 0.1f, 0.1f);
    private static readonly Vector3 SumpMistSize = new(2.8f, 2.4f, 2.8f), MouthMistSize = new(4f, 2.5f, 4f);

    /// <summary>A sump's mist: volumes along its way through and over each mouth, so the mouth reads as whiteness from outside.</summary>
    private static IEnumerable<MistVolume> SumpMist(WaterZone sump)
    {
        Vector3? last = null;
        foreach (Vector3 p in sump.Path)
        {
            if (last is Vector3 was && Vector3.Distance(was, p) < SumpMistStep)
            {
                continue;
            }

            last = p;
            yield return new MistVolume(p, SumpMistSize, 0f, SumpMistDensity, SumpMistGlow);
        }

        foreach (Vector3 mouth in sump.Mouths)
        {
            yield return new MistVolume(mouth + new Vector3(0f, MouthMistRise - MouthMistSize.Y * 0.5f, 0f), MouthMistSize, 0f, SumpMistDensity, SumpMistGlow);
        }
    }

    // The hall's light (owner, 2026-10-19: large, bright, distant sources, every one a thing to see): giant glowing fungi
    // standing on its floor, a few great crystal blooms hanging from its vault, and glow-worms spread over the vault like
    // stars; how many of each, how far apart, and how far in from the walls.
    private const int FewestGiants = 5, MostGiants = 9, FewestVaultBlooms = 1, MostVaultBlooms = 3, MostHallStars = 30;
    private const float GiantsApart = 9f, GiantsWithin = 0.85f, GiantClearOfPillar = 3f, VaultBloomsWithin = 0.5f, VaultBloomsApart = 14f;
    private const float StarsApart = 5f, StarsWithin = 0.9f;

    private static IEnumerable<EcologyPlacement> HallGrowths(float[] open, Hall hall, ulong seed, DungeonRandom random)
    {
        List<EcologyPlacement> growths = [];
        Vector2 Within(float share)
        {
            float a = Between(random, 0f, MathF.Tau), r = share * MathF.Sqrt(Between(random, 0f, 1f));
            float cos = MathF.Cos(-hall.Turn), sin = MathF.Sin(-hall.Turn);
            Vector2 local = new(MathF.Cos(a) * r * hall.RadiusX, MathF.Sin(a) * r * hall.RadiusZ);
            return hall.Centre + new Vector2((local.X * cos) - (local.Y * sin), (local.X * sin) + (local.Y * cos));
        }

        uint Seed() => (uint)(random.Next() >> 32);
        int giants = FewestGiants + (int)(random.Next() % (ulong)(MostGiants - FewestGiants + 1));
        for (int attempt = 0; attempt < giants * 10 && growths.Count < giants; attempt++)
        {
            Vector2 at = Within(GiantsWithin);
            if (hall.Pillars.Any(p => Vector2.Distance(p.At, at) < p.Radius * (1f + PillarFlare) + GiantClearOfPillar)
                || growths.Any(g => Vector2.Distance(new Vector2(g.At.X, g.At.Z), at) < GiantsApart))
            {
                continue;
            }

            Vector3 foot = Floor(open, new Vector3(at.X, hall.Floor + (hall.Height * HallSpring), at.Y));
            if (foot.Y < hall.Floor + (hall.Height * HallSpring * 0.6f))
            {
                growths.Add(new EcologyPlacement(EcologyKind.GiantFungus, foot, Vector3.UnitY, Between(random, 0.7f, 1.4f), Seed()));
            }
        }

        int blooms = FewestVaultBlooms + (int)(random.Next() % (ulong)(MostVaultBlooms - FewestVaultBlooms + 1));
        for (int attempt = 0; attempt < blooms * 10 && growths.Count(g => g.Kind == EcologyKind.VaultBloom) < blooms; attempt++)
        {
            Vector2 at = Within(VaultBloomsWithin);
            if (growths.Any(g => g.Kind == EcologyKind.VaultBloom && Vector2.Distance(new Vector2(g.At.X, g.At.Z), at) < VaultBloomsApart)
                || hall.Pillars.Any(p => Vector2.Distance(p.At, at) < p.Radius * (1f + PillarFlare) + GiantClearOfPillar))
            {
                continue;
            }

            Vector3 top = Ceiling(open, new Vector3(at.X, hall.Floor + (hall.Height * HallSpring), at.Y));
            if (top.Y > hall.Floor + (hall.Height * 0.6f))
            {
                growths.Add(new EcologyPlacement(EcologyKind.VaultBloom, top, -Vector3.UnitY, Between(random, 1f, 1.6f), Seed()));
            }
        }

        // A hall always has its sun: failing a clear place, the vault over its middle.
        if (!growths.Any(g => g.Kind == EcologyKind.VaultBloom))
        {
            Vector3 top = Ceiling(open, new Vector3(hall.Centre.X, hall.Floor + (hall.Height * HallSpring), hall.Centre.Y));
            growths.Add(new EcologyPlacement(EcologyKind.VaultBloom, top, -Vector3.UnitY, Between(random, 1f, 1.6f), Seed()));
        }

        for (int attempt = 0; attempt < MostHallStars * 6 && growths.Count(g => g.Kind == EcologyKind.GlowWorms) < MostHallStars; attempt++)
        {
            Vector2 at = Within(StarsWithin);
            Vector3 top = Ceiling(open, new Vector3(at.X, hall.Floor + (hall.Height * HallSpring), at.Y));
            if (top.Y > hall.Floor + (hall.Height * 0.5f) && !growths.Any(g => g.Kind != EcologyKind.GiantFungus && Vector3.Distance(g.At, top) < StarsApart))
            {
                growths.Add(new EcologyPlacement(EcologyKind.GlowWorms, top, -Vector3.UnitY, Between(random, 1.2f, 2f), Seed()));
            }
        }

        return growths;
    }

    // Ecology (#10129): how far apart growths of a kind stand, how many a cave may have, how damp a quiet floor must be
    // for fungi, how near running water glow-worms hang and how clear the drop below them must be, and how long dry a
    // level must be for crystals.
    private const float FungiApart = 3f, WormsApart = 4f, CrystalsApart = 8f;
    private static int MostFungi => Area(25);
    private static int MostWorms => Area(15);
    private static int MostCrystals => Area(8);
    private const float FungiDamp = 0.15f, QuietFlow = 0.15f, WormsNearFlow = 0.05f, WormsClearBelow = 2.5f;
    private const int CrystalsAfterEras = 2;

    // Root curtains (#10129): tree roots hang from ceilings close under the surface - within this many metres of it
    // (the plateau's ground over a cave open to the sky, else the top of the space), over a clear drop - spaced apart
    // and capped.
    private const float RootsUnderSurface = 7f, RootsClearBelow = 2f, RootsApart = 3.5f;
    private static int MostRoots => Area(14);
    private const float EcologyStep = 1f;

    /// <summary>
    /// Where the cave's life grows, from what the water did (#10129): fungi on floors where it is damp (water drips or
    /// stands) but nothing runs, glow-worms on ceilings over running water with room below them to hang, crystals on old
    /// dry levels; each kind spaced apart and capped, chosen by hash so a seed always grows the same cave.
    /// </summary>
    private static List<EcologyPlacement> Ecology(float[] open, byte[] formed, float[] flow, float[] drip, float[] standing, int eras, float finalTable, ulong seed,
        bool skyOpen, List<OvergrowthPlacement> overgrowth)
    {
        List<(EcologyKind Kind, Vector3 At, Vector3 Normal, float Rank)> candidates = [];
        // The overgrowth's mushrooms and root tangles (#10096), ranked by their own hash.
        List<(OvergrowthKind Kind, Vector3 At, float Rank)> growing = [];
        float Open(Vector3 p) => Trilinear(open, p);
        for (float z = 2f; z < GridZ * GridCell - 2f; z += EcologyStep)
            for (float y = 2f; y < GridY * GridCell - 2f; y += EcologyStep)
                for (float x = 2f; x < GridX * GridCell - 2f; x += EcologyStep)
                {
                    Vector3 p = new(x, y, z);
                    if (Open(p) <= 0f)
                    {
                        continue;
                    }

                    float damp = MathF.Max(GridTrilinear(drip, p), GridTrilinear(standing, p));
                    float running = GridTrilinear(flow, p);
                    float rank = NaturalCavern.Hash(seed ^ 0x1E01UL, (int)x, (int)y, (int)z);
                    int sample = FieldIndex(Math.Clamp((int)(x / FieldStep), 0, FieldX - 1), Math.Clamp((int)(y / FieldStep), 0, FieldY - 1), Math.Clamp((int)(z / FieldStep), 0, FieldZ - 1));
                    int age = formed[sample] == Unformed ? 0 : eras - 1 - formed[sample];
                    // A floor just below: fungi where it is damp and still.
                    if (Open(p - new Vector3(0f, EcologyStep, 0f)) <= 0f && damp >= FungiDamp && running < QuietFlow)
                    {
                        candidates.Add((EcologyKind.GlowFungi, Floor(open, p), Vector3.UnitY, rank));
                        growing.Add((OvergrowthKind.Mushrooms, Floor(open, p) - new Vector3(0f, 0.06f, 0f), NaturalCavern.Hash(seed ^ 0x0F62UL, (int)x, (int)y, (int)z)));
                    }

                    // A ceiling just above with a clear drop below: glow-worms over running water.
                    if (Open(p + new Vector3(0f, EcologyStep, 0f)) <= 0f && running >= WormsNearFlow && Open(p - new Vector3(0f, WormsClearBelow, 0f)) > 0f)
                    {
                        candidates.Add((EcologyKind.GlowWorms, Ceiling(open, p), -Vector3.UnitY, rank));
                    }

                    // A ceiling just under the surface, with room below: tree roots grown down through the rock.
                    float ground = skyOpen ? SurfaceAt(p.X, p.Z, seed) : FieldY * FieldStep;
                    if (Open(p + new Vector3(0f, EcologyStep, 0f)) <= 0f && Open(p - new Vector3(0f, RootsClearBelow, 0f)) > 0f && ground - p.Y < RootsUnderSurface)
                    {
                        Vector3 roof = Ceiling(open, p);
                        if (ground - roof.Y < RootsUnderSurface)
                        {
                            candidates.Add((EcologyKind.RootCurtain, roof, -Vector3.UnitY, rank));
                            growing.Add((OvergrowthKind.RootTangle, roof + new Vector3(0f, 0.15f, 0f), NaturalCavern.Hash(seed ^ 0x0F63UL, (int)x, (int)y, (int)z)));
                        }
                    }

                    // A wall beside, on a level long dry: crystals.
                    if (age >= CrystalsAfterEras && p.Y > finalTable && damp < FungiDamp && Wall(open, p) is Vector3 wallNormal)
                    {
                        candidates.Add((EcologyKind.CrystalBloom, p - (wallNormal * 0.45f), wallNormal, rank));
                    }
                }

        List<EcologyPlacement> chosen = [];
        foreach ((EcologyKind kind, float apart, int most) in new[] { (EcologyKind.GlowFungi, FungiApart, MostFungi), (EcologyKind.GlowWorms, WormsApart, MostWorms),
            (EcologyKind.CrystalBloom, CrystalsApart, MostCrystals), (EcologyKind.RootCurtain, RootsApart, MostRoots) })
        {
            foreach ((EcologyKind _, Vector3 at, Vector3 normal, float rank) in candidates.Where(c => c.Kind == kind).OrderByDescending(c => c.Rank))
            {
                if (chosen.Count(c => c.Kind == kind) >= most || chosen.Any(c => c.Kind == kind && Vector3.Distance(c.At, at) < apart))
                {
                    continue;
                }

                chosen.Add(new EcologyPlacement(kind, at, normal, 0.7f + (0.6f * rank), (uint)(rank * uint.MaxValue)));
            }
        }

        foreach ((OvergrowthKind kind, float apart, int most) in new[] { (OvergrowthKind.Mushrooms, MushroomsApart, MostMushrooms),
            (OvergrowthKind.RootTangle, RootTanglesApart, MostRootTangles) })
        {
            List<Vector3> placed = [];
            foreach ((OvergrowthKind _, Vector3 at, float rank) in growing.Where(g => g.Kind == kind).OrderByDescending(g => g.Rank))
            {
                if (placed.Count >= most || placed.Any(q => Vector3.Distance(q, at) < apart))
                {
                    continue;
                }

                placed.Add(at);
                float a = 2f * MathF.PI * rank;
                // A floor's up is near enough the world's for a cluster; roots hang from the roof as they grew.
                overgrowth.Add(new OvergrowthPlacement(kind, at, new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), kind == OvergrowthKind.Mushrooms ? 1.5f + rank : 0.9f + (0.3f * rank), rank));
            }
        }

        return chosen;
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-21, the atlas's abyssal seabed in air): how many vent
    /// orchards the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyVentOrchards { get; set; }

    /// <summary>The fewest pieces a chosen community must grow to count as one of its cave's communities, and of its signature pieces (#10304).</summary>
    private const int FewestCommunityPieces = 20, FewestSignaturePieces = 3;

    /// <summary>Whether any community's study switch is set: then the switches choose, not the region and depth (#10304).</summary>
    private static bool StudiedCommunities => Enum.GetValues<CaveCommunity>().Any(c => StudyCount(c) > 0);

    /// <summary>For studies (#10304): true grows none of the stranger communities in the caves generated next (unless a switch is set).</summary>
    internal static bool StudyNoCommunities { get; set; }

    /// <summary>The study switches that shape generation, for the generation cache's key.</summary>
    internal static string StudyKey => $"{string.Join(".", Enum.GetValues<CaveCommunity>().Select(StudyCount))}{(StudyNoCommunities ? "-none" : string.Empty)}";

    private static int StudyCount(CaveCommunity community) => community switch
    {
        CaveCommunity.VentOrchard => StudyVentOrchards,
        CaveCommunity.InvertedGrove => StudyInvertedGroves,
        CaveCommunity.FunnelGarden => StudyFunnelGardens,
        CaveCommunity.GlassSteppe => StudyGlassSteppes,
        CaveCommunity.RibbonThicket => StudyRibbonThickets,
        CaveCommunity.VeilForest => StudyVeilForests,
        CaveCommunity.CushionBarrens => StudyCushionBarrens,
        _ => StudyIvoryHeaths,
    };

    // Vent orchards: a seep is a still floor where the drip is strongest (at least this), the seeps so far apart. Round each,
    // a lattice so fine out to this radius; a floor counts within so far of the seep's height, flat enough (its neighbours
    // within this rise) and with room above for the piece. Growth is likeliest at the seep and fades out to the radius,
    // in bands so far apart; near the seep the tall tubes and clusters, then short tubes, then beds. Each piece keeps its
    // footprint clear of the others'.
    private const float SeepDrip = 0.05f, OrchardsApart = 24f, OrchardRadius = 10f, OrchardStep = 0.6f, OrchardRise = 4f, OrchardSlope = 0.7f;

    // A seep is chosen by its drip times the floor round it (an orchard needs ground): among the dampest so many, the floor
    // counted on a coarser lattice; and the pieces' footprints may overlap by this share (fused growth).
    private static int SeepsWeighed => Area(120);

    // Large caves: a seep with this much room above it grows an orchard out to this radius, its giants (and giant clusters,
    // this share of them) nearest the seep, this share of the pieces there. A trunk keeps this far clear of the cave's main
    // route (horizontally, within this rise of it), as trunks collide.
    private const float GiantRoom = 14f, GiantOrchardRadius = 16f, GiantReach = 0.45f, GiantShare = 0.5f, GiantClusterShare = 0.35f;
    private const float RouteClear = 1.2f, RouteRise = 3f, GiantUnderGround = 4f;

    /// <summary>
    /// Whether a foot stands within so far (across) of a route, whose points lie a few metres apart: measured to the line
    /// between each two (#10304: a trunk midway between two points stood closer than either), where the route runs from
    /// RouteRise under the foot to RouteRise over a trunk <paramref name="tall"/> high (#10304: a giant vent's trunk stood
    /// beside a route passing along a ledge above its foot).
    /// </summary>
    private static bool NearRoute(IReadOnlyList<Vector3> route, Vector3 foot, float within, float tall = 0f)
    {
        Vector2 at = new(foot.X, foot.Z);
        for (int i = 0; i < route.Count; i++)
        {
            Vector3 a = route[i], b = i + 1 < route.Count ? route[i + 1] : a;
            if (MathF.Max(a.Y, b.Y) <= foot.Y - RouteRise || MathF.Min(a.Y, b.Y) >= foot.Y + tall + RouteRise)
            {
                continue;
            }

            Vector2 a2 = new(a.X, a.Z), ab = new Vector2(b.X, b.Z) - a2;
            float along = ab.LengthSquared() > 1e-6f ? Math.Clamp(Vector2.Dot(at - a2, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            if (Vector2.Distance(at, a2 + (ab * along)) < within)
            {
                return true;
            }
        }

        return false;
    }

    // A great hall's orchard: its seep within this share of the hall's radius of its middle, or if none drips there, its
    // floor this share of its radius out from the middle (clear of a centrepiece there).
    private const float HallSeepReach = 0.6f, HallSeepOffset = 0.35f;
    private const float AreaStep = 1.5f, FootprintKept = 0.75f;
    private const float OrchardBand = 3.2f, OrchardBandDepth = 0.35f;

    // The orchard's zones, as shares of its radius: the tubes out to TubeReach (the taller nearer than TallReach), this
    // likely at the chimneys and thinning outward, a few short tubes and beds out to OuterReach at this share; shell beds
    // from beyond the chimneys out to ShellReach; mats from MatFrom to the edge, likeliest midway, overlapping closely (into one mat, not spots).
    private const float TubeDensest = 0.55f, TubeReach = 0.6f, TallReach = 0.3f, OuterReach = 0.9f, OuterShare = 0.12f;
    private const float ChimneyReach = 0.08f, ShellReach = 0.65f, ShellDensest = 0.55f, MatFrom = 0.3f, MatDensest = 0.75f, MatFloor = 0.1f, MatOverlap = 0.35f, MatSink = 0.03f;

    // The chimneys: their footprint (metres) and the top of the tallest (metres; the seep's glow and light hang there), and
    // how much larger they grow in a large cave.
    /// <summary>How far past an orchard's radius the dark's other growth gives way (metres).</summary>
    private const float OrchardEdge = 1f;

    private const float ChimneyFootprint = 1.1f, ChimneyTop = 2.6f, LargeChimney = 1.5f;
    private static int MostOrchardPieces => Area(1200);

    /// <summary>A vent piece's footprint (its radius, metres) and the room it needs above (metres).</summary>
    private static (float Radius, float Height) VentFootprint(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VentTall => (0.5f, 4f),
        OvergrowthKind.VentCluster => (0.85f, 4.4f),
        OvergrowthKind.VentShort => (0.28f, 1.4f),
        OvergrowthKind.VentGiant => (1.6f, 13f),
        OvergrowthKind.VentGiantCluster => (2.4f, 13.5f),
        OvergrowthKind.SeepChimney => (1.1f, 2.8f),
        OvergrowthKind.SeepShells => (0.55f, 0.4f),
        OvergrowthKind.SeepMat => (1.2f, 0.4f),
        _ => (0.6f, 0.8f),
    };

    /// <summary>
    /// Vent orchards (#10096; owner, 2026-10-21: an ecology is several elements working together): the cave's strongest still
    /// drips are seeps of warm, nutrient-bearing water welling out of mineral chimneys (their glowing tops the orchard's light).
    /// Round each, as round a deep-sea vent, its community in zones: the ivory tubes nearest (tall tubes, fused clusters and
    /// in a large cave giants), mussel and clam beds packed between their feet, pale bacterial mats spreading over the outer
    /// ground, a few short tubes and beds out among the mats. Returns each orchard's seep and radius.
    /// </summary>
    private static List<(Vector3 Seep, float Radius)> VentOrchards(float[] open, float[] drip, float[] flow, ulong seed, int count, List<EcologyPlacement> ecology,
        List<OvergrowthPlacement> growth, IReadOnlyList<Vector3> route, bool skyOpen, Hall? hall, IEnumerable<(Vector3 At, float Radius)> taken, Func<Vector3, bool> deep)
    {
        // The ground over a place: the plateau over a cave open to the sky, else the top of the space (as the roots read it).
        float Surface(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;
        float Open(Vector3 p) => Trilinear(open, p);
        List<(Vector3 At, float Drip)> seeps = [];
        for (float z = 2f; z < GridZ * GridCell - 2f; z += EcologyStep)
            for (float y = 2f; y < GridY * GridCell - 2f; y += EcologyStep)
                for (float x = 2f; x < GridX * GridCell - 2f; x += EcologyStep)
                {
                    Vector3 p = new(x, y, z);
                    float wet = GridTrilinear(drip, p);
                    if (wet >= SeepDrip && Open(p) > 0f && Open(p - new Vector3(0f, EcologyStep, 0f)) <= 0f && GridTrilinear(flow, p) < QuietFlow)
                    {
                        seeps.Add((Floor(open, p), wet));
                    }
                }

        // A floor round a seep: open above, within the orchard's rise of the seep, flat enough.
        Vector3? Ground(Vector3 seep, float dx, float dz)
        {
            Vector3 above = new(seep.X + dx, seep.Y + (OrchardRise * 0.5f), seep.Z + dz);
            if (Open(above) <= 0f)
            {
                return null;
            }

            Vector3 foot = Floor(open, above);
            return MathF.Abs(foot.Y - seep.Y) > OrchardRise
                || MathF.Abs(Floor(open, above + new Vector3(0.4f, 0f, 0f)).Y - foot.Y) > OrchardSlope
                || MathF.Abs(Floor(open, above + new Vector3(0f, 0f, 0.4f)).Y - foot.Y) > OrchardSlope ? null : foot;
        }

        int Area(Vector3 seep)
        {
            int n = 0;
            for (float dz = -OrchardRadius; dz <= OrchardRadius; dz += AreaStep)
                for (float dx = -OrchardRadius; dx <= OrchardRadius; dx += AreaStep)
                {
                    n += (dx * dx) + (dz * dz) <= OrchardRadius * OrchardRadius && Ground(seep, dx, dz) is not null ? 1 : 0;
                }

            return n;
        }

        List<Vector3> chosen = [];
        // Clear of the communities already grown (#10304).
        bool Clear(Vector3 c) => taken.All(t => Vector2.Distance(new Vector2(t.At.X, t.At.Z), new Vector2(c.X, c.Z)) >= t.Radius + OrchardRadius || MathF.Abs(t.At.Y - c.Y) > 8f);

        // A great hall, the largest cave, holds the first orchard (its giants): its dampest floor, else its floor near its middle.
        if (hall is not null)
        {
            bool InHall(Vector3 p) => Vector2.Distance(new Vector2(p.X, p.Z), hall.Centre) < MathF.Min(hall.RadiusX, hall.RadiusZ) * HallSeepReach && MathF.Abs(p.Y - hall.Floor) < OrchardRise;
            Vector3 middle = Floor(open, new Vector3(hall.Centre.X + (hall.RadiusX * HallSeepOffset), hall.Floor + (hall.Height * 0.5f), hall.Centre.Y));
            Vector3 first = seeps.Where(s => InHall(s.At)).OrderByDescending(s => s.Drip).Select(s => (Vector3?)s.At).FirstOrDefault() ?? middle;
            if (deep(first) && Clear(first))
            {
                chosen.Add(first);
            }
        }

        foreach ((Vector3 at, float _) in seeps.OrderByDescending(s => s.Drip).Take(SeepsWeighed).Select(s => (s.At, s.Drip * Area(s.At))).OrderByDescending(s => s.Item2))
        {
            if (chosen.Count >= count)
            {
                break;
            }

            if (deep(at) && Clear(at) && chosen.All(c => Vector3.Distance(c, at) >= OrchardsApart))
            {
                chosen.Add(at);
            }
        }

        List<(Vector3 At, float Radius)> placed = [], orchards = [];
        bool Trunk(OvergrowthKind kind) => kind is OvergrowthKind.VentTall or OvergrowthKind.VentCluster or OvergrowthKind.VentGiant or OvergrowthKind.VentGiantCluster
            or OvergrowthKind.SeepChimney;
        foreach (Vector3 seep in chosen)
        {
            // A large cave under a roof: room above the seep, and the ground (the plateau over a cave open to the sky) well above
            // that, so a giant never stands up out of a sinkhole into the daylight.
            bool large = Open(seep + new Vector3(0f, GiantRoom, 0f)) > 0f && Open(seep + new Vector3(0f, GiantRoom * 0.6f, 0f)) > 0f
                && Surface(seep.X, seep.Z) - seep.Y > GiantRoom + GiantUnderGround;
            float orchardRadius = large ? GiantOrchardRadius : OrchardRadius;
            orchards.Add((seep, orchardRadius));

            // The chimneys over the seep, its warm water welling out of their tops: the orchard's glow and light.
            float chimneyScale = large ? LargeChimney : 1f;
            float pickC = NaturalCavern.Hash(seed ^ 0x5EE9UL, (int)seep.X, (int)seep.Y, (int)seep.Z);
            growth.Add(new OvergrowthPlacement(OvergrowthKind.SeepChimney, seep - new Vector3(0f, 0.1f, 0f), new Vector3(MathF.Sin(MathF.Tau * pickC), 0f, MathF.Cos(MathF.Tau * pickC)),
                chimneyScale, 0.5f));
            ecology.Add(new EcologyPlacement(EcologyKind.VentSeep, seep + new Vector3(0f, (ChimneyTop * chimneyScale) - 0.1f, 0f), Vector3.UnitY, 1f, (uint)(pickC * uint.MaxValue)));
            placed.Add((seep, ChimneyFootprint * chimneyScale));
            List<(Vector3 At, float Radius)> mats = [];

            // In passes, each zone's community taking its ground in turn: the giants, the tubes, the shell beds packed between
            // the tubes' feet, then the mats spread over the outer ground (under what stands on it).
            for (int pass = 0; pass < 4; pass++)
            for (float dz = -orchardRadius; dz <= orchardRadius; dz += OrchardStep)
                for (float dx = -orchardRadius; dx <= orchardRadius; dx += OrchardStep)
                {
                    float d = MathF.Sqrt((dx * dx) + (dz * dz));
                    if (d > orchardRadius || growth.Count >= MostOrchardPieces * chosen.Count + 1000)
                    {
                        continue;
                    }

                    int hx = (int)((seep.X + dx) * 4f), hz = (int)((seep.Z + dz) * 4f);
                    float roll = NaturalCavern.Hash(seed ^ (0x0E01UL + (ulong)(pass * 0x101)), hx, (int)seep.Y, hz), pick = NaturalCavern.Hash(seed ^ (0x0E02UL + (ulong)(pass * 0x103)), hz, (int)seep.Y, hx);
                    float reach = d / orchardRadius;
                    float band = 1f - OrchardBandDepth + (OrchardBandDepth * MathF.Cos(MathF.Tau * d / OrchardBand));
                    OvergrowthKind? choice = pass switch
                    {
                        0 => large && reach < GiantReach && roll < GiantShare * TubeDensest
                            ? (NaturalCavern.Hash(seed ^ 0x61A7C0UL, hz, (int)seep.Y, hx) < GiantClusterShare ? OvergrowthKind.VentGiantCluster : OvergrowthKind.VentGiant) : null,
                        1 => reach < TubeReach
                            ? (roll < TubeDensest * MathF.Pow(1f - (reach / TubeReach), 0.5f) * band
                                ? (reach < TallReach ? (pick < 0.3f ? OvergrowthKind.VentCluster : pick < 0.75f ? OvergrowthKind.VentTall : OvergrowthKind.VentShort)
                                    : (pick < 0.15f ? OvergrowthKind.VentTall : pick < 0.65f ? OvergrowthKind.VentShort : OvergrowthKind.VentBed))
                                : null)
                            : reach < OuterReach && roll < OuterShare ? (pick < 0.4f ? OvergrowthKind.VentShort : OvergrowthKind.VentBed) : null,
                        2 => reach > ChimneyReach && reach < ShellReach && roll < ShellDensest * MathF.Pow(1f - (reach / ShellReach), 0.5f) ? OvergrowthKind.SeepShells : null,
                        _ => reach > MatFrom && roll < MatDensest * MathF.Sin(MathF.PI * Math.Clamp((reach - MatFrom) / (1f - MatFrom), 0f, 1f)) + MatFloor ? OvergrowthKind.SeepMat : null,
                    };
                    if (choice is not OvergrowthKind kind || Ground(seep, dx + ((pick - 0.5f) * OrchardStep * 0.6f), dz) is not Vector3 foot)
                    {
                        continue;
                    }

                    (float radius, float height) = VentFootprint(kind);
                    if (kind is OvergrowthKind.VentGiant or OvergrowthKind.VentGiantCluster && Surface(foot.X, foot.Z) - foot.Y < height + GiantUnderGround)
                    {
                        continue;
                    }

                    if (Trunk(kind) && NearRoute(route, foot, radius + RouteClear, height))
                    {
                        continue;
                    }

                    // Flat ground cover lies on floor all round it, never overhanging a ledge.
                    if (kind is OvergrowthKind.SeepMat or OvergrowthKind.VentBed or OvergrowthKind.SeepShells && !Footed(open, foot, radius))
                    {
                        continue;
                    }

                    // A mat lies under what stands on it, overlapping the next mat closely; anything else keeps its footprint.
                    List<(Vector3 At, float Radius)> against = kind == OvergrowthKind.SeepMat ? mats : placed;
                    float kept = kind == OvergrowthKind.SeepMat ? MatOverlap : FootprintKept;
                    if (Open(foot + new Vector3(0f, height, 0f)) <= 0f || Open(foot + new Vector3(0f, height * 0.5f, 0f)) <= 0f
                        || against.Any(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(foot.X, foot.Z)) < (q.Radius + radius) * kept))
                    {
                        continue;
                    }

                    against.Add((foot, radius));
                    float a = MathF.Tau * pick;
                    // Sunk a little, so a foot on a slope does not stand clear of it (a mat only a little: it lies on the rock).
                    float sink = kind == OvergrowthKind.SeepMat ? MatSink : 0.12f;
                    growth.Add(new OvergrowthPlacement(kind, foot - new Vector3(0f, sink, 0f), new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), 0.8f + (0.45f * pick), pick));
                }
        }

        return orchards;
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-21, the concept sheet "inverted grove - a community"): how
    /// many inverted groves the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyInvertedGroves { get; set; }

    // Inverted groves: under a ceiling within GroveUnderSurface of the surface (nutrients arrive from above), with at least
    // GroveDrop of clear fall below it, ranked by how much such ceiling lies round it (counted in AreaCell cells), the groves
    // so far apart, each out to GroveRadius. A hanger keeps GroveHeadroom clear above the floor under its crown; its length
    // (trunk and crown) and footprint by size. Sacs hang within SacReach of a hanger; the root web covers the ceiling between,
    // overlapping; the fallfield lies under the canopy.
    private const float GroveUnderSurface = 12f, GroveDrop = 6f, GroveAreaCell = 4f, GrovesApart = 26f, GroveRadius = 11f, GroveStep = 0.9f;
    private const float GroveHeadroom = 3.5f, HangerLength = 7f, SmallHangerLength = 4f, HangerFootprint = 2.6f, SmallHangerFootprint = 1.6f;
    private const float HangerDensest = 0.35f, HangerReach = 0.85f, SacReach = 3.5f, SacShare = 0.35f, SacLength = 2.5f, SacFootprint = 0.8f;
    private const float WebShare = 0.5f, WebFootprint = 2f, WebOverlap = 0.5f, CeilingSink = 0.15f, GroveRoofSpread = 3f;
    private const float LitterShare = 0.55f, LitterFootprint = 1.3f, LitterOverlap = 0.4f, CatcherShare = 0.12f, CatcherFootprint = 0.6f;
    private const float ScavengerShare = 0.04f, MoundFootprint = 1f, FallfieldReach = 0.9f, GroveEdge = 1f;
    private static int MostGrovePieces => Area(900);

    /// <summary>
    /// Inverted groves (#10096): where a ceiling lies close under the surface, nutrients seep down through it, and its
    /// community hangs from it - canopy hangers rooted in the ceiling with their crowns of red filaments (each crown's tips
    /// a faint amber light), seed sacs hanging below them, the pale root web over the ceiling between - while the floor
    /// beneath is a barren fallfield fed only by what drops: litter, cup catchers, scavenger mounds. Returns each grove's
    /// centre (on its ceiling) and radius.
    /// </summary>
    private static List<(Vector3 Roof, float Radius)> InvertedGroves(float[] open, ulong seed, int count, List<EcologyPlacement> ecology,
        List<OvergrowthPlacement> growth, bool skyOpen, IReadOnlyList<(Vector3 Seep, float Radius)> orchards, Func<Vector3, bool> deep)
    {
        float Open(Vector3 p) => Trilinear(open, p);
        float Surface(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;
        List<Vector3> roofs = [];
        for (float z = 2f; z < GridZ * GridCell - 2f; z += EcologyStep)
            for (float y = 2f; y < GridY * GridCell - 2f; y += EcologyStep)
                for (float x = 2f; x < GridX * GridCell - 2f; x += EcologyStep)
                {
                    Vector3 p = new(x, y, z);
                    if (Open(p) > 0f && Open(p + new Vector3(0f, EcologyStep, 0f)) <= 0f && Open(p - new Vector3(0f, GroveDrop, 0f)) > 0f
                        && Surface(x, z) - y < GroveUnderSurface)
                    {
                        roofs.Add(Ceiling(open, p));
                    }
                }

        // The ceiling round each, counted by cells.
        Dictionary<(int, int), int> cells = [];
        foreach (Vector3 r in roofs)
        {
            (int, int) k = ((int)MathF.Floor(r.X / GroveAreaCell), (int)MathF.Floor(r.Z / GroveAreaCell));
            cells[k] = cells.GetValueOrDefault(k) + 1;
        }

        int Area(Vector3 r)
        {
            int cx = (int)MathF.Floor(r.X / GroveAreaCell), cz = (int)MathF.Floor(r.Z / GroveAreaCell), n = 0, reach = (int)(GroveRadius / GroveAreaCell);
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    n += cells.GetValueOrDefault((cx + dx, cz + dz));
                }

            return n;
        }

        List<Vector3> chosen = [];
        foreach (Vector3 r in roofs.OrderByDescending(Area).ThenBy(r => NaturalCavern.Hash(seed ^ 0x6A0EUL, (int)r.X, (int)r.Y, (int)r.Z)))
        {
            if (chosen.Count >= count)
            {
                break;
            }

            if (deep(r) && chosen.All(c => Vector3.Distance(c, r) >= GrovesApart) && orchards.All(o => Vector2.Distance(new Vector2(o.Seep.X, o.Seep.Z), new Vector2(r.X, r.Z)) >= o.Radius + GroveRadius))
            {
                chosen.Add(r);
            }
        }

        List<(Vector3 Roof, float Radius)> groves = [];
        foreach (Vector3 centre in chosen)
        {
            groves.Add((centre, GroveRadius));
            List<(Vector3 At, float Radius)> hangers = [], sacs = [], web = [], litter = [], floor = [];
            for (int pass = 0; pass < 4; pass++)
            for (float dz = -GroveRadius; dz <= GroveRadius; dz += GroveStep)
                for (float dx = -GroveRadius; dx <= GroveRadius; dx += GroveStep)
                {
                    float d = MathF.Sqrt((dx * dx) + (dz * dz));
                    if (d > GroveRadius || growth.Count >= (MostGrovePieces * chosen.Count) + 4000)
                    {
                        continue;
                    }

                    int hx = (int)((centre.X + dx) * 4f), hz = (int)((centre.Z + dz) * 4f);
                    float roll = NaturalCavern.Hash(seed ^ (0x6E01UL + (ulong)(pass * 0x107)), hx, (int)centre.Y, hz), pick = NaturalCavern.Hash(seed ^ (0x6E02UL + (ulong)(pass * 0x109)), hz, (int)centre.Y, hx);
                    // The ceiling and floor at this column, near the grove's ceiling.
                    Vector3 probe = new(centre.X + dx + ((pick - 0.5f) * GroveStep * 0.6f), centre.Y - 1.5f, centre.Z + dz);
                    if (Open(probe) <= 0f)
                    {
                        continue;
                    }

                    Vector3 roof = Ceiling(open, probe), ground = Floor(open, probe);
                    float drop = roof.Y - ground.Y, reach = d / GroveRadius;
                    if (MathF.Abs(roof.Y - centre.Y) > GroveRoofSpread)
                    {
                        continue;
                    }

                    bool Clear(List<(Vector3 At, float Radius)> list, float radius, float kept) =>
                        list.All(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(roof.X, roof.Z)) >= (q.Radius + radius) * kept);
                    Vector3 hung = roof + new Vector3(0f, CeilingSink, 0f);
                    float a = MathF.Tau * pick;
                    Vector3 turn = new(MathF.Sin(a), 0f, MathF.Cos(a));
                    switch (pass)
                    {
                        case 0:
                            // Hangers: the large where the fall leaves headroom under its whole crown (#10304: at its own size,
                            // over the floor round its foot, not the one column, so no crown hangs to head height), else the small.
                            float scale = 0.85f + (0.3f * pick);
                            bool large = Headroom(open, roof, HangerFootprint, HangerLength * scale) >= (HangerLength * scale) + GroveHeadroom;
                            bool small = !large && Headroom(open, roof, SmallHangerFootprint, SmallHangerLength * scale) >= (SmallHangerLength * scale) + GroveHeadroom;
                            float footprint = large ? HangerFootprint : SmallHangerFootprint;
                            if (reach < HangerReach && (large || small) && roll < HangerDensest * MathF.Pow(1f - reach, 0.3f) && Clear(hangers, footprint, FootprintKept))
                            {
                                hangers.Add((roof, footprint));
                                growth.Add(new OvergrowthPlacement(large ? OvergrowthKind.GroveHanger : OvergrowthKind.GroveHangerSmall, hung, turn, scale, pick));
                                float length = large ? HangerLength : SmallHangerLength;
                                ecology.Add(new EcologyPlacement(EcologyKind.GroveCrown, roof - new Vector3(0f, length * 0.85f, 0f), -Vector3.UnitY, 1f, (uint)(pick * uint.MaxValue)));
                            }

                            break;
                        case 1:
                            if (Headroom(open, roof, SacFootprint, SacLength * (0.8f + (0.4f * pick))) >= (SacLength * (0.8f + (0.4f * pick))) + GroveHeadroom && roll < SacShare && hangers.Any(h => Vector2.Distance(new Vector2(h.At.X, h.At.Z), new Vector2(roof.X, roof.Z)) is var e && e > h.Radius * 0.4f && e < SacReach)
                                && Clear(sacs, SacFootprint, 1f))
                            {
                                sacs.Add((roof, SacFootprint));
                                growth.Add(new OvergrowthPlacement(OvergrowthKind.GroveSacs, hung, turn, 0.8f + (0.4f * pick), pick));
                            }

                            break;
                        case 2:
                            if (roll < WebShare && Clear(web, WebFootprint, WebOverlap))
                            {
                                web.Add((roof, WebFootprint));
                                growth.Add(new OvergrowthPlacement(OvergrowthKind.GroveRootweb, hung, turn, 0.9f + (0.3f * pick), pick));
                            }

                            break;
                        default:
                            // The fallfield under the canopy: litter overlapping into one ground, catchers and mounds on it.
                            if (reach >= FallfieldReach)
                            {
                                break;
                            }

                            Vector3 on = ground - new Vector3(0f, MatSink, 0f);
                            if (!Footed(open, ground, CatcherFootprint))
                            {
                                break;
                            }

                            if (roll < ScavengerShare && Footed(open, ground, MoundFootprint) && floor.All(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(on.X, on.Z)) >= (q.Radius + MoundFootprint) * FootprintKept))
                            {
                                floor.Add((on, MoundFootprint));
                                growth.Add(new OvergrowthPlacement(OvergrowthKind.GroveMound, on, turn, 0.8f + (0.5f * pick), pick));
                            }
                            else if (roll < ScavengerShare + CatcherShare && floor.All(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(on.X, on.Z)) >= (q.Radius + CatcherFootprint) * FootprintKept))
                            {
                                floor.Add((on, CatcherFootprint));
                                growth.Add(new OvergrowthPlacement(OvergrowthKind.GroveCatchers, on, turn, 0.8f + (0.5f * pick), pick));
                            }

                            float size = Footed(open, ground, LitterFootprint) ? 1f : Footed(open, ground, LitterFootprint * SmallLitter) ? SmallLitter : 0f;
                            if (pick < LitterShare && size > 0f && litter.All(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(on.X, on.Z)) >= (q.Radius + (LitterFootprint * size)) * LitterOverlap))
                            {
                                litter.Add((on, LitterFootprint * size));
                                growth.Add(new OvergrowthPlacement(OvergrowthKind.GroveLitter, on, turn, size * (0.9f + (0.3f * roll)), roll));
                            }

                            break;
                    }
                }
        }

        return groves;
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-22, the concept sheet "ivory heath - a community"): how
    /// many ivory heaths the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyIvoryHeaths { get; set; }

    // Ivory heaths: on dead flowstone floors (the drip's formations and coats near the floor, and no drip, standing water or
    // flow left to speak of), the heaths chosen by how much such floor lies round them, so far apart. Sparse, a desert: storage bulbs widely
    // spaced (each holding HeathGround of bare floor round it; the great ones where there is room above, hollow elders a
    // share), salt bloom at their feet, young tufts scattered in the gaps, pods in the floor's hollows. A piece's footprint
    // and the room it needs above.
    private const float HeathRadius = 13f, HeathStep = 1.3f, HeathsApart = 30f, HeathRise = 2.5f, HeathDry = 0.08f;
    private const float BulbShare = 0.14f, GreatBulbShare = 0.2f, HollowShare = 0.2f, BloomShare = 0.7f, TuftsShare = 0.1f, PodShare = 0.35f, HeathGround = 1.4f;
    private const float HeathFlowstoneNear = 2.5f, HeathFlowstoneAbove = 3f;
    private const int FewestHeathFloors = 6;
private static int MostHeathPieces => Area(300);

    private static (float Radius, float Height) HeathFootprint(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.IvoryBulbGreat => (1.2f + HeathGround, 4.2f),
        OvergrowthKind.IvoryBulb => (0.5f + HeathGround, 2f),
        OvergrowthKind.IvoryHollow => (0.65f + HeathGround, 1.6f),
        OvergrowthKind.IvoryTufts => (0.7f, 0.6f),
        _ => (0.3f, 0.2f),
    };

    /// <summary>
    /// Ivory heaths (#10096): where the drip once fell and has dried, an underground desert of water-hoarders - swollen
    /// ivory storage bulbs widely spaced, crowned by stiff bristle fans that comb the last moisture from the air, old hollow
    /// elders weathered to pitted shells, young bristle tufts in the bare floor between, salt bloom crusting the flowstone
    /// round the bulbs' feet, seed pods in the hollows. Returns each heath's centre and radius.
    /// </summary>
    private static List<(Vector3 Centre, float Radius)> IvoryHeaths(float[] open, bool[] flowstone, float[] drip, float[] standing, float[] flow, ulong seed, int count,
        List<OvergrowthPlacement> growth, IReadOnlyList<Vector3> route, IEnumerable<(Vector3 At, float Radius)> taken, bool skyOpen, Func<Vector3, bool> deep)
    {
        float Open(Vector3 p) => Trilinear(open, p);
        float Surface(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;
        bool Dry(Vector3 p) => Surface(p.X, p.Z) - p.Y >= FunnelUnderGround
            && MathF.Max(GridTrilinear(drip, p), GridTrilinear(standing, p)) < HeathDry && GridTrilinear(flow, p) < QuietFlow;
        // The drip's dead work near: flowstone (the formations it grew, the coats it laid) within HeathFlowstoneNear of the
        // floor, from just under it to HeathFlowstoneAbove over it.
        int near = (int)MathF.Ceiling(HeathFlowstoneNear / FieldStep), above = (int)MathF.Ceiling(HeathFlowstoneAbove / FieldStep);
        bool Flowstone(Vector3 floor)
        {
            int x = (int)(floor.X / FieldStep), y = (int)(floor.Y / FieldStep), z = (int)(floor.Z / FieldStep);
            for (int dy = -1; dy <= above; dy++)
                for (int dz = -near; dz <= near; dz++)
                    for (int dx = -near; dx <= near; dx++)
                    {
                        int sx = x + dx, sy = y + dy, sz = z + dz;
                        if (sx >= 0 && sx < FieldX && sy >= 0 && sy < FieldY && sz >= 0 && sz < FieldZ && flowstone[FieldIndex(sx, sy, sz)])
                        {
                            return true;
                        }
                    }

            return false;
        }

        List<Vector3> floors = [];
        for (float z = 2f; z < GridZ * GridCell - 2f; z += 2f)
            for (float y = 2f; y < GridY * GridCell - 2f; y += EcologyStep)
                for (float x = 2f; x < GridX * GridCell - 2f; x += 2f)
                {
                    Vector3 p = new(x, y, z);
                    if (Open(p) > 0f && Open(p - new Vector3(0f, EcologyStep, 0f)) <= 0f && Open(p + new Vector3(0f, 2.5f, 0f)) > 0f)
                    {
                        Vector3 f = Floor(open, p);
                        if (Flowstone(f) && Dry(f + new Vector3(0f, 0.5f, 0f)))
                        {
                            floors.Add(f);
                        }
                    }
                }

        Dictionary<(int, int, int), int> cells = [];
        foreach (Vector3 f in floors)
        {
            (int, int, int) k = ((int)MathF.Floor(f.X / 4f), (int)MathF.Floor(f.Y / 4f), (int)MathF.Floor(f.Z / 4f));
            cells[k] = cells.GetValueOrDefault(k) + 1;
        }

        int Area(Vector3 f)
        {
            int cx = (int)MathF.Floor(f.X / 4f), cy = (int)MathF.Floor(f.Y / 4f), cz = (int)MathF.Floor(f.Z / 4f), n = 0, reach = (int)(HeathRadius / 4f);
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    n += cells.GetValueOrDefault((cx + dx, cy, cz + dz));
                }

            return n;
        }

        List<(Vector3 Centre, float Radius)> heaths = [];
        foreach (Vector3 c in floors.Where(f => Area(f) >= FewestHeathFloors).OrderByDescending(Area).ThenBy(f => NaturalCavern.Hash(seed ^ 0x1E01UL, (int)f.X, (int)f.Y, (int)f.Z)))
        {
            if (heaths.Count >= count)
            {
                break;
            }

            if (deep(c) && heaths.All(o => Vector3.Distance(o.Centre, c) >= HeathsApart)
                && taken.All(t => Vector2.Distance(new Vector2(t.At.X, t.At.Z), new Vector2(c.X, c.Z)) >= t.Radius + HeathRadius || MathF.Abs(t.At.Y - c.Y) > 8f))
            {
                heaths.Add((c, HeathRadius));
            }
        }

        foreach ((Vector3 centre, float _) in heaths)
        {
            List<(Vector3 At, float Radius)> placed = [];
            for (int pass = 0; pass < 2; pass++)
            for (float dz = -HeathRadius; dz <= HeathRadius; dz += HeathStep)
                for (float dx = -HeathRadius; dx <= HeathRadius; dx += HeathStep)
                {
                    if ((dx * dx) + (dz * dz) > HeathRadius * HeathRadius || placed.Count >= MostHeathPieces)
                    {
                        continue;
                    }

                    int hx = (int)((centre.X + dx) * 4f), hz = (int)((centre.Z + dz) * 4f);
                    float roll = NaturalCavern.Hash(seed ^ (0x1E11UL + (ulong)(pass * 0x111)), hx, (int)centre.Y, hz), pick = NaturalCavern.Hash(seed ^ (0x1E12UL + (ulong)(pass * 0x113)), hz, (int)centre.Y, hx);
                    Vector3 probe = new(centre.X + dx + ((pick - 0.5f) * HeathStep * 0.7f), centre.Y + 1.5f, centre.Z + dz + ((roll - 0.5f) * HeathStep * 0.7f));
                    if (Open(probe) <= 0f)
                    {
                        continue;
                    }

                    Vector3 foot = Floor(open, probe);
                    if (MathF.Abs(foot.Y - centre.Y) > HeathRise || !Dry(foot + new Vector3(0f, 0.5f, 0f)))
                    {
                        continue;
                    }

                    OvergrowthKind? choice = null;
                    float scale = 0.75f + (0.5f * pick);
                    if (pass == 0 && roll < BulbShare)
                    {
                        choice = pick < GreatBulbShare && Open(foot + new Vector3(0f, HeathFootprint(OvergrowthKind.IvoryBulbGreat).Height, 0f)) > 0f ? OvergrowthKind.IvoryBulbGreat
                            : pick > 1f - HollowShare ? OvergrowthKind.IvoryHollow : OvergrowthKind.IvoryBulb;
                        if (choice == OvergrowthKind.IvoryBulbGreat)
                        {
                            scale = 0.85f + (0.3f * pick / GreatBulbShare);
                        }
                    }
                    else if (pass == 1)
                    {
                        // A hollow: the floor round it stands higher.
                        float around = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ }.Average(v => Floor(open, foot + (v * 0.9f) + new Vector3(0f, 1f, 0f)).Y);
                        choice = around - foot.Y > HollowDepth && roll < PodShare ? OvergrowthKind.IvoryPods : roll < TuftsShare ? OvergrowthKind.IvoryTufts : null;
                    }

                    if (choice is not OvergrowthKind kind)
                    {
                        continue;
                    }

                    (float radius, float height) = HeathFootprint(kind);
                    bool bulb = kind is OvergrowthKind.IvoryBulb or OvergrowthKind.IvoryBulbGreat or OvergrowthKind.IvoryHollow;
                    if ((bulb && Open(foot + new Vector3(0f, height * scale, 0f)) <= 0f)
                        || placed.Any(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(foot.X, foot.Z)) < (q.Radius + radius) * FootprintKept)
                        || (bulb && NearRoute(route, foot, (radius - HeathGround) * scale + RouteClear))
                        || (!bulb && !Footed(open, foot, radius * 0.6f)))
                    {
                        continue;
                    }

                    placed.Add((foot, radius));
                    float a = MathF.Tau * roll;
                    Vector3 facing = new(MathF.Sin(a), 0f, MathF.Cos(a));
                    growth.Add(new OvergrowthPlacement(kind, foot - new Vector3(0f, bulb ? 0.06f * scale : MatSink, 0f), facing, scale, pick));
                    // Salt bloom crusts the flowstone round a bulb's foot, where its roots draw the water out.
                    if (bulb && pick * 7f % 1f < BloomShare)
                    {
                        Vector3 bloom = Floor(open, foot + (new Vector3(MathF.Cos(a), 0f, -MathF.Sin(a)) * 0.4f * scale) + new Vector3(0f, 1f, 0f));
                        if (MathF.Abs(bloom.Y - foot.Y) < 0.4f && Footed(open, bloom, 0.5f))
                        {
                            growth.Add(new OvergrowthPlacement(OvergrowthKind.IvoryBloom, bloom - new Vector3(0f, MatSink, 0f), -facing, 0.9f + (0.4f * roll), roll));
                        }
                    }
                }
        }

        return heaths;
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-22, the concept sheet "cushion barrens - a community"): how
    /// many cushion barrens the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyCushionBarrens { get; set; }

    // Cushion barrens: on the rubble slopes of fallen-in chambers (the breakdown's blocks), the colonies chosen by how much
    // rubble floor lies within BarrensRadius of a chamber, so far apart. Hummocks on and near the rubble and on the floor's
    // other high points (the great ones on high points, where the floor stands BarrensHigh over the floor round it, with room above), split elders a share of them,
    // their domes clear of the route; mats between, overlapping; buds at the colony's rim. A piece's footprint and the room
    // it needs above.
    private const float BarrensRadius = 12f, BarrensStep = 1f, BarrensApart = 25f, BarrensRise = 4f, BarrensHigh = 0.3f, BarrensProbe = 1.8f;
    private const float HummockShare = 0.32f, GreatShare = 0.3f, SplitShare = 0.15f, RustShare = 0.4f, CushionMatShare = 0.35f, BudShare = 0.18f, BudFrom = 0.65f;
    private const float RubbleNear = 2.5f, DomeSink = 0.12f, HummockScaleLeast = 0.75f, HummockScaleSpread = 0.55f;
    private const int FewestRubbleFloors = 4;
private static int MostBarrensPieces => Area(400);

    private static (float Radius, float Height) CushionFootprint(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.CushionGreat => (2.3f, 2.8f),
        OvergrowthKind.CushionSplit => (1.2f, 1.3f),
        OvergrowthKind.CushionHummock or OvergrowthKind.CushionHummockRust => (1.15f, 1.1f),
        OvergrowthKind.CushionMat => (1f, 0.1f),
        _ => (0.4f, 0.3f),
    };

    /// <summary>
    /// Cushion barrens (#10096): a colony smothers the rubble of a fallen-in chamber until the slope is one soft living
    /// thing - hummocks of close nap swelling over the blocks, knee-high to enormous, packed up the slope, old ones split
    /// open on their ribbed orange-red interiors, thin mats joining them over the bare rock, and young buds at the colony's
    /// rim creeping onto the floor. Returns each colony's centre and radius.
    /// </summary>
    private static List<(Vector3 Centre, float Radius)> CushionBarrens(float[] open, bool[] rubble, ulong seed, int count, IReadOnlyList<Vector3> chambers,
        List<OvergrowthPlacement> growth, IReadOnlyList<Vector3> route, IEnumerable<(Vector3 At, float Radius)> taken, bool skyOpen, Func<Vector3, bool> deep)
    {
        float Open(Vector3 p) => Trilinear(open, p);
        float Surface(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;
        bool Rubble(Vector3 p)
        {
            int x = (int)(p.X / FieldStep), y = (int)((p.Y - (FieldStep * 0.5f)) / FieldStep), z = (int)(p.Z / FieldStep);
            return x >= 0 && x < FieldX && y >= 0 && y < FieldY && z >= 0 && z < FieldZ && rubble[FieldIndex(x, y, z)];
        }

        bool NearRubble(Vector3 floor) => Rubble(floor)
            || new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ }.Any(v => Rubble(Floor(open, floor + (v * RubbleNear) + new Vector3(0f, 1f, 0f))));

        // Each chamber's rubble floor round it.
        List<(Vector3 Centre, int Rubble)> candidates = [];
        foreach (Vector3 chamber in chambers)
        {
            if (Open(chamber) <= 0f)
            {
                continue;
            }

            Vector3 centre = Floor(open, chamber);
            if (Surface(centre.X, centre.Z) - centre.Y < FunnelUnderGround)
            {
                continue;
            }

            int n = 0;
            for (float dz = -BarrensRadius; dz <= BarrensRadius; dz += 2f)
                for (float dx = -BarrensRadius; dx <= BarrensRadius; dx += 2f)
                {
                    Vector3 probe = centre + new Vector3(dx, 1.5f, dz);
                    if ((dx * dx) + (dz * dz) <= BarrensRadius * BarrensRadius && Open(probe) > 0f && Rubble(Floor(open, probe)))
                    {
                        n++;
                    }
                }

            if (n >= FewestRubbleFloors)
            {
                candidates.Add((centre, n));
            }
        }

        List<(Vector3 Centre, float Radius)> colonies = [];
        foreach ((Vector3 c, int _) in candidates.OrderByDescending(c => c.Rubble).ThenBy(c => NaturalCavern.Hash(seed ^ 0xC051UL, (int)c.Centre.X, (int)c.Centre.Y, (int)c.Centre.Z)))
        {
            if (colonies.Count >= count)
            {
                break;
            }

            if (deep(c) && colonies.All(o => Vector3.Distance(o.Centre, c) >= BarrensApart)
                && taken.All(t => Vector2.Distance(new Vector2(t.At.X, t.At.Z), new Vector2(c.X, c.Z)) >= t.Radius + BarrensRadius || MathF.Abs(t.At.Y - c.Y) > 8f))
            {
                colonies.Add((c, BarrensRadius));
            }
        }

        foreach ((Vector3 centre, float _) in colonies)
        {
            List<(Vector3 At, float Radius)> placed = [];
            for (int pass = 0; pass < 2; pass++)
            for (float dz = -BarrensRadius; dz <= BarrensRadius; dz += BarrensStep)
                for (float dx = -BarrensRadius; dx <= BarrensRadius; dx += BarrensStep)
                {
                    float d = MathF.Sqrt((dx * dx) + (dz * dz));
                    if (d > BarrensRadius || placed.Count >= MostBarrensPieces)
                    {
                        continue;
                    }

                    int hx = (int)((centre.X + dx) * 4f), hz = (int)((centre.Z + dz) * 4f);
                    float roll = NaturalCavern.Hash(seed ^ (0xC001UL + (ulong)(pass * 0x111)), hx, (int)centre.Y, hz), pick = NaturalCavern.Hash(seed ^ (0xC002UL + (ulong)(pass * 0x113)), hz, (int)centre.Y, hx);
                    Vector3 probe = new(centre.X + dx + ((pick - 0.5f) * BarrensStep * 0.7f), centre.Y + 2.5f, centre.Z + dz + ((roll - 0.5f) * BarrensStep * 0.7f));
                    if (Open(probe) <= 0f)
                    {
                        continue;
                    }

                    Vector3 foot = Floor(open, probe);
                    if (MathF.Abs(foot.Y - centre.Y) > BarrensRise || Surface(foot.X, foot.Z) - foot.Y < FunnelUnderGround)
                    {
                        continue;
                    }

                    OvergrowthKind? choice = null;
                    float around = pass == 0 ? new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ }.Average(v => Floor(open, foot + (v * BarrensProbe) + new Vector3(0f, 1.5f, 0f)).Y) : foot.Y;
                    if (pass == 0 && roll < HummockShare && (NearRubble(foot) || foot.Y - around > BarrensHigh))
                    {
                        choice = foot.Y - around > BarrensHigh && pick < GreatShare && Open(foot + new Vector3(0f, CushionFootprint(OvergrowthKind.CushionGreat).Height, 0f)) > 0f ? OvergrowthKind.CushionGreat
                            : pick > 1f - SplitShare ? OvergrowthKind.CushionSplit
                            : NaturalCavern.Hash(seed ^ 0xC003UL, hx, hz, (int)centre.Y) < RustShare ? OvergrowthKind.CushionHummockRust : OvergrowthKind.CushionHummock;
                    }
                    else if (pass == 1)
                    {
                        choice = d > BarrensRadius * BudFrom && roll < BudShare ? OvergrowthKind.CushionBuds : roll < CushionMatShare ? OvergrowthKind.CushionMat : null;
                    }

                    if (choice is not OvergrowthKind kind)
                    {
                        continue;
                    }

                    (float radius, float height) = CushionFootprint(kind);
                    bool dome = kind is OvergrowthKind.CushionGreat or OvergrowthKind.CushionSplit or OvergrowthKind.CushionHummock or OvergrowthKind.CushionHummockRust;
                    float scale = dome && kind != OvergrowthKind.CushionGreat ? HummockScaleLeast + (HummockScaleSpread * pick) : 0.85f + (0.3f * pick);
                    float kept = kind == OvergrowthKind.CushionMat ? MatOverlap : FootprintKept;
                    if ((dome && Open(foot + new Vector3(0f, height * scale, 0f)) <= 0f)
                        || placed.Any(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(foot.X, foot.Z)) < (q.Radius + (radius * scale)) * kept)
                        || (dome && NearRoute(route, foot, (radius * scale) + RouteClear))
                        || (!dome && !Footed(open, foot, radius * 0.6f)))
                    {
                        continue;
                    }

                    placed.Add((foot, radius * scale));
                    float a = MathF.Tau * roll;
                    growth.Add(new OvergrowthPlacement(kind, foot - new Vector3(0f, dome ? DomeSink * scale : MatSink, 0f), new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), scale, pick));
                }
        }

        return colonies;
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-22, the concept sheet "veil forest - a community"): how
    /// many veil forests the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyVeilForests { get; set; }

    // Veil forests: on the draft's line - the routes between the cave's openings (from the arrival's sink to each other
    // sink), the air blowing towards the higher opening - in its widest places (wall to wall across the draft, measured
    // VeilProbe above the floor, out to VeilReach either way, at least VeilNarrowest) with room above for a fan, so far apart.
    // Within VeilRadius of each: fans (the great ones where there is room) faced square to the draft at the line's nearest
    // point, their trunks clear of the route; behind each trunk a drift trailing downwind, puffs in it; comb fans along the
    // walls' feet. A piece's footprint (a fan's is its lace's reach, so neighbouring fans hardly cross) and the room it needs
    // above (and its lace's half width either side at its middle height).
    private const float VeilRadius = 11f, VeilStep = 1.2f, VeilsApart = 30f, VeilNarrowest = 7f, VeilProbe = 1.5f, VeilReach = 14f, VeilRise = 2.5f;
    private const float FanShare = 0.18f, GreatFanShare = 0.45f, CombShare = 0.45f, CombWall = 1.1f, DriftBehind = 0.5f, PuffShare = 0.6f, PuffAlong = 1.6f;
    private const float FanTrunkRadius = 0.4f, FanScaleSpread = 0.3f;
    private static int MostVeilPieces => Area(300);

    private static (float Radius, float Height, float Span) VeilFootprint(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VeilFanGreat => (3f, 8.8f, 3.6f),
        OvergrowthKind.VeilFan => (2f, 5.6f, 2.3f),
        OvergrowthKind.VeilComb => (0.75f, 0.7f, 0f),
        _ => (0.5f, 0.4f, 0f),
    };

    /// <summary>
    /// Veil forests (#10096): where the air moves through a cave between its openings, great porous lace fans on twisted
    /// trunks stand square to the draft, sieving what it carries, their buttress roots braced upwind; young comb fans line
    /// the walls' feet facing the same way; behind each trunk what the fans sift settles in a pale drift trailing downwind,
    /// small puffballs and cups rooting in it - so the forest shows which way the air moves. Returns each forest's centre and
    /// radius.
    /// </summary>
    private static List<(Vector3 Centre, float Radius)> VeilForests(float[] open, ulong seed, int count, IReadOnlyList<IReadOnlyList<Vector3>> drafts,
        List<OvergrowthPlacement> growth, IReadOnlyList<Vector3> route, IEnumerable<(Vector3 At, float Radius)> taken, bool skyOpen, Func<Vector3, bool> deep)
    {
        float Open(Vector3 p) => Trilinear(open, p);
        float Surface(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;

        // The draft's line: each route between openings, its points with the way the air moves there (towards the higher end).
        List<(Vector3 At, Vector3 Way)> line = [];
        foreach (IReadOnlyList<Vector3> draft in drafts)
        {
            float toEnd = draft.Count > 1 && draft[^1].Y >= draft[0].Y ? 1f : -1f;
            for (int i = 1; i < draft.Count - 1; i++)
            {
                Vector3 way = draft[i + 1] - draft[i - 1];
                way.Y = 0f;
                if (way.LengthSquared() > 1e-4f)
                {
                    line.Add((draft[i], Vector3.Normalize(way) * toEnd));
                }
            }
        }

        if (line.Count == 0)
        {
            return [];
        }

        Vector3 WayAt(Vector3 p) => line.MinBy(l => Vector3.DistanceSquared(l.At, p)).Way;

        // The widest places on the line, with room above for a fan, under the ground.
        List<(Vector3 Floor, float Width)> candidates = [];
        foreach ((Vector3 at, Vector3 way) in line)
        {
            Vector3 floor = Floor(open, at);
            if (Surface(floor.X, floor.Z) - floor.Y < FunnelUnderGround || Open(floor + new Vector3(0f, VeilFootprint(OvergrowthKind.VeilFan).Height, 0f)) <= 0f)
            {
                continue;
            }

            Vector3 across = new(-way.Z, 0f, way.X);
            float Reach(Vector3 dir)
            {
                float d = 0f;
                while (d < VeilReach && Open(floor + new Vector3(0f, VeilProbe, 0f) + (dir * d)) > 0f) d += 0.5f;
                return d;
            }

            float width = Reach(across) + Reach(-across);
            if (width >= VeilNarrowest)
            {
                candidates.Add((floor, width));
            }
        }

        List<(Vector3 Centre, float Radius)> forests = [];
        foreach ((Vector3 c, float _) in candidates.OrderByDescending(c => c.Width).ThenBy(c => NaturalCavern.Hash(seed ^ 0x7E11UL, (int)c.Floor.X, (int)c.Floor.Y, (int)c.Floor.Z)))
        {
            if (forests.Count >= count)
            {
                break;
            }

            if (deep(c) && forests.All(f => Vector3.Distance(f.Centre, c) >= VeilsApart)
                && taken.All(t => Vector2.Distance(new Vector2(t.At.X, t.At.Z), new Vector2(c.X, c.Z)) >= t.Radius + VeilRadius || MathF.Abs(t.At.Y - c.Y) > 8f))
            {
                forests.Add((c, VeilRadius));
            }
        }

        foreach ((Vector3 centre, float _) in forests)
        {
            List<(Vector3 At, float Radius)> placed = [];
            for (int pass = 0; pass < 2; pass++)
            for (float dz = -VeilRadius; dz <= VeilRadius; dz += VeilStep)
                for (float dx = -VeilRadius; dx <= VeilRadius; dx += VeilStep)
                {
                    if ((dx * dx) + (dz * dz) > VeilRadius * VeilRadius || placed.Count >= MostVeilPieces)
                    {
                        continue;
                    }

                    int hx = (int)((centre.X + dx) * 4f), hz = (int)((centre.Z + dz) * 4f);
                    float roll = NaturalCavern.Hash(seed ^ (0x7E01UL + (ulong)(pass * 0x111)), hx, (int)centre.Y, hz), pick = NaturalCavern.Hash(seed ^ (0x7E02UL + (ulong)(pass * 0x113)), hz, (int)centre.Y, hx);
                    Vector3 probe = new(centre.X + dx + ((pick - 0.5f) * VeilStep * 0.7f), centre.Y + 1.5f, centre.Z + dz + ((roll - 0.5f) * VeilStep * 0.7f));
                    if (Open(probe) <= 0f)
                    {
                        continue;
                    }

                    Vector3 foot = Floor(open, probe);
                    if (MathF.Abs(foot.Y - centre.Y) > VeilRise || Surface(foot.X, foot.Z) - foot.Y < FunnelUnderGround)
                    {
                        continue;
                    }

                    Vector3 way = WayAt(foot), across = new(-way.Z, 0f, way.X);
                    OvergrowthKind? choice = null;
                    float scale = 1f + ((pick - 0.5f) * FanScaleSpread);
                    if (pass == 0 && roll < FanShare)
                    {
                        // A fan: a great one where its lace has room, else the common one.
                        foreach (OvergrowthKind fan in pick < GreatFanShare ? new[] { OvergrowthKind.VeilFanGreat, OvergrowthKind.VeilFan } : [OvergrowthKind.VeilFan])
                        {
                            (float _, float h, float span) = VeilFootprint(fan);
                            Vector3 middle = foot + new Vector3(0f, h * scale * 0.7f, 0f);
                            if (Open(foot + new Vector3(0f, h * scale, 0f)) > 0f && Open(middle + (across * span * scale)) > 0f && Open(middle - (across * span * scale)) > 0f)
                            {
                                choice = fan;
                                break;
                            }
                        }
                    }
                    else if (pass == 1 && roll < CombShare && new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ }.Any(v => Open(foot + new Vector3(0f, 0.5f, 0f) + (v * CombWall)) <= 0f))
                    {
                        choice = OvergrowthKind.VeilComb;
                    }

                    if (choice is not OvergrowthKind kind)
                    {
                        continue;
                    }

                    (float radius, float height, float _) = VeilFootprint(kind);
                    bool fanned = kind is OvergrowthKind.VeilFan or OvergrowthKind.VeilFanGreat;
                    if ((kind == OvergrowthKind.VeilComb && Open(foot + new Vector3(0f, height, 0f)) <= 0f)
                        || placed.Any(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(foot.X, foot.Z)) < (q.Radius + radius) * FootprintKept)
                        || (fanned && NearRoute(route, foot, FanTrunkRadius + RouteClear))
                        || (kind == OvergrowthKind.VeilComb && !Footed(open, foot, radius * 0.6f)))
                    {
                        continue;
                    }

                    placed.Add((foot, radius));
                    // Every piece faces downwind (its +z the draft's way): a fan's face square to it, its roots braced upwind.
                    growth.Add(new OvergrowthPlacement(kind, foot - new Vector3(0f, 0.05f, 0f), way, fanned ? scale : 0.85f + (0.3f * pick), pick));
                    if (!fanned)
                    {
                        continue;
                    }

                    // The drift in the trunk's lee, trailing downwind, puffs in it further along.
                    Vector3 lee = Floor(open, foot + (way * DriftBehind * scale) + new Vector3(0f, 1f, 0f));
                    if (MathF.Abs(lee.Y - foot.Y) < 0.6f && Footed(open, lee, 0.6f))
                    {
                        growth.Add(new OvergrowthPlacement(OvergrowthKind.VeilDrift, lee - new Vector3(0f, MatSink, 0f), way, 0.8f + (0.4f * roll), pick));
                        Vector3 puffs = Floor(open, lee + (way * PuffAlong * (0.6f + (0.4f * pick))) + (across * (roll - 0.5f) * 0.6f) + new Vector3(0f, 1f, 0f));
                        if (pick < PuffShare && MathF.Abs(puffs.Y - lee.Y) < 0.5f && Footed(open, puffs, 0.3f))
                        {
                            growth.Add(new OvergrowthPlacement(OvergrowthKind.VeilPuffs, puffs - new Vector3(0f, 0.02f, 0f), way, 0.85f + (0.3f * roll), roll));
                        }
                    }
                }
        }

        return forests;
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-22, the concept sheet "ribbon thicket - a community"): how
    /// many ribbon thickets the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyRibbonThickets { get; set; }

    // Ribbon thickets: along the routes the water traced through the cave, where the passage is wet (standing or running water at its floor, or drip
    // somewhere up to RibbonWetAbove over it, at least RibbonDamp),
    // between RibbonNarrowest and RibbonWidest across (wall to wall, measured RibbonProbe above the floor, up to RibbonReach
    // either way) and tall enough for an arch scaled to its width; runs of at least RibbonRunFewest such places, the longest
    // first. An arch every RibbonArchEvery route points (they lie about 2 m apart), sized to the passage (the narrow arch spans 6 m,
    // the wide 9, each scaled within these bounds), a curtain from the ceiling between arches where it is high enough, shed
    // beds on the floor beside the arches' feet.
    private const float RibbonWetAbove = 12f, RibbonDamp = 0.02f, RibbonNarrowest = 3.6f, RibbonWidest = 12f, RibbonProbe = 1.5f, RibbonReach = 7f;
    private const float ArchSpan = 6f, ArchRise = 5f, WideSpan = 9f, WideRise = 7f, ArchHeadroom = 0.4f, CurtainRoom = 4.2f, ShedShare = 0.6f;
    private const float NarrowScaleLeast = 0.6f, NarrowScaleMost = 1.25f, WideScaleLeast = 0.8f, WideScaleMost = 1.35f;
    /// <summary>How far round a thicket's places the dark's other growth gives way and later communities keep clear.</summary>
    private const float ThicketReach = 5f;

    private const int RibbonRunFewest = 3, RibbonArchEvery = 1, MostRibbonArches = 30;
    private const float RibbonArchApart = 1.8f;

    /// <summary>
    /// Ribbon thickets (#10096): in tall, wet passages, woody arches rooted in both walls span the way, their curtains of
    /// long twisting ribbons hanging to the floor for the traveller to push through (the ribbons do not collide; the arches'
    /// feet do, against the walls), knot nurseries at their crowns, more curtains hung from the ceiling between them, and
    /// shed beds of fallen ribbons rotting on the floor. Returns each thicket's places along the route.
    /// </summary>
    private static List<Vector3> RibbonThickets(float[] open, float[] drip, float[] standing, float[] flow, ulong seed, int count,
        IReadOnlyList<IReadOnlyList<Vector3>> routes, List<OvergrowthPlacement> growth, IEnumerable<(Vector3 At, float Radius)> taken, Func<Vector3, bool> deep)
    {
        // Every route the water's paths trace through the cave (they share their start: an arch is not placed twice).
        List<(IReadOnlyList<Vector3> Route, List<int> Run)> runs = [];
        foreach (IReadOnlyList<Vector3> route in routes)
        {
            runs.AddRange(Runs(route).Select(r => (route, r)));
        }

        List<Vector3> places = [];
        int arches = 0;
        foreach ((IReadOnlyList<Vector3> route, List<int> chosen) in runs.Where(r => deep(r.Route[r.Run[r.Run.Count / 2]])).OrderByDescending(r => r.Run.Count).Take(count))
        {
            arches = Thicket(route, chosen, arches);
        }

        return places;

        // Runs of fitting places along a route, bridging a single place that does not fit (the thicket runs on past it).
        IEnumerable<List<int>> Runs(IReadOnlyList<Vector3> route)
        {
            List<int> run = [];
            int misses = 0;
            for (int i = 0; i <= route.Count; i++)
            {
                if (i < route.Count && Fit(route, i) is not null
                    && taken.All(t => Vector2.Distance(new Vector2(t.At.X, t.At.Z), new Vector2(route[i].X, route[i].Z)) >= t.Radius || MathF.Abs(t.At.Y - route[i].Y) > 8f))
                {
                    run.Add(i);
                    misses = 0;
                    continue;
                }

                if (i < route.Count && run.Count > 0 && ++misses <= 1)
                {
                    continue;
                }

                if (run.Count >= RibbonRunFewest)
                {
                    yield return run;
                }

                run = [];
                misses = 0;
            }
        }

        float Open(Vector3 p) => Trilinear(open, p);
        (Vector3 Centre, Vector3 Across, Vector3 Along, float Width, float Height, OvergrowthKind Kind, float Scale)? Fit(IReadOnlyList<Vector3> route, int i)
        {
            if (i < 1 || i >= route.Count - 1)
            {
                return null;
            }

            Vector3 along = route[i + 1] - route[i - 1];
            along.Y = 0f;
            if (along.LengthSquared() < 1e-4f)
            {
                return null;
            }

            along = Vector3.Normalize(along);
            Vector3 across = new(-along.Z, 0f, along.X);
            Vector3 floor = Floor(open, route[i]);
            // Wet: water stands at the floor or drips from somewhere in the passage's height above it.
            float wet = MathF.Max(GridTrilinear(standing, floor + Vector3.UnitY), GridTrilinear(flow, floor + Vector3.UnitY));
            for (float up = 1f; up <= RibbonWetAbove && wet < RibbonDamp; up += 1f)
            {
                wet = MathF.Max(wet, GridTrilinear(drip, floor + new Vector3(0f, up, 0f)));
            }

            if (wet < RibbonDamp)
            {
                return null;
            }

            float Reach(Vector3 dir)
            {
                float d = 0f;
                while (d < RibbonReach && Open(floor + new Vector3(0f, RibbonProbe, 0f) + (dir * d)) > 0f) d += 0.25f;
                return d;
            }

            float left = Reach(-across), right = Reach(across), width = left + right;
            if (width < RibbonNarrowest || width > RibbonWidest || left >= RibbonReach || right >= RibbonReach)
            {
                return null;
            }

            Vector3 centre = Floor(open, floor + (across * ((right - left) / 2f)) + new Vector3(0f, 1f, 0f));
            float height = Ceiling(open, centre + new Vector3(0f, 1f, 0f)).Y - centre.Y;
            bool wide = width > ArchSpan * NarrowScaleMost;
            float scale = wide ? Math.Clamp(width / WideSpan, WideScaleLeast, WideScaleMost) : Math.Clamp(width / ArchSpan, NarrowScaleLeast, NarrowScaleMost);
            if (height < ((wide ? WideRise : ArchRise) * scale) + ArchHeadroom)
            {
                return null;
            }

            return (centre, across, along, width, height, wide ? OvergrowthKind.RibbonArchWide : OvergrowthKind.RibbonArch, scale);
        }

        int Thicket(IReadOnlyList<Vector3> route, List<int> chosen, int arches)
        {
            Vector3? previous = null;
            for (int k = 0; k < chosen.Count && arches < MostRibbonArches; k += RibbonArchEvery)
            {
                if (Fit(route, chosen[k]) is not var (centre, across, along, _, height, kind, scale) || places.Any(q => Vector3.Distance(q, centre) < RibbonArchApart))
                {
                    continue;
                }

                float pick = NaturalCavern.Hash(seed ^ 0x81B0UL, (int)(centre.X * 4f), (int)centre.Y, (int)(centre.Z * 4f));
                // The arch's span (its x) across the passage: its +z, the facing, along the way.
                growth.Add(new OvergrowthPlacement(kind, centre - new Vector3(0f, 0.15f, 0f), along, scale, pick));
                places.Add(centre);
                arches++;
                if (previous is Vector3 before)
                {
                    // A curtain from the ceiling between this arch and the last, where it is high enough.
                    Vector3 mid = Vector3.Lerp(before, centre, 0.5f);
                    Vector3 roof = Ceiling(open, mid + new Vector3(0f, 1f, 0f));
                    if (roof.Y - mid.Y > CurtainRoom)
                    {
                        growth.Add(new OvergrowthPlacement(OvergrowthKind.RibbonCurtain, roof + new Vector3(0f, 0.1f, 0f), along, 0.9f + (0.3f * pick), pick));
                    }
                }

                previous = centre;
                // Shed beds beside the feet, along the way.
                foreach (float side in new[] { -1f, 1f })
                {
                    float roll = NaturalCavern.Hash(seed ^ (side > 0 ? 0x81B1UL : 0x81B2UL), (int)(centre.X * 4f), (int)centre.Y, (int)(centre.Z * 4f));
                    Vector3 at = Floor(open, centre + (across * side * 1.2f * scale) + (along * (roll - 0.5f) * 1.5f) + new Vector3(0f, 1f, 0f));
                    if (roll < ShedShare && MathF.Abs(at.Y - centre.Y) < 0.8f && Footed(open, at, 0.9f))
                    {
                        float a = MathF.Tau * roll;
                        growth.Add(new OvergrowthPlacement(OvergrowthKind.RibbonShed, at - new Vector3(0f, MatSink, 0f), new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), 0.9f + (0.3f * roll), roll));
                    }
                }
            }

            return arches;
        }
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-22, the concept sheet "glass steppe - a community"): how
    /// many glass steppes the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyGlassSteppes { get; set; }

    // Glass steppes: on dry, still, open floor (no drip or standing water to speak of, nothing running, on a level long
    // dry), the colonies chosen by how much such floor lies round them, so far apart, each out to SteppeRadius. Sparse: tufts
    // every so often (large ones fewer), backs rarer, grit in the floor's hollows, spires at the rim; a light for every so
    // many tufts, at most so many a colony.
    private const float SteppeRadius = 14f, SteppeStep = 1.1f, SteppesApart = 30f, SteppeDryDamp = 0.08f, SteppeRise = 2.5f;
    private const float TuftShare = 0.22f, LargeTuftShare = 0.25f, BackShare = 0.035f, GritShare = 0.35f, SpireShare = 0.05f, SpireFrom = 0.75f;
    private const float HollowDepth = 0.12f, SteppeLightEvery = 7f, SteppeLightAbove = 0.6f;
    private const int SteppeLights = 4, SteppeAfterEras = 1, MostSteppePieces = 600;

    private static (float Radius, float Height) SteppeFootprint(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.SteppeTuftLarge => (0.7f, 1.2f),
        OvergrowthKind.SteppeBack => (1.2f, 1f),
        OvergrowthKind.SteppeGrit => (1f, 0.3f),
        OvergrowthKind.SteppeSpire => (0.5f, 5f),
        _ => (0.4f, 0.8f),
    };

    /// <summary>
    /// Glass steppes (#10096): on a cave's dry, still, open floors, an organism lies buried, its stiff glass feelers rising in
    /// sparse tufts whose beaded tips glow warm - the steppe's scattered light - its smooth milky backs breaking the floor
    /// here and there, the glass it sheds lying as grit in the hollows, and tall sentinel spires standing at the colony's
    /// rim. Returns each colony's centre and radius.
    /// </summary>
    private static List<(Vector3 Centre, float Radius)> GlassSteppes(float[] open, byte[] formed, float[] drip, float[] standing, float[] flow, int eras,
        float finalTable, ulong seed, int count, List<OvergrowthPlacement> growth, List<EcologyPlacement> ecology, IReadOnlyList<Vector3> route,
        IEnumerable<(Vector3 At, float Radius)> taken, bool skyOpen, Func<Vector3, bool> deep)
    {
        float Open(Vector3 p) => Trilinear(open, p);
        float Surface(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;
        bool Dry(Vector3 p)
        {
            // Underground only: never the plateau over a cave open to the sky.
            if (Surface(p.X, p.Z) - p.Y < FunnelUnderGround)
            {
                return false;
            }

            int sample = FieldIndex(Math.Clamp((int)(p.X / FieldStep), 0, FieldX - 1), Math.Clamp((int)(p.Y / FieldStep), 0, FieldY - 1), Math.Clamp((int)(p.Z / FieldStep), 0, FieldZ - 1));
            int age = formed[sample] == Unformed ? 0 : eras - 1 - formed[sample];
            return age >= SteppeAfterEras && p.Y > finalTable && MathF.Max(GridTrilinear(drip, p), GridTrilinear(standing, p)) < SteppeDryDamp && GridTrilinear(flow, p) < QuietFlow;
        }

        List<Vector3> floors = [];
        for (float z = 2f; z < GridZ * GridCell - 2f; z += 2f)
            for (float y = 2f; y < GridY * GridCell - 2f; y += EcologyStep)
                for (float x = 2f; x < GridX * GridCell - 2f; x += 2f)
                {
                    Vector3 p = new(x, y, z);
                    if (Open(p) > 0f && Open(p - new Vector3(0f, EcologyStep, 0f)) <= 0f && Open(p + new Vector3(0f, 2.5f, 0f)) > 0f && Dry(p))
                    {
                        floors.Add(Floor(open, p));
                    }
                }

        Dictionary<(int, int, int), int> cells = [];
        foreach (Vector3 f in floors)
        {
            (int, int, int) k = ((int)MathF.Floor(f.X / 4f), (int)MathF.Floor(f.Y / 4f), (int)MathF.Floor(f.Z / 4f));
            cells[k] = cells.GetValueOrDefault(k) + 1;
        }

        int Area(Vector3 f)
        {
            int cx = (int)MathF.Floor(f.X / 4f), cy = (int)MathF.Floor(f.Y / 4f), cz = (int)MathF.Floor(f.Z / 4f), n = 0, reach = (int)(SteppeRadius / 4f);
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    n += cells.GetValueOrDefault((cx + dx, cy, cz + dz));
                }

            return n;
        }

        List<(Vector3 Centre, float Radius)> colonies = [];
        foreach (Vector3 c in floors.OrderByDescending(Area).ThenBy(f => NaturalCavern.Hash(seed ^ 0x57E9UL, (int)f.X, (int)f.Y, (int)f.Z)))
        {
            if (colonies.Count >= count)
            {
                break;
            }

            if (deep(c) && colonies.All(o => Vector3.Distance(o.Centre, c) >= SteppesApart)
                && taken.All(t => Vector2.Distance(new Vector2(t.At.X, t.At.Z), new Vector2(c.X, c.Z)) >= t.Radius + SteppeRadius || MathF.Abs(t.At.Y - c.Y) > 8f))
            {
                colonies.Add((c, SteppeRadius));
            }
        }

        foreach ((Vector3 centre, float _) in colonies)
        {
            List<(Vector3 At, float Radius)> placed = [];
            int tufts = 0, lights = 0;
            for (int pass = 0; pass < 2; pass++)
            for (float dz = -SteppeRadius; dz <= SteppeRadius; dz += SteppeStep)
                for (float dx = -SteppeRadius; dx <= SteppeRadius; dx += SteppeStep)
                {
                    float d = MathF.Sqrt((dx * dx) + (dz * dz));
                    if (d > SteppeRadius || placed.Count >= MostSteppePieces)
                    {
                        continue;
                    }

                    int hx = (int)((centre.X + dx) * 4f), hz = (int)((centre.Z + dz) * 4f);
                    float roll = NaturalCavern.Hash(seed ^ (0x5701UL + (ulong)(pass * 0x111)), hx, (int)centre.Y, hz), pick = NaturalCavern.Hash(seed ^ (0x5702UL + (ulong)(pass * 0x113)), hz, (int)centre.Y, hx);
                    Vector3 probe = new(centre.X + dx + ((pick - 0.5f) * SteppeStep * 0.7f), centre.Y + 1.5f, centre.Z + dz + ((roll - 0.5f) * SteppeStep * 0.7f));
                    if (Open(probe) <= 0f)
                    {
                        continue;
                    }

                    Vector3 foot = Floor(open, probe);
                    float reach = d / SteppeRadius;
                    if (MathF.Abs(foot.Y - centre.Y) > SteppeRise || !Dry(foot + new Vector3(0f, 0.5f, 0f)))
                    {
                        continue;
                    }

                    OvergrowthKind? choice;
                    if (pass == 0)
                    {
                        // The large pieces first: spires at the rim, backs anywhere.
                        choice = reach > SpireFrom && roll < SpireShare ? OvergrowthKind.SteppeSpire : roll < BackShare ? OvergrowthKind.SteppeBack : null;
                    }
                    else
                    {
                        // A hollow: the floor round it stands higher.
                        float around = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ }.Average(v => Floor(open, foot + (v * 0.9f) + new Vector3(0f, 1f, 0f)).Y);
                        choice = around - foot.Y > HollowDepth && roll < GritShare ? OvergrowthKind.SteppeGrit
                            : roll < TuftShare ? (pick < LargeTuftShare ? OvergrowthKind.SteppeTuftLarge : OvergrowthKind.SteppeTuft) : null;
                    }

                    if (choice is not OvergrowthKind kind)
                    {
                        continue;
                    }

                    (float radius, float height) = SteppeFootprint(kind);
                    bool flat = kind is OvergrowthKind.SteppeGrit or OvergrowthKind.SteppeBack;
                    if (Open(foot + new Vector3(0f, height, 0f)) <= 0f
                        || placed.Any(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(foot.X, foot.Z)) < (q.Radius + radius) * FootprintKept)
                        || (kind == OvergrowthKind.SteppeSpire && NearRoute(route, foot, radius + RouteClear, height))
                        || (flat && !Footed(open, foot, radius * 0.8f)))
                    {
                        continue;
                    }

                    placed.Add((foot, radius));
                    float a = MathF.Tau * pick;
                    float sink = kind == OvergrowthKind.SteppeBack ? 0.25f : kind == OvergrowthKind.SteppeGrit ? MatSink : 0.06f;
                    growth.Add(new OvergrowthPlacement(kind, foot - new Vector3(0f, sink, 0f), new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), 0.85f + (0.3f * roll), pick));
                    bool glows = kind is OvergrowthKind.SteppeTuft or OvergrowthKind.SteppeTuftLarge or OvergrowthKind.SteppeSpire;
                    if (glows && ++tufts % (int)SteppeLightEvery == 0 && lights < SteppeLights)
                    {
                        lights++;
                        ecology.Add(new EcologyPlacement(EcologyKind.SteppeGlow, foot + new Vector3(0f, kind == OvergrowthKind.SteppeSpire ? 4.2f : SteppeLightAbove, 0f), Vector3.UnitY, 1f, (uint)(pick * uint.MaxValue)));
                    }
                }
        }

        return colonies;
    }

    /// <summary>
    /// For the underground ecology study (#10096; owner, 2026-10-21, the concept sheet "funnel garden - a community"): how
    /// many funnel gardens the karst caves generated next grow, 0 none. Set, it overrides the choice by region and depth (#10304).
    /// </summary>
    internal static int StudyFunnelGardens { get; set; }

    // Funnel gardens: on the floor under a sink (the shaft that dust and drip fall down), found within FunnelDrop of it, out
    // to FunnelRadius; keystones (giants, where there is room above, then tall funnels) nearest the fall, small funnels and
    // unfurled elders further out, debris patches between, drinkers at the large funnels' feet. A piece's footprint (its cup
    // overhangs the others' little) and the room it needs above.
    private const float FunnelDrop = 40f, FunnelRadius = 12f, FunnelStep = 0.8f, FunnelRise = 3f, FunnelKeystoneReach = 0.4f, FunnelSmallReach = 0.8f;
    private const float FunnelDensest = 0.5f, UnfurledShare = 0.35f, DebrisShare = 0.25f, DrinkerOffset = 1.1f, FunnelRouteClear = 1f;
    private const int MostFunnelPieces = 500;

    /// <summary>How far under the ground a funnel garden's shaft is entered (metres); its floor lies at least twice that under it.</summary>
    private const float FunnelUnderGround = 4f;

    private static (float Radius, float Height) FunnelFootprint(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.FunnelGiant => (2.2f, 7f),
        OvergrowthKind.FunnelTall => (1.3f, 4f),
        OvergrowthKind.FunnelSmall => (0.65f, 2f),
        OvergrowthKind.FunnelUnfurled => (1.6f, 1.2f),
        _ => (0.9f, 0.6f),
    };

    /// <summary>
    /// Funnel gardens (#10096): under a cave's sinks, where dust and drip fall from the surface, living catch-basins stand
    /// open to what falls - the great funnels nearest the fall, smaller ones and old unfurled elders (their debris become
    /// gardens) further out, debris patches between, and at the large funnels' feet the spill of their overflow with its
    /// ring of drinker tubes. Returns each garden's centre and radius.
    /// </summary>
    private static List<(Vector3 Centre, float Radius)> FunnelGardens(float[] open, ulong seed, int count, Vector3[] sinks, List<OvergrowthPlacement> growth,
        IReadOnlyList<Vector3> route, IEnumerable<(Vector3 At, float Radius)> taken, bool skyOpen, Func<Vector3, bool> deep)
    {
        float Open(Vector3 p) => Trilinear(open, p);
        // The ground over a place: the plateau over a cave open to the sky, else the top of the space.
        float Surface(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;
        List<(Vector3 Floor, int Area)> candidates = [];
        foreach (Vector3 sink in sinks)
        {
            // Down the shaft from the sink to the first floor under it, starting below the ground (over a cave open to the
            // sky, the sink's top is the open air above the plateau), and a cave floor well under the ground.
            Vector3 p = new(sink.X, MathF.Min(sink.Y, Surface(sink.X, sink.Z) - FunnelUnderGround), sink.Z);
            while (p.Y > 2f && Open(p) <= 0f && sink.Y - p.Y < FunnelDrop) p.Y -= 0.5f;
            if (Open(p) <= 0f)
            {
                continue;
            }

            Vector3 floor = Floor(open, p);
            if (Surface(floor.X, floor.Z) - floor.Y < FunnelUnderGround * 2f)
            {
                continue;
            }

            if (!deep(floor) || taken.Any(t => Vector2.Distance(new Vector2(t.At.X, t.At.Z), new Vector2(floor.X, floor.Z)) < t.Radius + FunnelRadius && MathF.Abs(t.At.Y - floor.Y) < 8f))
            {
                continue;
            }

            int area = 0;
            for (float dz = -FunnelRadius; dz <= FunnelRadius; dz += 2f)
                for (float dx = -FunnelRadius; dx <= FunnelRadius; dx += 2f)
                {
                    Vector3 q = new(floor.X + dx, floor.Y + 1f, floor.Z + dz);
                    area += (dx * dx) + (dz * dz) <= FunnelRadius * FunnelRadius && Open(q) > 0f && MathF.Abs(Floor(open, q).Y - floor.Y) < FunnelRise ? 1 : 0;
                }

            candidates.Add((floor, area));
        }

        List<(Vector3 Centre, float Radius)> gardens = [];
        foreach ((Vector3 centre, int _) in candidates.OrderByDescending(c => c.Area).Take(count))
        {
            gardens.Add((centre, FunnelRadius));
            List<(Vector3 At, float Radius)> placed = [];
            for (int pass = 0; pass < 3; pass++)
            for (float dz = -FunnelRadius; dz <= FunnelRadius; dz += FunnelStep)
                for (float dx = -FunnelRadius; dx <= FunnelRadius; dx += FunnelStep)
                {
                    float d = MathF.Sqrt((dx * dx) + (dz * dz));
                    if (d > FunnelRadius || placed.Count >= MostFunnelPieces)
                    {
                        continue;
                    }

                    int hx = (int)((centre.X + dx) * 4f), hz = (int)((centre.Z + dz) * 4f);
                    float roll = NaturalCavern.Hash(seed ^ (0xF001UL + (ulong)(pass * 0x10B)), hx, (int)centre.Y, hz), pick = NaturalCavern.Hash(seed ^ (0xF002UL + (ulong)(pass * 0x10D)), hz, (int)centre.Y, hx);
                    Vector3 probe = new(centre.X + dx + ((pick - 0.5f) * FunnelStep * 0.6f), centre.Y + 1.5f, centre.Z + dz);
                    if (Open(probe) <= 0f)
                    {
                        continue;
                    }

                    Vector3 foot = Floor(open, probe);
                    float reach = d / FunnelRadius;
                    if (MathF.Abs(foot.Y - centre.Y) > FunnelRise || Surface(foot.X, foot.Z) - foot.Y < FunnelUnderGround)
                    {
                        continue;
                    }

                    OvergrowthKind? choice = pass switch
                    {
                        0 => reach < FunnelKeystoneReach && roll < FunnelDensest * (1f - (reach / FunnelKeystoneReach) * 0.5f)
                            ? (Open(foot + new Vector3(0f, FunnelFootprint(OvergrowthKind.FunnelGiant).Height, 0f)) > 0f && pick < 0.45f ? OvergrowthKind.FunnelGiant : OvergrowthKind.FunnelTall)
                            : null,
                        1 => reach < FunnelSmallReach && roll < FunnelDensest * (1f - reach)
                            ? (pick < UnfurledShare || reach > 0.5f && pick < UnfurledShare * 1.6f ? OvergrowthKind.FunnelUnfurled : OvergrowthKind.FunnelSmall) : null,
                        _ => roll < DebrisShare * (1f - (reach * 0.6f)) ? OvergrowthKind.FunnelDebris : null,
                    };
                    if (choice is not OvergrowthKind kind)
                    {
                        continue;
                    }

                    (float radius, float height) = FunnelFootprint(kind);
                    bool stands = kind is OvergrowthKind.FunnelGiant or OvergrowthKind.FunnelTall;
                    if (Open(foot + new Vector3(0f, height, 0f)) <= 0f || Open(foot + new Vector3(0f, height * 0.5f, 0f)) <= 0f
                        || placed.Any(q => Vector2.Distance(new Vector2(q.At.X, q.At.Z), new Vector2(foot.X, foot.Z)) < (q.Radius + radius) * FootprintKept)
                        || (stands && NearRoute(route, foot, (radius * 0.4f) + FunnelRouteClear))
                        || (!stands && !Footed(open, foot, radius * 0.7f)))
                    {
                        continue;
                    }

                    placed.Add((foot, radius));
                    float a = MathF.Tau * pick;
                    growth.Add(new OvergrowthPlacement(kind, foot - new Vector3(0f, 0.1f, 0f), new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), 0.85f + (0.3f * roll), pick));
                    if (stands)
                    {
                        // Its overflow at its foot, the drinkers' ring round the spill, on the side the floor allows.
                        Vector3 side = new(MathF.Cos(a * 1.7f), 0f, MathF.Sin(a * 1.7f));
                        Vector3 spill = Floor(open, foot + (side * radius * DrinkerOffset * 0.6f) + new Vector3(0f, 1f, 0f));
                        if (MathF.Abs(spill.Y - foot.Y) < 0.6f && Footed(open, spill, 0.7f))
                        {
                            growth.Add(new OvergrowthPlacement(OvergrowthKind.FunnelDrinkers, spill - new Vector3(0f, MatSink, 0f), side, 0.9f + (0.3f * pick), roll));
                        }
                    }
                }
        }

        return gardens;
    }

    /// <summary>
    /// Whether a flat piece (a mat, litter) of this radius lies on floor all round its foot - floor within FootedRise of it
    /// at FootedReach of its radius each way - so it never overhangs a ledge's edge, its flat underside showing from below.
    /// </summary>
    private static bool Footed(float[] open, Vector3 foot, float radius)
    {
        foreach (Vector2 d in (ReadOnlySpan<Vector2>)[Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY,
            Vector2.Normalize(Vector2.One), -Vector2.Normalize(Vector2.One), Vector2.Normalize(new Vector2(1f, -1f)), Vector2.Normalize(new Vector2(-1f, 1f))])
        {
            Vector3 above = foot + new Vector3(d.X * radius * FootedReach, FootedRise, d.Y * radius * FootedReach);
            if (Trilinear(open, above) <= 0f || MathF.Abs(Floor(open, above).Y - foot.Y) > FootedRise)
            {
                return false;
            }
        }

        return true;
    }

    // A flat piece's footprint checked this share of its radius out, its floor within this rise of its foot.
    private const float FootedReach = 0.65f, FootedRise = 0.6f;

    /// <summary>A litter patch that does not lie whole on the floor at its size may at this share of it.</summary>
    private const float SmallLitter = 0.5f;

    /// <summary>The floor under an open place: the first rock looking down.</summary>
    private static Vector3 Floor(float[] open, Vector3 p)
    {
        while (p.Y > 1f && Trilinear(open, p) > 0f) p.Y -= 0.1f;
        return p + new Vector3(0f, 0.08f, 0f);
    }

    /// <summary>
    /// How far a ceiling stands over the floor beneath a piece hung <paramref name="length"/> from it (#10304): the least
    /// drop under its foot and at four points round it at a share of its footprint, those taken at the crown's height, where
    /// the piece is widest - one in rock means the crown would hang into a wall or a shaft too narrow for it, so none.
    /// </summary>
    private static float Headroom(float[] open, Vector3 roof, float footprint, float length)
    {
        float least = float.MaxValue;
        for (int k = 0; k < 5; k++)
        {
            float a = k * MathF.Tau / 4f;
            Vector3 at = k == 0 ? roof - new Vector3(0f, 0.3f, 0f)
                : roof + new Vector3(MathF.Cos(a) * footprint * HeadroomReach, -length * CrownShare, MathF.Sin(a) * footprint * HeadroomReach);
            if (Trilinear(open, at) <= 0f)
            {
                return 0f;
            }

            least = MathF.Min(least, roof.Y - Floor(open, at).Y);
        }

        return least;
    }

    /// <summary>The share of a hung piece's footprint out to which its headroom is measured, and of its length down to its crown.</summary>
    private const float HeadroomReach = 0.7f, CrownShare = 0.6f;

    /// <summary>The length a grove's hung piece reaches down from its ceiling at its scale (#10304), for the bank's headroom check.</summary>
    internal static float HungLength(OvergrowthKind kind, float scale) => kind switch
    {
        OvergrowthKind.GroveHanger => HangerLength * scale,
        OvergrowthKind.GroveHangerSmall => SmallHangerLength * scale,
        OvergrowthKind.GroveSacs => SacLength * scale,
        _ => 0f,
    };

    /// <summary>The headroom a grove leaves under its hung pieces (metres).</summary>
    internal const float GroveHeadroomMetres = GroveHeadroom;

    /// <summary>The ceiling over an open place: the first rock looking up.</summary>
    private static Vector3 Ceiling(float[] open, Vector3 p)
    {
        while (p.Y < GridY * GridCell && Trilinear(open, p) > 0f) p.Y += 0.1f;
        return p - new Vector3(0f, 0.05f, 0f);
    }

    /// <summary>The open-facing normal of a wall within half a metre beside a place, or null when none is.</summary>
    private static Vector3? Wall(float[] open, Vector3 p)
    {
        foreach (Vector3 side in (ReadOnlySpan<Vector3>)[Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ])
        {
            if (Trilinear(open, p + (side * 0.5f)) <= 0f)
            {
                return -side;
            }
        }

        return null;
    }

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
        /// <summary>The cave's living (and mineral) light: where each growth stands (#10129).</summary>
        internal required IReadOnlyList<EcologyPlacement> Ecology { get; init; }

        /// <summary>The stranger communities the cave grew (#10304), in the order they were grown.</summary>
        internal IReadOnlyList<CaveCommunity> Communities { get; init; } = [];

        /// <summary>The region and depth the cave was generated for (#10304).</summary>
        internal CaveSetting Setting { get; init; }

        /// <summary>The overgrowth the dark grows (#10096): mushrooms on damp still floors, root tangles under the surface.</summary>
        internal required IReadOnlyList<OvergrowthPlacement> Overgrowth { get; init; }
        /// <summary>A tiankeng's floor centre and radius, or null; and whether the cave opens to the sky (it has a surface).</summary>
        internal required (Vector3 Floor, float Radius)? Tiankeng { get; init; }
        /// <summary>The cave's flooded places: lakes, pools and mist sumps (#10094).</summary>
        internal required IReadOnlyList<WaterZone> WaterZones { get; init; }

        /// <summary>Streams falling into the chasm from the passages it cut.</summary>
        internal required Waterfall[] Waterfalls { get; init; }

        /// <summary>The chasm, when the cave has one.</summary>
        internal required ChasmShape? Chasm { get; init; }

        /// <summary>The great hall, when the cave has one.</summary>
        internal required Hall? GreatHall { get; init; }
        internal required bool SkyOpen { get; init; }
        /// <summary>The era each field sample was opened in (<see cref="Unformed"/> where never), and the samples sediment filled.</summary>
        internal required byte[] Formed { get; init; }
        internal required bool[] Sediment { get; init; }

        /// <summary>The samples fallen rock filled (rubble) and formations grew in (flowstone), #10095.</summary>
        internal required bool[] Rubble { get; init; }
        internal required bool[] Flowstone { get; init; }
        /// <summary>The last era that carried water through the cave: what it opened is the active level.</summary>
        internal required int LastFlowEra { get; init; }

        /// <summary>
        /// The rock a solid voxel is made of, from the age of the space it faces: washed rock on the active level (opened by
        /// the last flowing era, or within reach of today's water), patinated rock on levels dry for two eras or more,
        /// sediment where it settled, plain rock between (#10094).
        /// </summary>
        internal BlockId RockAt(Vector3 p)
        {
            int x = Math.Clamp((int)(p.X / FieldStep), 0, FieldX - 1), y = Math.Clamp((int)(p.Y / FieldStep), 0, FieldY - 1), z = Math.Clamp((int)(p.Z / FieldStep), 0, FieldZ - 1);
            int here = FieldIndex(x, y, z);
            if (Sediment[here])
            {
                // Mud where the water still reaches, dry silt above it.
                return p.Y < FinalTable + WetReach ? SedimentRock : SiltRock;
            }

            if (Rubble[here])
            {
                return RubbleRock;
            }

            if (Flowstone[here])
            {
                return FlowstoneRock;
            }

            // Under the sky: the plateau's ground and the tiankeng's floor are soil and turf, not cave rock.
            if (SkyOpen)
            {
                bool openAbove = y + 1 < FieldY && Open[FieldIndex(x, y + 1, z)] > 0f;
                bool underSky = p.Y > SurfaceAt(p.X, p.Z, Seed) - SoilDepth
                    || (Tiankeng is (Vector3 floor, float radius) && Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(floor.X, floor.Z)) < radius && p.Y < floor.Y + TiankengFloorSoil);
                if (underSky && openAbove)
                {
                    return BlockId.Grass;
                }

                if (underSky)
                {
                    return BlockId.Dirt;
                }
            }

            // The youngest opening beside the voxel is the space its face looks into.
            int faces = -1;
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < 0 || ny < 0 || nz < 0 || nx >= FieldX || ny >= FieldY || nz >= FieldZ)
                        {
                            continue;
                        }

                        byte era = Formed[FieldIndex(nx, ny, nz)];
                        if (era != Unformed && Open[FieldIndex(nx, ny, nz)] > 0f)
                        {
                            faces = Math.Max(faces, era);
                        }
                    }

            if (faces < 0)
            {
                return PlainRock;
            }

            int age = Eras.Length - 1 - faces;
            // A floor within the water's reach, where it runs quietly or stands, is mud.
            bool facesUp = y + 1 < FieldY && Open[FieldIndex(x, y + 1, z)] > 0f;
            if (facesUp && p.Y < FinalTable + WetReach && GridTrilinear(Flow, p + new Vector3(0f, FieldStep, 0f)) < MudQuietFlow)
            {
                return SedimentRock;
            }

            // Older rock under dripping water is coated in flowstone.
            float drip = GridTrilinear(Drip, p + new Vector3(0f, GridCell, 0f));
            if (faces != LastFlowEra && age >= 1 && drip >= FlowstoneDrip)
            {
                return FlowstoneRock;
            }

            bool washed = faces == LastFlowEra || p.Y < FinalTable + WetReach;
            // Life on the rock (#10129), in patches: slime where the water keeps it wet (not on what faces down), moss where
            // light reaches, moonmilk on long-dry rock under a light drip.
            bool facesDown = y > 0 && Open[FieldIndex(x, y - 1, z)] > 0f && !facesUp;
            if (washed && !facesDown && Patch(p, SlimeFrequency, 0x5A1UL) > SlimeAbove)
            {
                return SlimeRock;
            }

            if (Lit(p) && Patch(p, MossFrequency, 0x5A2UL) > MossAbove)
            {
                return MossRock;
            }

            if (!washed && age >= PatinaAfterEras && drip >= MoonmilkDrip && Patch(p, MoonmilkFrequency, 0x5A3UL) > MoonmilkAbove)
            {
                return MoonmilkRock;
            }

            return washed ? WashedRock
                : age >= PatinaAfterEras ? PatinatedRock
                : PlainRock;
        }

        /// <summary>A patchy noise for where growth takes (0 to 1).</summary>
        private float Patch(Vector3 p, float frequency, ulong salt) => NaturalCavern.Noise(p * frequency, Seed ^ salt);

        /// <summary>
        /// Whether light reaches rock (#10129): near a sink, where the surface's water (and light) comes in, or under a
        /// tiankeng's or a chasm's opening, within reach of the sky's light down its walls.
        /// </summary>
        private bool Lit(Vector3 p)
        {
            foreach (Vector3 sink in SinkPoints)
            {
                if (Vector3.DistanceSquared(sink, p) < MossNearSink * MossNearSink)
                {
                    return true;
                }
            }

            if (!SkyOpen || SurfaceAt(p.X, p.Z, Seed) - p.Y > MossBelowSky)
            {
                return false;
            }

            Vector2 at = new(p.X, p.Z);
            return (Tiankeng is (Vector3 floor, float radius) && Vector2.Distance(at, new Vector2(floor.X, floor.Z)) < radius + MossBeyondOpening)
                || (Chasm is ChasmShape chasm && Vector2.Distance(at, chasm.Centre) < chasm.Radius * (1f + ChasmWobble) + ChasmBand + MossBeyondOpening);
        }
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

    /// <summary>
    /// Where the last generation's time went, by stage (#10304: finding what grows faster than the box): each mark adds the
    /// time since the last to its stage. Not thread-safe; for the bank's sequential timings.
    /// </summary>
    internal static readonly Dictionary<string, double> Stages = [];

    private static long markAt;

    private static void Mark(string stage)
    {
        long now = Stopwatch.GetTimestamp();
        Stages[stage] = Stages.GetValueOrDefault(stage) + Stopwatch.GetElapsedTime(markAt, now).TotalMilliseconds;
        markAt = now;
    }

    /// <param name="setting">The region above and the cave's depth (#10304); null for a temperate, middling cave.</param>
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable) Generate(ulong seed, double voxelSize = 1d, Centrepiece? centrepiece = null,
        CaveSetting? setting = null)
    {
        Stages.Clear();
        ShortestLog.Clear();
        markAt = Stopwatch.GetTimestamp();
        Result cave = Build(seed, centrepiece, setting);
        Mark("build-tail");
        Func<Vector3, float> shape = cave.At;
        (DungeonVolume volume, RockDensity rock) = StudyField.Sample(shape, ChunksX, ChunksY, ChunksZ, voxelSize, cave.RockAt);
        Mark("sample");
        // The arrival is underground, at the foot of the first sink, even when the surface is open above it.
        float below = cave.SkyOpen ? MathF.Min(cave.SinkPoints[0].Y, SurfaceAt(cave.SinkPoints[0].X, cave.SinkPoints[0].Z, seed) - SinkDepthBelowSurface) : cave.SinkPoints[0].Y;
        Vector3 arrive = StudyField.OnFloor(shape, cave.SinkPoints[0].X, cave.SinkPoints[0].Z, below);
        Vector3 spring = StudyField.OnFloor(shape, cave.Spring.X, cave.Spring.Z, cave.Spring.Y + 1f);
        // The lights: the arrival and the spring, a light at each feature off the way (the maze, bedding rooms, then
        // chambers), and the rest spread along the main route.
        List<Vector3> lights = [arrive + (Vector3.UnitY * LightAboveFloor), spring + (Vector3.UnitY * LightAboveFloor)];
        Vector3[] features = [.. (cave.Sponge is Vector3 sponge ? [sponge] : Array.Empty<Vector3>()), .. (cave.MazeAt is Vector3 maze ? [maze] : Array.Empty<Vector3>()),
            .. cave.Rifts, .. cave.BeddingRooms, .. cave.Chambers];
        foreach (Vector3 feature in features.Take(MostFeatureLights))
        {
            lights.Add(StudyField.OnFloor(shape, feature.X, feature.Z, feature.Y) + (Vector3.UnitY * LightAboveFloor));
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
        long cardsFrom = Stopwatch.GetTimestamp();
        List<OvergrowthPlacement> overgrowth = [.. cave.Overgrowth];
        List<MossCard> cards = MossCards(cave, overgrowth);
        Mark("cards");
        // Nothing grows under the water.
        // (An arch spans over water, its crown above it, and a curtain hangs from the ceiling: those stand.)
        overgrowth.RemoveAll(g => g.Kind is not (OvergrowthKind.RibbonArch or OvergrowthKind.RibbonArchWide or OvergrowthKind.RibbonCurtain)
            && cave.WaterZones.Any(zone => zone.BedUnder(g.At) is not null && zone.Surface > g.At.Y));
        DungeonLayout layout = new($"karst-cave-{seed:x}", new DungeonVolume(volume.ChunksX, volume.ChunksY, volume.ChunksZ, BlockId.Air),
            arrive, arrive + new Vector3(0.5f, 0f, 0f), lights)
        {
            Rock = rock,
            Natural = true,
            Shape = shape,
            VoxelSize = voxelSize,
            SkyOpen = cave.SkyOpen,
            GreatHall = cave.GreatHall is not null,
            // The Engine holds at most 64 fog volumes in a view.
            Mist = [.. (cave.GreatHall is Hall mistHall ? [HallMist(mistHall)] : Array.Empty<MistVolume>())
                .Concat(cave.WaterZones.Where(z => z.Kind == WaterZoneKind.MistSump).SelectMany(SumpMist)).Take(MostMistVolumes)],
            Waterfalls = cave.Waterfalls,
            WaterZones = cave.WaterZones,
            Forest = cave.SkyOpen ? SinkholeForest(cave) : [],
            MossCards = cards,
            Overgrowth = overgrowth,
            Abyss = cave.Chasm is ChasmShape abyss ? (new Vector3(abyss.Centre.X, 0f, abyss.Centre.Y), (abyss.Radius * (1f + ChasmWobble)) + ChasmBand) : null,
            Ecology = cave.Ecology,
            Communities = cave.Communities,
            MainRoute = cave.MainRoute,
        };
        string grown = string.Join(",", Enum.GetValues<MossCardKind>().Select(k => $"{k}:{cards.Count(c => c.Kind == k)}"));
        return (layout, plan, new DungeonVerdict(true, string.Create(CultureInfo.InvariantCulture,
            $"{Readout(cave)} mossCards={cards.Count} ({grown}) overgrowth={overgrowth.Count} in {Stopwatch.GetElapsedTime(cardsFrom).TotalMilliseconds:F0} ms"), 0), volume);
    }

    /// <summary>What a generated cave came to, in one line.</summary>
    internal static string Readout(Result cave) => string.Create(CultureInfo.InvariantCulture,
        $"karst cave: eras {string.Join("+", cave.Eras)}{(cave.Tiankeng is (Vector3 tf, float tr) ? $" tiankeng r={tr:F0} at {tf.X:F0},{tf.Y:F0},{tf.Z:F0}" : string.Empty)}{(cave.Chasm is ChasmShape ch ? $" chasm r={ch.Radius:F0} at {ch.Centre.X:F0},{ch.Centre.Y:F0}, {ch.Ledges.Length} ledges, {ch.Bridges.Length} bridges, {cave.Waterfalls.Length} waterfalls ({string.Join(" ", cave.Waterfalls.Select(w => $"{w.Drop:F0}m{(w.Lands ? "" : "+abyss")}"))})" : string.Empty)}{(cave.WaterZones.Count > 0 ? $" water: {string.Join(" ", cave.WaterZones.GroupBy(w => w.Kind).Select(g => $"{g.Count()} {g.Key}"))}" : string.Empty)}{(cave.GreatHall is Hall gh ? $" hall {gh.RadiusX * 2f:F0}x{gh.RadiusZ * 2f:F0}x{gh.Height:F0} m at {gh.Centre.X:F0},{gh.Floor:F0},{gh.Centre.Y:F0}, {gh.Pillars.Length} pillars" : string.Empty)}, {cave.Conduits} conduits, {cave.Chambers.Length} chambers, {cave.Rifts.Length} rifts, {cave.Formations} formations, communities [{string.Join(" ", cave.Communities)}] under {cave.Setting.Key}, generated in {cave.Milliseconds:F0} ms; passages ")
        + string.Join(", ", cave.Passages.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Name} {p.Metres:F0} m narrowest {p.Before:F2}->{p.After:F2} m widened {p.Widened}")));

    /// <summary>Generates the cave: the rock, the water's rounds through it, the shapes it leaves, and the passages checked.</summary>
    internal static Result Build(ulong seed, Centrepiece? centrepiece = null, CaveSetting? setting = null)
    {
        CaveSetting place = setting ?? CaveSetting.Temperate;
        long started = Stopwatch.GetTimestamp();
        DungeonRandom random = new(seed);
        markAt = started;
        float[] solubility = Rock(random, seed, out bool[] joint, out bool[] parting, out float[] beds, out JointPlane[] planes);
        Mark("rock");

        // Where water enters.
        Vector3[] sinks = new Vector3[Sinks];
        for (int i = 0; i < Sinks; i++)
        {
            sinks[i] = new Vector3(Between(random, 8f, (GridX * GridCell) - 30f), (GridY - 2.5f) * GridCell, Between(random, 8f, (GridZ * GridCell) - 8f));
        }

        float[] discharge = [.. Enumerable.Range(0, Sinks).Select(_ => Between(random, 0.5f, 1.5f))];

        // The cave's history: a few eras drawn without repeats, in the order they happen, the water table falling from
        // one to the next. A cave always has an era of flowing water.
        Era[] eras = DrawEras(random, centrepiece);
        // A chasm's place is chosen before the water runs, so no sink opens over it.
        (Vector2 Centre, float Radius)? chasmAt = null;
        if (eras.Contains(Era.Chasm))
        {
            Vector2 middle = new((FieldX * FieldStep * 0.5f) + Between(random, -ChasmWander, ChasmWander), (FieldZ * FieldStep * 0.5f) + Between(random, -ChasmWander, ChasmWander));
            float radius = Between(random, ChasmLeast, ChasmMost);
            chasmAt = (middle, radius);
            for (int i = 0; i < sinks.Length; i++)
            {
                Vector2 from = new Vector2(sinks[i].X, sinks[i].Z) - middle;
                float keep = radius * (1f + ChasmWobble) + SinkClearOfChasm;
                if (from.Length() < keep)
                {
                    Vector2 away = from.LengthSquared() > 1e-4f ? Vector2.Normalize(from) : Vector2.UnitX;
                    Vector2 moved = middle + (away * keep);
                    sinks[i] = new Vector3(Math.Clamp(moved.X, 8f, (GridX * GridCell) - 8f), sinks[i].Y, Math.Clamp(moved.Y, 8f, (GridZ * GridCell) - 8f));
                }
            }
        }

        float[] tables = [.. eras.Select((_, i) => eras.Length == 1 ? LowestTable : HighestTable + ((LowestTable - HighestTable) * i / (eras.Length - 1)))];
        float finalTable = tables[^1];

        float[] open = new float[FieldX * FieldY * FieldZ];
        Array.Fill(open, -FieldLimit);
        bool[] sediment = new bool[open.Length];
        // What fell (breakdown, collapses, boulders) and what grew (formations), for the rock it is made of (#10095).
        bool[] rubble = new bool[open.Length], flowstone = new bool[open.Length];
        // The era each open sample was first opened in, for what age does to it later.
        byte[] formed = new byte[open.Length];
        Array.Fill(formed, Unformed);
        float[] conduit = new float[GridX * GridY * GridZ];
        float[] activeFlow = new float[conduit.Length];
        List<Vector3> chambers = [], beddingRooms = [], mazes = [], rifts = [];
        List<(Vector3 At, float Share, int Era)> passages0 = [];
        Vector3? sponge = null;
        (Vector3 Floor, float Radius)? tiankeng = null;
        Hall? greatHall = null;
        ChasmShape? chasm = null;
        bool skyOpen = false;
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
                    Mark("flow");
                    break;
                case Era.JointRifts:
                    Rifts(open, planes, conduit, table, seed, rifts);
                    Mark("rifts");
                    break;
                case Era.FloodMaze:
                    FloodMazes(open, passages0, sinks[0], table, seed, random, mazes);
                    Mark("mazes");
                    break;
                case Era.Hypogene:
                    sponge = Hypogene(open, seed, random);
                    Mark("hypogene");
                    break;
                case Era.Infill:
                    Infill(open, formed, sediment, (byte)index, seed);
                    Mark("infill");
                    break;
                case Era.Breakdown:
                    Collapses(open, beds, passages0, index, random, chambers);
                    Mark("collapses");
                    break;
                case Era.Chasm:
                    LaySurface(open, seed);
                    Mark("surface");
                    chasm = Chasm(open, beds, passages0, chasmAt!.Value.Centre, chasmAt.Value.Radius, seed, random);
                    skyOpen = true;
                    break;
                case Era.GreatHall:
                    greatHall = GreatHall(open, beds, seed, random);
                    Mark("hall");
                    break;
                case Era.Tiankeng:
                    tiankeng = Tiankeng(open, beds, passages0, seed, random);
                    Mark("tiankeng");
                    skyOpen = true;
                    break;
            }

            for (int i = 0; i < open.Length; i++)
            {
                if (before[i] <= 0f && open[i] > 0f)
                {
                    formed[i] = (byte)index;
                }
            }

            if (eras[index] is Era.Breakdown or Era.GreatHall or Era.Tiankeng)
            {
                MarkFilled(before, open, rubble);
                Mark("markfilled");
            }
        }

        float[] beforeBreakdown = (float[])open.Clone();
        foreach (Vector3 chamber in chambers)
        {
            Breakdown(open, chamber, random);
        }

        MarkFilled(beforeBreakdown, open, rubble);
        Mark("breakdown");

        float[] beforeDrip = (float[])open.Clone();
        int formations = Drip(open, joint, formed, eras.Length, finalTable, random);
        MarkFilled(beforeDrip, open, flowstone);
        Mark("drip");

        Roughen(open, seed, beds, finalTable);
        Mark("roughen");
        Enclose(open, skyOpen, chasm);
        Mark("enclose");

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
        Mark("clearance");
        Vector3 start = sinks[0];
        // The routes made walkable (#10304): the main route from the first sink to the spring, then each other sink joined to
        // whichever place already joined is nearest to it (a tree grown outward, Prim's), so a route spans the gap to a
        // neighbour rather than the whole cave back to the first sink - in a large cave those long routes made the pass grow
        // with its area times its size.
        List<(string Name, Vector3 From, Vector3 To)> routes = [("spring", start, spring)];
        List<Vector3> joined = [start, spring];
        List<(int Index, Vector3 At)> waiting = [.. sinks.Skip(1).Select((s, i) => (i + 1, s))];
        while (waiting.Count > 0)
        {
            (int index, Vector3 at, Vector3 from) = waiting
                .SelectMany(w => joined.Select(j => (w.Index, w.At, From: j)))
                .MinBy(e => Vector3.DistanceSquared(e.At, e.From));
            routes.Add(($"sink{index}", from, at));
            joined.Add(at);
            waiting.RemoveAll(w => w.Index == index);
        }

        List<Passage> passages = [];
        List<Vector3> mainRoute = [];
        List<List<int>> routePaths = [];
        // Every route is widened where it pinches first, then the clearance is measured once and each route's final way found
        // (#10304: measuring it again after each widened route made a large cave's generation grow with its area squared).
        List<(float Before, int Widened)> widened = [.. routes.Select(r => WidenPassage(open, clearance, r.From, r.To))];
        if (widened.Any(w => w.Widened > 0))
        {
            clearance = Clearance(open);
        }

        Mark("passages-clearance");
        for (int r = 0; r < routes.Count; r++)
        {
            (string name, Vector3 from, Vector3 to) = routes[r];
            (Passage passage, List<int> path) = FinishPassage(clearance, name, from, to, widened[r].Before, widened[r].Widened);
            passages.Add(passage);
            routePaths.Add(path);
            if (name == "spring")
            {
                mainRoute = [.. path.Where((_, i) => i % 4 == 0).Select(FieldCentre)];
            }
        }

        // The searches' working arrays are a field's worth: let them go with this cave.
        searchValue = null;
        searchBack = null;
        searchStamp = null;

        Mark("passages");
        List<OvergrowthPlacement> darkGrowth = [];
        List<EcologyPlacement> ecology = Ecology(open, formed, activeFlow, drip, standing, eras.Length, finalTable, seed, skyOpen, darkGrowth);
        Mark("ecology");
        // The cave's stranger communities (#10304; owner, 2026-10-23): the study switches when any is set, else one to three
        // chosen by the region above and by depth, each grown where the cave has its sites and they lie deep enough. Each is
        // its own community: the dark's everyday growth gives way inside it, and later ones keep clear of earlier ones.
        List<(Vector3 Seep, float Radius)> orchards = [];
        List<(Vector3 At, float Radius)> claimed = [];
        List<CaveCommunity> grown = [];
        float GroundAbove(float x, float z) => skyOpen ? SurfaceAt(x, z, seed) : FieldY * FieldStep;
        Func<Vector3, bool> DeepFor(CaveCommunity community) =>
            p => place.DepthMetres + GroundAbove(p.X, p.Z) - p.Y >= CaveCommunities.Of(community).DepthMetres;
        int Grow(CaveCommunity community, int count, Func<Vector3, bool> deep)
        {
            switch (community)
            {
                case CaveCommunity.VentOrchard:
                {
                    orchards = VentOrchards(open, drip, activeFlow, seed, count, ecology, darkGrowth, mainRoute, skyOpen, greatHall, claimed, deep);
                    bool InOrchard(Vector3 p) => orchards.Any(o => Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(o.Seep.X, o.Seep.Z)) < o.Radius + OrchardEdge && MathF.Abs(p.Y - o.Seep.Y) < OrchardRise + 2f);
                    darkGrowth.RemoveAll(g => g.Kind == OvergrowthKind.Mushrooms && InOrchard(g.At));
                    ecology.RemoveAll(e => e.Kind == EcologyKind.GlowFungi && InOrchard(e.At));
                    claimed.AddRange(orchards.Select(o => (o.Seep, o.Radius)));
                    return orchards.Count;
                }

                case CaveCommunity.InvertedGrove:
                {
                    List<(Vector3 Roof, float Radius)> groves = InvertedGroves(open, seed, count, ecology, darkGrowth, skyOpen, claimed, deep);
                    // Under a grove the dark's root curtains and tangles, mushrooms and glow fungi give way.
                    bool UnderGrove(Vector3 p) => groves.Any(g => Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(g.Roof.X, g.Roof.Z)) < g.Radius + GroveEdge && p.Y <= g.Roof.Y + GroveRoofSpread);
                    darkGrowth.RemoveAll(g => g.Kind is OvergrowthKind.Mushrooms or OvergrowthKind.RootTangle && UnderGrove(g.At));
                    ecology.RemoveAll(e => e.Kind is EcologyKind.GlowFungi or EcologyKind.RootCurtain && UnderGrove(e.At));
                    claimed.AddRange(groves.Select(g => (g.Roof, g.Radius)));
                    return groves.Count;
                }

                case CaveCommunity.FunnelGarden:
                {
                    List<(Vector3 Centre, float Radius)> gardens = FunnelGardens(open, seed, count, sinks, darkGrowth, mainRoute, claimed, skyOpen, deep);
                    darkGrowth.RemoveAll(g => g.Kind == OvergrowthKind.Mushrooms
                        && gardens.Any(f => Vector2.Distance(new Vector2(g.At.X, g.At.Z), new Vector2(f.Centre.X, f.Centre.Z)) < f.Radius + 1f && MathF.Abs(g.At.Y - f.Centre.Y) < FunnelRise + 2f));
                    claimed.AddRange(gardens);
                    return gardens.Count;
                }

                case CaveCommunity.GlassSteppe:
                {
                    List<(Vector3 Centre, float Radius)> steppes = GlassSteppes(open, formed, drip, standing, activeFlow, eras.Length, finalTable, seed, count,
                        darkGrowth, ecology, mainRoute, claimed, skyOpen, deep);
                    // On a steppe the crystals and the dark's mushrooms give way.
                    bool OnSteppe(Vector3 p) => steppes.Any(s => Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(s.Centre.X, s.Centre.Z)) < s.Radius + 1f && MathF.Abs(p.Y - s.Centre.Y) < SteppeRise + 2f);
                    darkGrowth.RemoveAll(g => g.Kind == OvergrowthKind.Mushrooms && OnSteppe(g.At));
                    ecology.RemoveAll(e => e.Kind == EcologyKind.CrystalBloom && OnSteppe(e.At));
                    claimed.AddRange(steppes);
                    return steppes.Count;
                }

                case CaveCommunity.RibbonThicket:
                {
                    List<Vector3> thicket = RibbonThickets(open, drip, standing, activeFlow, seed, count,
                        [.. routePaths.Select(path => (IReadOnlyList<Vector3>)[.. path.Where((_, i) => i % 4 == 0).Select(FieldCentre)])], darkGrowth, claimed, deep);
                    // In a thicket the dark's mushrooms and root curtains give way.
                    bool InThicket(Vector3 p) => thicket.Any(t => Vector3.Distance(t, p) < ThicketReach);
                    darkGrowth.RemoveAll(g => g.Kind is OvergrowthKind.Mushrooms or OvergrowthKind.RootTangle && InThicket(g.At));
                    ecology.RemoveAll(e => e.Kind == EcologyKind.RootCurtain && InThicket(e.At));
                    claimed.AddRange(thicket.Select(t => (t, ThicketReach)));
                    return thicket.Count;
                }

                case CaveCommunity.VeilForest:
                {
                    // The draft's line: the routes from the arrival's sink to the cave's other sinks (its openings).
                    List<(Vector3 Centre, float Radius)> veils = VeilForests(open, seed, count,
                        [.. routes.Select((r, i) => (r, i)).Where(x => x.r.Name != "spring").Select(x => (IReadOnlyList<Vector3>)[.. routePaths[x.i].Where((_, k) => k % 4 == 0).Select(FieldCentre)])],
                        darkGrowth, mainRoute, claimed, skyOpen, deep);
                    darkGrowth.RemoveAll(g => g.Kind == OvergrowthKind.Mushrooms
                        && veils.Any(v => Vector2.Distance(new Vector2(g.At.X, g.At.Z), new Vector2(v.Centre.X, v.Centre.Z)) < v.Radius + 1f && MathF.Abs(g.At.Y - v.Centre.Y) < VeilRise + 2f));
                    claimed.AddRange(veils);
                    return veils.Count;
                }

                case CaveCommunity.CushionBarrens:
                {
                    List<(Vector3 Centre, float Radius)> barrens = CushionBarrens(open, rubble, seed, count, chambers, darkGrowth, mainRoute, claimed, skyOpen, deep);
                    darkGrowth.RemoveAll(g => g.Kind == OvergrowthKind.Mushrooms
                        && barrens.Any(b => Vector2.Distance(new Vector2(g.At.X, g.At.Z), new Vector2(b.Centre.X, b.Centre.Z)) < b.Radius + 1f && MathF.Abs(g.At.Y - b.Centre.Y) < BarrensRise + 2f));
                    claimed.AddRange(barrens);
                    return barrens.Count;
                }

                default:
                {
                    List<(Vector3 Centre, float Radius)> heaths = IvoryHeaths(open, flowstone, drip, standing, activeFlow, seed, count, darkGrowth, mainRoute, claimed, skyOpen, deep);
                    darkGrowth.RemoveAll(g => g.Kind == OvergrowthKind.Mushrooms
                        && heaths.Any(h => Vector2.Distance(new Vector2(g.At.X, g.At.Z), new Vector2(h.Centre.X, h.Centre.Z)) < h.Radius + 1f && MathF.Abs(g.At.Y - h.Centre.Y) < HeathRise + 2f));
                    claimed.AddRange(heaths);
                    return heaths.Count;
                }
            }
        }

        if (StudiedCommunities)
        {
            foreach (CaveCommunity community in Enum.GetValues<CaveCommunity>())
            {
                if (StudyCount(community) is int studied and > 0 && Grow(community, studied, _ => true) > 0)
                {
                    grown.Add(community);
                }
            }
        }
        else if (!StudyNoCommunities)
        {
            // The cave's floor lies at most its height deeper than its top.
            float deepest = place.DepthMetres + (FieldY * FieldStep);
            int target = CaveCommunities.Target(seed, place.DepthMetres);
            foreach (CaveCommunity community in CaveCommunities.Candidates(place, deepest, seed))
            {
                if (grown.Count >= target)
                {
                    break;
                }

                // A community counts only once it has grown a stand of its own, its signature pieces among them (#10304: a
                // grove over a shaft too narrow for its hangers grew only litter and root web); one that falls short is
                // undone - its pieces and lights go, the growth it displaced returns - and leaves room for the next.
                List<OvergrowthPlacement> growthBefore = [.. darkGrowth];
                List<EcologyPlacement> ecologyBefore = [.. ecology];
                int claimedBefore = claimed.Count;
                if (Grow(community, Area(1), DeepFor(community)) > 0
                    && darkGrowth.Count(g => CaveCommunities.Grows(community, g.Kind)) >= FewestCommunityPieces
                    && darkGrowth.Count(g => CaveCommunities.Signature(community, g.Kind)) >= FewestSignaturePieces)
                {
                    grown.Add(community);
                    continue;
                }

                darkGrowth.Clear();
                darkGrowth.AddRange(growthBefore);
                ecology.Clear();
                ecology.AddRange(ecologyBefore);
                claimed.RemoveRange(claimedBefore, claimed.Count - claimedBefore);
                if (community == CaveCommunity.VentOrchard)
                {
                    orchards = [];
                }
            }
        }

        Mark("communities");

        Waterfall[] waterfalls = chasm is ChasmShape fallInto ? Waterfalls(open, activeFlow, passages0, fallInto, seed) : [];
        List<WaterZone> waterZones = WaterZones(open, finalTable);
        Mark("waterzones");
        waterZones.AddRange(PerchedWater(open, routePaths, finalTable, waterZones));
        Mark("perched");
        Luminous(waterZones, ecology, sinks, skyOpen, seed);
        Mark("luminous");
        // A vent orchard whose seep lies under the water grows nothing (Generate drops its growth there), nor glows.
        foreach ((Vector3 seepAt, float _) in orchards.Where(o => waterZones.Any(zone => zone.BedUnder(o.Seep) is not null && zone.Surface > o.Seep.Y)))
        {
            ecology.RemoveAll(e => e.Kind == EcologyKind.VentSeep && Vector2.Distance(new Vector2(e.At.X, e.At.Z), new Vector2(seepAt.X, seepAt.Z)) < 0.5f);
        }

        if (greatHall is Hall builtHall)
        {
            ecology.AddRange(HallGrowths(open, builtHall, seed, random));
        }

        // A feature's place is the most open point near it, so its light and its view stand in the space, not in rock.
        sponge = sponge is Vector3 found ? MostOpenNear(open, found, SpongeRadii.Y) : null;
        Vector3[] riftPlaces = [.. rifts.Select(r => MostOpenNear(open, r, RiftRise))];
        Vector3[] mazePlaces = [.. mazes.Select(m => MostOpenNear(open, m, MazeTop))];
        Mark("features");
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
            MazeAt = mazePlaces.Length > 0 ? mazePlaces[0] : null,
            BeddingRooms = [.. beddingRooms],
            Passages = [.. passages],
            Conduits = carved,
            Seed = seed,
            Formations = formations,
            Eras = eras,
            Ecology = ecology,
            Communities = grown,
            Setting = place,
            Overgrowth = darkGrowth,
            Tiankeng = tiankeng,
            GreatHall = greatHall,
            Chasm = chasm,
            Waterfalls = waterfalls,
            WaterZones = waterZones,
            SkyOpen = skyOpen,
            Formed = formed,
            Sediment = sediment,
            Rubble = rubble,
            Flowstone = flowstone,
            LastFlowEra = Array.FindLastIndex(eras, Flows),
            Rifts = riftPlaces,
            Sponge = sponge,
            FinalTable = finalTable,
            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }

    private const byte Unformed = byte.MaxValue;

    /// <summary>
    /// The rock blocks the age classes are drawn with (each bound to its own look of the cave rock): plain, washed by the
    /// active water, patinated by long dry ages, and sediment.
    /// </summary>
    internal const BlockId PlainRock = BlockId.Stone, WashedRock = BlockId.WashedRock, PatinatedRock = BlockId.PatinatedRock, SedimentRock = BlockId.Dirt;

    // The cave's other materials (#10095): silt (sediment above the water's reach, dry), flowstone and rubble.
    internal const BlockId SiltRock = BlockId.Sand, FlowstoneRock = BlockId.Flowstone, RubbleRock = BlockId.Rubble;

    // The cave's growth (#10129): its blocks, how large its patches are (cycles per metre) and how much of the rock each
    // takes where it can grow (above this patch noise: slime takes about half the wet rock, moss a third of lit rock, moonmilk
    // a little of the dry), how light a drip moonmilk wants, and where light reaches - how near a sink, how far below
    // the sky, and how far past an opening's rim.
    internal const BlockId MossRock = BlockId.MossyRock, SlimeRock = BlockId.SlimyRock, MoonmilkRock = BlockId.Moonmilk;
    private const float SlimeFrequency = 0.6f, SlimeAbove = 0.45f, MossFrequency = 0.35f, MossAbove = 0.55f;
    private const float MoonmilkFrequency = 0.5f, MoonmilkAbove = 0.4f, MoonmilkDrip = 0.02f;
    private const float MossNearSink = 6f, MossBelowSky = 20f, MossBeyondOpening = 2f;

    /// <summary>Where water drips at least this much (the 2 m drip field, 0 to 1), older rock is coated in flowstone.</summary>
    private const float FlowstoneDrip = 0.12f;

    /// <summary>A floor within the water's reach where it runs slower than this (the active flow, 0 to 1) is mud: the stream beds stay washed rock.</summary>
    private const float MudQuietFlow = 0.15f;

    /// <summary>Marks the samples a pass filled with rock (open before, solid after): what fell, or what grew.</summary>
    private static void MarkFilled(float[] before, float[] open, bool[] mark)
    {
        for (int i = 0; i < open.Length; i++)
        {
            if (before[i] > 0f && open[i] <= 0f)
            {
                mark[i] = true;
            }
        }
    }

    /// <summary>How far above today's water table rock still counts as the active level, and how many dry eras make a patina.</summary>
    private const float WetReach = 3f;
    private const int PatinaAfterEras = 2;

    /// <summary>The most open sample within a reach of a place (the place itself when nothing near is open).</summary>
    private static Vector3 MostOpenNear(float[] open, Vector3 near, float reach)
    {
        Vector3 best = near;
        float most = 0f;
        ForBox(near, new Vector3(reach), (index, p) =>
        {
            if (open[index] > most)
            {
                most = open[index];
                best = p;
            }
        });
        return best;
    }

    /// <summary>A cave's eras: between the fewest and most, no kind twice, in the order they happen, at least one flowing.</summary>
    private static Era[] DrawEras(DungeonRandom random, Centrepiece? centrepiece)
    {
        List<Era> pool = [.. Enum.GetValues<Era>().Where(era => era is not (Era.GreatHall or Era.Chasm))];
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

        // A centrepiece - a great hall or a chasm - is rare, drawn apart from the rest; a study may ask for one or rule both out.
        // A chasm is the cave's opening to the sky: no tiankeng beside it.
        float roll = Between(random, 0f, 1f);
        Centrepiece centre = centrepiece ?? (roll < GreatHallChance ? Centrepiece.GreatHall : roll < GreatHallChance + ChasmChance ? Centrepiece.Chasm : Centrepiece.None);
        if (centre == Centrepiece.GreatHall)
        {
            drawn.Add(Era.GreatHall);
        }
        else if (centre == Centrepiece.Chasm)
        {
            drawn.Remove(Era.Tiankeng);
            drawn.Add(Era.Chasm);
        }

        // Eras that work on passages already there (floods backing up, sediment, fractures opened from them, collapse) come
        // after the first era that opened any; with only a late stream, they follow the stream.
        List<Era> ordered = [.. drawn.Distinct().OrderBy(era => era)];
        int first = ordered.FindIndex(era => Flows(era) || era == Era.Hypogene);
        List<Era> waiting = [.. ordered.Take(first).Where(NeedsPassages)];
        ordered.RemoveAll(waiting.Contains);
        ordered.InsertRange(ordered.FindIndex(era => Flows(era) || era == Era.Hypogene) + 1, waiting);
        return [.. ordered];
    }

    /// <summary>Eras that act on passages an earlier era opened.</summary>
    private static bool NeedsPassages(Era era) => era is Era.FloodMaze or Era.Infill or Era.JointRifts or Era.Breakdown or Era.Chasm or Era.GreatHall or Era.Tiankeng;

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
    private static void Infill(float[] open, byte[] formed, bool[] sediment, byte era, ulong seed)
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
                        sediment[above] |= open[above] <= 0f;
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
    /// <param name="skyOpen">Whether the top is left open to the sky (a cave with a surface).</param>
    /// <param name="chasm">A chasm, which falls through the bottom of the space: no rind under it.</param>
    private static void Enclose(float[] open, bool skyOpen, ChasmShape? chasm)
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
                    bool bottomless = chasm is ChasmShape c && Vector2.Distance(new Vector2(p.X, p.Z), c.Centre) < c.Radius * (1f + ChasmWobble) + ChasmBand;
                    float below = bottomless ? float.MaxValue : p.Y, above = skyOpen ? float.MaxValue : far.Y;
                    float edge = MathF.Min(MathF.Min(MathF.Min(p.X, far.X), MathF.Min(below, above)), MathF.Min(p.Z, far.Z));
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
        (float before, int widened) = WidenPassage(open, clearance, from, to);
        if (widened > 0)
        {
            clearance = Clearance(open);
        }

        return FinishPassage(clearance, name, from, to, before, widened);
    }

    /// <summary>
    /// The first half of making sure a way can be walked: how wide the widest way is now, and the samples along the shortest
    /// way that are too narrow for a body, widened. The clearance is not measured again here (the caller does, once).
    /// </summary>
    private static (float Before, int Widened) WidenPassage(float[] open, float[] clearance, Vector3 from, Vector3 to)
    {
        Mark("passages-other");
        (float before, List<int> path) = WayAtLeast(clearance, from, to);
        int widened = 0;
        foreach (int sample in path)
        {
            if (clearance[sample] < CrawlRadius)
            {
                Widen(open, FieldCentre(sample), CrawlRadius + WidenMargin);
                widened++;
            }
        }

        Mark("passages-widen");
        return (before, widened);
    }

    /// <summary>The second half: the way as it now is, with the clearance measured after the widening.</summary>
    private static (Passage Passage, List<int> Path) FinishPassage(float[] clearance, string name, Vector3 from, Vector3 to, float before, int widened)
    {
        (float after, List<int> final) = WayAtLeast(clearance, from, to);
        return (new Passage(name, before, after, widened, final.Count * FieldStep), final);
    }

    /// <summary>
    /// The way between two places at the width a standing body needs, or as near it as there is: the narrowest the widest way
    /// gets (up to that width) and the shortest way at least that wide. The standing width is tried first: when the shortest
    /// way at it is that wide all along, so is the widest, and that way is the answer, without measuring the widest
    /// (#10304: that measure explored every space wider than the way's narrowest, in a large cave most of it).
    /// </summary>
    private static (float Narrowest, List<int> Path) WayAtLeast(float[] clearance, Vector3 from, Vector3 to)
    {
        // Only through samples a body stands in: a way exists or not, found within the cave's open space either way.
        List<int> standing = Shortest(clearance, from, to, StandRadius, strict: true);
        Mark("passages-shortest");
        if (standing.Count > 0)
        {
            return (StandRadius, standing);
        }

        (float narrowest, _) = Widest(clearance, from, to);
        Mark("passages-widest");
        List<int> path = Shortest(clearance, from, to, MathF.Min(narrowest, StandRadius));
        Mark("passages-shortest");
        return (narrowest, path);
    }

    // The way searches' working arrays, one field's worth kept and reused (#10304: allocating and clearing them for every
    // search cost a large cave most of its generation). A sample's value counts only when its stamp is this search's.
    [ThreadStatic]
    private static float[]? searchValue;

    [ThreadStatic]
    private static int[]? searchBack, searchStamp;

    [ThreadStatic]
    private static int searchRound;

    /// <summary>Starts a search: the working arrays sized to the field, every sample's value and way back cleared at once.</summary>
    private static void BeginSearch(int length)
    {
        if (searchValue is null || searchValue.Length != length)
        {
            searchValue = new float[length];
            searchBack = new int[length];
            searchStamp = new int[length];
            searchRound = 0;
        }

        searchRound++;
        if (searchRound == int.MaxValue)
        {
            Array.Clear(searchStamp!);
            searchRound = 1;
        }
    }

    private static float SearchValue(int i, float unset) => searchStamp![i] == searchRound ? searchValue![i] : unset;

    private static int SearchBack(int i) => searchStamp![i] == searchRound ? searchBack![i] : -1;

    private static void SetSearch(int i, float value, int back)
    {
        searchStamp![i] = searchRound;
        searchValue![i] = value;
        searchBack![i] = back;
    }

    /// <summary>
    /// The shortest way between two places through samples at least <paramref name="least"/> clear. With nothing that clear,
    /// the way through every sample (rock included) that is cheapest by how much rock it crosses: what widening would open.
    /// </summary>
    /// <param name="strict">Whether samples narrower than <paramref name="least"/> are closed (no way through them, an empty way if
    /// none), rather than costly.</param>
    private static List<int> Shortest(float[] clearance, Vector3 from, Vector3 to, float least, bool strict = false)
    {
        int start = NearestOpen(clearance, from), goal = NearestOpen(clearance, to);
        if (start < 0 || goal < 0)
        {
            return [];
        }

        // With no way open at all (least 0), every sample costs the same, so the shortest way is a straight one: built
        // directly rather than searched for across the whole field (#10304: such searches settled the entire field).
        if (least <= 0f)
        {
            ShortestLog.Add((least, 0));
            return Straight(start, goal);
        }

        // A* toward the goal: every step costs at least 1 sample, so the samples still to cross are a lower bound that never
        // overestimates (#10304: a search spreading evenly flooded a large cave's open sky).
        BeginSearch(clearance.Length);
        (int goalX, int goalY, int goalZ) = FieldCell3(goal);
        int Remaining(int x, int y, int z) => Math.Abs(x - goalX) + Math.Abs(y - goalY) + Math.Abs(z - goalZ);
        PriorityQueue<int, float> frontier = new();
        SetSearch(start, 0f, -1);
        (int startX, int startY, int startZ) = FieldCell3(start);
        frontier.Enqueue(start, Remaining(startX, startY, startZ));
        int explored = 0;
        while (frontier.TryDequeue(out int c, out float estimate))
        {
            (int px, int py, int pz) = FieldCell3(c);
            float known = estimate - Remaining(px, py, pz);
            if (known > SearchValue(c, float.MaxValue) + 1e-3f)
            {
                continue;
            }

            known = SearchValue(c, float.MaxValue);

            explored++;
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
                if (Sky(n) || (strict && clearance[n] < least))
                {
                    continue;
                }

                float step = clearance[n] >= least ? 1f : 1f + (PinchCost * (least - clearance[n]));
                float through = known + step;
                if (through < SearchValue(n, float.MaxValue))
                {
                    SetSearch(n, through, c);
                    frontier.Enqueue(n, through + Remaining(nx, ny, nz));
                }
            }
        }

        ShortestLog.Add((least, explored));
        if (strict && SearchValue(goal, float.MaxValue) == float.MaxValue)
        {
            return [];
        }

        List<int> path = [];
        for (int at = goal; at >= 0; at = SearchBack(at))
        {
            path.Add(at);
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Whether a sample is in the open sky over the surface (#10304): routes are ways underground, so the searches never
    /// cross it; in a cave open to the sky they would otherwise search the whole sky, cheap and wide, before the cave.
    /// </summary>
    private static bool Sky(int index) => FieldCell3(index).Y * FieldStep > SurfaceLevel + SurfaceRoll;

    /// <summary>A straight way of face-adjacent samples from one to another: each step along the axis furthest behind the line.</summary>
    private static List<int> Straight(int start, int goal)
    {
        (int x, int y, int z) = FieldCell3(start);
        (int gx, int gy, int gz) = FieldCell3(goal);
        int total = Math.Abs(gx - x) + Math.Abs(gy - y) + Math.Abs(gz - z);
        List<int> path = [start];
        (int sx, int sy, int sz) = (x, y, z);
        for (int taken = 1; taken <= total; taken++)
        {
            // Where the line would be after this many steps, and the axis lagging it most.
            float t = taken / (float)total;
            float lagX = (MathF.Abs(gx - sx) * t) - MathF.Abs(x - sx), lagY = (MathF.Abs(gy - sy) * t) - MathF.Abs(y - sy), lagZ = (MathF.Abs(gz - sz) * t) - MathF.Abs(z - sz);
            if (lagX >= lagY && lagX >= lagZ && x != gx)
            {
                x += Math.Sign(gx - x);
            }
            else if (lagY >= lagZ && y != gy)
            {
                y += Math.Sign(gy - y);
            }
            else if (z != gz)
            {
                z += Math.Sign(gz - z);
            }
            else if (x != gx)
            {
                x += Math.Sign(gx - x);
            }
            else
            {
                y += Math.Sign(gy - y);
            }

            path.Add(FieldIndex(x, y, z));
        }

        return path;
    }

    /// <summary>The last generation's way searches (#10304 timing): each one's least clearance and how many samples it settled.</summary>
    internal static readonly List<(float Least, int Explored)> ShortestLog = [];

    /// <summary>The widest a way's width is told apart up to (metres): a standing body's, all its caller asks about.</summary>
    private const float WidestCap = StandRadius;

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

        // Widths are compared only up to WidestCap: the caller asks whether a way is at least a body wide, so a wider one need not
        // be told from another; among equally wide samples the search steps toward the goal (#10304: open sky, the widest
        // space of all, was searched entire before the cave).
        BeginSearch(clearance.Length);
        (int goalX, int goalY, int goalZ) = FieldCell3(goal);
        PriorityQueue<int, (float NegativeWidth, int Reach)> frontier = new();
        float startWidth = MathF.Min(clearance[start], WidestCap);
        SetSearch(start, startWidth, -1);
        frontier.Enqueue(start, (-startWidth, 0));
        while (frontier.TryDequeue(out int c, out (float NegativeWidth, int Reach) popped))
        {
            float negative = popped.NegativeWidth;
            float bestC = SearchValue(c, 0f);
            if (-negative < bestC)
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
                float width = MathF.Min(bestC, MathF.Min(clearance[n], WidestCap));
                if (clearance[n] > 0f && width > SearchValue(n, 0f) && !Sky(n))
                {
                    SetSearch(n, width, c);
                    frontier.Enqueue(n, (-width, Math.Abs(nx - goalX) + Math.Abs(ny - goalY) + Math.Abs(nz - goalZ)));
                }
            }
        }

        float bestGoal = SearchValue(goal, 0f);
        if (bestGoal <= 0f)
        {
            return (0f, []);
        }

        List<int> path = [];
        for (int at = goal; at >= 0; at = SearchBack(at))
        {
            path.Add(at);
        }

        path.Reverse();
        return (bestGoal, path);
    }

    /// <summary>The open sample with the most clearance within a few metres of a place.</summary>
    private static int NearestOpen(float[] clearance, Vector3 p)
    {
        const float Reach = 4f;
        int found = -1;
        float most = 0f;
        ForBox(p, new Vector3(Reach), (index, _) =>
        {
            if (clearance[index] > most && !Sky(index))
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
