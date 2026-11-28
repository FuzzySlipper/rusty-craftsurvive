using System.Collections.Concurrent;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A cave mouth's way underground (#10889): the tendril the overworld's generator carves from the mouth down into the
/// hill, and the box a cave is generated in, placed under the hill so the tendril's end is the cave's way in. The cave's
/// own (canonical) frame is turned a quarter at a time so the cave always runs on into the hill from its way in. All of it
/// is a pure function of the mouth's site and the ground, so it is the same from every chunk that asks.
/// </summary>
internal sealed class CavePlacement
{
    /// <summary>How many voxels across, up and along the cave's box is (the karst generator's default box, 96 x 64 x 96 m).</summary>
    internal const int BoxChunksX = 6, BoxChunksY = 4, BoxChunksZ = 6;

    internal const int SizeX = BoxChunksX * TerrainConstants.ChunkEdgeLength;
    internal const int SizeY = BoxChunksY * TerrainConstants.ChunkEdgeLength;
    internal const int SizeZ = BoxChunksZ * TerrainConstants.ChunkEdgeLength;

    /// <summary>Where in the cave's box its way in (its first sink) lies, in its own metres: near the end facing the mouth, across the middle.</summary>
    internal static readonly Vector2 WayIn = new(12.5f, 48.5f);

    /// <summary>How high in the cave's box the generator's sinks lie (its grid's top less two and a half cells of two metres).</summary>
    internal const float SinkHeight = SizeY - 5f;

    /// <summary>
    /// How far the cave's box top lies under the mouth's floor. The tendril falls this far and the sink's depth in the box
    /// besides: about 20 m in all, over <see cref="TendrilRun"/>, so the twilight zone is a walk, not a shaft.
    /// </summary>
    internal const float BoxTopBelowMouth = 12f;

    /// <summary>How far the tendril runs into the hill, in metres along the mouth's facing.</summary>
    internal const int TendrilRun = 60;

    /// <summary>The tendril's section: wider than tall, its floor a little under the mouth's.</summary>
    internal const float TendrilHalfWidth = 1.9f, TendrilHalfHeight = 1.6f;

    /// <summary>How high over the mouth's floor the tendril's centre starts.</summary>
    private const float TendrilStartAboveFloor = 1.4f;

    /// <summary>How far the tendril runs on below the way in, so it opens into the cave's first passage rather than meeting it at a point.</summary>
    private const float TendrilBelowWayIn = 2f;

    /// <summary>How far the tendril bends aside at most, in metres.</summary>
    private const float TendrilWander = 4f;

    /// <summary>How far apart the tendril's path points are, in metres.</summary>
    private const float TendrilStep = 2f;

    /// <summary>The rock kept over a cave everywhere: its air never comes nearer the overworld's ground than this.</summary>
    internal const int RoofMetres = 6;

    /// <summary>Two caves' boxes and tendrils keep at least this far apart.</summary>
    internal const int ApartMetres = 8;

    private CavePlacement(PoiSite site, int forwardX, int forwardZ, IReadOnlyList<CaveTendril> tendrils, Vector3 corner, PlannedBox? planned = null)
    {
        Site = site;
        ForwardX = forwardX;
        ForwardZ = forwardZ;
        Tendrils = tendrils;
        Corner = corner;
        Planned = planned;
        // The cave's +x runs along the mouth's facing, its +z a quarter turn from it (a rotation, not a mirror).
        (AcrossX, AcrossZ) = (-forwardZ, forwardX);
        Vector3[] corners = [Corner, ToWorld(new Vector3(SizeX, 0, 0)), ToWorld(new Vector3(0, 0, SizeZ)), ToWorld(new Vector3(SizeX, 0, SizeZ))];
        BoxMinimum = ((long)MathF.Floor(corners.Min(c => c.X)), (long)MathF.Floor(Corner.Y), (long)MathF.Floor(corners.Min(c => c.Z)));
        BoxMaximum = ((long)MathF.Ceiling(corners.Max(c => c.X)) - 1, (long)MathF.Floor(Corner.Y) + SizeY - 1, (long)MathF.Ceiling(corners.Max(c => c.Z)) - 1);
        // A box with no tendril (a planned box inside its system, #11219) has bounds no point lies in.
        TendrilMinimum = tendrils.Count == 0 ? (long.MaxValue / 4, long.MaxValue / 4, long.MaxValue / 4)
            : (tendrils.Min(t => t.Minimum.X), tendrils.Min(t => t.Minimum.Y), tendrils.Min(t => t.Minimum.Z));
        TendrilMaximum = tendrils.Count == 0 ? (long.MinValue / 4, long.MinValue / 4, long.MinValue / 4)
            : (tendrils.Max(t => t.Maximum.X), tendrils.Max(t => t.Maximum.Y), tendrils.Max(t => t.Maximum.Z));
    }

