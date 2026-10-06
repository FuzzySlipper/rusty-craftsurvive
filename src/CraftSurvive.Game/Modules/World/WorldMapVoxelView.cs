using System.Numerics;
using CraftSurvive.Game.Modules.Places;
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
/// A continent (#9552) draws in three tiers: the whole continent at a kilometre, a window of region
/// tiles at 32 m around the party, and the 8 m patch. Each finer tier admits only ground whose region
/// tiles are built, so the map never waits on one, and the tier beneath sinks where a finer one covers.
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
    /// <summary>
    /// Beyond these camera distances, in map units (one coarse cell), each layer is drawn from coarse
    /// meshes (#9563): the detail patch when the camera has pulled back past about 770 m, the whole
    /// map only at the farthest zooms, where its 64 m coarse facets are below a pixel or two.
    /// </summary>
    private const double DetailLayerCoarseBeyond = 24;
    private const double CoarseLayerCoarseBeyond = 160;
    /// <summary>A continent's whole-world tier: one voxel a kilometre.</summary>
    internal const double ContinentCellMetres = 1000;
    private const double ContinentVoxel = ContinentCellMetres / CellMetres;
    private const double ContinentChunkMetres = EdgeLength * ContinentCellMetres;
    private const double ContinentLayerCoarseBeyond = 2000;
    /// <summary>Region chunks on each side of the party's covered by a continent's 32 m window (one chunk is 512 m): about 8 km across.</summary>
    private const int RegionRadiusChunks = 8;
    private const double RegionChunkMetres = EdgeLength * CellMetres;
    /// <summary>Region chunks over which the window's ground eases onto the continent's, so the tiers meet flush.</summary>
    private const int RegionFadeChunks = 2;
    private const double RegionFadeMetres = RegionFadeChunks * RegionChunkMetres;
    /// <summary>A continental river paints the kilometre cells its channel passes within half a diagonal of, so its cells join.</summary>
    private const double ContinentRiverReach = 0.71;
    /// <summary>A river paints a finer tier's cells its channel passes within half a cell of.</summary>
    private const double RiverReach = 0.5;
    /// <summary>Picking marches in steps that grow with the camera distance, so a continent-wide view stays quick.</summary>
    private const float PickStepPerDistance = 0.002f;
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
    private readonly Dictionary<KnownPlaceKind, Appearance> placeMarkers = [];
    private KnownPlace[] places = [];
    private readonly Appearance party;
    private readonly MapVoxelLayer coarse;
    private readonly MapVoxelLayer detail;
    /// <summary>A continent's region tiles and its whole-world tier; null on a regional map.</summary>
    private readonly MapRegions? regions;
    private readonly MapVoxelLayer? continent;
    private readonly HashSet<(long X, long Z)> regionCovered = [];
    private readonly long firstRegionChunk, lastRegionChunk;
    private Vector2 followed;
    private Vector2 regionMinimum, regionMaximum;
    private bool detailPending;
    private readonly MapCameraRig rig;
    private readonly MapClutter clutter;
    private readonly TerrainGroundMaterials? coarseGround, detailGround, continentGround;
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
                if (map.Scale.Continental) continentGround = new TerrainGroundMaterials(engine, content, ContinentCellMetres, textures);
            }
            foreach (KnownPlaceKind kind in Enum.GetValues<KnownPlaceKind>())
                placeMarkers[kind] = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, MapPalette.Place(kind)));
            party = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, PartyColor));
            routeMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, RouteColor));
            waypointMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, WaypointColor));
            waypoint = new(partyWorldFeet.X, partyWorldFeet.Z);

            firstDetailChunk = (long)Math.Floor(-map.Radius / DetailChunkMetres);
            lastDetailChunk = (long)Math.Floor((map.Radius - 1) / DetailChunkMetres);
            firstRegionChunk = (long)Math.Floor(-map.Radius / RegionChunkMetres);
            lastRegionChunk = (long)Math.Floor((map.Radius - 1) / RegionChunkMetres);
            regions = map.Scale.Continental ? MapRegions.For(map) : null;
            Func<double, double, MapSample> fine = regions is null ? map.Sample : RegionGround;
            Func<double, double, RiverInfluence?> fineRivers = regions is null ? map.Rivers.Nearest : regions.RiverNear;
            if (regions is not null)
                continent = Layer(ContinentVoxel, ContinentCellMetres, map.Sample, map.Rivers.Nearest, ContinentRiverReach, (x, z) => UnderRegion(x, z) ? PatchSink * ContinentVoxel : 0,
                    false, continentGround, ContinentLayerCoarseBeyond);
            coarse = Layer(1, CellMetres, fine, fineRivers, RiverReach, (x, z) => InDetail(x, z) ? PatchSink : 0, false, coarseGround, CoarseLayerCoarseBeyond);
            detail = Layer(1d / DetailPerCell, DetailMetres, fine, fineRivers, RiverReach, (_, _) => 0, true, detailGround, DetailLayerCoarseBeyond);
            clutter = new MapClutter(engine, map, fine);
            // The windows come first: the ground beneath samples them to sink under each finer tier.
            Follow(new(partyWorldFeet.X, partyWorldFeet.Z));
            if (continent is not null)
            {
                long firstContinent = (long)Math.Floor(-map.Radius / ContinentChunkMetres), lastContinent = (long)Math.Floor((map.Radius - 1) / ContinentChunkMetres);
                continent.Want(from cz in Range(firstContinent, lastContinent) from cx in Range(firstContinent, lastContinent) select (cx, cz));
            }
            else
            {
                long firstCoarse = GridMath.FloorDivide(-cells / 2, EdgeLength), lastCoarse = GridMath.FloorDivide(cells - cells / 2 - 1, EdgeLength);
                coarse.Want(from cz in Range(firstCoarse, lastCoarse) from cx in Range(firstCoarse, lastCoarse) select (cx, cz));
            }
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

    internal bool Loaded => (continent?.Settled ?? true) && coarse.Settled && detail.Settled && !detailPending;
    internal string Readout => FormattableString.Invariant(
        $"style={Style.Name};tiers={(continent is null ? 2 : 3)};continentChunks={continent?.ResidentChunks ?? 0}+{continent?.PendingChunks ?? 0}pending;regionColumns={regionCovered.Count};cells={cells};cellMetres={CellMetres};detailMetres={DetailMetres};clutter={clutter.Count};coarseChunks={coarse.ResidentChunks}+{coarse.PendingChunks}pending;detailChunks={detail.ResidentChunks}+{detail.PendingChunks}pending;windowShifts={windowShifts};window={detailMinimum.X:F0},{detailMinimum.Y:F0};loaded={Loaded};coarseWorkMs={coarse.WorkMilliseconds:F0};detailWorkMs={detail.WorkMilliseconds:F0};wallMs={loadMilliseconds:F0};lastPick={PickReadout};lod={LevelOfDetailReadout()};")
        + rig.Readout;

    private string LevelOfDetailReadout()
    {
        (ulong coarseChunks, ulong coarseCoarse) = coarse.LevelOfDetail();
        (ulong detailChunks, ulong detailCoarse) = detail.LevelOfDetail();
        (ulong continentChunks, ulong continentCoarse) = continent?.LevelOfDetail() ?? (0, 0);
        return FormattableString.Invariant($"continent {continentCoarse}/{continentChunks} map {coarseCoarse}/{coarseChunks} detail {detailCoarse}/{detailChunks} coarse");
    }

    internal void Activate() => rig.Activate();

    internal bool Steer(ReadOnlySpan<ProductInputEvent> events) => rig.Steer(events);

    internal void SetCamera(float distance, float yawDegrees, float pitchDegrees) => rig.Set(distance, yawDegrees, pitchDegrees);

    /// <summary>Site markers and the party token, placed on the faceted relief.</summary>
    internal AppearanceFact[] Facts => [
        .. clutter.Facts(rig.Distance <= ClutterDistance),
        Marker(ProductIds.WorldMapPartyObject, partyPosition, PartyScalePerDistance, party),
        Marker(ProductIds.WorldMapWaypointObject, Surface(waypoint.X, waypoint.Y), PartyScalePerDistance, waypointMarker),
        .. routeMarkers.Select((point, i) => Marker(ProductIds.WorldMapRouteBase + (ulong)i, point, RouteMarkerScalePerDistance, routeMarker)),
        .. places.Take(ProductIds.WorldMapPlaceLimit).Select((place, i) => Marker(ProductIds.WorldMapPlaceBase + (ulong)i,
            Surface(place.Position.X, place.Position.Y), place.Kind == KnownPlaceKind.Home ? PartyScalePerDistance : MarkerScalePerDistance, placeMarkers[place.Kind]))];

    /// <summary>Whether a sled marker is drawn (the sled left behind), for the travel readout.</summary>
    internal bool ShowsSled => places.Any(place => place.Kind == KnownPlaceKind.Sled);

    /// <summary>Show the known places (#9471); the list replaces the previous one.</summary>
    internal void ShowPlaces(IReadOnlyList<KnownPlace> known)
    {
        if (known.SequenceEqual(places)) return;
        places = [.. known];
        factsVersion++;
    }

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
            // A long route spreads its markers out rather than stopping short of its end.
            double routeMetres = 0;
            for (int i = 1; i < points.Count; i++) routeMetres += Vector2.Distance(points[i - 1], points[i]);
            double spacing = Math.Max(RouteMarkerSpacingMetres, routeMetres / ProductIds.WorldMapRouteLimit);
            double carried = 0;
            for (int i = 1; i < points.Count && markers.Count < ProductIds.WorldMapRouteLimit; i++)
            {
                Vector2 a = points[i - 1], b = points[i];
                double length = Vector2.Distance(a, b);
                for (double along = spacing - carried; along < length; along += spacing)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)(along / length));
                    markers.Add(Surface(p.X, p.Y));
                }
                carried = (carried + length) % spacing;
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
        float previous = 0, step = Math.Max(PickStepUnits, rig.Distance * PickStepPerDistance);
        for (float t = step; t <= reach; t += step)
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
        if (regions is not null)
        {
            // Region tiles finish off-thread: admit what they now cover, and the patch once it can be drawn.
            if (detailPending) Follow(followed);
            else CoverRegion();
            if (continent!.PendingChunks > 0 || !continent.Settled)
            {
                continent.Advance(budget);
                if (!continent.Settled && loadMilliseconds == 0) return;
                budget /= 2;
            }
        }
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
        followed = world;
        CoverRegion();
        long span = 2 * DetailRadiusChunks;
        long maximumStart = Math.Max(firstDetailChunk, lastDetailChunk - span + 1);
        long x0 = Math.Clamp((long)Math.Floor(world.X / DetailChunkMetres) - DetailRadiusChunks, firstDetailChunk, maximumStart);
        long z0 = Math.Clamp((long)Math.Floor(world.Y / DetailChunkMetres) - DetailRadiusChunks, firstDetailChunk, maximumStart);
        if (x0 == windowChunkX && z0 == windowChunkZ) { detailPending = false; return; }
        if (regions is not null)
        {
            // The patch moves only once every tile it covers is built; until then the last one stays.
            double minX = x0 * DetailChunkMetres, minZ = z0 * DetailChunkMetres;
            double maxX = (x0 + span) * DetailChunkMetres, maxZ = (z0 + span) * DetailChunkMetres;
            if (!regions.Ready(minX, minZ, maxX, maxZ))
            {
                regions.Prefetch(minX, minZ, maxX, maxZ);
                detailPending = true;
                return;
            }
        }
        detailPending = false;
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

    /// <summary>
    /// A continent's 32 m window around the party: every region chunk column whose tiles are built is
    /// admitted, the rest are queued, and the continent tier is re-sunk where the cover changed.
    /// </summary>
    private void CoverRegion()
    {
        if (regions is null) return;
        long span = 2 * RegionRadiusChunks;
        long maximumStart = Math.Max(firstRegionChunk, lastRegionChunk - span + 1);
        long x0 = Math.Clamp((long)Math.Floor(followed.X / RegionChunkMetres) - RegionRadiusChunks, firstRegionChunk, maximumStart);
        long z0 = Math.Clamp((long)Math.Floor(followed.Y / RegionChunkMetres) - RegionRadiusChunks, firstRegionChunk, maximumStart);
        long x1 = Math.Min(lastRegionChunk, x0 + span - 1), z1 = Math.Min(lastRegionChunk, z0 + span - 1);
        Vector2 minimum = new((float)(x0 * RegionChunkMetres), (float)(z0 * RegionChunkMetres)), maximum = new((float)((x1 + 1) * RegionChunkMetres), (float)((z1 + 1) * RegionChunkMetres));
        if (minimum != regionMinimum || maximum != regionMaximum)
        {
            // The window moved: the bands beside both its old and new edges carry a changed fade.
            (Vector2 oldMinimum, Vector2 oldMaximum) = (regionMinimum, regionMaximum);
            (regionMinimum, regionMaximum) = (minimum, maximum);
            coarse.Invalidate(FadeBand(oldMinimum, oldMaximum).Concat(FadeBand(minimum, maximum)));
        }
        regions.Prefetch(x0 * RegionChunkMetres, z0 * RegionChunkMetres, (x1 + 1) * RegionChunkMetres, (z1 + 1) * RegionChunkMetres);
        HashSet<(long X, long Z)> covered = [];
        for (long cz = z0; cz <= z1; cz++)
        for (long cx = x0; cx <= x1; cx++)
            if (regions.Ready(cx * RegionChunkMetres, cz * RegionChunkMetres, (cx + 1) * RegionChunkMetres, (cz + 1) * RegionChunkMetres))
                covered.Add((cx, cz));
        if (covered.SetEquals(regionCovered)) return;
        HashSet<(long X, long Z)> changed = [.. covered];
        changed.SymmetricExceptWith(regionCovered);
        regionCovered.Clear();
        regionCovered.UnionWith(covered);
        coarse.Want(covered);
        continent!.Invalidate(changed.Select(column => (
            (long)Math.Floor(column.X * RegionChunkMetres / ContinentChunkMetres), (long)Math.Floor(column.Z * RegionChunkMetres / ContinentChunkMetres))));
        factsVersion++;
    }

    /// <summary>Region chunk columns within the fade band inside a window's edges.</summary>
    private static IEnumerable<(long X, long Z)> FadeBand(Vector2 minimum, Vector2 maximum)
    {
        long x0 = (long)Math.Floor(minimum.X / RegionChunkMetres), x1 = (long)Math.Floor(maximum.X / RegionChunkMetres) - 1;
        long z0 = (long)Math.Floor(minimum.Y / RegionChunkMetres), z1 = (long)Math.Floor(maximum.Y / RegionChunkMetres) - 1;
        return from cz in Range(z0, z1) from cx in Range(x0, x1)
               where cx < x0 + RegionFadeChunks || cx > x1 - RegionFadeChunks || cz < z0 + RegionFadeChunks || cz > z1 - RegionFadeChunks
               select (cx, cz);
    }

    /// <summary>
    /// A continent's region ground as the map draws it: the region tiles' geography, easing onto the
    /// continent's own surface across the window's outer band, so the tiers meet without a wall.
    /// </summary>
    private MapSample RegionGround(double worldX, double worldZ)
    {
        MapSample region = regions!.Sample(worldX, worldZ);
        double inset = Math.Min(Math.Min(worldX - regionMinimum.X, regionMaximum.X - worldX), Math.Min(worldZ - regionMinimum.Y, regionMaximum.Y - worldZ));
        double fade = WorldMap.Smooth(Math.Clamp(inset / RegionFadeMetres, 0, 1));
        if (fade >= 1) return region;
        double broad = map.Sample(worldX, worldZ).Elevation;
        return region with { Elevation = broad + fade * (region.Elevation - broad) };
    }

    /// <summary>
    /// Where the continent tier sinks: beneath region ground past the window's fade band. Its ground
    /// slopes down to the sunk depth between kilometre cells, and inside the band that slope stays
    /// under region ground, which there eases onto the continent's own surface.
    /// </summary>
    private bool UnderRegion(double worldX, double worldZ) =>
        InRegion(worldX, worldZ) && Math.Min(Math.Min(worldX - regionMinimum.X, regionMaximum.X - worldX), Math.Min(worldZ - regionMinimum.Y, regionMaximum.Y - worldZ)) >= RegionFadeMetres;

    private bool InRegion(double worldX, double worldZ) =>
        regions is null || regionCovered.Contains(((long)Math.Floor(worldX / RegionChunkMetres), (long)Math.Floor(worldZ / RegionChunkMetres)));

    private bool InDetail(double worldX, double worldZ) =>
        worldX >= detailMinimum.X && worldZ >= detailMinimum.Y && worldX < detailMaximum.X && worldZ < detailMaximum.Y;

    public void Dispose()
    {
        clutter?.Dispose();
        detail?.Dispose();
        coarse?.Dispose();
        continent?.Dispose();
        continentGround?.Dispose();
        detailGround?.Dispose();
        coarseGround?.Dispose();
        rig?.Dispose();
        party?.Dispose();
        routeMarker?.Dispose();
        waypointMarker?.Dispose();
        foreach (Appearance placeMarker in placeMarkers.Values) placeMarker.Dispose();
        foreach (Material material in materials.Values) material.Dispose();
    }

    /// <summary>
    /// One streamed layer: each voxel column samples the map at its centre, with exaggerated relief,
    /// lowered by <paramref name="sink"/> coarse voxels at a world point. Columns beyond the map are empty.
    /// </summary>
    private MapVoxelLayer Layer(double voxelSize, double cellMetres, Func<double, double, MapSample> geography, Func<double, double, RiverInfluence?> rivers, double riverReach,
        Func<double, double, double> sink, bool localRelief, TerrainGroundMaterials? ground, double coarseBeyond)
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
                MapSample sample = geography(worldX, worldZ);
                // The detail patch carries the same regional relief walking terrain adds to the map.
                surface[i] = origin + Top(sample, worldX, worldZ, localRelief) / metresPerVoxel - sink(worldX, worldZ) / voxelSize;
                material[i] = sample.Elevation < GenerationConstants.WaterLevel ? Slot(MapBiome.Sea)
                    : rivers(worldX, worldZ) is RiverInfluence river && river.Distance < river.HalfWidth + cellMetres * riverReach ? RiverSlot
                    : sample.Rock >= ExposedRock ? RockSlot
                    : Slot(WorldMap.Biome(sample), ground is null ? Tone(worldX, worldZ) : Tones / 2);
            }
        }
        if (ground is null) return new MapVoxelLayer(engine, voxelSize, Sample, materials, coarseBeyond: coarseBeyond);
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
            ([.. layered.Select(entry => entry.Slot)], [.. layered.Select(entry => entry.Layer)], ground.Settings.TransitionCells), coarseBeyond);
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
    private Vector3 Surface(double worldX, double worldZ)
    {
        bool inDetail = InDetail(worldX, worldZ);
        // On a continent, region ground is read only where its tier is drawn, so it is always built.
        MapSample sample = regions is not null && (inDetail || InRegion(worldX, worldZ)) ? RegionGround(worldX, worldZ) : map.Sample(worldX, worldZ);
        return Position(worldX, Top(sample, worldX, worldZ, inDetail), worldZ);
    }

    private Vector3 Position(double worldX, double elevation, double worldZ)
    {
        double metresPerUnit = CellMetres / WorldMapPresentation.VerticalExaggeration;
        return new((float)(worldX / CellMetres), (float)(OriginY + Math.Max(elevation, GenerationConstants.WaterLevel) / metresPerUnit),
            (float)(worldZ / CellMetres));
    }

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
