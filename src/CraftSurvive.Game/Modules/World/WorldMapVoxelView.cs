using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// Prototype faceted map view (#9436, #9437): the generated map voxelized as dual-contoured
/// terrain, sampling the same <see cref="WorldMap"/> walking terrain does. A coarse layer covers
/// the whole map; a finer patch around the party carries close-range detail, and the coarse
/// ground beneath that patch is lowered so the two never fight. The patch follows the party in
/// whole-chunk steps (#9468): only the strips that enter, leave or change their edge fade are
/// streamed, within a per-update budget. A camera rig locked to the party zooms and orbits.
/// Presentation units are one coarse cell (<see cref="CellMetres"/>).
/// </summary>
internal sealed class WorldMapVoxelView : IDisposable
{
    internal const double CellMetres = 32;
    private const int DetailPerCell = 4;
    private const double DetailMetres = CellMetres / DetailPerCell;
    /// <summary>Detail chunks on each side of the party's chunk covered by the patch (one chunk is 128 m).</summary>
    private const int DetailRadiusChunks = 8;
    private const int EdgeLength = MapVoxelLayer.EdgeLength;
    private const double DetailChunkMetres = EdgeLength * DetailMetres;
    /// <summary>Coarse voxels the coarse ground sinks under the detail patch, hiding it there.</summary>
    private const double PatchSink = 1.5;
    /// <summary>
    /// Metres over which regional relief fades out toward the patch edge, so it meets the coarse map
    /// flush. One chunk wide, so a one-chunk shift re-streams only the strips beside each edge.
    /// </summary>
    private const double EdgeFadeMetres = DetailChunkMetres;
    private const int OriginY = -20000;
    private const int ChunksPerUpdate = 24;
    private const double ExposedRock = 0.6;
    // Markers keep a steady apparent size: their scale follows the camera's distance.
    private const float MarkerScalePerDistance = 0.012f;
    private const float PartyScalePerDistance = 0.006f;
    private const float MinimumMarkerScale = 0.04f;
    /// <summary>Click picking marches the camera ray in quarter cells and refines the crossing this many times.</summary>
    private const float PickStepUnits = 0.25f;
    private const int PickRefinements = 12;
    private const double FallbackAspect = 16.0 / 9.0;
    private const float CloseDistance = 6;
    // Each environment has a few tones chosen by noise, so close views read as mottled ground.
    private const int Tones = 3;
    private const float ToneStep = 0.07f;
    private const double ToneWavelength = 90;
    private const ulong ToneSalt = 0x5851F42D4C957F2DUL;
    private const float FramingDistance = 1.25f;
    private const uint RiverSlot = 1;
    private const uint RockSlot = 2;
    private const uint FirstBiomeSlot = 3;
    private static readonly Color PartyColor = new(0.95f, 0.95f, 0.85f, 1);

    private readonly IEngineContext engine;
    private readonly WorldMap map;
    private readonly int cells;
    private readonly Dictionary<uint, Material> materials = [];
    private readonly Appearance marker;
    private readonly Appearance party;
    private readonly MapVoxelLayer coarse;
    private readonly MapVoxelLayer detail;
    private readonly MapCameraRig rig;
    private readonly MapClutter clutter;
    private readonly TerrainGroundMaterials? coarseGround, detailGround;
    /// <summary>Clutter is shown only from this camera distance inward, where it reads at all.</summary>
    private const float ClutterDistance = 160;
    private Vector3 partyPosition;
    private Vector2 waypoint;
    private Vector3[] routeMarkers = [];
    private readonly Appearance routeMarker, waypointMarker;
    private int factsVersion, publishedVersion = -1;
    private bool waypointForward, waypointBack, waypointLeft, waypointRight;
    // The waypoint moves at a fixed fraction of the camera distance per update, so it suits any zoom.
    private const float WaypointSpeedPerDistance = 0.012f;
    private const double RouteMarkerSpacingMetres = 48;
    private const float RouteMarkerScalePerDistance = 0.004f;
    private static readonly Color RouteColor = new(0.98f, 0.86f, 0.3f, 1);
    private static readonly Color WaypointColor = new(0.35f, 0.85f, 1f, 1);
    private long startedAt;
    private float publishedDistance = float.NaN;
    private Vector2 detailMinimum, detailMaximum;
    private long windowChunkX = long.MinValue, windowChunkZ = long.MinValue;
    private readonly long firstDetailChunk, lastDetailChunk;
    private int windowShifts;
    private double loadMilliseconds;