    /// <summary>The tendrils from the surface into this box: one for a landmark cave, one per mouth for a planned box, maybe none.</summary>
    internal IReadOnlyList<CaveTendril> Tendrils { get; }

    /// <summary>The planned system and box this placement holds (#11219), or null for a landmark cave.</summary>
    internal PlannedBox? Planned { get; }

    /// <summary>Whether the box is a planned system's (#11219) rather than a landmark cave mouth's.</summary>
    internal bool IsPlanned => Planned is not null;

    internal PoiSite Site { get; }

    /// <summary>The mouth's facing into the hill: one of the four cardinals.</summary>
    internal int ForwardX { get; }
    internal int ForwardZ { get; }

    /// <summary>Where the cave's +z runs in the world: a quarter turn from its facing.</summary>
    internal int AcrossX { get; }
    internal int AcrossZ { get; }

    /// <summary>The cave's box's own origin in world metres (a voxel corner): its (0, 0, 0).</summary>
    internal Vector3 Corner { get; }

    /// <summary>The world voxels the box covers, inclusive.</summary>
    internal (long X, long Y, long Z) BoxMinimum { get; }
    internal (long X, long Y, long Z) BoxMaximum { get; }

    /// <summary>The world voxels the tendril can carve, inclusive.</summary>
    internal (long X, long Y, long Z) TendrilMinimum { get; }
    internal (long X, long Y, long Z) TendrilMaximum { get; }

    /// <summary>The (first) tendril's centre line, from outside the mouth to below the way in, in world metres; empty with none.</summary>
    internal IReadOnlyList<Vector3> Path => Tendrils.Count > 0 ? Tendrils[0].Path : [];

    /// <summary>The cave's way in, in world metres: the tendril's end.</summary>
    internal Vector3 WayInWorld => ToWorld(new Vector3(WayIn.X, SinkHeight, WayIn.Y));

    /// <summary>A stable key for the mouth: its site's anchor cell (for a planned box, a key of its system and index).</summary>
    internal (long X, long Z) Key => (Site.CellX, Site.CellZ);

    /// <summary>The cave's quarter turns about +y from its own frame to the world's (0: its +x is the world's +x).</summary>
    internal int QuarterTurns => (ForwardX, ForwardZ) switch
    {
        (1, 0) => 0,
        (0, -1) => 1,
        (-1, 0) => 2,
        _ => 3,
    };

    /// <summary>The cave's turn from its own frame to the world's, as a rotation about +y.</summary>
    internal Quaternion Turn => Quaternion.CreateFromAxisAngle(Vector3.UnitY, QuarterTurns * MathF.PI / 2f);

    /// <summary>A point of the cave's own frame (metres from its box's origin) in world metres.</summary>
    internal Vector3 ToWorld(Vector3 canonical) => new(
        Corner.X + (ForwardX * canonical.X) + (AcrossX * canonical.Z),
        Corner.Y + canonical.Y,
        Corner.Z + (ForwardZ * canonical.X) + (AcrossZ * canonical.Z));

    /// <summary>A world point in the cave's own frame.</summary>
    internal Vector3 ToCanonical(Vector3 world)
    {
        Vector3 relative = world - Corner;
        return new Vector3((relative.X * ForwardX) + (relative.Z * ForwardZ), relative.Y, (relative.X * AcrossX) + (relative.Z * AcrossZ));
    }

    /// <summary>The cave's voxel a world voxel is, when it lies in the box.</summary>
    internal bool InBox(long x, long y, long z, out int i, out int j, out int k)
    {
        i = j = k = 0;
        if (x < BoxMinimum.X || x > BoxMaximum.X || y < BoxMinimum.Y || y > BoxMaximum.Y || z < BoxMinimum.Z || z > BoxMaximum.Z)
        {
            return false;
        }

        Vector3 canonical = ToCanonical(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
        i = (int)MathF.Floor(canonical.X);
        j = (int)MathF.Floor(canonical.Y);
        k = (int)MathF.Floor(canonical.Z);
        return i >= 0 && i < SizeX && j >= 0 && j < SizeY && k >= 0 && k < SizeZ;
    }

    internal bool BoxTouches(long xMin, long xMax, long zMin, long zMax) =>
        xMax >= BoxMinimum.X && xMin <= BoxMaximum.X && zMax >= BoxMinimum.Z && zMin <= BoxMaximum.Z;

    internal bool TendrilTouches(long xMin, long xMax, long zMin, long zMax) =>
        xMax >= TendrilMinimum.X && xMin <= TendrilMaximum.X && zMax >= TendrilMinimum.Z && zMin <= TendrilMaximum.Z;

    internal bool ChunkTouchesBox(TerrainChunkAddress chunk)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        long x0 = chunk.X * edge, y0 = chunk.Y * edge, z0 = chunk.Z * edge;
        return x0 + edge - 1 >= BoxMinimum.X && x0 <= BoxMaximum.X
            && y0 + edge - 1 >= BoxMinimum.Y && y0 <= BoxMaximum.Y
            && z0 + edge - 1 >= BoxMinimum.Z && z0 <= BoxMaximum.Z;
    }

