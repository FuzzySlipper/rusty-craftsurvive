using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// Prototype faceted map view (#9436, #9437): the generated map voxelized as dual-contoured
/// terrain, sampling the same <see cref="WorldMap"/> walking terrain does. A coarse layer covers
/// the whole map; a finer patch around the party carries close-range detail, and the coarse
/// ground beneath that patch is lowered so the two never fight. A camera rig locked to the
/// party zooms and orbits. Presentation units are one coarse cell (<see cref="CellMetres"/>).
/// </summary>
internal sealed class WorldMapVoxelView : IDisposable
{
    internal const double CellMetres = 32;
    private const int DetailPerCell = 4;
    private const double DetailMetres = CellMetres / DetailPerCell;
    /// <summary>Coarse cells on each side of the party covered by the detail patch.</summary>
    private const int DetailRadiusCells = 32;
    /// <summary>Coarse voxels the coarse ground sinks under the detail patch, hiding it there.</summary>
    private const double PatchSink = 1.5;
    /// <summary>Metres over which regional relief fades out toward the patch edge, so it meets the coarse map flush.</summary>
    private const double EdgeFadeMetres = 160;
    private const int OriginY = -20000;
    private const int ChunksPerUpdate = 24;
    private const double ExposedRock = 0.6;
    // Markers keep a steady apparent size: their scale follows the camera's distance.
    private const float MarkerScalePerDistance = 0.012f;
    private const float PartyScalePerDistance = 0.006f;
    private const float MinimumMarkerScale = 0.04f;
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
    private readonly Vector2 detailMinimum, detailMaximum;
    private double loadMilliseconds;

