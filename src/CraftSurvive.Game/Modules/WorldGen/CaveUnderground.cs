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

    private readonly Vector3[] path;

    private CavePlacement(PoiSite site, int forwardX, int forwardZ, Vector3[] path, Vector3 corner)
    {
        Site = site;
        ForwardX = forwardX;
        ForwardZ = forwardZ;
        this.path = path;
        Corner = corner;
        // The cave's +x runs along the mouth's facing, its +z a quarter turn from it (a rotation, not a mirror).
        (AcrossX, AcrossZ) = (-forwardZ, forwardX);
        Vector3[] corners = [Corner, ToWorld(new Vector3(SizeX, 0, 0)), ToWorld(new Vector3(0, 0, SizeZ)), ToWorld(new Vector3(SizeX, 0, SizeZ))];
        BoxMinimum = ((long)MathF.Floor(corners.Min(c => c.X)), (long)MathF.Floor(Corner.Y), (long)MathF.Floor(corners.Min(c => c.Z)));
        BoxMaximum = ((long)MathF.Ceiling(corners.Max(c => c.X)) - 1, (long)MathF.Floor(Corner.Y) + SizeY - 1, (long)MathF.Ceiling(corners.Max(c => c.Z)) - 1);
        float reachX = TendrilHalfWidth + 1f, reachY = TendrilHalfHeight + 1f;
        TendrilMinimum = ((long)MathF.Floor(path.Min(p => p.X) - reachX), (long)MathF.Floor(path.Min(p => p.Y) - reachY), (long)MathF.Floor(path.Min(p => p.Z) - reachX));
        TendrilMaximum = ((long)MathF.Ceiling(path.Max(p => p.X) + reachX), (long)MathF.Ceiling(path.Max(p => p.Y) + reachY), (long)MathF.Ceiling(path.Max(p => p.Z) + reachX));
    }

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

    /// <summary>The tendril's centre line, from outside the mouth to below the way in, in world metres.</summary>
    internal IReadOnlyList<Vector3> Path => path;

    /// <summary>The cave's way in, in world metres: the tendril's end.</summary>
    internal Vector3 WayInWorld => ToWorld(new Vector3(WayIn.X, SinkHeight, WayIn.Y));

    /// <summary>A stable key for the mouth: its site's anchor cell.</summary>
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

    /// <summary>
    /// How far inside the tendril a world point is, in metres (positive inside, empty; negative in the rock round it): its
    /// section's half-height less its distance from the centre line, the section squashed to a circle.
    /// </summary>
    internal float TendrilOpen(Vector3 point)
    {
        if (point.X < TendrilMinimum.X || point.X > TendrilMaximum.X + 1 || point.Y < TendrilMinimum.Y || point.Y > TendrilMaximum.Y + 1
            || point.Z < TendrilMinimum.Z || point.Z > TendrilMaximum.Z + 1)
        {
            return float.NegativeInfinity;
        }

        float squash = TendrilHalfHeight / TendrilHalfWidth;
        Vector3 scale = new(squash, 1f, squash);
        float nearest = float.PositiveInfinity;
        Vector3 p = point * scale;
        // The path runs straight along the mouth's facing a step apart, so only the segments near the point's own
        // distance along it can be nearest (the bend aside is far smaller than a tendril's reach along).
        float along = ((point.X - path[0].X) * ForwardX) + ((point.Z - path[0].Z) * ForwardZ);
        int first = Math.Clamp((int)MathF.Floor(along / TendrilStep) - 2, 0, path.Length - 2);
        int last = Math.Clamp((int)MathF.Floor(along / TendrilStep) + 2, 0, path.Length - 2);
        for (int s = first; s <= last; s++)
        {
            Vector3 a = path[s] * scale, b = path[s + 1] * scale;
            Vector3 ab = b - a;
            float t = Math.Clamp(Vector3.Dot(p - a, ab) / Math.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(p, a + (ab * t)));
        }

        // The wall is rock, not a bore: its distance wanders by a smooth noise a few metres across.
        return TendrilHalfHeight - MathF.Sqrt(nearest) + (TendrilRoughness * Noise(point * TendrilRoughnessFrequency, Site));
    }

    /// <summary>How far the tendril's wall wanders in and out, in metres, and how often (per metre).</summary>
    private const float TendrilRoughness = 0.45f, TendrilRoughnessFrequency = 0.3f;

    /// <summary>A smooth value noise in -1..1, a pure function of the point and the mouth.</summary>
    private static float Noise(Vector3 p, PoiSite site)
    {
        int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y), z0 = (int)MathF.Floor(p.Z);
        float fx = p.X - x0, fy = p.Y - y0, fz = p.Z - z0;
        fx = fx * fx * (3f - (2f * fx));
        fy = fy * fy * (3f - (2f * fy));
        fz = fz * fz * (3f - (2f * fz));
        ulong salt = ((ulong)site.CellX * 0x9E37_79B9UL) ^ ((ulong)site.CellZ * 0x85EB_CA6BUL);
        float Corner(int dx, int dy, int dz) =>
            (Mix(salt ^ ((ulong)(x0 + dx) * 0x27D4_EB2F_1656_67C5UL) ^ ((ulong)(y0 + dy) * 0x1656_67B1_9E37_79F9UL) ^ ((ulong)(z0 + dz) * 0xC2B2_AE3D_27D4_EB4FUL)) & 0xFFFF) / 32767.5f - 1f;
        float Lerp(float a, float b, float t) => a + ((b - a) * t);
        float x00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), fx), x10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), fx);
        float x01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), fx), x11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), fx);
        return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
    }

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
        CavePlacement placement = new(site, fx, fz, [.. points], corner);
        // The finite world's edge: the box and the tendril keep inside it, clear of the border wall.
        long inner = radius - (GenerationConstants.WorldWallThickness + ApartMetres);
        if (Math.Min(placement.BoxMinimum.X, placement.TendrilMinimum.X) < -inner || Math.Max(placement.BoxMaximum.X, placement.TendrilMaximum.X) > inner
            || Math.Min(placement.BoxMinimum.Z, placement.TendrilMinimum.Z) < -inner || Math.Max(placement.BoxMaximum.Z, placement.TendrilMaximum.Z) > inner)
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

    private static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51_AFD7_ED55_8CCDUL;
        value ^= value >> 33;
        value *= 0xC4CE_B9FE_1A85_EC53UL;
        return value ^ (value >> 33);
    }
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
    private readonly ConcurrentDictionary<(long X, long Z), CavePlacement[]> nearChunk = new();
    private const int NearChunkCacheLimit = 16_384;

    // The caves admitted so far, swapped whole so a reader on any thread sees one consistent set.
    private volatile IReadOnlyDictionary<(long X, long Z), CaveInterior> admitted = new Dictionary<(long X, long Z), CaveInterior>();

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
        if (nearChunk.Count >= NearChunkCacheLimit) nearChunk.Clear();
        nearChunk[(chunkX, chunkZ)] = found;
        return found;
    }

    /// <summary>Whether anything underground (a tendril, or a cave's box) reaches into a chunk: its chunk is never cached.</summary>
    internal bool Touches(TerrainChunkAddress chunk)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        long y0 = chunk.Y * edge, y1 = y0 + edge - 1;
        foreach (CavePlacement cave in NearChunkColumn(chunk.X, chunk.Z))
        {
            if ((y1 >= cave.BoxMinimum.Y && y0 <= cave.BoxMaximum.Y) || (y1 >= cave.TendrilMinimum.Y && y0 <= cave.TendrilMaximum.Y))
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
    internal (ushort Material, float Magnitude)? At(VoxelAddress voxel, long surface, double height, ushort generated)
    {
        CavePlacement[] near = NearChunkColumn(GridMath.FloorDivide(voxel.X, TerrainConstants.ChunkEdgeLength), GridMath.FloorDivide(voxel.Z, TerrainConstants.ChunkEdgeLength));
        if (near.Length == 0)
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

        // Rock round a tendril is the overworld's ground: bedrock the world's floor puts there is stone, so it is reconstructed.
        if (material == TerrainConstants.EmptyMaterial || material == (ushort)BlockId.Bedrock)
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
        material = TerrainConstants.EmptyMaterial;
        magnitude = 0f;
        IReadOnlyDictionary<(long X, long Z), CaveInterior> caves = admitted;
        if (caves.Count == 0)
        {
            return false;
        }

        CavePlacement[] near = NearChunkColumn(GridMath.FloorDivide(voxel.X, TerrainConstants.ChunkEdgeLength), GridMath.FloorDivide(voxel.Z, TerrainConstants.ChunkEdgeLength));
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