    internal bool ChunkTouchesTendril(TerrainChunkAddress chunk)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        long x0 = chunk.X * edge, y0 = chunk.Y * edge, z0 = chunk.Z * edge;
        return x0 + edge - 1 >= TendrilMinimum.X && x0 <= TendrilMaximum.X
            && y0 + edge - 1 >= TendrilMinimum.Y && y0 <= TendrilMaximum.Y
            && z0 + edge - 1 >= TendrilMinimum.Z && z0 <= TendrilMaximum.Z;
    }

    /// <summary>
    /// How far inside a tendril a world point is, in metres (positive inside, empty; negative in the rock round it); the most
    /// open of the box's tendrils.
    /// </summary>
    internal float TendrilOpen(Vector3 point)
    {
        float open = float.NegativeInfinity;
        foreach (CaveTendril tendril in Tendrils)
        {
            open = MathF.Max(open, tendril.Open(point));
        }

        return open;
    }

    /// <summary>How far under the tendril's centre line its level floor lies: about 2.8 m of floor across, 2.7 m of headroom.</summary>
    internal const float TendrilFloorBelowCentre = 1.1f;

    /// <summary>
    /// The way underground behind a cave mouth, or null where none fits: the mouth's facing, the tendril's path down into
    /// the hill and the box under it.
    /// </summary>
    internal static CavePlacement? Place(PoiSite site, long radius)
    {
        (long forwardX, long forwardZ) = PoiStructures.CardinalOf(site.Aspect);
        int fx = (int)forwardX, fz = (int)forwardZ;
        int ax = -fz, az = fx;
        ulong hash = Mix(((ulong)site.CellX * 0x9E37_79B9_7F4A_7C15UL) ^ ((ulong)site.CellZ * 0xC2B2_AE3D_27D4_EB4FUL) ^ (ulong)site.Variant);
        float wander = ((hash & 0xFFFF) / 65535f * 2f - 1f) * TendrilWander;
        float startY = site.Ground + TendrilStartAboveFloor;
        // The box's top lies under the mouth's floor; its first sink a little under that, where the tendril ends.
        float wayInY = site.Ground - BoxTopBelowMouth - (SizeY - SinkHeight);
        float endY = wayInY - TendrilBelowWayIn;
        // The centre line: from a metre outside the mouth straight down an even slope (about 20 degrees) to the way in, so
        // even a mouth on level ground opens into a short sloping cut rather than a trench; bending aside once and back.
        List<Vector3> points = [];
        int steps = (int)MathF.Ceiling((TendrilRun + 1) / TendrilStep);
        for (int n = 0; n <= steps; n++)
        {
            float s = Math.Min(TendrilRun + 1f, n * TendrilStep);
            float t = s / (TendrilRun + 1f);
            float along = s - 1f;
            float aside = wander * MathF.Sin(MathF.PI * t);
            float y = startY + ((endY - startY) * t);
            points.Add(new Vector3(site.X + 0.5f + (fx * along) + (ax * aside), y, site.Z + 0.5f + (fz * along) + (az * aside)));
        }

        Vector3 end = points[^1] + new Vector3(0f, TendrilBelowWayIn, 0f);
        // The box's origin: the way in's world point less its place in the box, turned.
        Vector3 corner = new(
            end.X - (fx * WayIn.X) - (ax * WayIn.Y),
            MathF.Round(end.Y - SinkHeight),
            end.Z - (fz * WayIn.X) - (az * WayIn.Y));
        corner = new Vector3(MathF.Round(corner.X), corner.Y, MathF.Round(corner.Z));
        CavePlacement placement = new(site, fx, fz, [new CaveTendril([.. points], fx, fz, CaveTendril.SaltFor(site))], corner);
        // The finite world's bounds: the box and tendril stay inside the border wall and above the bedrock floor (#11187).
        long inner = radius - (GenerationConstants.WorldWallThickness + ApartMetres);
        long floorTop = -GenerationConstants.TerrainDepth + GenerationConstants.WorldFloorThickness;
        if (Math.Min(placement.BoxMinimum.X, placement.TendrilMinimum.X) < -inner || Math.Max(placement.BoxMaximum.X, placement.TendrilMaximum.X) > inner
            || Math.Min(placement.BoxMinimum.Z, placement.TendrilMinimum.Z) < -inner || Math.Max(placement.BoxMaximum.Z, placement.TendrilMaximum.Z) > inner
            || Math.Min(placement.BoxMinimum.Y, placement.TendrilMinimum.Y) < floorTop)
        {
            return null;
        }

        return placement;
    }

    /// <summary>Whether two placements come within <see cref="ApartMetres"/> of each other (their boxes and tendrils' bounds).</summary>
    internal bool Crowds(CavePlacement other)
    {
        (long X, long Z) min = (Math.Min(BoxMinimum.X, TendrilMinimum.X) - ApartMetres, Math.Min(BoxMinimum.Z, TendrilMinimum.Z) - ApartMetres);
        (long X, long Z) max = (Math.Max(BoxMaximum.X, TendrilMaximum.X) + ApartMetres, Math.Max(BoxMaximum.Z, TendrilMaximum.Z) + ApartMetres);
        return other.BoxTouches(min.X, max.X, min.Z, max.Z) || other.TendrilTouches(min.X, max.X, min.Z, max.Z);
    }

    internal static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51_AFD7_ED55_8CCDUL;
        value ^= value >> 33;
        value *= 0xC4CE_B9FE_1A85_EC53UL;
        return value ^ (value >> 33);
    }

    /// <summary>
    /// A planned system's box as a placement (#11219): axis-aligned (its own frame the world's), its corner the plan's origin,
    /// with a tendril for each of its mouths that has one. The site stands for the box: its anchor cell is the box's key, its
    /// place the box's centre on the ground.
    /// </summary>
    internal static CavePlacement ForPlanned(PlannedBox planned, IReadOnlyList<CaveTendril> tendrils)
    {
        UndergroundBox box = planned.Box;
        PoiSite site = new(planned.KeyX, planned.KeyZ, PoiKind.CaveMouth, (long)Math.Floor(box.OriginX + (SizeX * 0.5)), (long)Math.Floor(box.OriginZ + (SizeZ * 0.5)),
            (long)Math.Floor(box.GroundElevation), 0, 0, 0);
        return new CavePlacement(site, 1, 0, tendrils, new Vector3((float)box.OriginX, (float)box.OriginY, (float)box.OriginZ), planned);
    }
}