    internal WorldMapVoxelView(IEngineContext engine, ProductContent content, WorldMap map, Vector3 partyWorldFeet, MapSurfaceStyle style)
    {
        this.engine = engine;
        this.map = map;
        Style = style;
        PartyWorld = partyWorldFeet;
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

            // The detail window, in coarse cells, centred on the party and kept inside the map.
            int half = cells / 2;
            int partyCellX = (int)Math.Floor(partyWorldFeet.X / CellMetres) + half;
            int partyCellZ = (int)Math.Floor(partyWorldFeet.Z / CellMetres) + half;
            int windowX = Math.Clamp(partyCellX - DetailRadiusCells, 0, Math.Max(0, cells - 2 * DetailRadiusCells));
            int windowZ = Math.Clamp(partyCellZ - DetailRadiusCells, 0, Math.Max(0, cells - 2 * DetailRadiusCells));
            int windowCells = Math.Min(2 * DetailRadiusCells, cells);

            double halfMetres = half * CellMetres;
            detailMinimum = new((float)(windowX * CellMetres - halfMetres), (float)(windowZ * CellMetres - halfMetres));
            detailMaximum = detailMinimum + new Vector2((float)(windowCells * CellMetres));
            coarse = Layer(1, CellMetres, -half, -half, cells, cells,
                (x, z) => x >= windowX && z >= windowZ && x < windowX + windowCells && z < windowZ + windowCells ? PatchSink : 0, false, coarseGround);
            detail = Layer(1d / DetailPerCell, DetailMetres, (windowX - half) * DetailPerCell, (windowZ - half) * DetailPerCell,
                windowCells * DetailPerCell, windowCells * DetailPerCell, (_, _) => 0, true, detailGround);
            partyPosition = Surface(partyWorldFeet.X, partyWorldFeet.Z);
            clutter = new MapClutter(engine, map, detailMinimum, detailMaximum, Surface);
            rig = new MapCameraRig(engine, partyPosition, CloseDistance, cells * FramingDistance, cells);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal MapSurfaceStyle Style { get; }

    /// <summary>The party position, in world metres, this view was built around.</summary>
    internal Vector3 PartyWorld { get; }
    internal bool Loaded => coarse.Loaded && detail.Loaded;
    internal string Readout => FormattableString.Invariant(
        $"style={Style.Name};cells={cells};cellMetres={CellMetres};detailMetres={DetailMetres};clutter={clutter.Count};coarseChunks={coarse.LoadedChunks}/{coarse.TotalChunks};detailChunks={detail.LoadedChunks}/{detail.TotalChunks};loaded={Loaded};coarseWorkMs={coarse.WorkMilliseconds:F0};detailWorkMs={detail.WorkMilliseconds:F0};wallMs={loadMilliseconds:F0};")
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

    /// <summary>Move the party token; the camera follows it.</summary>
    internal void MoveParty(Vector2 world)
    {
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
    /// Move the waypoint with held W/A/S/D relative to the camera's heading. Returns true when T asks
    /// for a route to it.
    /// </summary>
    internal bool SteerWaypoint(ReadOnlySpan<ProductInputEvent> events)
    {
        bool plan = false;
        foreach (ProductInputEvent input in events)
        {
            if (input.Kind == InputEventKind.Clear) { waypointForward = waypointBack = waypointLeft = waypointRight = false; continue; }
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

    private AppearanceFact Marker(ulong id, Vector3 ground, float scalePerDistance, Appearance appearance)
    {
        publishedDistance = rig.Distance;
        publishedVersion = factsVersion;
        float scale = Math.Max(MinimumMarkerScale, rig.Distance * scalePerDistance);
        return new(id, false, 0, new(ground + Vector3.UnitY * scale, Quaternion.Identity, Vector3.One * scale), appearance, true, RenderLayer.Scene);
    }

    /// <summary>Admit a bounded batch: the whole map first, then the detail around the party.</summary>
    internal void Advance()
    {
        if (Loaded) return;
        if (startedAt == 0) startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!coarse.Loaded) coarse.Advance(ChunksPerUpdate);
        else detail.Advance(ChunksPerUpdate);
        if (Loaded) loadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
    }

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
    /// One layer: each column samples the map at its centre, with exaggerated relief.
    /// <paramref name="sinkCells"/> lowers a column by that many coarse voxels.
    /// </summary>
    private MapVoxelLayer Layer(double voxelSize, double cellMetres, long originX, long originZ, int width, int depth,
        Func<int, int, double> sinkCells, bool localRelief, TerrainGroundMaterials? ground)
    {
        double metresPerVoxel = cellMetres / WorldMapPresentation.VerticalExaggeration;
        double origin = OriginY / voxelSize;
        double[] surface = new double[width * depth];
        uint[] material = new uint[width * depth];
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            double worldX = (originX + x + 0.5) * cellMetres, worldZ = (originZ + z + 0.5) * cellMetres;
            MapSample sample = map.Sample(worldX, worldZ);
            // The detail patch carries the same regional relief walking terrain adds to the map.
            double top = Top(sample, worldX, worldZ, localRelief);
            int i = z * width + x;
            surface[i] = origin + top / metresPerVoxel - sinkCells(x, z) / voxelSize;
            material[i] = sample.Elevation < GenerationConstants.WaterLevel ? Slot(MapBiome.Sea)
                : RiverCovers(worldX, worldZ, cellMetres) ? RiverSlot
                : sample.Rock >= ExposedRock ? RockSlot
                : Slot(WorldMap.Biome(sample), ground is null ? Tone(worldX, worldZ) : Tones / 2);
        }
        if (ground is null) return new MapVoxelLayer(engine, voxelSize, originX, originZ, width, depth, surface, material, materials);
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
        return new MapVoxelLayer(engine, voxelSize, originX, originZ, width, depth, surface, material, bindings,
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
    private Vector3 Surface(double worldX, double worldZ)
    {
        bool detailed = worldX >= detailMinimum.X && worldZ >= detailMinimum.Y && worldX < detailMaximum.X && worldZ < detailMaximum.Y;
        return Position(worldX, Top(map.Sample(worldX, worldZ), worldX, worldZ, detailed), worldZ);
    }

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
