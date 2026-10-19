using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The rock route study's natural cavern (#10090): one space shaped freely as a field, with no regard for routes, to push
/// the rock routes apart on what caves can be rather than on cut passages. A formation hall (hanging stalactites, rising
/// stalagmites, a few columns, a sunken pool basin), a wide low bedding-plane crawl with layered ceiling ledges, a deep
/// shaft with banded ring walls and a breakdown floor, and a jagged tunnel climbing to a window halfway up the shaft.
/// The field is in metres, positive in the open; <see cref="Open"/> evaluates it anywhere, so a finer mesh can sample it
/// finer than the one-metre voxels can hold it.
/// </summary>
internal static class NaturalCavern
{
    internal const int ChunksX = 5, ChunksY = 4, ChunksZ = 5;

    /// <summary>The field is held within this many metres of its surface; deeper rock and open space read the same.</summary>
    private const float FieldLimit = 2f;

    // The formation hall: an ellipsoid with a gently uneven floor and a pool basin sunk into it.
    private static readonly Vector3 HallCentre = new(26f, 18f, 26f), HallRadii = new(20f, 10f, 17f);
    private const float HallFloor = 12f, HallFloorRoll = 0.5f;
    private static readonly Vector2 PoolCentre = new(31f, 31f);
    private const float PoolRadius = 8f, PoolDepth = 1.3f, PoolRim = 1.5f;

    // Formations: hanging, rising and joined (columns).
    private const int Stalactites = 46, Stalagmites = 22, Columns = 5;
    private const float StalactiteShortest = 2.5f, StalactiteLongest = 9f, StalactiteThinnest = 0.6f, StalactiteThickest = 2.2f;
    private const float StalagmiteShortest = 1.2f, StalagmiteTallest = 5f, StalagmiteThinnest = 0.8f, StalagmiteThickest = 2f;
    private const float ColumnWaist = 1f, ColumnFoot = 3f;
    /// <summary>How softly formations meet the rock they hang from or rise out of.</summary>
    private const float FormationBlend = 0.8f;

    // The bedding-plane crawl: its line across the floor, its half-width, and its low layered ceiling.
    private static readonly Vector3[] CrawlLine = [new(40f, 12.2f, 24f), new(50f, 11.8f, 26f), new(56f, 11.2f, 36f), new(58f, 10.6f, 48f), new(60f, 10f, 56f)];
    private const float CrawlHalfWidth = 7.5f, CrawlWidthRoll = 2f, CrawlHeight = 2.8f, CrawlLedge = 0.45f, CrawlLedges = 3f;
    private const float CrawlFloorRoll = 0.45f, CrawlRimstone = 0.12f, CrawlRimstoneFrequency = 1.7f;

    // The shaft: its axis, its span, a bell-shaped chamber at its foot and banded ring walls.
    private static readonly Vector2 ShaftAxis = new(60f, 58f);
    private const float ShaftFloor = 9f, ShaftTop = 59f, ShaftRadius = 7.5f, ShaftBell = 5f, ShaftBellHeight = 7f;
    private const float ShaftBand = 0.9f, ShaftBandFrequency = 1.5f, ShaftFlute = 0.5f, ShaftFlutes = 7f, ShaftBreakdown = 1.3f;
    /// <summary>How far the shaft's axis wanders and how much narrower it is at its top than its foot.</summary>
    private const float ShaftWander = 3.5f, ShaftTaper = 3f;
    /// <summary>A natural ledge winding up the shaft wall: how far it juts, how thick it is, how far it climbs per turn, and where it runs.</summary>
    private const float LedgeJut = 2.2f, LedgeThickness = 0.9f, LedgeRisePerTurn = 11f, LedgeBottom = 13f, LedgeTop = 52f;

    // A natural rock bridge spanning the hall: its ends, its thickness and how far it sags in the middle.
    private static readonly Vector3 BridgeFrom = new(8f, 20f, 36f), BridgeTo = new(42f, 22f, 15f);
    private const float BridgeRadius = 1.5f, BridgeSag = 2.5f;