    internal WorldMapVoxelView(IEngineContext engine, ProductContent content, WorldMap map, Vector3 partyWorldFeet, MapSurfaceStyle style)
    {
        this.engine = engine;
        this.map = map;
        Style = style;
        cells = Math.Max(1, (int)Math.Round(map.Configuration.Size / CellMetres));
        try
        {
            materials[RiverSlot] = Flat(MapPalette.River);
            materials[RockSlot] = Flat(MapPalette.Stone);
            foreach (MapBiome biome in Enum.GetValues<MapBiome>())
            for (int tone = 0; tone < Tones; tone++)
                materials[Slot(biome, tone)] = Flat(Shade(MapPalette.For(biome), 1 + (tone - Tones / 2) * ToneStep));
            if (style.Textures is GroundTextureSet textures)
            {
                // Tiles span the same ground at both resolutions: the scale is in each layer's cells.
                coarseGround = new TerrainGroundMaterials(engine, content, CellMetres, textures);
                detailGround = new TerrainGroundMaterials(engine, content, DetailMetres, textures);
            }
            marker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, new Color(1, 0.38f, 0.1f, 1)));
            party = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, PartyColor));
            routeMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, RouteColor));
            waypointMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, WaypointColor));
            waypoint = new(partyWorldFeet.X, partyWorldFeet.Z);

            firstDetailChunk = (long)Math.Floor(-map.Radius / DetailChunkMetres);
            lastDetailChunk = (long)Math.Floor((map.Radius - 1) / DetailChunkMetres);
            coarse = Layer(1, CellMetres, (x, z) => InDetail(x, z) ? PatchSink : 0, false, coarseGround);
            detail = Layer(1d / DetailPerCell, DetailMetres, (_, _) => 0, true, detailGround);
            clutter = new MapClutter(engine, map);
            // The window comes first: the coarse ground samples it to sink beneath the patch.
            Follow(new(partyWorldFeet.X, partyWorldFeet.Z));
            long firstCoarse = GridMath.FloorDivide(-cells / 2, EdgeLength), lastCoarse = GridMath.FloorDivide(cells - cells / 2 - 1, EdgeLength);
            coarse.Want(from cz in Range(firstCoarse, lastCoarse) from cx in Range(firstCoarse, lastCoarse) select (cx, cz));
            partyPosition = Surface(partyWorldFeet.X, partyWorldFeet.Z);
            rig = new MapCameraRig(engine, partyPosition, CloseDistance, cells * FramingDistance, cells);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal MapSurfaceStyle Style { get; }

    internal bool Loaded => coarse.Settled && detail.Settled;
    internal string Readout => FormattableString.Invariant(
        $"style={Style.Name};cells={cells};cellMetres={CellMetres};detailMetres={DetailMetres};clutter={clutter.Count};coarseChunks={coarse.ResidentChunks}+{coarse.PendingChunks}pending;detailChunks={detail.ResidentChunks}+{detail.PendingChunks}pending;windowShifts={windowShifts};window={detailMinimum.X:F0},{detailMinimum.Y:F0};loaded={Loaded};coarseWorkMs={coarse.WorkMilliseconds:F0};detailWorkMs={detail.WorkMilliseconds:F0};wallMs={loadMilliseconds:F0};lastPick={PickReadout};")
        + rig.Readout;

    internal void Activate() => rig.Activate();

    internal bool Steer(ReadOnlySpan<ProductInputEvent> events) => rig.Steer(events);

    internal void SetCamera(float distance, float yawDegrees, float pitchDegrees) => rig.Set(distance, yawDegrees, pitchDegrees);

    /// <summary>Site markers and the party token, placed on the faceted relief.</summary>
    internal AppearanceFact[] Facts => [
        .. clutter.Facts(rig.Distance <= ClutterDistance),
        Marker(ProductIds.WorldMapPartyObject, partyPosition, PartyScalePerDistance, party),
        Marker(ProductIds.WorldMapWaypointObject, Surface(waypoint.X, waypoint.Y), PartyScalePerDistance, waypointMarker),
        .. routeMarkers.Select((point, i) => Marker(ProductIds.WorldMapRouteBase + (ulong)i, point, RouteMarkerScalePerDistance, routeMarker)),
        .. map.Sites.Select((site, i) => Marker(ProductIds.WorldMapSiteBase + (ulong)i,
            Surface(site.X, site.Z), MarkerScalePerDistance, marker))];

    /// <summary>Whether zoom, the party, its route or the waypoint changed since the facts were last published.</summary>
    internal bool MarkersStale => rig.Distance != publishedDistance || factsVersion != publishedVersion;

    /// <summary>The keyboard waypoint, in world metres X/Z.</summary>
    internal Vector2 Waypoint => waypoint;
    /// <summary>Where the last free-cursor click landed, or null when it missed the map.</summary>
    private Vector2? lastPick;
    internal string PickReadout => lastPick is Vector2 p ? FormattableString.Invariant($"{p.X:F0},{p.Y:F0}") : "none";

    /// <summary>Move the party token; the camera and the detail patch follow it.</summary>
    internal void MoveParty(Vector2 world)
    {
        Follow(world);
        partyPosition = Surface(world.X, world.Y);
        rig.FocusOn(partyPosition);
        factsVersion++;
    }

    /// <summary>Show a planned route as a ribbon of markers on the drawn surface, or clear it.</summary>
    internal void ShowRoute(IReadOnlyList<Vector2>? points)
    {
        List<Vector3> markers = [];
        if (points is not null)
        {
            double carried = 0;
            for (int i = 1; i < points.Count && markers.Count < ProductIds.WorldMapRouteLimit; i++)
            {
                Vector2 a = points[i - 1], b = points[i];
                double length = Vector2.Distance(a, b);
                for (double along = RouteMarkerSpacingMetres - carried; along < length; along += RouteMarkerSpacingMetres)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)(along / length));
                    markers.Add(Surface(p.X, p.Y));
                }
                carried = (carried + length) % RouteMarkerSpacingMetres;
            }
        }
        routeMarkers = [.. markers];
        factsVersion++;
    }

    /// <summary>
    /// Move the waypoint with held W/A/S/D relative to the camera's heading, or put it where a
    /// free-cursor click lands on the relief. Returns true when T or a click asks for a route to it.
    /// </summary>
    internal bool SteerWaypoint(ReadOnlySpan<ProductInputEvent> events)
    {
        bool plan = false;
        foreach (ProductInputEvent input in events)
        {
            if (input.Kind == InputEventKind.Clear) { waypointForward = waypointBack = waypointLeft = waypointRight = false; continue; }
            if (input is { Kind: InputEventKind.PointerButton, PointerButton: PointerButton.Primary, Edge: InputEdge.Pressed, HasPosition: true })
            {
                lastPick = Pick(new(input.X, input.Y));
                if (lastPick is Vector2 picked)
                {
                    waypoint = picked;
                    factsVersion++;
                    plan = true;
                }
                continue;
            }
            if (input.Kind != InputEventKind.Key || input.Edge == InputEdge.None) continue;
            bool held = input.Edge == InputEdge.Pressed;
            switch (input.Keyboard)
            {
                case KeyboardControl.KeyW: waypointForward = held; break;
                case KeyboardControl.KeyS: waypointBack = held; break;
                case KeyboardControl.KeyA: waypointLeft = held; break;
                case KeyboardControl.KeyD: waypointRight = held; break;
                case KeyboardControl.KeyT when held: plan = true; break;
            }
        }
        float forward = (waypointForward ? 1 : 0) - (waypointBack ? 1 : 0), right = (waypointRight ? 1 : 0) - (waypointLeft ? 1 : 0);
        if (forward != 0 || right != 0)
        {
            // Camera-relative: ahead is the camera's ground heading, right is a quarter turn clockwise from it.
            Vector2 ahead = rig.Heading, side = new(-ahead.Y, ahead.X);
            float step = (float)(rig.Distance * WaypointSpeedPerDistance * CellMetres);
            Vector2 moved = waypoint + (ahead * forward + side * right) * step;
            float limit = (float)map.Radius;
            waypoint = Vector2.Clamp(moved, new(-limit), new(limit));
            factsVersion++;
        }
        return plan;
    }

    /// <summary>
    /// Where a viewport point (normalized, bottom-left) lands on the drawn relief: march the camera ray
    /// in steps finer than a coarse cell until it passes below the surface, then refine by bisection.
    /// Null when the ray leaves the map without meeting it.
    /// </summary>
    internal Vector2? Pick(Vector2 point)
    {
        CameraSurfaceReadout surface = engine.CameraView.ReadSurface();
        double aspect = surface.Reported && surface.CssHeight > 0 ? surface.CssWidth / surface.CssHeight : FallbackAspect;
        CameraRay ray = rig.Ray(point, aspect);
        Vector3 direction = Vector3.Normalize(ray.Direction);
        float reach = rig.Distance * 2 + cells * 2;
        bool Below(float t)
        {
            Vector3 at = ray.Origin + direction * t;
            return at.Y <= Surface(at.X * CellMetres, at.Z * CellMetres).Y;
        }
        float previous = 0;
        for (float t = PickStepUnits; t <= reach; t += PickStepUnits)
        {
            if (!Below(t)) { previous = t; continue; }
            float low = previous, high = t;
            for (int i = 0; i < PickRefinements; i++)
            {
                float middle = (low + high) / 2;
                if (Below(middle)) high = middle; else low = middle;
            }
            Vector3 hit = ray.Origin + direction * high;
            Vector2 world = new((float)(hit.X * CellMetres), (float)(hit.Z * CellMetres));
            float limit = (float)map.Radius;
            return Math.Abs(world.X) <= limit && Math.Abs(world.Y) <= limit ? world : null;
        }
        return null;
    }

    private AppearanceFact Marker(ulong id, Vector3 ground, float scalePerDistance, Appearance appearance)
    {
        publishedDistance = rig.Distance;
        publishedVersion = factsVersion;
        float scale = Math.Max(MinimumMarkerScale, rig.Distance * scalePerDistance);
        return new(id, false, 0, new(ground + Vector3.UnitY * scale, Quaternion.Identity, Vector3.One * scale), appearance, true, RenderLayer.Scene);
    }

    /// <summary>Apply a bounded batch each update: the whole map first, then the patch around the party.</summary>
    internal void Advance()
    {
        bool wasLoaded = Loaded;
        if (startedAt == 0) startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int budget = ChunksPerUpdate;
        if (coarse.PendingChunks > 0 || !coarse.Settled)
        {
            coarse.Advance(budget);
            // Until the whole map is up, it has the budget; afterwards both share it.
            if (!coarse.Settled && loadMilliseconds == 0) return;
            budget /= 2;
        }
        detail.Advance(budget);
        if (!wasLoaded && Loaded && loadMilliseconds == 0)
            loadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
    }

    /// <summary>
    /// Keep the detail window centred on the party, in whole detail chunks and inside the map. A
    /// shift streams the strips entering and leaving, re-streams the strips beside each edge whose
    /// fade changed, re-sinks the coarse cells whose cover changed, and re-scatters the clutter.
    /// </summary>
    private void Follow(Vector2 world)
    {
        long span = 2 * DetailRadiusChunks;
        long maximumStart = Math.Max(firstDetailChunk, lastDetailChunk - span + 1);
        long x0 = Math.Clamp((long)Math.Floor(world.X / DetailChunkMetres) - DetailRadiusChunks, firstDetailChunk, maximumStart);
        long z0 = Math.Clamp((long)Math.Floor(world.Y / DetailChunkMetres) - DetailRadiusChunks, firstDetailChunk, maximumStart);
        if (x0 == windowChunkX && z0 == windowChunkZ) return;
        bool first = windowChunkX == long.MinValue;
        (Vector2 oldMinimum, Vector2 oldMaximum) = (detailMinimum, detailMaximum);
        long oldX0 = windowChunkX, oldZ0 = windowChunkZ;
        windowChunkX = x0;
        windowChunkZ = z0;
        long x1 = Math.Min(lastDetailChunk, x0 + span - 1), z1 = Math.Min(lastDetailChunk, z0 + span - 1);
        detailMinimum = new((float)Math.Max(-map.Radius, x0 * DetailChunkMetres), (float)Math.Max(-map.Radius, z0 * DetailChunkMetres));
        detailMaximum = new((float)Math.Min(map.Radius, (x1 + 1) * DetailChunkMetres), (float)Math.Min(map.Radius, (z1 + 1) * DetailChunkMetres));

        detail.Want(from cz in Range(z0, z1) from cx in Range(x0, x1) select (cx, cz));
        if (!first)
        {
            windowShifts++;
            // Columns beside either window's edges carry a fade that has changed.
            detail.Invalidate(Ring(x0, z0, x1, z1).Concat(Ring(oldX0, oldZ0, Math.Min(lastDetailChunk, oldX0 + span - 1), Math.Min(lastDetailChunk, oldZ0 + span - 1))));
            coarse.Invalidate(CoarseChanged(oldMinimum, oldMaximum));
        }
        clutter.Scatter(detailMinimum, detailMaximum, Surface);
        factsVersion++;
    }

    /// <summary>Coarse chunk columns holding a cell whose cover by the patch changed.</summary>
    private IEnumerable<(long X, long Z)> CoarseChanged(Vector2 oldMinimum, Vector2 oldMaximum)
    {
        HashSet<(long, long)> changed = [];
        long ax = (long)Math.Floor(Math.Min(oldMinimum.X, detailMinimum.X) / CellMetres), bx = (long)Math.Floor(Math.Max(oldMaximum.X, detailMaximum.X) / CellMetres);
        long az = (long)Math.Floor(Math.Min(oldMinimum.Y, detailMinimum.Y) / CellMetres), bz = (long)Math.Floor(Math.Max(oldMaximum.Y, detailMaximum.Y) / CellMetres);
        for (long z = az; z <= bz; z++)
        for (long x = ax; x <= bx; x++)
        {
            double cx = (x + 0.5) * CellMetres, cz = (z + 0.5) * CellMetres;
            bool before = cx >= oldMinimum.X && cz >= oldMinimum.Y && cx < oldMaximum.X && cz < oldMaximum.Y;
            if (before != InDetail(cx, cz)) changed.Add((GridMath.FloorDivide(x, EdgeLength), GridMath.FloorDivide(z, EdgeLength)));
        }
        return changed;
    }

    private static IEnumerable<(long X, long Z)> Ring(long x0, long z0, long x1, long z1) =>
        from cz in Range(z0, z1) from cx in Range(x0, x1) where cx == x0 || cx == x1 || cz == z0 || cz == z1 select (cx, cz);

    private static IEnumerable<long> Range(long first, long last)
    {
        for (long i = first; i <= last; i++) yield return i;
    }

    private bool InDetail(double worldX, double worldZ) =>
        worldX >= detailMinimum.X && worldZ >= detailMinimum.Y && worldX < detailMaximum.X && worldZ < detailMaximum.Y;

    public void Dispose()
    {
        clutter?.Dispose();
        detail?.Dispose();
        coarse?.Dispose();
        detailGround?.Dispose();
        coarseGround?.Dispose();
        rig?.Dispose();
        party?.Dispose();
        routeMarker?.Dispose();
        waypointMarker?.Dispose();
        marker?.Dispose();
        foreach (Material material in materials.Values) material.Dispose();
    }

    /// <summary>
    /// One streamed layer: each voxel column samples the map at its centre, with exaggerated relief,
    /// lowered by <paramref name="sink"/> coarse voxels at a world point. Columns beyond the map are empty.
    /// </summary>
    private MapVoxelLayer Layer(double voxelSize, double cellMetres, Func<double, double, double> sink, bool localRelief, TerrainGroundMaterials? ground)
    {
        double metresPerVoxel = cellMetres / WorldMapPresentation.VerticalExaggeration;
        double origin = OriginY / voxelSize;
        void Sample(long chunkX, long chunkZ, Span<double> surface, Span<uint> material)
        {
            for (int z = 0; z < EdgeLength; z++)
            for (int x = 0; x < EdgeLength; x++)
            {
                int i = z * EdgeLength + x;
                double worldX = (chunkX * EdgeLength + x + 0.5) * cellMetres, worldZ = (chunkZ * EdgeLength + z + 0.5) * cellMetres;
                if (Math.Abs(worldX) > map.Radius || Math.Abs(worldZ) > map.Radius) { surface[i] = double.NaN; continue; }
                MapSample sample = map.Sample(worldX, worldZ);
                // The detail patch carries the same regional relief walking terrain adds to the map.
                surface[i] = origin + Top(sample, worldX, worldZ, localRelief) / metresPerVoxel - sink(worldX, worldZ) / voxelSize;
                material[i] = sample.Elevation < GenerationConstants.WaterLevel ? Slot(MapBiome.Sea)
                    : RiverCovers(worldX, worldZ, cellMetres) ? RiverSlot
                    : sample.Rock >= ExposedRock ? RockSlot
                    : Slot(WorldMap.Biome(sample), ground is null ? Tone(worldX, worldZ) : Tones / 2);
            }
        }
        if (ground is null) return new MapVoxelLayer(engine, voxelSize, Sample, materials);
        // Textured: environments on a layer share the blended material; the rest keep their own.
        Dictionary<uint, Material> bindings = new(materials);
        List<(uint Slot, uint Layer)> layered = [];
        foreach (MapBiome biome in Enum.GetValues<MapBiome>())
        {
            if (biome == MapBiome.Sea) continue;
            // Textures carry their own variation, so a textured layer uses one tone per environment
            // (an Engine terrain layer set takes at most 16 slots).
            int layer = Style.Layer(biome);
            uint slot = Slot(biome);
            if (layer >= 0) { bindings[slot] = ground.Layered; layered.Add((slot, (uint)layer)); }
            else if (Style.PlainRock is string rock) bindings[slot] = ground.Plain(rock);
        }
        if (Style.RockLayer is int rockLayer) { bindings[RockSlot] = ground.Layered; layered.Add((RockSlot, (uint)rockLayer)); }
        else if (Style.PlainRock is string plain) bindings[RockSlot] = ground.Plain(plain);
        return new MapVoxelLayer(engine, voxelSize, Sample, bindings,
            ([.. layered.Select(entry => entry.Slot)], [.. layered.Select(entry => entry.Layer)], ground.Settings.TransitionCells));
    }

    /// <summary>
    /// The drawn top at a point: ground (with regional relief where the detail patch carries it),
    /// raised to a river's surface or the sea. Markers and the party stand on exactly this.
    /// </summary>
    private double Top(MapSample sample, double worldX, double worldZ, bool localRelief)
    {
        double ground = localRelief
            ? sample.Elevation + EdgeFade(worldX, worldZ) * RegionalTerrain.Relief(map.Configuration.Contract.GeographyNoiseSeed, sample, worldX, worldZ)
            : sample.Elevation;
        return Math.Max(sample.InRiver ? Math.Max(ground, sample.RiverSurface) : ground, GenerationConstants.WaterLevel);
    }

    /// <summary>1 inside the patch, easing to 0 at its edge.</summary>
    private double EdgeFade(double worldX, double worldZ)
    {
        double inset = Math.Min(Math.Min(worldX - detailMinimum.X, detailMaximum.X - worldX), Math.Min(worldZ - detailMinimum.Y, detailMaximum.Y - worldZ));
        return WorldMap.Smooth(Math.Clamp(inset / EdgeFadeMetres, 0, 1));
    }

    /// <summary>A point on the faceted surface as drawn: the detail patch where it covers, else the coarse map.</summary>
    private Vector3 Surface(double worldX, double worldZ) =>
        Position(worldX, Top(map.Sample(worldX, worldZ), worldX, worldZ, InDetail(worldX, worldZ)), worldZ);

    private Vector3 Position(double worldX, double elevation, double worldZ)
    {
        double metresPerUnit = CellMetres / WorldMapPresentation.VerticalExaggeration;
        return new((float)(worldX / CellMetres), (float)(OriginY + Math.Max(elevation, GenerationConstants.WaterLevel) / metresPerUnit),
            (float)(worldZ / CellMetres));
    }

    /// <summary>A river is painted on a cell when its channel reaches within half a cell of the centre.</summary>
    private bool RiverCovers(double x, double z, double cellMetres) =>
        map.Rivers.Nearest(x, z) is RiverInfluence river && river.Distance < river.HalfWidth + cellMetres / 2;

    private Material Flat(Color color) =>
        engine.Graphics.CreateMaterial(new MaterialRequest(color, default, 1f, color, Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));

    private static uint Slot(MapBiome biome, int tone = Tones / 2) => FirstBiomeSlot + (uint)((int)biome * Tones + tone);

    private int Tone(double x, double z)
    {
        double noise = MapNoise.Fbm(map.Configuration.Contract.GeographyNoiseSeed ^ ToneSalt, x / ToneWavelength, z / ToneWavelength, 2, 0.5);
        return Math.Clamp((int)Math.Floor((noise * 1.6 + 1) / 2 * Tones), 0, Tones - 1);
    }

    private static Color Shade(Color color, float factor) =>
        new(Math.Min(1, color.R * factor), Math.Min(1, color.G * factor), Math.Min(1, color.B * factor), 1);
}