/// <summary>
/// A tendril from the surface down to a cave (#10889; #11219 lifts it out of its placement, so a planned box can hold one per
/// mouth, or none): its centre line, a level-floored section wider than tall, and a wall wandering by a smooth noise.
/// </summary>
internal sealed class CaveTendril
{
    private readonly Vector3[] path;
    private readonly int forwardX, forwardZ;
    private readonly ulong salt;

    /// <param name="path">The centre line, a <see cref="CavePlacement"/>'s tendril step apart.</param>
    /// <param name="forwardX">The run's horizontal heading (a cardinal), or 0, 0 for a shaft, whose every segment is searched.</param>
    /// <param name="salt">The wall noise's salt (a landmark mouth's comes from its site, <see cref="SaltFor"/>).</param>
    internal CaveTendril(Vector3[] path, int forwardX, int forwardZ, ulong salt)
    {
        this.path = path;
        this.forwardX = forwardX;
        this.forwardZ = forwardZ;
        this.salt = salt;
        float reachX = CavePlacement.TendrilHalfWidth + 1f, reachY = CavePlacement.TendrilHalfHeight + 1f;
        Minimum = ((long)MathF.Floor(path.Min(p => p.X) - reachX), (long)MathF.Floor(path.Min(p => p.Y) - reachY), (long)MathF.Floor(path.Min(p => p.Z) - reachX));
        Maximum = ((long)MathF.Ceiling(path.Max(p => p.X) + reachX), (long)MathF.Ceiling(path.Max(p => p.Y) + reachY), (long)MathF.Ceiling(path.Max(p => p.Z) + reachX));
    }

    internal IReadOnlyList<Vector3> Path => path;
    internal (long X, long Y, long Z) Minimum { get; }
    internal (long X, long Y, long Z) Maximum { get; }

    /// <summary>A landmark mouth's wall-noise salt, from its anchor cell.</summary>
    internal static ulong SaltFor(PoiSite site) => ((ulong)site.CellX * 0x9E37_79B9UL) ^ ((ulong)site.CellZ * 0x85EB_CA6BUL);