    // The jagged tunnel: a winding climb from the hall's north wall to a window on the shaft.
    private static readonly Vector3[] TunnelLine = [new(22f, 15f, 41f), new(17f, 19f, 55f), new(27f, 25f, 68f), new(41f, 30f, 67f), new(53f, 33f, 60f)];
    private const float TunnelRadius = 2.1f, TunnelJag = 1f, TunnelJagFrequency = 0.55f, TunnelFloorBelow = 1.4f;

    /// <summary>How softly the spaces run into each other where they meet.</summary>
    private const float SpaceBlend = 2f;

    // The rock's own unevenness: broad swells and a finer grain, the grain finer than one metre can hold.
    private const float SwellAmplitude = 0.6f, SwellFrequency = 0.15f, GrainAmplitude = 0.25f, GrainFrequency = 0.45f;
    private const float DetailAmplitude = 0.14f, DetailFrequency = 1.4f;

    /// <summary>Where the lights (each with its fire) stand, as x, a height to look down from, and z; they are set on the floor below (two on the shaft's ledge).</summary>
    private static readonly Vector3[] LightPlaces =
    [
        new(12f, 16f, 22f), new(18f, 16f, 34f), new(24f, 16f, 14f), new(40f, 16f, 30f), new(24f, 16f, 28f),
        new(47f, 15f, 25.5f), new(55f, 14f, 35f), new(58f, 13f, 47f),
        new(57f, 14f, 55f), new(64f, 14f, 61f), new(66f, 30f, 58f), new(55f, 46f, 57f),
        new(18f, 22f, 54f), new(30f, 28f, 68f), new(44f, 32f, 66f), new(52f, 35f, 60f),
    ];

    private const float LightAboveFloor = 1.2f;

    private const ulong Seed = 0x6E61_7475_7261_6Cul;

    private static readonly Formation[] Hanging = Formations(Stalactites, 1, StalactiteShortest, StalactiteLongest, StalactiteThinnest, StalactiteThickest);
    private static readonly Formation[] Rising = Formations(Stalagmites, 2, StalagmiteShortest, StalagmiteTallest, StalagmiteThinnest, StalagmiteThickest);
    private static readonly Formation[] Joined = Formations(Columns, 3, 0f, 0f, ColumnWaist, ColumnFoot);

    private readonly record struct Formation(float X, float Z, float Length, float Radius);

