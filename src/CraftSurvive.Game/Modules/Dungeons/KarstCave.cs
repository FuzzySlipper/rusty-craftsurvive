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

    // A tiankeng: the ground surface it opens through (open air above, the plateau's level and roll), its radius and how
    // deep under the surface its floor may lie, the bell of its walls, the bands at the beds, and the boulders on its floor.
    private const float SurfaceLevel = 57f, SurfaceRoll = 2.5f, SurfaceFrequency = 0.04f, SurfaceBlend = 1f;
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
    private const int MostBridges = 3;
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
    private const int MostWaterfalls = 3;
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
            Vector3[] at = [.. route.Select(FieldCentre)];
            float[] floor = [.. at.Select(p => Floor(open, p).Y)];
            float[] ceiling = [.. at.Select(p => Ceiling(open, p).Y)];
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
                if (seed < 0 || taken[seed] || found.Any(z => z.Contains(FieldCentre(seed))) || open[seed] <= 0f)
                {
                    continue;
                }

                List<int>? body = FillBelow(open, seed, level, table);
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
                    List<int> sump = [.. body.Where(k => path.Any(q => Vector3.DistanceSquared(q, FieldCentre(k)) < SumpReach * SumpReach))];
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
    private const int MostMistVolumes = 64;

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
    private const int MostFungi = 25, MostWorms = 15, MostCrystals = 8;
    private const float FungiDamp = 0.15f, QuietFlow = 0.15f, WormsNearFlow = 0.05f, WormsClearBelow = 2.5f;
    private const int CrystalsAfterEras = 2;
    private const float EcologyStep = 1f;

    /// <summary>
    /// Where the cave's life grows, from what the water did (#10129): fungi on floors where it is damp (water drips or
    /// stands) but nothing runs, glow-worms on ceilings over running water with room below them to hang, crystals on old
    /// dry levels; each kind spaced apart and capped, chosen by hash so a seed always grows the same cave.
    /// </summary>
    private static List<EcologyPlacement> Ecology(float[] open, byte[] formed, float[] flow, float[] drip, float[] standing, int eras, float finalTable, ulong seed)
    {
        List<(EcologyKind Kind, Vector3 At, Vector3 Normal, float Rank)> candidates = [];
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
                    }

                    // A ceiling just above with a clear drop below: glow-worms over running water.
                    if (Open(p + new Vector3(0f, EcologyStep, 0f)) <= 0f && running >= WormsNearFlow && Open(p - new Vector3(0f, WormsClearBelow, 0f)) > 0f)
                    {
                        candidates.Add((EcologyKind.GlowWorms, Ceiling(open, p), -Vector3.UnitY, rank));
                    }

                    // A wall beside, on a level long dry: crystals.
                    if (age >= CrystalsAfterEras && p.Y > finalTable && damp < FungiDamp && Wall(open, p) is Vector3 wallNormal)
                    {
                        candidates.Add((EcologyKind.CrystalBloom, p - (wallNormal * 0.45f), wallNormal, rank));
                    }
                }

        List<EcologyPlacement> chosen = [];
        foreach ((EcologyKind kind, float apart, int most) in new[] { (EcologyKind.GlowFungi, FungiApart, MostFungi), (EcologyKind.GlowWorms, WormsApart, MostWorms), (EcologyKind.CrystalBloom, CrystalsApart, MostCrystals) })
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

        return chosen;
    }

    /// <summary>The floor under an open place: the first rock looking down.</summary>
    private static Vector3 Floor(float[] open, Vector3 p)
    {
        while (p.Y > 1f && Trilinear(open, p) > 0f) p.Y -= 0.1f;
        return p + new Vector3(0f, 0.08f, 0f);
    }

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

    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable) Generate(ulong seed, double voxelSize = 1d, Centrepiece? centrepiece = null)
    {
        Result cave = Build(seed, centrepiece);
        Func<Vector3, float> shape = cave.At;
        (DungeonVolume volume, RockDensity rock) = StudyField.Sample(shape, ChunksX, ChunksY, ChunksZ, voxelSize, cave.RockAt);
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
            Abyss = cave.Chasm is ChasmShape abyss ? (new Vector3(abyss.Centre.X, 0f, abyss.Centre.Y), (abyss.Radius * (1f + ChasmWobble)) + ChasmBand) : null,
            Ecology = cave.Ecology,
        };
        return (layout, plan, new DungeonVerdict(true, Readout(cave), 0), volume);
    }

    /// <summary>What a generated cave came to, in one line.</summary>
    internal static string Readout(Result cave) => string.Create(CultureInfo.InvariantCulture,
        $"karst cave: eras {string.Join("+", cave.Eras)}{(cave.Tiankeng is (Vector3 tf, float tr) ? $" tiankeng r={tr:F0} at {tf.X:F0},{tf.Y:F0},{tf.Z:F0}" : string.Empty)}{(cave.Chasm is ChasmShape ch ? $" chasm r={ch.Radius:F0} at {ch.Centre.X:F0},{ch.Centre.Y:F0}, {ch.Ledges.Length} ledges, {ch.Bridges.Length} bridges, {cave.Waterfalls.Length} waterfalls ({string.Join(" ", cave.Waterfalls.Select(w => $"{w.Drop:F0}m{(w.Lands ? "" : "+abyss")}"))})" : string.Empty)}{(cave.WaterZones.Count > 0 ? $" water: {string.Join(" ", cave.WaterZones.GroupBy(w => w.Kind).Select(g => $"{g.Count()} {g.Key}"))}" : string.Empty)}{(cave.GreatHall is Hall gh ? $" hall {gh.RadiusX * 2f:F0}x{gh.RadiusZ * 2f:F0}x{gh.Height:F0} m at {gh.Centre.X:F0},{gh.Floor:F0},{gh.Centre.Y:F0}, {gh.Pillars.Length} pillars" : string.Empty)}, {cave.Conduits} conduits, {cave.Chambers.Length} chambers, {cave.Rifts.Length} rifts, {cave.Formations} formations, generated in {cave.Milliseconds:F0} ms; passages ")
        + string.Join(", ", cave.Passages.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Name} {p.Metres:F0} m narrowest {p.Before:F2}->{p.After:F2} m widened {p.Widened}")));

    /// <summary>Generates the cave: the rock, the water's rounds through it, the shapes it leaves, and the passages checked.</summary>
    internal static Result Build(ulong seed, Centrepiece? centrepiece = null)
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
                    Infill(open, formed, sediment, (byte)index, seed);
                    break;
                case Era.Breakdown:
                    Collapses(open, beds, passages0, index, random, chambers);
                    break;
                case Era.Chasm:
                    LaySurface(open, seed);
                    chasm = Chasm(open, beds, passages0, chasmAt!.Value.Centre, chasmAt.Value.Radius, seed, random);
                    skyOpen = true;
                    break;
                case Era.GreatHall:
                    greatHall = GreatHall(open, beds, seed, random);
                    break;
                case Era.Tiankeng:
                    tiankeng = Tiankeng(open, beds, passages0, seed, random);
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
            }
        }

        float[] beforeBreakdown = (float[])open.Clone();
        foreach (Vector3 chamber in chambers)
        {
            Breakdown(open, chamber, random);
        }

        MarkFilled(beforeBreakdown, open, rubble);

        float[] beforeDrip = (float[])open.Clone();
        int formations = Drip(open, joint, formed, eras.Length, finalTable, random);
        MarkFilled(beforeDrip, open, flowstone);

        Roughen(open, seed, beds, finalTable);
        Enclose(open, skyOpen, chasm);

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
        List<List<int>> routePaths = [];
        foreach ((string name, Vector3 to) in routes)
        {
            (Passage passage, List<int> path) = EnsurePassage(open, ref clearance, name, start, to);
            passages.Add(passage);
            routePaths.Add(path);
            if (name == "spring")
            {
                mainRoute = [.. path.Where((_, i) => i % 4 == 0).Select(FieldCentre)];
            }
        }

        List<EcologyPlacement> ecology = Ecology(open, formed, activeFlow, drip, standing, eras.Length, finalTable, seed);
        Waterfall[] waterfalls = chasm is ChasmShape fallInto ? Waterfalls(open, activeFlow, passages0, fallInto, seed) : [];
        List<WaterZone> waterZones = WaterZones(open, finalTable);
        waterZones.AddRange(PerchedWater(open, routePaths, finalTable, waterZones));
        if (greatHall is Hall builtHall)
        {
            ecology.AddRange(HallGrowths(open, builtHall, seed, random));
        }

        // A feature's place is the most open point near it, so its light and its view stand in the space, not in rock.
        sponge = sponge is Vector3 found ? MostOpenNear(open, found, SpongeRadii.Y) : null;
        Vector3[] riftPlaces = [.. rifts.Select(r => MostOpenNear(open, r, RiftRise))];
        Vector3[] mazePlaces = [.. mazes.Select(m => MostOpenNear(open, m, MazeTop))];
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
    // takes where it can grow (above this patch noise: slime takes about half the wet rock, moss most lit rock, moonmilk
    // a little of the dry), how light a drip moonmilk wants, and where light reaches - how near a sink, how far below
    // the sky, and how far past an opening's rim.
    internal const BlockId MossRock = BlockId.MossyRock, SlimeRock = BlockId.SlimyRock, MoonmilkRock = BlockId.Moonmilk;
    private const float SlimeFrequency = 0.6f, SlimeAbove = 0.45f, MossFrequency = 0.35f, MossAbove = 0.35f;
    private const float MoonmilkFrequency = 0.5f, MoonmilkAbove = 0.4f, MoonmilkDrip = 0.02f;
    private const float MossNearSink = 8f, MossBelowSky = 25f, MossBeyondOpening = 3f;

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