    /// <summary>
    /// How far inside the tendril a world point is, in metres (positive inside, empty; negative in the rock round it): its
    /// section's half-height less its distance from the centre line, the section squashed to a circle.
    /// </summary>
    internal float Open(Vector3 point)
    {
        if (point.X < Minimum.X || point.X > Maximum.X + 1 || point.Y < Minimum.Y || point.Y > Maximum.Y + 1
            || point.Z < Minimum.Z || point.Z > Maximum.Z + 1)
        {
            return float.NegativeInfinity;
        }

        const float halfWidth = CavePlacement.TendrilHalfWidth, halfHeight = CavePlacement.TendrilHalfHeight;
        float squash = halfHeight / halfWidth;
        Vector3 scale = new(squash, 1f, squash);
        float nearest = float.PositiveInfinity;
        Vector3 p = point * scale;
        // A run goes straight along its heading a step apart, so only the segments near the point's own distance along it can
        // be nearest (the bend aside is far smaller than a tendril's reach along). A shaft's segments are all searched.
        int first = 0, last = path.Length - 2;
        if (forwardX != 0 || forwardZ != 0)
        {
            float along = ((point.X - path[0].X) * forwardX) + ((point.Z - path[0].Z) * forwardZ);
            first = Math.Clamp((int)MathF.Floor(along / Step) - 2, 0, path.Length - 2);
            last = Math.Clamp((int)MathF.Floor(along / Step) + 2, 0, path.Length - 2);
        }

        float centreY = point.Y;
        for (int s = first; s <= last; s++)
        {
            Vector3 a = path[s] * scale, b = path[s + 1] * scale;
            Vector3 ab = b - a;
            float t = Math.Clamp(Vector3.Dot(p - a, ab) / Math.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
            Vector3 on = a + (ab * t);
            float d = Vector3.DistanceSquared(p, on);
            if (d < nearest)
            {
                nearest = d;
                centreY = on.Y;
            }
        }

        // The wall is rock, not a bore: its distance wanders by a smooth noise a few metres across, worked out only near the
        // wall (deeper in the rock the distance is all a chunk needs, and the noise is most of a tendril chunk's cost). The
        // floor stays smooth, so the slope can be walked up as well as down (R10889-1): a lip in it is climbed only by a jump.
        float open = halfHeight - MathF.Sqrt(nearest);
        if (open < -(Roughness + NoiseMargin))
        {
            return open;
        }

        float floorward = Math.Clamp((centreY - point.Y) / halfHeight, 0f, 1f);
        float walls = open + (Roughness * (1f - floorward) * Noise(point * RoughnessFrequency));
        // A level floor across the section, following the slope: a round section's lower curve is too steep to stand on
        // where the walk climbs the inside of a bend (the controller's steep-slope solver gives up there, R10889-1).
        return MathF.Min(walls, point.Y - (centreY - CavePlacement.TendrilFloorBelowCentre));
    }

    /// <summary>How far apart a run's path points are, in metres (the placement's tendril step).</summary>
    private const float Step = 2f;

    /// <summary>How far the tendril's wall wanders in and out, in metres, and how often (per metre).</summary>
    private const float Roughness = 0.45f, RoughnessFrequency = 0.3f;

    /// <summary>Past this far into the rock beyond the wall's wander, the noise cannot bring the wall nearer than the chunk cares.</summary>
    private const float NoiseMargin = 2f;

    /// <summary>A smooth value noise in -1..1, a pure function of the point and the tendril's salt.</summary>
    private float Noise(Vector3 p)
    {
        int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y), z0 = (int)MathF.Floor(p.Z);
        float fx = p.X - x0, fy = p.Y - y0, fz = p.Z - z0;
        fx = fx * fx * (3f - (2f * fx));
        fy = fy * fy * (3f - (2f * fy));
        fz = fz * fz * (3f - (2f * fz));
        ulong s = salt;
        float Corner(int dx, int dy, int dz) =>
            (CavePlacement.Mix(s ^ ((ulong)(x0 + dx) * 0x27D4_EB2F_1656_67C5UL) ^ ((ulong)(y0 + dy) * 0x1656_67B1_9E37_79F9UL) ^ ((ulong)(z0 + dz) * 0xC2B2_AE3D_27D4_EB4FUL)) & 0xFFFF) / 32767.5f - 1f;
        float Lerp(float a, float b, float t) => a + ((b - a) * t);
        float x00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), fx), x10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), fx);
        float x01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), fx), x11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), fx);
        return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
    }
}

/// <summary>A planned system's box at runtime (#11219): the system, the box, and the key its placement goes by.</summary>
internal sealed record PlannedBox(UndergroundSystem System, UndergroundBox Box)
{
    /// <summary>A placement key no landmark cell reaches (they lie within a few thousand cells of the origin).</summary>
    internal long KeyX => PlannedKeyBase + (long)(System.Seed & 0xFFFF_FFFFUL);
    internal long KeyZ => Box.Index;

    private const long PlannedKeyBase = 1L << 40;
}

/// <summary>
/// The caves under the overworld (#10889, Patch 2026-11-18: caves share the terrain's systems): every cave mouth's
/// tendril, carved by the generator, and the caves generated so far, read into the overworld's own chunks where their
/// boxes lie. A cave not generated yet is rock; once admitted its chunks are generated again with it. The caves'
/// dimensions are cheated, not fitted: a box lies wherever its tendril's end puts it, and its air is sealed
/// <see cref="CavePlacement.RoofMetres"/> under the overworld's ground wherever the two would meet.
/// </summary>
internal sealed class CaveUnderground
{
    /// <summary>How far round a region cave mouths are looked for: a tendril's run and a box's diagonal.</summary>
    private const long Reach = CavePlacement.TendrilRun + 140;

    private readonly PoiPlacement pois;
    private readonly long radius;
    private readonly ConcurrentDictionary<(long X, long Z), CavePlacement?> placed = new();
    private readonly SharedCache<(long X, long Z), CavePlacement[]> nearChunk = new(NearChunkCacheLimit);
    private const int NearChunkCacheLimit = 16_384;