    /// <summary>The cavern as a study layout: the field as rock, the voxels as stone where it is solid, and its places.</summary>
    /// <param name="voxelSize">The voxels' side in metres: the world's block, or finer for the micro-voxel study (#10094). The field is the same metres whatever it is.</param>
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable) Generate(ulong seed, double voxelSize = 1d)
    {
        (DungeonVolume volume, RockDensity rock) = StudyField.Sample(Open, ChunksX, ChunksY, ChunksZ, voxelSize);
        DungeonCell arrival = Cell(OnFloor(12f, 22f, 16f));
        DungeonCell shaftFoot = Cell(OnFloor(ShaftAxis.X, ShaftAxis.Y - 4f, 14f));
        DungeonPlan plan = new(seed, 0, DungeonMix.MostlyCave, 1, arrival, shaftFoot,
            [arrival, Cell(OnFloor(22f, 28f, 16f)), Cell(OnFloor(48f, 25.5f, 15f)), shaftFoot, Cell(OnFloor(51f, 61f, 34f))], shaftFoot);
        Vector3 arrive = OnFloor(12f, 22f, 16f);
        DungeonLayout layout = new("natural-cavern", new DungeonVolume(volume.ChunksX, volume.ChunksY, volume.ChunksZ, BlockId.Air), arrive, OnFloor(10f, 22f, 16f),
            [.. LightPlaces.Select(place => OnFloor(place.X, place.Z, place.Y) + (Vector3.UnitY * LightAboveFloor))])
        {
            Rock = rock,
            Natural = true,
            Shape = Open,
            VoxelSize = voxelSize,
        };
        return (layout, plan, new DungeonVerdict(true, "natural cavern study: routes not asked for", 0), volume);
    }

    private static Vector3 OnFloor(float x, float z, float fromY) => StudyField.OnFloor(Open, x, z, fromY);

    private static DungeonCell Cell(Vector3 p) => StudyField.Cell(p);

    /// <summary>The field at a point of the layout, in metres: positive in the open, negative in rock.</summary>
    internal static float Open(Vector3 p)
    {
        float open = SmoothMax(SmoothMax(Hall(p), Crawl(p), SpaceBlend), SmoothMax(Shaft(p), Tunnel(p), SpaceBlend), SpaceBlend);
        if (open > -FieldLimit - SwellAmplitude)
        {
            open = SmoothMin(open, -MathF.Max(FormationRock(p), Bridge(p)), FormationBlend);
            open += (SwellAmplitude * Signed(Noise(p * SwellFrequency, Seed)))
                + (GrainAmplitude * Signed(Noise(p * GrainFrequency, Seed ^ 0x51UL)))
                + (DetailAmplitude * Signed(Noise(p * DetailFrequency, Seed ^ 0xA3UL)));
        }

        return Math.Clamp(open, -FieldLimit, FieldLimit);
    }

    private static float Hall(Vector3 p)
    {
        Vector3 q = (p - HallCentre) / HallRadii;
        float shell = (1f - q.Length()) * MathF.Min(HallRadii.X, MathF.Min(HallRadii.Y, HallRadii.Z));
        return SmoothMin(shell, p.Y - HallFloorAt(p.X, p.Z), 1f);
    }

    private static float HallFloorAt(float x, float z)
    {
        float roll = HallFloorRoll * Signed(Noise(new Vector3(x * 0.12f, 0f, z * 0.12f), Seed ^ 0x77UL));
        float fromPool = Vector2.Distance(new Vector2(x, z), PoolCentre);
        float basin = PoolDepth * (1f - SmoothStep(PoolRadius - PoolRim, PoolRadius, fromPool));
        return HallFloor + roll - basin;
    }

    /// <summary>The ceiling's height over a place in the hall, where hanging formations start.</summary>
    private static float HallCeilingAt(float x, float z)
    {
        float dx = (x - HallCentre.X) / HallRadii.X, dz = (z - HallCentre.Z) / HallRadii.Z;
        return HallCentre.Y + (HallRadii.Y * MathF.Sqrt(MathF.Max(0f, 1f - (dx * dx) - (dz * dz))));
    }

    /// <summary>How deep a point is inside a formation, in metres (positive inside).</summary>
    private static float FormationRock(Vector3 p)
    {
        float rock = -FieldLimit;
        foreach (Formation f in Hanging)
        {
            float top = HallCeilingAt(f.X, f.Z) + 1f;
            float along = (top - p.Y) / (f.Length + 1f);
            if (along is >= 0f and <= 1f)
            {
                float r = f.Radius * (1f - along) * (1f - along * 0.3f);
                rock = MathF.Max(rock, r - Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(f.X, f.Z)));
            }
        }

        foreach (Formation f in Rising)
        {
            float foot = HallFloorAt(f.X, f.Z) - 0.5f;
            float along = (p.Y - foot) / (f.Length + 0.5f);
            if (along is >= 0f and <= 1f)
            {
                float r = f.Radius * MathF.Sqrt(1f - along);
                rock = MathF.Max(rock, r - Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(f.X, f.Z)));
            }
        }

        foreach (Formation f in Joined)
        {
            float foot = HallFloorAt(f.X, f.Z), top = HallCeilingAt(f.X, f.Z);
            if (p.Y >= foot - 1f && p.Y <= top + 1f)
            {
                // An hourglass: wide where it rises and where it hangs, waisted where the two met.
                float t = Math.Clamp((p.Y - foot) / MathF.Max(1f, top - foot), 0f, 1f);
                float r = f.Length + ((f.Radius - f.Length) * MathF.Pow(MathF.Abs((2f * t) - 1f), 1.6f));
                rock = MathF.Max(rock, r - Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(f.X, f.Z)));
            }
        }

        return rock;
    }

    private static float Crawl(Vector3 p)
    {
        (float across, float along, float baseY) = NearestOnLine(CrawlLine, p, flat: true);
        float halfWidth = CrawlHalfWidth + (CrawlWidthRoll * Signed(Noise(new Vector3(along * 0.18f, 0f, 0f), Seed ^ 0x13UL)));
        float rimstone = CrawlRimstone * MathF.Abs(MathF.Sin(p.X * CrawlRimstoneFrequency) * MathF.Sin(p.Z * CrawlRimstoneFrequency * 1.3f));
        float floor = baseY + (CrawlFloorRoll * Signed(Noise(new Vector3(p.X * 0.3f, 0f, p.Z * 0.3f), Seed ^ 0x29UL))) + rimstone;
        // The ceiling is bedding: flat planes stepping in ledges, not a vault.
        float ledge = CrawlLedge * MathF.Floor(CrawlLedges * Noise(new Vector3(p.X * 0.09f, 0f, p.Z * 0.09f), Seed ^ 0x3BUL));
        float ceiling = baseY + CrawlHeight + ledge;
        return SmoothMin(SmoothMin(halfWidth - across, ceiling - p.Y, 1.2f), p.Y - floor, 0.6f);
    }

    private static float Shaft(Vector3 p)
    {
        Vector2 d = new Vector2(p.X, p.Z) - ShaftAxisAt(p.Y);
        float angle = MathF.Atan2(d.Y, d.X);
        float bell = ShaftBell * (1f - SmoothStep(ShaftFloor, ShaftFloor + ShaftBellHeight, p.Y))
            - (ShaftTaper * SmoothStep(ShaftFloor, ShaftTop, p.Y));
        float bands = ShaftBand * MathF.Sin((p.Y * ShaftBandFrequency) + (2f * Noise(new Vector3(angle * 0.8f, p.Y * 0.1f, 0f), Seed ^ 0x45UL)));
        float flutes = ShaftFlute * MathF.Sin((ShaftFlutes * angle) + (p.Y * 0.25f));
        float radius = ShaftRadius + bell + bands + flutes;
        float floor = ShaftFloor + (ShaftBreakdown * Noise(new Vector3(p.X * 0.35f, 0f, p.Z * 0.35f), Seed ^ 0x5FUL));
        float dome = ShaftTop - p.Y + (0.15f * (radius - d.Length()));
        float open = SmoothMin(SmoothMin(radius - d.Length(), p.Y - floor, 1f), dome, 2f);

        // The ledge: rock against the wall, a ledge's thickness under the helix's tread at every turn.
        if (p.Y is > LedgeBottom - LedgeThickness and < LedgeTop)
        {
            float turns = (angle + MathF.PI) / (2f * MathF.PI);
            float above = (p.Y - LedgeBottom - (turns * LedgeRisePerTurn)) % LedgeRisePerTurn;
            above = above < 0f ? above + LedgeRisePerTurn : above;
            float vertical = MathF.Min(above - (LedgeRisePerTurn - LedgeThickness), LedgeRisePerTurn - above);
            float slab = MathF.Min(vertical, d.Length() - (radius - LedgeJut));
            open = SmoothMin(open, -slab, 0.4f);
        }

        return open;
    }

    /// <summary>The shaft's axis at a height: it wanders, so the shaft is a throat, not a bore.</summary>
    private static Vector2 ShaftAxisAt(float y) => ShaftAxis + (ShaftWander * new Vector2(
        Signed(Noise(new Vector3(y * 0.05f, 0f, 0f), Seed ^ 0x8BUL)), Signed(Noise(new Vector3(0f, y * 0.05f, 0f), Seed ^ 0x8CUL))));

    /// <summary>How deep a point is inside the bridge, in metres (positive inside): a sagging, swelling span.</summary>
    private static float Bridge(Vector3 p)
    {
        Vector3 ab = BridgeTo - BridgeFrom;
        float t = Math.Clamp(Vector3.Dot(p - BridgeFrom, ab) / ab.LengthSquared(), 0f, 1f);
        Vector3 on = BridgeFrom + (ab * t) - new Vector3(0f, BridgeSag * 4f * t * (1f - t), 0f);
        float swell = BridgeRadius * (1f + (0.6f * MathF.Abs((2f * t) - 1f)));
        return swell - Vector3.Distance(p, on);
    }

    private static float Tunnel(Vector3 p)
    {
        (float distance, _, float centreY) = NearestOnLine(TunnelLine, p, flat: false);
        float jag = TunnelJag * MathF.Abs(Signed(Noise(p * TunnelJagFrequency, Seed ^ 0x61UL)));
        float tube = TunnelRadius + jag - distance;
        return SmoothMin(tube, p.Y - (centreY - TunnelFloorBelow), 0.5f);
    }

    /// <summary>
    /// The nearest point of a polyline: the distance to it (across the floor only when <paramref name="flat"/>), how far
    /// along the line it lies, and the line's height there.
    /// </summary>
    private static (float Distance, float Along, float Y) NearestOnLine(Vector3[] line, Vector3 p, bool flat)
    {
        float best = float.MaxValue, bestAlong = 0f, bestY = line[0].Y, travelled = 0f;
        for (int i = 0; i < line.Length - 1; i++)
        {
            Vector3 a = line[i], b = line[i + 1];
            Vector3 ab = flat ? new Vector3(b.X - a.X, 0f, b.Z - a.Z) : b - a;
            Vector3 ap = flat ? new Vector3(p.X - a.X, 0f, p.Z - a.Z) : p - a;
            float t = Math.Clamp(Vector3.Dot(ap, ab) / ab.LengthSquared(), 0f, 1f);
            float distance = (ap - (ab * t)).Length();
            if (distance < best)
            {
                best = distance;
                bestAlong = travelled + (t * ab.Length());
                bestY = a.Y + ((b.Y - a.Y) * t);
            }

            travelled += ab.Length();
        }

        return (best, bestAlong, bestY);
    }

    private static Formation[] Formations(int count, ulong salt, float shortest, float longest, float thinnest, float thickest)
    {
        DungeonRandom random = new(Seed ^ (salt * 0x9E37_79B9_7F4A_7C15UL));
        Formation[] formations = new Formation[count];
        for (int i = 0; i < count; i++)
        {
            float x, z;
            do
            {
                x = HallCentre.X + (HallRadii.X * 0.85f * Signed(Unit(random)));
                z = HallCentre.Z + (HallRadii.Z * 0.85f * Signed(Unit(random)));
            }
            while (Square((x - HallCentre.X) / HallRadii.X) + Square((z - HallCentre.Z) / HallRadii.Z) > 0.7f);

            formations[i] = new Formation(x, z, shortest + ((longest - shortest) * Unit(random)), thinnest + ((thickest - thinnest) * Unit(random)));
        }

        return formations;
    }

    private static float Unit(DungeonRandom random) => (random.Next() >> 40) / (float)(1UL << 24);

    private static float Square(float v) => v * v;

    internal static float Signed(float unit) => (unit * 2f) - 1f;

    internal static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    internal static float SmoothMin(float a, float b, float k)
    {
        float h = Math.Clamp(0.5f + (0.5f * (b - a) / k), 0f, 1f);
        return b + ((a - b) * h) - (k * h * (1f - h));
    }

    internal static float SmoothMax(float a, float b, float k) => -SmoothMin(-a, -b, k);

    /// <summary>Smooth value noise in [0, 1].</summary>
    internal static float Noise(Vector3 p, ulong seed)
    {
        int ix = (int)MathF.Floor(p.X), iy = (int)MathF.Floor(p.Y), iz = (int)MathF.Floor(p.Z);
        float fx = p.X - ix, fy = p.Y - iy, fz = p.Z - iz;
        fx = fx * fx * (3 - (2 * fx));
        fy = fy * fy * (3 - (2 * fy));
        fz = fz * fz * (3 - (2 * fz));
        float L(int dx, int dy, int dz) => Hash(seed, ix + dx, iy + dy, iz + dz);
        static float Lerp(float a, float b, float t) => a + ((b - a) * t);
        return Lerp(Lerp(Lerp(L(0, 0, 0), L(1, 0, 0), fx), Lerp(L(0, 1, 0), L(1, 1, 0), fx), fy),
            Lerp(Lerp(L(0, 0, 1), L(1, 0, 1), fx), Lerp(L(0, 1, 1), L(1, 1, 1), fx), fy), fz);
    }

    internal static float Hash(ulong seed, int x, int y, int z)
    {
        ulong h = seed ^ ((ulong)(uint)x * 0x9E37_79B1UL) ^ ((ulong)(uint)y * 0x85EB_CA77UL << 17) ^ ((ulong)(uint)z * 0xC2B2_AE3DUL << 31);
        h ^= h >> 33;
        h *= 0xFF51_AFD7_ED55_8CCDUL;
        h ^= h >> 33;
        return (h >> 40) / (float)(1UL << 24);
    }
}