    // The caves admitted so far, swapped whole so a reader on any thread sees one consistent set.
    private volatile IReadOnlyDictionary<(long X, long Z), CaveInterior> admitted = new Dictionary<(long X, long Z), CaveInterior>();

    // The planned systems' boxes the walking session holds (#11219), swapped whole likewise.
    private volatile IReadOnlyList<CavePlacement> planned = [];

    /// <summary>The planned systems' boxes held so far (#11219).</summary>
    internal IReadOnlyList<CavePlacement> Planned => planned;

    /// <summary>
    /// Holds a planned system's boxes from now on (#11219): their tendrils carve the overworld, and a landmark cave mouth
    /// whose way would crowd one of them has none (the plan's system takes its place). Returns the chunks this changes: the
    /// new tendrils', and the tendrils and boxes of the landmark caves that gave way.
    /// </summary>
    internal (Func<TerrainChunkAddress, bool> Changed, IReadOnlyList<CavePlacement> GaveWay) Hold(IReadOnlyList<CavePlacement> boxes)
    {
        if (boxes.Count == 0 || boxes.Any(b => !b.IsPlanned))
        {
            throw new ArgumentException("Only planned boxes are held this way.", nameof(boxes));
        }

        CavePlacement[] gaveWay = [.. placed.Values.OfType<CavePlacement>().Where(landmark => boxes.Any(landmark.Crowds))];
        planned = [.. planned, .. boxes];
        placed.Clear();
        nearChunk.Clear();
        Revision++;
        return (chunk => boxes.Any(b => b.ChunkTouchesTendril(chunk)) || gaveWay.Any(g => g.ChunkTouchesTendril(chunk) || g.ChunkTouchesBox(chunk)), gaveWay);
    }

    internal CaveUnderground(PoiPlacement pois, long radius)
    {
        this.pois = pois ?? throw new ArgumentNullException(nameof(pois));
        this.radius = radius;
    }

    /// <summary>How many caves are read into the chunks now.</summary>
    internal int AdmittedCount => admitted.Count;

    /// <summary>Bumped whenever a cave is admitted or let go, so what was generated without it is generated again.</summary>
    internal long Revision { get; private set; }

    /// <summary>The way underground behind a cave mouth, or null when it has none (it would leave the world, or crowd a cave before it).</summary>
    internal CavePlacement? For(PoiSite site)
    {
        if (site.Kind != PoiKind.CaveMouth)
        {
            return null;
        }

        return placed.GetOrAdd((site.CellX, site.CellZ), _ =>
        {
            if (CavePlacement.Place(site, radius) is not CavePlacement candidate)
            {
                return null;
            }

            // A mouth whose way would crowd a planned box (#11219) has none: the plan's system takes its place.
            if (planned.Any(candidate.Crowds))
            {
                return null;
            }

            // A mouth whose way would crowd an earlier mouth's (by anchor cell) has none: the earlier keeps its cave.
            foreach (PoiSite other in SitesNear(candidate.Site.X, candidate.Site.Z, Reach * 2))
            {
                if (other.Kind == PoiKind.CaveMouth && Earlier(other, site) && CavePlacement.Place(other, radius) is CavePlacement earlier
                    && candidate.Crowds(earlier))
                {
                    return null;
                }
            }

            return candidate;
        });
    }

    /// <summary>The ways underground whose tendril or box reaches into a region of columns.</summary>
    internal IReadOnlyList<CavePlacement> Near(long xMin, long xMax, long zMin, long zMax)
    {
        List<CavePlacement> found = [];
        foreach (PoiSite site in SitesNear((xMin + xMax) / 2, (zMin + zMax) / 2, Reach + Math.Max(xMax - xMin, zMax - zMin)))
        {
            if (For(site) is CavePlacement cave && (cave.BoxTouches(xMin, xMax, zMin, zMax) || cave.TendrilTouches(xMin, xMax, zMin, zMax)))
            {
                found.Add(cave);
            }
        }

        foreach (CavePlacement box in planned)
        {
            if (box.BoxTouches(xMin, xMax, zMin, zMax) || box.TendrilTouches(xMin, xMax, zMin, zMax))
            {
                found.Add(box);
            }
        }

        return found;
    }

    /// <summary>The ways underground reaching into one chunk column, kept.</summary>
    internal CavePlacement[] NearChunkColumn(long chunkX, long chunkZ)
    {
        if (nearChunk.TryGetValue((chunkX, chunkZ), out CavePlacement[]? known))
        {
            return known;
        }

        long edge = TerrainConstants.ChunkEdgeLength;
        CavePlacement[] found = [.. Near(chunkX * edge, (chunkX * edge) + edge - 1, chunkZ * edge, (chunkZ * edge) + edge - 1)];
        nearChunk.Set((chunkX, chunkZ), found);
        return found;
    }

    /// <summary>
    /// Whether any cave's tendril or box overlaps <paramref name="chunk"/> in 3D (admitted or not): used to
    /// drop stale worker chunks when <see cref="Revision"/> changes due to cave admission or release.
    /// </summary>
    internal bool Touches(TerrainChunkAddress chunk)
    {
        foreach (CavePlacement cave in NearChunkColumn(chunk.X, chunk.Z))
        {
            if (cave.ChunkTouchesBox(chunk) || cave.ChunkTouchesTendril(chunk))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="cave"/> currently alters voxels or densities inside <paramref name="chunk"/> (#11187):
    /// true for chunks overlapped in 3D by its tendril, or by its box once admitted. Un-admitted boxes leave
    /// overworld chunks untouched and safely cacheable.
    /// </summary>
    internal bool AffectsChunk(CavePlacement cave, TerrainChunkAddress chunk) =>
        cave.ChunkTouchesTendril(chunk) || (admitted.ContainsKey(cave.Key) && cave.ChunkTouchesBox(chunk));

    /// <summary>
    /// Whether any tendril or currently-admitted cave box affects <paramref name="chunk"/> (#11187):
    /// chunks outside tendrils and admitted boxes hold pure overworld terrain and may use the disk chunk cache.
    /// </summary>
    internal bool AffectsChunk(TerrainChunkAddress chunk)
    {
        IReadOnlyDictionary<(long X, long Z), CaveInterior> caves = admitted;
        foreach (CavePlacement cave in NearChunkColumn(chunk.X, chunk.Z))
        {
            if (cave.ChunkTouchesTendril(chunk) || (caves.ContainsKey(cave.Key) && cave.ChunkTouchesBox(chunk)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The cave generated for a way underground, if admitted.</summary>
    internal CaveInterior? Interior(CavePlacement cave) => admitted.TryGetValue(cave.Key, out CaveInterior? interior) ? interior : null;

    /// <summary>Reads a generated cave into the chunks from now on.</summary>
    internal void Admit(CavePlacement cave, CaveInterior interior)
    {
        if (interior.SizeX != CavePlacement.SizeX || interior.SizeY != CavePlacement.SizeY || interior.SizeZ != CavePlacement.SizeZ)
        {
            throw new ArgumentException("A cave's interior must fill its placement's box.", nameof(interior));
        }

        Dictionary<(long X, long Z), CaveInterior> next = new(admitted) { [cave.Key] = interior };
        admitted = next;
        Revision++;
    }

    /// <summary>Lets a cave go: its box is rock again.</summary>
    internal void Release(CavePlacement cave)
    {
        if (!admitted.ContainsKey(cave.Key))
        {
            return;
        }

        Dictionary<(long X, long Z), CaveInterior> next = new(admitted);
        next.Remove(cave.Key);
        admitted = next;
        Revision++;
    }

    /// <summary>
    /// What lies underground at a voxel, given what the overworld's generator put there: a tendril's air, a cave's voxel
    /// under its seal, or nothing (null) where neither reaches. The density is a magnitude in voxel units; the material's
    /// solidity gives its sign.
    /// </summary>
    internal (ushort Material, float Magnitude)? At(VoxelAddress voxel, long surface, double height, ushort generated) =>
        At(voxel, surface, height, generated, NearChunkColumn(GridMath.FloorDivide(voxel.X, TerrainConstants.ChunkEdgeLength), GridMath.FloorDivide(voxel.Z, TerrainConstants.ChunkEdgeLength)));

    /// <summary>Underground lookup against a pre-gathered set of chunk-overlapping caves (#11179).</summary>
    internal (ushort Material, float Magnitude)? At(VoxelAddress voxel, long surface, double height, ushort generated, CavePlacement[] near)
    {
        if (near.Length == 0 || generated == (ushort)BlockId.Bedrock)
        {
            return null;
        }

        Vector3 centre = new(voxel.X + 0.5f, voxel.Y + 0.5f, voxel.Z + 0.5f);
        float open = float.NegativeInfinity;
        bool reached = false;
        ushort material = generated;
        // The overworld's own open distance (positive in air over the ground), and its rock below.
        float ground = (float)(centre.Y - (height + 1d));
        float distance = generated == TerrainConstants.EmptyMaterial ? Math.Max(ground, 0.5f) : Math.Min(ground, -0.5f);
        IReadOnlyDictionary<(long X, long Z), CaveInterior> caves = admitted;
        foreach (CavePlacement cave in near)
        {
            // A cave's voxel, under the seal that keeps its air off the overworld's ground.
            long seal = surface - CavePlacement.RoofMetres;
            if (voxel.Y < seal && caves.TryGetValue(cave.Key, out CaveInterior? interior) && cave.InBox(voxel.X, voxel.Y, voxel.Z, out int i, out int j, out int k))
            {
                int index = interior.Index(i, j, k);
                ushort inside = interior.Materials[index];
                float magnitude = Math.Max(interior.Densities[index], MinimumMagnitude);
                float sealed_ = seal - centre.Y;
                if (inside == TerrainConstants.EmptyMaterial && sealed_ < magnitude)
                {
                    // Air near the seal closes into rock there: its open distance never reaches past the seal.
                    if (sealed_ <= 0f)
                    {
                        inside = (ushort)BlockId.CaveLimestone;
                        magnitude = Math.Max(-sealed_, MinimumMagnitude);
                    }
                    else
                    {
                        magnitude = sealed_;
                    }
                }

                material = inside;
                distance = inside == TerrainConstants.EmptyMaterial ? magnitude : -magnitude;
                reached = true;
            }

            float tendril = cave.TendrilOpen(centre);
            if (tendril > -2f)
            {
                open = MathF.Max(open, tendril);
                reached = true;
            }
        }

        if (!reached)
        {
            return null;
        }

        // The tendrils' air joins whatever else is open; in their rock the nearer surface wins.
        float combined = MathF.Max(distance, open);
        if (combined > 0f)
        {
            return (TerrainConstants.EmptyMaterial, Math.Max(combined, MinimumMagnitude));
        }

        // Rock round a tendril or sealed cave roof is stone so empty voxels inside rock stay solid (#11187: bedrock sits below every cave).
        if (material == TerrainConstants.EmptyMaterial)
        {
            material = TerrainConstants.StoneMaterial;
        }

        return (material, Math.Max(-combined, MinimumMagnitude));
    }

    private const float MinimumMagnitude = 0.001f;

    /// <summary>
    /// A cave's own voxel where an admitted box holds it under its seal and no tendril reaches it: answered before the
    /// overworld's generator is asked anything, which a cave's chunk would only throw away. False elsewhere.
    /// </summary>
    internal bool InCave(VoxelAddress voxel, long surface, out ushort material, out float magnitude)
    {
        if (admitted.Count == 0)
        {
            material = TerrainConstants.EmptyMaterial;
            magnitude = 0f;
            return false;
        }

        return InCave(voxel, surface, NearChunkColumn(GridMath.FloorDivide(voxel.X, TerrainConstants.ChunkEdgeLength), GridMath.FloorDivide(voxel.Z, TerrainConstants.ChunkEdgeLength)), out material, out magnitude);
    }

    /// <summary>Admitted-cave lookup against a pre-gathered set of chunk-overlapping caves (#11179).</summary>
    internal bool InCave(VoxelAddress voxel, long surface, CavePlacement[] near, out ushort material, out float magnitude)
    {
        material = TerrainConstants.EmptyMaterial;
        magnitude = 0f;
        IReadOnlyDictionary<(long X, long Z), CaveInterior> caves = admitted;
        if (caves.Count == 0 || near.Length == 0)
        {
            return false;
        }

        long seal = surface - CavePlacement.RoofMetres;
        if (voxel.Y >= seal)
        {
            return false;
        }

        CaveInterior? holding = null;
        int index = 0;
        foreach (CavePlacement cave in near)
        {
            if (voxel.X >= cave.TendrilMinimum.X - 2 && voxel.X <= cave.TendrilMaximum.X + 2 && voxel.Y >= cave.TendrilMinimum.Y - 2
                && voxel.Y <= cave.TendrilMaximum.Y + 2 && voxel.Z >= cave.TendrilMinimum.Z - 2 && voxel.Z <= cave.TendrilMaximum.Z + 2)
            {
                return false;
            }

            if (holding is null && caves.TryGetValue(cave.Key, out CaveInterior? interior) && cave.InBox(voxel.X, voxel.Y, voxel.Z, out int i, out int j, out int k))
            {
                holding = interior;
                index = interior.Index(i, j, k);
            }
        }

        if (holding is null)
        {
            return false;
        }

        material = holding.Materials[index];
        magnitude = Math.Max(holding.Densities[index], MinimumMagnitude);
        float sealed_ = seal - (voxel.Y + 0.5f);
        if (material == TerrainConstants.EmptyMaterial && sealed_ < magnitude)
        {
            if (sealed_ <= 0f)
            {
                material = (ushort)BlockId.CaveLimestone;
                magnitude = Math.Max(-sealed_, MinimumMagnitude);
            }
            else
            {
                magnitude = sealed_;
            }
        }

        return true;
    }

    private IEnumerable<PoiSite> SitesNear(long x, long z, long reach)
    {
        long cell = PoiConstants.CellSize;
        for (long cellX = GridMath.FloorDivide(x - reach, cell); cellX <= GridMath.FloorDivide(x + reach, cell); cellX++)
        {
            for (long cellZ = GridMath.FloorDivide(z - reach, cell); cellZ <= GridMath.FloorDivide(z + reach, cell); cellZ++)
            {
                if (pois.SiteAt(cellX, cellZ) is PoiSite site)
                {
                    yield return site;
                }
            }
        }
    }

    private static bool Earlier(PoiSite a, PoiSite b) => a.CellX < b.CellX || (a.CellX == b.CellX && a.CellZ < b.CellZ);
}
