using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Places;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The world map (#9436, #9437, #9946): the generated map voxelized as dual-contoured terrain, sampling the same <see cref="WorldMap"/> walking terrain does. A coarse layer covers
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
    /// <summary>Relief is drawn taller than true scale so a 300 m range reads on a map kilometres across.</summary>
    internal const double VerticalExaggeration = 3;
    private const int DetailPerCell = 4;
    private const double DetailMetres = CellMetres / DetailPerCell;
    /// <summary>Detail chunks on each side of the party's chunk covered by the patch (one chunk is 128 m).</summary>
    private const int DetailRadiusChunks = 8;
    private const int EdgeLength = MapVoxelLayer.EdgeLength;
    private const double DetailChunkMetres = EdgeLength * DetailMetres;
    /// <summary>Coarse voxels a tier sinks under the finer tier that covers it (the continent under the region window).</summary>
    private const double PatchSink = 1.5;
    /// <summary>
    /// Under the detail patch each coarse column sinks below the lowest detail ground within a cell of it,
    /// sampled at this many points a cell, by this margin (coarse voxels; #9946). A coarse surface spans a
    /// whole cell between its columns and does not follow a valley the patch draws between them; a fixed
    /// sink left it standing through the patch there, and nearly level with it elsewhere, where the two fought.
    /// </summary>
    private const int SinkSamplesPerCell = 2;
    private const double SinkMargin = 0.35;
    /// <summary>
    /// How the patch meets the coarse ground (#9952). Toward its edge the patch draws the coarse ground's
    /// own surface - its column tops blended as the coarse mesh spans them - this far (metres) under it,
    /// with no regional relief; the coarse ground is not sunk there, so it covers the patch's edge and the
    /// lip where the patch's surface ends. Further in, the relief eases in and the coarse ground sinks
    /// under it in proportion, so the two surfaces trade places where they are all but the same.
    /// </summary>
    private const double EdgeUnderMetres = 1.5;
    /// <summary>
    /// Where the relief eases in, measured from the patch's edge with its corners rounded: none within the
    /// margin, all of it past the band, the boundary moved in and out by low noise of this amplitude and
    /// wavelength so the detailed ground does not outline a rectangle. The whole reach stays within
    /// <see cref="EdgeRingChunks"/> detail chunks of the edge, which a shift re-streams.
    /// </summary>
    private const double EdgeMarginMetres = 2 * CellMetres, EdgeBandMetres = 112, EdgeNoiseMetres = 40, EdgeNoiseWavelength = 300, EdgeCornerMetres = 224;
    /// <summary>
    /// Columns this many cells from a window's edge never sink under it. A mesh vertex is about the mean of
    /// the four columns around it, so a sunk column pulls the surface down a cell and a half about it; any
    /// nearer the edge, and the coarser ground would dip there and show the finer one's edge (#9952 review).
    /// </summary>
    private const double UnsunkCells = 2;
    private const int EdgeRingChunks = 2;
    private const ulong EdgeNoiseSalt = 0x3C6EF372FE94F82BUL;
    /// <summary>A coarse column sinks only once the relief within a cell of it reaches this share, easing in.</summary>
    private const double SinkFromFade = 0.1;
    private const int OriginY = -20000;
    private const int ChunksPerUpdate = 24;
    /// <summary>
    /// Beyond these camera distances, in map units (one coarse cell), each layer would be drawn from coarse
    /// meshes (#9563); zero draws it at full resolution always. Every tier does (#9952 review): a finer
    /// tier's edge is matched to the surface the coarser tier's full mesh draws, and a coarse mesh, on a
    /// lattice twice as coarse, stands off it - the edges showed as lines at the zooms that used one. It
    /// costs about 3 ms a frame on the map screen at the farthest zoom.
    /// </summary>
    private const double DetailLayerCoarseBeyond = 0;
    private const double CoarseLayerCoarseBeyond = 0;
    /// <summary>A continent's whole-world tier: one voxel a kilometre.</summary>
    internal const double ContinentCellMetres = 1000;
    private const double ContinentVoxel = ContinentCellMetres / CellMetres;
    private const double ContinentChunkMetres = EdgeLength * ContinentCellMetres;
    private const double ContinentLayerCoarseBeyond = CoarseLayerCoarseBeyond;
    /// <summary>Region chunks on each side of the party's covered by a continent's 32 m window (one chunk is 512 m): about 8 km across.</summary>
    private const int RegionRadiusChunks = 8;
    private const double RegionChunkMetres = EdgeLength * CellMetres;
    /// <summary>Region chunks over which the window's ground eases onto the continent's, so the tiers meet flush.</summary>
    /// <summary>
    /// How the region window meets the continent (#9952 review), as the patch meets the coarse ground: by
    /// its edge the window draws the continent's own surface this far under it, and its own relief eases in
    /// past a margin across a band, the boundary rounded at the corners and moved by noise. The margin is
    /// over a continent cell, so the continent columns nearest the edge, which never sink, cover it.
    /// </summary>
    private const double RegionUnderMetres = 12;
    private const double RegionMarginMetres = 1536, RegionBandMetres = 1024, RegionNoiseMetres = 192, RegionNoiseWavelength = 2400, RegionCornerMetres = 1536;
    /// <summary>Region chunks within which the window's fade reaches from its edge, re-sampled when it moves.</summary>
    private const int RegionFadeChunks = 6;

    /// <summary>A river paints a finer tier's cells its channel passes within half a cell of.</summary>
    private const double RiverReach = 0.5;
    /// <summary>
    /// The coarser tiers draw their rivers as ribbons (#9948): the region or regional map's at 32 m,
    /// and a continent's own beyond the region window. The detail patch still paints its own 8 m cells.
    /// </summary>
    private const int RegionRiverTier = 0, ContinentRiverTier = 1;
    private static readonly RiverRibbonShape RegionRivers = new(SpacingMetres: CellMetres / 2, MinimumHalfWidthMetres: CellMetres * 0.3);
    private static readonly RiverRibbonShape ContinentRivers = new(SpacingMetres: ContinentCellMetres, MinimumHalfWidthMetres: ContinentCellMetres * 0.3);
    /// <summary>How far a ribbon floats over its tier's interpolated ground (map units), so the ground's mesh between columns does not cover it.</summary>
    private const float RegionRiverLift = 0.15f;
    /// <summary>Where the patch's relief reaches this share, its own painted rivers take over from the coarse tier's ribbons.</summary>
    private const double RibbonGivesWay = 0.5;
    private const float ContinentRiverLift = (float)(0.1 * ContinentVoxel);
    /// <summary>How far the sea and lake surfaces float over the ground the tiers flatten to their levels (map units).</summary>
    private const float WaterLift = 0.2f;
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
    /// <summary>Ground under water draws as sand, so the strip a shore shows above the water reads as a beach.</summary>
    private const MapBiome ShoreBiome = MapBiome.Desert;
    private const uint RockSlot = 2;
    private const uint FirstBiomeSlot = 3;
    private static readonly Color PartyColor = new(0.95f, 0.95f, 0.85f, 1);

    private readonly IEngineContext engine;
    private readonly WorldMap map;
    private readonly int cells;
    private readonly Dictionary<uint, Material> materials = [];
    private readonly Dictionary<KnownPlaceKind, Appearance> placeMarkers = [];
    private readonly Appearance clusterMarker;
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
    /// <summary>A patch step left the region window's move to the next update (#10347).</summary>
    private bool coverWaits;
    private readonly MapCameraRig rig;
    private readonly MapClutter clutter;
    private readonly TerrainGroundMaterials? coarseGround, detailGround, continentGround;
    /// <summary>Clutter is shown only from this camera distance inward, where it reads at all.</summary>
    private const float ClutterDistance = 160;
    private Vector3 partyPosition;
    private Vector2 waypoint;
    private Vector3[] routeMarkers = [];
    private readonly Appearance routeMarker, waypointMarker;
    /// <summary>The frontier's posts (#9817): where the expedition is turned back, drawn where it crosses land.</summary>
    private readonly Vector3[] frontierPosts = [];
    private readonly Appearance? frontierMarker;
    private const double FrontierPostSpacingMetres = 1500;
    private const float FrontierPostScalePerDistance = 0.006f;
    private readonly MapWeather weather;
    private readonly MapRiverRibbons riverRibbons;
    private readonly MapWaterSurfaces waterSurfaces;
    /// <summary>The ground the coarse and detail tiers sample: a continent's region ground, or the map's.</summary>
    private readonly Func<double, double, MapSample> fineGround;
    /// <summary>A continent's network reaches shaped for the region window, by id, so a window change shapes only new ones.</summary>
    private readonly Dictionary<int, RiverPoint[]> regionReaches = [];
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
    /// <summary>A study sweep's end and step (#10345): the party token moves so far toward it each update, whatever the travel rules.</summary>
    private Vector2? sweepTo;
    private float sweepStep;

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
                placeMarkers[kind] = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, MapPalette.Place(kind).Linear()));
            clusterMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, MapPalette.PlaceCluster.Linear()));
            party = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, PartyColor.Linear()));
            routeMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, RouteColor.Linear()));
            waypointMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, WaypointColor.Linear()));
            weather = new MapWeather(engine, Surface, CellMetres, CellMetres / VerticalExaggeration);
            waypoint = new(partyWorldFeet.X, partyWorldFeet.Z);

            firstDetailChunk = (long)Math.Floor(-map.Radius / DetailChunkMetres);
            lastDetailChunk = (long)Math.Floor((map.Radius - 1) / DetailChunkMetres);
            firstRegionChunk = (long)Math.Floor(-map.Radius / RegionChunkMetres);
            lastRegionChunk = (long)Math.Floor((map.Radius - 1) / RegionChunkMetres);
            regions = map.Scale.Continental ? MapRegions.For(map) : null;
            Func<double, double, MapSample> fine = fineGround = regions is null ? map.Sample : RegionGround;
            Func<double, double, RiverInfluence?> fineRivers = regions is null ? map.Rivers.Nearest : regions.RiverNear;
            // The coarser tiers leave rivers to the ribbons; lakes and the sea they still paint.
            if (regions is not null)
                continent = Layer(ContinentVoxel, ContinentCellMetres, map.Sample, NoRiver, 0, ContinentSink,
                    false, continentGround, ContinentLayerCoarseBeyond);
            // Each finer tier chooses its environments toward its edge as the tier beside it blends them.
            static int Transition(TerrainGroundMaterials? beside) => (int)(beside?.Settings.TransitionCells ?? 0);
            coarse = Layer(1, CellMetres, fine, NoRiver, 0, (x, z) => InDetail(x, z) ? UnderDetail(fine, x, z) : 0, false, coarseGround, CoarseLayerCoarseBeyond,
                regions is null ? null : (RegionFade, ContinentCellMetres, Transition(continentGround), map.Sample, []));
            detail = Layer(1d / DetailPerCell, DetailMetres, fine, fineRivers, RiverReach, (_, _) => 0, true, detailGround, DetailLayerCoarseBeyond,
                (EdgeFade, CellMetres, Transition(coarseGround), fine, detailDitherSlots));
            clutter = new MapClutter(engine, content, map, fine);
            riverRibbons = new MapRiverRibbons(engine, materials[RiverSlot]);
            waterSurfaces = new MapWaterSurfaces(engine, map, materials[Slot(MapBiome.Sea)], kind => Flat(MapPalette.LakeColour(kind)),
                (x, z) => map.Sample(x, z).Elevation, (x, z, level) => Position(x, level, z) + (Vector3.UnitY * WaterLift));
            // The windows come first: the ground beneath samples them to sink under each finer tier.
            Follow(new(partyWorldFeet.X, partyWorldFeet.Z));
            if (regions is null) RebuildRegionRivers(null);
            else RebuildContinentRivers(null);
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
            if (FrontierBound.For(map) is FrontierBound bound)
            {
                frontierMarker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, MapPalette.Frontier.Linear()));
                frontierPosts = [.. bound.FrontierOnLand(FrontierPostSpacingMetres).Take(ProductIds.WorldMapFrontierLimit).Select(post => Surface(post.X, post.Y))];
            }
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
        $"style={Style.Name};tiers={(continent is null ? 2 : 3)};continentChunks={continent?.ResidentChunks ?? 0}+{continent?.PendingChunks ?? 0}pending;regionColumns={regionCovered.Count};cells={cells};cellMetres={CellMetres};detailMetres={DetailMetres};clutter={clutter.Count};coarseChunks={coarse.ResidentChunks}+{coarse.PendingChunks}pending;detailChunks={detail.ResidentChunks}+{detail.PendingChunks}pending;windowShifts={windowShifts};window={detailMinimum.X:F0},{detailMinimum.Y:F0};loaded={Loaded};coarseWorkMs={coarse.WorkMilliseconds:F0};detailWorkMs={detail.WorkMilliseconds:F0};wallMs={loadMilliseconds:F0};lastPick={PickReadout};lod={LevelOfDetailReadout()};party={partyPosition.X:F1},{partyPosition.Y:F1},{partyPosition.Z:F1};{weather.Readout()}")
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

    /// <summary>Site markers and the party token, placed on the relief.</summary>
    internal AppearanceFact[] Facts => [
        .. clutter.Facts(rig.Distance <= ClutterDistance),
        Marker(ProductIds.WorldMapPartyObject, partyPosition, PartyScalePerDistance, party),
        Marker(ProductIds.WorldMapWaypointObject, Surface(waypoint.X, waypoint.Y), PartyScalePerDistance, waypointMarker),
        .. routeMarkers.Select((point, i) => Marker(ProductIds.WorldMapRouteBase + (ulong)i, point, RouteMarkerScalePerDistance, routeMarker)),
        .. frontierMarker is Appearance post
            ? frontierPosts.Select((point, i) => Marker(ProductIds.WorldMapFrontierBase + (ulong)i, point, FrontierPostScalePerDistance, post))
            : [],
        .. weather.Facts(rig.Distance),
        .. riverRibbons.Facts,
        .. waterSurfaces.Facts,
        .. PlaceClusters.Group(places, (float)(rig.Distance * CellMetres * ClusterPerDistance)).Take(ProductIds.WorldMapPlaceLimit).Select((cluster, i) =>
            Marker(ProductIds.WorldMapPlaceBase + (ulong)i, Surface(cluster.Position.X, cluster.Position.Y),
                cluster.Single ? (cluster.First.Kind == KnownPlaceKind.Home ? PartyScalePerDistance : MarkerScalePerDistance)
                    : MarkerScalePerDistance * (1 + ClusterGrowth * MathF.Log2(cluster.Count)),
                cluster.Single ? placeMarkers[cluster.First.Kind] : clusterMarker))];

    /// <summary>
    /// Places within this fraction of the camera distance of each other draw as one cluster marker,
    /// which grows with how many it holds; zooming in separates them.
    /// </summary>
    private const double ClusterPerDistance = 0.05;
    private const float ClusterGrowth = 0.5f;

    /// <summary>Whether a sled marker is drawn (the sled left behind), for the travel readout.</summary>
    internal bool ShowsSled => places.Any(place => place.Kind == KnownPlaceKind.Sled);

    /// <summary>Show the known places (#9471); the list replaces the previous one.</summary>
    internal void ShowPlaces(IReadOnlyList<KnownPlace> known)
    {
        if (known.SequenceEqual(places)) return;
        places = [.. known];
        factsVersion++;
    }

    /// <summary>Show the weather fronts near the party (#9739); the list replaces the previous one.</summary>
    internal void ShowWeather(IReadOnlyList<WeatherMarker> fronts)
    {
        weather.Show(fronts);
        factsVersion++;
    }

    /// <summary>How many fronts the map draws, for the readout.</summary>
    internal int WeatherShown => weather.Shown;

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
        Profile.Mark("move.surface");
        rig.FocusOn(partyPosition);
        factsVersion++;
    }

    /// <summary>
    /// A repeatable stream for the deferral study (#10345): put the party at a point, forget the layers' timings, then move
    /// it toward another so many metres an update, so two runs stream the same columns in the same updates.
    /// </summary>
    internal void Sweep(Vector2 from, Vector2 to, float metresPerUpdate)
    {
        MoveParty(from);
        foreach (MapVoxelLayer layer in Layers) layer.ResetTimings();
        Profile.Reset();
        regionMoves = regionPatchResamples = regionCoarseResamples = 0;
        sweepTo = to;
        sweepStep = metresPerUpdate;
    }

    internal bool Sweeping => sweepTo is not null;

    /// <summary>
    /// Whether a window shift resamples only what it changed (#10346): the detail chunks whose edge fade moved, and the
    /// coarse sink from kept ground ranges. Off re-streams both windows' edge rings and samples every sink afresh, as
    /// before, for the A/B. A sweep's shift took 0.8-1.2 s that way, nearly all of it this resampling. Exact also keeps the
    /// patch's relief tops (<see cref="PatchKept"/>) and lets a window's fade skip its noise where it cannot reach.
    /// </summary>
    internal static bool ExactShift { get; set; } = true;

    /// <summary>
    /// How a continent's region window moving its 512 m step resamples (#10347). Exact (the default): region ground eases on
    /// the window's fade alone, so only what reads it where the fade changed is dropped or resampled (<see cref="MoveRegion"/>),
    /// and the move waits for the update after the patch's step it always falls with. False resamples both windows' whole fade
    /// bands and drops all the ground the patch and the coarse sink keep, in the patch's step's update, as before, for the A/B.
    /// A continent sweep's slowest update fell from 0.76-0.80 s that way to 0.44-0.45 s.
    /// </summary>
    internal static bool ExactRegion { get; set; } = true;

    /// <summary>Where the open map's updates spend their time (#10346).</summary>
    internal MapUpdateProfile Profile { get; } = new();

    private IEnumerable<MapVoxelLayer> Layers => continent is null ? [coarse, detail] : [continent, coarse, detail];

    /// <summary>Each layer's stream cost since the last sweep began.</summary>
    internal string StreamReadout => FormattableString.Invariant(
        $"sweeping={Sweeping} deferred={MapVoxelLayer.Deferred} loaded={Loaded} windowShifts={windowShifts} regionMoves={regionMoves} regionCoarse={regionCoarseResamples} regionPatch={regionPatchResamples} party={followed.X:F0},{followed.Y:F0} | ")
        + string.Join(" | ", (continent is null ? new[] { ("coarse", coarse), ("detail", detail) } : [("continent", continent), ("coarse", coarse), ("detail", detail)])
            .Select(entry => entry.Item2.TimingReadout(entry.Item1)))
        + " | " + Profile.Readout;

    /// <summary>Each layer's sampled ground as a digest (#10346): a window reached by a sweep should hold what a jump to it builds afresh.</summary>
    internal string DigestReadout => FormattableString.Invariant(
        $"window={detailMinimum.X:F0},{detailMinimum.Y:F0} continent={continent?.Digest() ?? 0:x16} coarse={coarse.Digest():x16} detail={detail.Digest():x16}");

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

    /// <summary>Release held waypoint/orbit controls when another mode takes the view.</summary>
    internal void ClearInput()
    {
        waypointForward = waypointBack = waypointLeft = waypointRight = false;
        rig.ClearInput();
    }

    /// <summary>Move the waypoint with held keys or a free-cursor pick; true requests a route.</summary>
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
        if (sweepTo is Vector2 to)
        {
            Vector2 offset = to - followed;
            bool last = offset.Length() <= sweepStep;
            MoveParty(last ? to : followed + (Vector2.Normalize(offset) * sweepStep));
            if (last) sweepTo = null;
            Profile.Mark("sweep.move");
        }
        bool wasLoaded = Loaded;
        if (startedAt == 0) startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int budget = ChunksPerUpdate;
        if (regions is not null)
        {
            // Region tiles finish off-thread: admit what they now cover, and the patch once it can be drawn.
            if (detailPending) Follow(followed);
            // A patch step this update left the region's move to the next (#10347).
            else if (coverWaits) coverWaits = false;
            else CoverRegion();
            Profile.Mark("advance.cover");
            if (continent!.PendingChunks > 0 || !continent.Settled)
            {
                continent.Advance(budget);
                Profile.Mark("advance.continent");
                if (!continent.Settled && loadMilliseconds == 0) return;
                budget /= 2;
            }
        }
        if (coarse.PendingChunks > 0 || !coarse.Settled)
        {
            coarse.Advance(budget);
            Profile.Mark("advance.coarse");
            // Until the whole map is up, it has the budget; afterwards both share it.
            if (!coarse.Settled && loadMilliseconds == 0) return;
            budget /= 2;
        }
        detail.Advance(budget);
        Profile.Mark("advance.detail");
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
        long span = 2 * DetailRadiusChunks;
        long maximumStart = Math.Max(firstDetailChunk, lastDetailChunk - span + 1);
        long x0 = Math.Clamp((long)Math.Floor(world.X / DetailChunkMetres) - DetailRadiusChunks, firstDetailChunk, maximumStart);
        long z0 = Math.Clamp((long)Math.Floor(world.Y / DetailChunkMetres) - DetailRadiusChunks, firstDetailChunk, maximumStart);
        // A region chunk holds four detail chunks across, so the region window's step always falls with a step of the patch's.
        // There the region's move waits for the next update (#10347), which resamples what it changed, patch columns included;
        // the windows come first otherwise (the first, or a jump), as the ground beneath samples them.
        bool step = ExactRegion && regions is not null && windowChunkX != long.MinValue && (x0 != windowChunkX || z0 != windowChunkZ)
            && Math.Abs(x0 - windowChunkX) <= 1 && Math.Abs(z0 - windowChunkZ) <= 1;
        if (step) coverWaits = true;
        else CoverRegion();
        Profile.Mark("follow.cover");
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
                if (step) { coverWaits = false; CoverRegion(); }
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

        foreach ((long X, long Z) gone in patchKept.Keys.Where(key => key.X < x0 || key.X > x1 || key.Z < z0 || key.Z > z1).ToArray()) patchKept.Remove(gone);
        detail.Want(from cz in Range(z0, z1) from cx in Range(x0, x1) select (cx, cz));
        Profile.Mark("follow.want");
        if (!first)
        {
            windowShifts++;
            // Columns in both windows whose edge fade has changed; those just admitted were sampled with the new one.
            long oldX1 = Math.Min(lastDetailChunk, oldX0 + span - 1), oldZ1 = Math.Min(lastDetailChunk, oldZ0 + span - 1);
            detail.Invalidate(ExactShift ? FadeChanged(oldX0, oldZ0, oldX1, oldZ1, oldMinimum, oldMaximum, x0, z0, x1, z1)
                : Ring(x0, z0, x1, z1, EdgeRingChunks).Concat(Ring(oldX0, oldZ0, oldX1, oldZ1, EdgeRingChunks)));
            Profile.Mark("follow.detailInvalidate");
            // Cells whose cover changed, and the edge rings of both windows, whose columns stand over the patch's lip.
            coarse.Invalidate(CoarseChanged(oldMinimum, oldMaximum).Concat(CoarseEdgeRing(oldMinimum, oldMaximum)).Concat(CoarseEdgeRing(detailMinimum, detailMaximum)).Distinct());
            Profile.Mark("follow.coarseInvalidate");
        }
        clutter.Scatter(detailMinimum, detailMaximum, Surface, EdgeFade);
        Profile.Mark("follow.scatter");
        // The coarse tier's ribbons give way to the patch's own rivers where its relief is drawn.
        if (!first) RebuildRegionRivers(RiverTilesOver(oldMinimum, oldMaximum).Concat(RiverTilesOver(detailMinimum, detailMaximum)).ToHashSet());
        Profile.Mark("follow.rivers");
        factsVersion++;
    }

    /// <summary>
    /// Detail chunk columns in both the old and the new window whose edge fade differs between them (#10346). The fade
    /// at a point reads its inset from each axis's edges only up to <see cref="EdgeRingChunks"/> chunks, past which it
    /// is whole, so a column changes only along an axis whose edges moved, and only where it lies within that reach
    /// of them in either window. A shift along one axis leaves the strips along the other as they were.
    /// </summary>
    private IEnumerable<(long X, long Z)> FadeChanged(long oldX0, long oldZ0, long oldX1, long oldZ1, Vector2 oldMinimum, Vector2 oldMaximum,
        long x0, long z0, long x1, long z1)
    {
        for (long cz = Math.Max(z0, oldZ0); cz <= Math.Min(z1, oldZ1); cz++)
        for (long cx = Math.Max(x0, oldX0); cx <= Math.Min(x1, oldX1); cx++)
            if (AxisFadeChanged(cx, oldMinimum.X, oldMaximum.X, detailMinimum.X, detailMaximum.X)
                || AxisFadeChanged(cz, oldMinimum.Y, oldMaximum.Y, detailMinimum.Y, detailMaximum.Y))
                yield return (cx, cz);
    }

    private static bool AxisFadeChanged(long chunk, float oldMinimum, float oldMaximum, float minimum, float maximum)
    {
        if (oldMinimum == minimum && oldMaximum == maximum) return false;
        double reach = EdgeRingChunks * DetailChunkMetres, low = chunk * DetailChunkMetres, high = (chunk + 1) * DetailChunkMetres;
        bool Whole(float first, float last) => low >= first + reach && high <= last - reach;
        return !(Whole(oldMinimum, oldMaximum) && Whole(minimum, maximum));
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

    /// <summary>Coarse chunk columns holding a cell within the edge's reach (where the relief eases in) of a window's edge.</summary>
    private static IEnumerable<(long X, long Z)> CoarseEdgeRing(Vector2 minimum, Vector2 maximum)
    {
        double reach = (EdgeRingChunks * DetailChunkMetres) + CellMetres;
        long x0 = GridMath.FloorDivide((long)Math.Floor(minimum.X / CellMetres), EdgeLength), x1 = GridMath.FloorDivide((long)Math.Floor((maximum.X - 1) / CellMetres), EdgeLength);
        long z0 = GridMath.FloorDivide((long)Math.Floor(minimum.Y / CellMetres), EdgeLength), z1 = GridMath.FloorDivide((long)Math.Floor((maximum.Y - 1) / CellMetres), EdgeLength);
        long ix0 = GridMath.FloorDivide((long)Math.Floor((minimum.X + reach) / CellMetres), EdgeLength), ix1 = GridMath.FloorDivide((long)Math.Floor((maximum.X - reach) / CellMetres), EdgeLength);
        long iz0 = GridMath.FloorDivide((long)Math.Floor((minimum.Y + reach) / CellMetres), EdgeLength), iz1 = GridMath.FloorDivide((long)Math.Floor((maximum.Y - reach) / CellMetres), EdgeLength);
        return from cz in Range(z0, z1) from cx in Range(x0, x1)
               where cx <= ix0 || cx >= ix1 || cz <= iz0 || cz >= iz1
               select (cx, cz);
    }

    /// <summary>The region tier's ribbon tiles over a window, and a tile around it.</summary>
    private static IEnumerable<(long X, long Z)> RiverTilesOver(Vector2 minimum, Vector2 maximum) =>
        from tz in Range((long)Math.Floor(minimum.Y / RegionChunkMetres) - 1, (long)Math.Floor(maximum.Y / RegionChunkMetres) + 1)
        from tx in Range((long)Math.Floor(minimum.X / RegionChunkMetres) - 1, (long)Math.Floor(maximum.X / RegionChunkMetres) + 1)
        select (tx, tz);

    /// <summary>Chunk columns within <paramref name="depth"/> chunks of a window's edge: what a shift re-streamed before #10346, for the A/B.</summary>
    private static IEnumerable<(long X, long Z)> Ring(long x0, long z0, long x1, long z1, int depth) =>
        from cz in Range(z0, z1) from cx in Range(x0, x1)
        where cx < x0 + depth || cx > x1 - depth || cz < z0 + depth || cz > z1 - depth
        select (cx, cz);

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
            if (ExactRegion)
            {
                MoveRegion(oldMinimum, oldMaximum);
            }
            else
            {
                sinkRanges.Clear();
                sinkLattice.Clear();
                patchKept.Clear();
                Profile.Mark("region.keep");
                List<(long X, long Z)> band = [.. FadeBand(oldMinimum, oldMaximum).Concat(FadeBand(minimum, maximum)).Distinct()];
                coarse.Invalidate(band);
                regionMoves++;
                regionCoarseResamples += band.Count;
                Profile.Mark("region.coarse");
                // Ribbons over those bands stand on the ground whose fade changed (#9948 review), and a tile around reads it.
                RebuildRegionRivers(FadeBand(oldMinimum, oldMaximum).Concat(FadeBand(minimum, maximum))
                    .SelectMany(tile => from dz in Range(-1, 1) from dx in Range(-1, 1) select (tile.X + dx, tile.Z + dz)).ToHashSet());
                Profile.Mark("region.rivers");
                // The continent's sink and its own ribbons follow the window's fade too.
                HashSet<(long X, long Z)> continentChunks = [.. ContinentChunksOver(oldMinimum, oldMaximum).Concat(ContinentChunksOver(minimum, maximum))];
                continent!.Invalidate(continentChunks);
                Profile.Mark("region.continent");
                RebuildContinentRivers(continentChunks);
                Profile.Mark("region.continentRivers");
            }
        }

        regions.Prefetch(x0 * RegionChunkMetres, z0 * RegionChunkMetres, (x1 + 1) * RegionChunkMetres, (z1 + 1) * RegionChunkMetres);
        HashSet<(long X, long Z)> covered = [];
        for (long cz = z0; cz <= z1; cz++)
        for (long cx = x0; cx <= x1; cx++)
            if (regions.Ready(cx * RegionChunkMetres, cz * RegionChunkMetres, (cx + 1) * RegionChunkMetres, (cz + 1) * RegionChunkMetres))
                covered.Add((cx, cz));
        Profile.Mark("region.ready");
        if (covered.SetEquals(regionCovered)) return;
        HashSet<(long X, long Z)> changed = [.. covered];
        changed.SymmetricExceptWith(regionCovered);
        regionCovered.Clear();
        regionCovered.UnionWith(covered);
        coarse.Want(covered);
        Profile.Mark("region.want");
        HashSet<(long X, long Z)> changedChunks = [.. changed];
        RebuildRegionRivers(changedChunks);
        RebuildContinentRivers(new HashSet<(long X, long Z)>(changed.Select(column => (
            (long)Math.Floor(column.X * RegionChunkMetres / ContinentChunkMetres), (long)Math.Floor(column.Z * RegionChunkMetres / ContinentChunkMetres)))));
        continent!.Invalidate(changed.Select(column => (
            (long)Math.Floor(column.X * RegionChunkMetres / ContinentChunkMetres), (long)Math.Floor(column.Z * RegionChunkMetres / ContinentChunkMetres))));
        Profile.Mark("region.coverChanged");
        factsVersion++;
    }

    /// <summary>
    /// The region window moved from these bounds to the current ones (#10347). Region ground eases on the window's fade alone,
    /// so it changed exactly where the fade did: only what reads it at such a point is dropped or resampled. That is the
    /// patch's and the coarse sink's kept ground, the coarse tops and the patch dither's slots; the patch's and the coarse
    /// tier's chunk columns in the window (those entering are sampled with the new fade, those leaving go); and the ribbon
    /// tiles over them. The continent re-sinks as before; its own ribbons read no fade, only the cover, which they follow when
    /// it changes.
    /// </summary>
    private void MoveRegion(Vector2 oldMinimum, Vector2 oldMaximum)
    {
        // Whether the fade moved at a point, on the sink's lattice (every point the coarse tier reads lies on it) or the
        // patch's column centres; each is read once.
        Dictionary<(long, long), bool> lattice = [];
        bool Moved(double x, double z) => WindowFade(x, z, oldMinimum, oldMaximum, RegionMarginMetres, RegionBandMetres, RegionNoiseMetres,
            RegionNoiseWavelength, RegionCornerMetres) != RegionFade(x, z);
        bool LatticeMoved(long i, long j)
        {
            if (!lattice.TryGetValue((i, j), out bool moved)) lattice[(i, j)] = moved = Moved(i * LatticeMetres, j * LatticeMetres);
            return moved;
        }

        bool Steady(double minX, double minZ, double maxX, double maxZ) => RegionSteady(minX, minZ, maxX, maxZ, oldMinimum, oldMaximum, regionMinimum, regionMaximum);
        // Whether any lattice point over a box moved, past the box test that the fade is steady over all of it.
        bool LatticeChanged(double minX, double minZ, double maxX, double maxZ)
        {
            if (Steady(minX, minZ, maxX, maxZ)) return false;
            for (long j = (long)Math.Ceiling(minZ / LatticeMetres); j <= (long)Math.Floor(maxZ / LatticeMetres); j++)
            for (long i = (long)Math.Ceiling(minX / LatticeMetres); i <= (long)Math.Floor(maxX / LatticeMetres); i++)
                if (LatticeMoved(i, j)) return true;
            return false;
        }

        // A coarse column is read at its centre, a lattice point; its sink, over the lattice within a cell of it.
        bool ColumnMoved((long X, long Z) c) => LatticeMoved((2 * c.X) + 1, (2 * c.Z) + 1);
        // A patch chunk column reads ground at its column centres, the coarse tops within a cell and the dither's coarse
        // slots within its transition and a cell.
        double patchReach = ((coarseGround?.Settings.TransitionCells ?? 0) + 2) * CellMetres;
        bool PatchChanged((long X, long Z) chunk)
        {
            double minX = chunk.X * DetailChunkMetres, minZ = chunk.Z * DetailChunkMetres;
            if (LatticeChanged(minX - patchReach, minZ - patchReach, minX + DetailChunkMetres + patchReach, minZ + DetailChunkMetres + patchReach)) return true;
            if (Steady(minX, minZ, minX + DetailChunkMetres, minZ + DetailChunkMetres)) return false;
            for (int z = 0; z < EdgeLength; z++)
            for (int x = 0; x < EdgeLength; x++)
                if (Moved(((chunk.X * EdgeLength) + x + 0.5) * DetailMetres, ((chunk.Z * EdgeLength) + z + 0.5) * DetailMetres)) return true;
            return false;
        }

        // Whether a patch chunk column lies within the reach of the patch's edge fade, where it draws the coarse surface.
        const double edgeWhole = EdgeMarginMetres + (2 * EdgeNoiseMetres) + EdgeBandMetres + FadeNoiseSlackMetres;
        bool PatchEdge((long X, long Z) chunk)
        {
            double minX = chunk.X * DetailChunkMetres, minZ = chunk.Z * DetailChunkMetres;
            return !(minX >= detailMinimum.X + edgeWhole && minX + DetailChunkMetres <= detailMaximum.X - edgeWhole
                && minZ >= detailMinimum.Y + edgeWhole && minZ + DetailChunkMetres <= detailMaximum.Y - edgeWhole);
        }

        foreach ((long, long) column in coarseTops.Keys.Where(c => ColumnMoved(c)).ToArray()) coarseTops.Remove(column);
        foreach ((long, long) column in detailDitherSlots.Keys.Where(c => ColumnMoved(c)).ToArray()) detailDitherSlots.Remove(column);
        foreach ((long, long) point in sinkLattice.Keys.Where(p => LatticeMoved(p.Item1, p.Item2)).ToArray()) sinkLattice.Remove(point);
        foreach ((long, long) column in sinkRanges.Keys.Where(c => LatticeChanged((c.Item1 - 0.5) * CellMetres, (c.Item2 - 0.5) * CellMetres,
            (c.Item1 + 1.5) * CellMetres, (c.Item2 + 1.5) * CellMetres)).ToArray())
            sinkRanges.Remove(column);
        Profile.Mark("region.keep");
        // A kept patch column whose own centre's fade moved reads its ground afresh; the coarse surface the patch's edge eases
        // onto is read afresh where the coarse tops it interpolates changed. A chunk column is resampled where either did and
        // it is drawn from it: its own columns moved, or it lies by the patch's edge, where the coarse surface and the dither show.
        HashSet<(long X, long Z)> patchChanged = [];
        foreach (((long X, long Z) chunk, PatchColumns kept) in patchKept)
        {
            double minX = chunk.X * DetailChunkMetres, minZ = chunk.Z * DetailChunkMetres;
            bool own = false;
            if (!Steady(minX, minZ, minX + DetailChunkMetres, minZ + DetailChunkMetres))
                for (int z = 0; z < EdgeLength; z++)
                for (int x = 0; x < EdgeLength; x++)
                {
                    if (!Moved(((chunk.X * EdgeLength) + x + 0.5) * DetailMetres, ((chunk.Z * EdgeLength) + z + 0.5) * DetailMetres)) continue;
                    int i = (z * EdgeLength) + x;
                    (kept.Full[i], kept.Own[i]) = (double.NaN, PatchColumns.Unknown);
                    own = true;
                }

            bool coarseMoved = LatticeChanged(minX - patchReach, minZ - patchReach, minX + DetailChunkMetres + patchReach, minZ + DetailChunkMetres + patchReach);
            if (coarseMoved) Array.Fill(kept.Coarse, double.NaN);
            if (own || (coarseMoved && PatchEdge(chunk))) patchChanged.Add(chunk);
        }

        // The patch's chunk columns whose ground changed are resampled (none, while it lies well inside the region window).
        if (windowChunkX != long.MinValue)
        {
            long span = 2 * DetailRadiusChunks;
            long dx1 = Math.Min(lastDetailChunk, windowChunkX + span - 1), dz1 = Math.Min(lastDetailChunk, windowChunkZ + span - 1);
            List<(long X, long Z)> resample = [.. from cz in Range(windowChunkZ, dz1) from cx in Range(windowChunkX, dx1)
                                                  where patchChanged.Contains((cx, cz)) || (!patchKept.ContainsKey((cx, cz)) && PatchChanged((cx, cz)))
                                                  select (cx, cz)];
            detail.Invalidate(resample);
            // The clutter stands on the patch's ground.
            if (resample.Count > 0) clutter.Scatter(detailMinimum, detailMaximum, Surface, EdgeFade);
            regionPatchResamples += resample.Count;
        }

        Profile.Mark("region.patch");
        // Region chunk columns (a coarse chunk and a ribbon tile each) over both windows whose ground changed: within a cell of
        // them for a coarse chunk, whose sinks read that far, and two for a ribbon, which reads the ground a little beside its line.
        long ax = (long)Math.Floor(Math.Min(oldMinimum.X, regionMinimum.X) / RegionChunkMetres), bx = (long)Math.Floor(Math.Max(oldMaximum.X, regionMaximum.X) / RegionChunkMetres) - 1;
        long az = (long)Math.Floor(Math.Min(oldMinimum.Y, regionMinimum.Y) / RegionChunkMetres), bz = (long)Math.Floor(Math.Max(oldMaximum.Y, regionMaximum.Y) / RegionChunkMetres) - 1;
        bool Changed(long cx, long cz, double reach) => LatticeChanged((cx * RegionChunkMetres) - reach, (cz * RegionChunkMetres) - reach,
            ((cx + 1) * RegionChunkMetres) + reach, ((cz + 1) * RegionChunkMetres) + reach);
        List<(long X, long Z)> coarseChanged = [.. from cz in Range(az, bz) from cx in Range(ax, bx) where Changed(cx, cz, CellMetres) select (cx, cz)];
        coarse.Invalidate(coarseChanged);
        regionMoves++;
        regionCoarseResamples += coarseChanged.Count;
        Profile.Mark("region.coarse");
        RebuildRegionRivers((from cz in Range(az, bz) from cx in Range(ax, bx) where Changed(cx, cz, 2 * CellMetres) select (cx, cz)).ToHashSet());
        Profile.Mark("region.rivers");
        continent!.Invalidate([.. ContinentChunksOver(oldMinimum, oldMaximum).Concat(ContinentChunksOver(regionMinimum, regionMaximum)).Distinct()]);
        Profile.Mark("region.continent");
    }

    /// <summary>For the stream readout: region window moves since the sweep began, and the patch and coarse chunk columns they resampled.</summary>
    private long regionMoves, regionPatchResamples, regionCoarseResamples;

    /// <summary>The coarse sink's lattice spacing: the coarse tier's column centres lie on it too.</summary>
    private const double LatticeMetres = CellMetres / SinkSamplesPerCell;

    /// <summary>
    /// Whether the region window's fade is the same over a box in the old window and the new (#10347), so the region ground,
    /// which eases on it alone, is too: along each axis whose edges moved, the box lies where the fade is whole in both
    /// windows (past the band and the noise's reach) or none in both (within the margin, short of its reach). Past its reach
    /// on one axis the fade is set by the other's inset alone, as <see cref="WindowFade"/> measures it.
    /// </summary>
    private static bool RegionSteady(double minX, double minZ, double maxX, double maxZ, Vector2 oldMinimum, Vector2 oldMaximum, Vector2 minimum, Vector2 maximum) =>
        RegionAxisSteady(minX, maxX, oldMinimum.X, oldMaximum.X, minimum.X, maximum.X) && RegionAxisSteady(minZ, maxZ, oldMinimum.Y, oldMaximum.Y, minimum.Y, maximum.Y);

    private static bool RegionAxisSteady(double low, double high, float oldMinimum, float oldMaximum, float minimum, float maximum)
    {
        if (oldMinimum == minimum && oldMaximum == maximum) return true;
        const double whole = RegionMarginMetres + (2 * RegionNoiseMetres) + RegionBandMetres + FadeNoiseSlackMetres, none = RegionMarginMetres - FadeNoiseSlackMetres;
        bool Whole(float first, float last) => low >= first + whole && high <= last - whole;
        bool None(float first, float last) => high <= first + none || low >= last - none;
        return (Whole(oldMinimum, oldMaximum) && Whole(minimum, maximum)) || (None(oldMinimum, oldMaximum) && None(minimum, maximum));
    }

    /// <summary>The continent's chunk columns over a window, and a chunk around it.</summary>
    private static IEnumerable<(long X, long Z)> ContinentChunksOver(Vector2 minimum, Vector2 maximum) =>
        from cz in Range((long)Math.Floor(minimum.Y / ContinentChunkMetres) - 1, (long)Math.Floor(maximum.Y / ContinentChunkMetres) + 1)
        from cx in Range((long)Math.Floor(minimum.X / ContinentChunkMetres) - 1, (long)Math.Floor(maximum.X / ContinentChunkMetres) + 1)
        select (cx, cz);

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
        double fade = RegionFade(worldX, worldZ);
        if (fade >= 1) return region;
        // By the edge, the continent's own surface as its kilometre columns draw it, a little under it.
        double drawn = Interpolated(worldX, worldZ, ContinentCellMetres, (cx, cz) => map.Sample(cx, cz).Elevation, ContinentTops()) - (RegionUnderMetres * (1 - fade));
        return region with { Elevation = drawn + fade * (region.Elevation - drawn) };
    }

    /// <summary>How fully the region window's own ground is drawn at a point: none at its edge, all past its band.</summary>
    private double RegionFade(double worldX, double worldZ) => WindowFade(worldX, worldZ, regionMinimum, regionMaximum,
        RegionMarginMetres, RegionBandMetres, RegionNoiseMetres, RegionNoiseWavelength, RegionCornerMetres);

    /// <summary>
    /// Where the continent tier sinks: beneath region ground past the window's fade band. Its ground
    /// slopes down to the sunk depth between kilometre cells, and inside the band that slope stays
    /// under region ground, which there eases onto the continent's own surface.
    /// </summary>
    private double ContinentSink(double worldX, double worldZ)
    {
        // The columns within a cell of the window's edge keep their height, so the continent never dips outside it.
        double inset = Math.Min(Math.Min(worldX - regionMinimum.X, regionMaximum.X - worldX), Math.Min(worldZ - regionMinimum.Y, regionMaximum.Y - worldZ));
        if (!InRegion(worldX, worldZ) || inset < UnsunkCells * ContinentCellMetres) return 0;
        double fade = 0;
        for (int j = -1; j <= 1; j++)
        for (int i = -1; i <= 1; i++)
            fade = Math.Max(fade, RegionFade(worldX + (i * ContinentCellMetres), worldZ + (j * ContinentCellMetres)));
        // Only as deep as the region's ground can fall under the continent's here: its offset, and the fade's share of a full sink.
        double metres = (2 * RegionUnderMetres) + (fade * PatchSink * ContinentCellMetres / VerticalExaggeration);
        return WorldMap.Smooth(Math.Clamp(fade / SinkFromFade, 0, 1)) * metres * VerticalExaggeration / CellMetres;
    }

    private bool InRegion(double worldX, double worldZ) =>
        regions is null || regionCovered.Contains(((long)Math.Floor(worldX / RegionChunkMetres), (long)Math.Floor(worldZ / RegionChunkMetres)));

    private bool InDetail(double worldX, double worldZ) =>
        worldX >= detailMinimum.X && worldZ >= detailMinimum.Y && worldX < detailMaximum.X && worldZ < detailMaximum.Y;

    /// <summary>
    /// The region tier's ribbons (the whole map's on a regional map) in the named tiles, or all: a
    /// continent's from its network reaches in the region window, where it is built.
    /// </summary>
    private void RebuildRegionRivers(IReadOnlySet<(long X, long Z)>? tiles)
    {
        IEnumerable<RiverPoint[]> reaches;
        if (regions is null) reaches = map.Rivers.Reaches;
        else
        {
            if (regionCovered.Count == 0 && regionReaches.Count == 0) return;
            MapDrainage drainage = regions.Drainage;
            SortedSet<int> ids = drainage.ReachesWithin(regionMinimum.X, regionMinimum.Y, regionMaximum.X, regionMaximum.Y);
            foreach (int gone in regionReaches.Keys.Where(id => !ids.Contains(id)).ToArray()) regionReaches.Remove(gone);
            foreach (int id in ids)
                if (!regionReaches.ContainsKey(id)) regionReaches[id] = drainage.Shaped(id);
            reaches = regionReaches.Values;
        }

        Dictionary<(long, long), double> tops = [];
        riverRibbons?.Rebuild(RegionRiverTier, RegionChunkMetres, reaches, RegionRivers,
            (x, z) => InRegion(x, z) && !(InDetail(x, z) && EdgeFade(x, z) >= RibbonGivesWay),
            // By the window's edge the continent's surface covers the region's, so a ribbon lies on the higher.
            (x, z, water) => Position(x, Math.Max(water, Math.Max(Interpolated(x, z, CellMetres, (cx, cz) => Top(fineGround(cx, cz), 0), tops),
                regions is null ? double.MinValue : Interpolated(x, z, ContinentCellMetres, (cx, cz) => map.Sample(cx, cz).Elevation, ContinentTops()))), z)
                + (Vector3.UnitY * RegionRiverLift),
            tiles);
        factsVersion++;
    }

    /// <summary>A continent's own ribbons in the named continent tiles, or all, wherever the region window does not draw its rivers.</summary>
    private void RebuildContinentRivers(IReadOnlySet<(long X, long Z)>? tiles)
    {
        if (regions is null) return;
        Dictionary<(long, long), double> tops = [];
        riverRibbons?.Rebuild(ContinentRiverTier, ContinentChunkMetres, map.Rivers.Reaches, ContinentRivers, (x, z) => !InRegion(x, z),
            (x, z, water) => Position(x, Math.Max(water, Interpolated(x, z, ContinentCellMetres, (cx, cz) => Top(map.Sample(cx, cz), 0), tops)), z) + (Vector3.UnitY * ContinentRiverLift),
            tiles);
        factsVersion++;
    }

    /// <summary>
    /// A tier's ground at a world point as its mesh draws it. Its columns stand at cell centres; with smooth
    /// vertex placement the surface's vertices sit at the cell corners between them, each about the mean of
    /// the four columns around it, and the mesh spans those. Tops are kept in <paramref name="tops"/>.
    /// </summary>
    private static double Interpolated(double worldX, double worldZ, double cellMetres, Func<double, double, double> top, Dictionary<(long, long), double> tops)
    {
        double fx = worldX / cellMetres, fz = worldZ / cellMetres;
        long ix = (long)Math.Floor(fx), iz = (long)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        double Column(long cx, long cz)
        {
            if (!tops.TryGetValue((cx, cz), out double height)) tops[(cx, cz)] = height = top((cx + 0.5) * cellMetres, (cz + 0.5) * cellMetres);
            return height;
        }

        double Corner(long kx, long kz) => (Column(kx - 1, kz - 1) + Column(kx, kz - 1) + Column(kx - 1, kz) + Column(kx, kz)) / 4;
        double near = Corner(ix, iz) + ((Corner(ix + 1, iz) - Corner(ix, iz)) * tx), far = Corner(ix, iz + 1) + ((Corner(ix + 1, iz + 1) - Corner(ix, iz + 1)) * tx);
        return near + ((far - near) * tz);
    }

    private static RiverInfluence? NoRiver(double worldX, double worldZ) => null;

    /// <summary>
    /// A 4 by 4 ordered (Bayer) threshold for a column, 0 to 1: any few columns together take every step of
    /// it. Each block of columns turns and mirrors the matrix its own way, so the steps fall in no rows.
    /// </summary>
    private double Ordered(long column, long row)
    {
        long blockX = GridMath.FloorDivide(column, OrderedSide), blockZ = GridMath.FloorDivide(row, OrderedSide);
        int x = (int)(column - (blockX * OrderedSide)), z = (int)(row - (blockZ * OrderedSide));
        int turn = (int)(MapNoise.Unit(map.Configuration.Contract.GeographyNoiseSeed ^ OrderedSalt, blockX, blockZ) * 8);
        if ((turn & 1) != 0) x = OrderedSide - 1 - x;
        if ((turn & 2) != 0) z = OrderedSide - 1 - z;
        if ((turn & 4) != 0) (x, z) = (z, x);
        return (OrderedMatrix[(z * OrderedSide) + x] + 0.5) / (OrderedSide * OrderedSide);
    }

    private const ulong OrderedSalt = 0x9B05688C2B3E6C1FUL;

    private const int OrderedSide = 4;
    private static readonly int[] OrderedMatrix = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <summary>
    /// One of the coarser tier's materials at a point, in the shares its terrain layers blend there: each of
    /// its columns within <paramref name="transition"/> cells, weighed by a tent, as its vertices weigh the
    /// slots about them. <paramref name="pick"/> (0 to 1) chooses along the shares. Slots are kept per column.
    /// </summary>
    private uint Blended(double worldX, double worldZ, double cellMetres, int transition, Func<double, double, MapSample> coarser,
        Dictionary<(long, long), uint> slots, bool toned, double pick)
    {
        if (slots.Count > CoarseTopsLimit) slots.Clear();
        double fx = (worldX / cellMetres) - 0.5, fz = (worldZ / cellMetres) - 0.5;
        long cx = (long)Math.Round(fx), cz = (long)Math.Round(fz);
        Span<(uint Slot, double Weight)> shares = stackalloc (uint, double)[((2 * transition) + 3) * ((2 * transition) + 3)];
        int count = 0;
        double total = 0, reach = transition + 1;
        for (long j = cz - transition - 1; j <= cz + transition + 1; j++)
        for (long i = cx - transition - 1; i <= cx + transition + 1; i++)
        {
            double weight = Math.Max(0, reach - Math.Abs(i - fx)) * Math.Max(0, reach - Math.Abs(j - fz));
            if (weight <= 0) continue;
            if (!slots.TryGetValue((i, j), out uint slot))
            {
                double x = (i + 0.5) * cellMetres, z = (j + 0.5) * cellMetres;
                MapSample sample = coarser(x, z);
                slots[(i, j)] = slot = sample.Rock >= ExposedRock ? RockSlot : Slot(WorldMap.Biome(sample), toned ? Tone(x, z) : Tones / 2);
            }

            int k = 0;
            while (k < count && shares[k].Slot != slot) k++;
            if (k == count) shares[count++] = (slot, 0);
            shares[k].Weight += weight;
            total += weight;
        }

        double target = pick * total;
        for (int k = 0; k < count; k++)
        {
            target -= shares[k].Weight;
            if (target < 0) return shares[k].Slot;
        }

        return shares[Math.Max(0, count - 1)].Slot;
    }

    /// <summary>Lets go of what the snapshot just published no longer names.</summary>
    internal void AfterAppearanceSnapshot() => riverRibbons.AfterSnapshot();

    public void Dispose()
    {
        riverRibbons?.Dispose();
        waterSurfaces?.Dispose();
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
        frontierMarker?.Dispose();
        weather?.Dispose();
        foreach (Appearance placeMarker in placeMarkers.Values) placeMarker.Dispose();
        clusterMarker?.Dispose();
        foreach (Material material in materials.Values) material.Dispose();
    }

    /// <summary>
    /// One streamed layer: each voxel column samples the map at its centre, with exaggerated relief,
    /// lowered by <paramref name="sink"/> coarse voxels at a world point. Columns beyond the map are empty.
    /// </summary>
    /// <param name="dither">
    /// Toward a finer tier's edge, where its ground is chosen by: how far (metres) a column's environment
    /// is drawn from about it, and the ground drawn there (#9952). The coarser tier beside it blends its
    /// environments over a box of its own cells; a column choosing from a point jittered over a box as wide
    /// reproduces that blend, so environment edges do not change sharpness, in a straight line, at the edge.
    /// </param>
    private MapVoxelLayer Layer(double voxelSize, double cellMetres, Func<double, double, MapSample> geography, Func<double, double, RiverInfluence?> rivers, double riverReach,
        Func<double, double, double> sink, bool localRelief, TerrainGroundMaterials? ground, double coarseBeyond,
        (Func<double, double, double> Fade, double CellMetres, int Transition, Func<double, double, MapSample> Ground, Dictionary<(long, long), uint> Slots)? dither = null)
    {
        double metresPerVoxel = cellMetres / VerticalExaggeration;
        double origin = OriginY / voxelSize;
        void Sample(long chunkX, long chunkZ, Span<double> surface, Span<uint> material)
        {
            PatchColumns? kept = localRelief ? PatchKept(chunkX, chunkZ) : null;
            for (int z = 0; z < EdgeLength; z++)
            for (int x = 0; x < EdgeLength; x++)
            {
                int i = z * EdgeLength + x;
                double worldX = (chunkX * EdgeLength + x + 0.5) * cellMetres, worldZ = (chunkZ * EdgeLength + z + 0.5) * cellMetres;
                if (Math.Abs(worldX) > map.Radius || Math.Abs(worldZ) > map.Radius) { surface[i] = double.NaN; continue; }
                // A kept column whose relief is known reads nothing new of the ground: none of it moves with the window.
                bool reuse = kept is not null && kept.Own[i] != PatchColumns.Unknown && !double.IsNaN(kept.Full[i]);
                MapSample sample = reuse ? default : geography(worldX, worldZ);
                uint ownSlot = reuse ? kept!.Own[i]
                    : sample.Rock >= ExposedRock ? RockSlot : Slot(WorldMap.Biome(sample), ground is null ? Tone(worldX, worldZ) : Tones / 2);
                // Ground under the sea or a lake is drawn as its shore, its own surface covering it (#9952); a river's channel as the river.
                uint water = reuse ? kept!.Water[i]
                    : UnderStandingWater(sample) ? Slot(ShoreBiome, ground is null ? Tone(worldX, worldZ) : Tones / 2)
                    : rivers(worldX, worldZ) is RiverInfluence river && river.Distance < river.HalfWidth + cellMetres * riverReach ? RiverSlot
                    : PatchColumns.Dry;
                if (kept is not null) (kept.Own[i], kept.Water[i]) = (ownSlot, water);
                // Toward a finer tier's edge each column takes its own environment with the share of its fade,
                // else one of the coarser tier's in the shares that tier blends there, by an ordered threshold.
                if (dither is var (ditherFade, coarserCell, transition, coarserGround, coarserSlots) && ditherFade(worldX, worldZ) is double faded && faded < 1)
                {
                    double threshold = Ordered((chunkX * EdgeLength) + x, (chunkZ * EdgeLength) + z);
                    if (threshold >= faded)
                        ownSlot = Blended(worldX, worldZ, coarserCell, transition, coarserGround, coarserSlots, ground is null, (threshold - faded) / (1 - faded));
                }

                // The detail patch carries the same regional relief walking terrain adds to the map.
                surface[i] = origin + Top(sample, worldX, worldZ, localRelief, kept, i) / metresPerVoxel - sink(worldX, worldZ) / voxelSize;
                material[i] = water != PatchColumns.Dry ? water : ownSlot;
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
    private double Top(MapSample sample, double worldX, double worldZ, bool localRelief) => Top(sample, worldX, worldZ, localRelief, null, 0);

    /// <param name="kept">A patch chunk column's kept ground (<see cref="PatchKept"/>), or null to keep none.</param>
    private double Top(MapSample sample, double worldX, double worldZ, bool localRelief, PatchColumns? kept, int column)
    {
        if (!localRelief) return Top(sample, 0);
        double fade = EdgeFade(worldX, worldZ);
        double full = 0;
        if (fade > 0)
        {
            full = kept?.Full[column] ?? double.NaN;
            if (double.IsNaN(full))
            {
                full = Top(sample, Relief(sample, worldX, worldZ));
                if (kept is not null) kept.Full[column] = full;
            }
        }

        if (fade >= 1) return full;
        // Toward its edge the patch draws the coarse ground's own surface, a little under it.
        double coarseSurface = kept?.Coarse[column] ?? double.NaN;
        if (double.IsNaN(coarseSurface))
        {
            coarseSurface = Interpolated(worldX, worldZ, CellMetres, (cx, cz) => Top(fineGround(cx, cz), 0), CoarseTops());
            if (kept is not null) kept.Coarse[column] = coarseSurface;
        }

        return coarseSurface - (EdgeUnderMetres * (1 - fade)) + (fade * (full - coarseSurface));
    }

    /// <summary>
    /// What the patch reads of the ground in a chunk column, none of which moves with the window (#10346): each column's
    /// top with its relief and the coarse surface over it (NaN until first needed), its own environment's slot before
    /// any dither, and the shore or river slot water draws it with (<see cref="Unknown"/> until sampled).
    /// </summary>
    private sealed class PatchColumns
    {
        internal const uint Unknown = uint.MaxValue, Dry = 0;
        internal readonly double[] Full = new double[MapVoxelLayer.ColumnCount], Coarse = new double[MapVoxelLayer.ColumnCount];
        internal readonly uint[] Own = new uint[MapVoxelLayer.ColumnCount], Water = new uint[MapVoxelLayer.ColumnCount];

        internal PatchColumns()
        {
            Array.Fill(Full, double.NaN);
            Array.Fill(Coarse, double.NaN);
            Array.Fill(Own, Unknown);
        }
    }

    /// <summary>
    /// The patch's kept ground for a chunk column: a window shift re-streams the columns whose edge fade changed, and
    /// only the fade and the dither it drives are new. Kept while the column is in the window; a continent's region
    /// window moving drops the columns' ground where its fade, which the region ground eases on, changed (<see cref="MoveRegion"/>).
    /// </summary>
    private PatchColumns? PatchKept(long chunkX, long chunkZ)
    {
        if (!ExactShift) return null;
        if (!patchKept.TryGetValue((chunkX, chunkZ), out PatchColumns? kept)) patchKept[(chunkX, chunkZ)] = kept = new PatchColumns();
        return kept;
    }

    private readonly Dictionary<(long X, long Z), PatchColumns> patchKept = [];

    /// <summary>The coarse column tops the patch's edge reads, kept while they are wanted and dropped when the cache grows past a bound.</summary>
    private Dictionary<(long, long), double> CoarseTops()
    {
        if (coarseTops.Count > CoarseTopsLimit) coarseTops.Clear();
        return coarseTops;
    }

    private readonly Dictionary<(long, long), double> coarseTops = [];
    /// <summary>The coarse tier's environment slots the patch's edge dither reads, by coarse column: of region ground on a continent.</summary>
    private readonly Dictionary<(long, long), uint> detailDitherSlots = [];
    private readonly Dictionary<(long, long), double> continentTops = [];
    /// <summary>Each coarse column's drawn top under the patch and the lowest the patch can draw within a cell of it, kept as <see cref="CoarseTops"/> are; a continent's region window moving drops those where its fade, which the region ground eases on, changed.</summary>
    private readonly Dictionary<(long, long), (double Top, double Lowest)> sinkRanges = [];
    /// <summary>The lowest the patch can draw at each point the sink samples, on its lattice of <see cref="SinkSamplesPerCell"/> points a cell.</summary>
    private readonly Dictionary<(long, long), double> sinkLattice = [];

    private Dictionary<(long, long), double> ContinentTops()
    {
        if (continentTops.Count > CoarseTopsLimit) continentTops.Clear();
        return continentTops;
    }
    private const int CoarseTopsLimit = 65_536;

    private static double Top(MapSample sample, double relief) => UnderStandingWater(sample)
        ? WaterRules.Surface(sample) - WaterBedMetres
        : Math.Max(sample.Elevation + relief, WaterRules.Surface(sample));

    /// <summary>Whether ground stands under the sea or a lake, whose own surfaces draw the water over it (#9952).</summary>
    private static bool UnderStandingWater(MapSample sample) =>
        sample.Elevation < GenerationConstants.WaterLevel || (sample.InLake && sample.Elevation < sample.LakeSurface);

    /// <summary>
    /// Ground under the sea or a lake draws this far under the water's level: a bed the water's surface
    /// stands clear over, rather than ground flattened to the level, which the surface fought (#9952 review).
    /// </summary>
    private const double WaterBedMetres = 6;

    private double Relief(MapSample sample, double worldX, double worldZ) =>
        RegionalTerrain.Relief(map.Configuration.Contract.GeographyNoiseSeed, sample, worldX, worldZ);

    /// <summary>
    /// How far a coarse column under the detail patch sinks, in coarse voxels: below the lowest the patch
    /// can draw within a cell of it, with or without its regional relief - the edge fade lies between - so
    /// the sink does not change as the patch moves.
    /// </summary>
    private double UnderDetail(Func<double, double, MapSample> geography, double worldX, double worldZ)
    {
        // The columns by the patch's edge keep their height; further in, so does any with no relief drawn within a cell.
        double inset = Math.Min(Math.Min(worldX - detailMinimum.X, detailMaximum.X - worldX), Math.Min(worldZ - detailMinimum.Y, detailMaximum.Y - worldZ));
        if (inset < UnsunkCells * CellMetres) return 0;
        double fade = 0;
        for (int j = -1; j <= 1; j++)
        for (int i = -1; i <= 1; i++)
            fade = Math.Max(fade, EdgeFade(worldX + (i * CellMetres), worldZ + (j * CellMetres)));
        if (fade <= 0) return 0;
        // The ground's range does not move with the patch: kept, so a shift re-sinks a column from its fade alone (#10346).
        (long, long) column = ((long)Math.Floor(worldX / CellMetres), (long)Math.Floor(worldZ / CellMetres));
        if (sinkRanges.Count > CoarseTopsLimit) sinkRanges.Clear();
        if (!ExactShift)
        {
            sinkRanges.Clear();
            sinkLattice.Clear();
        }

        if (!sinkRanges.TryGetValue(column, out (double Top, double Lowest) range))
        {
            double drawn = Top(geography(worldX, worldZ), 0), under = drawn;
            if (sinkLattice.Count > CoarseTopsLimit) sinkLattice.Clear();
            for (int j = -SinkSamplesPerCell; j <= SinkSamplesPerCell; j++)
            for (int i = -SinkSamplesPerCell; i <= SinkSamplesPerCell; i++)
            {
                double x = worldX + i * CellMetres / SinkSamplesPerCell, z = worldZ + j * CellMetres / SinkSamplesPerCell;
                // Neighbouring columns share the sample points: each is read once (#10346).
                (long, long) point = ((long)Math.Round(x * SinkSamplesPerCell / CellMetres), (long)Math.Round(z * SinkSamplesPerCell / CellMetres));
                if (!sinkLattice.TryGetValue(point, out double low))
                {
                    MapSample sample = geography(x, z);
                    sinkLattice[point] = low = Math.Min(Top(sample, 0), Top(sample, Relief(sample, x, z)));
                }

                under = Math.Min(under, low);
            }

            sinkRanges[column] = range = (drawn, under);
        }

        return WorldMap.Smooth(Math.Clamp(fade / SinkFromFade, 0, 1)) * ((fade * (range.Top - range.Lowest) * VerticalExaggeration / CellMetres) + SinkMargin);
    }

    /// <summary>
    /// How much of the regional relief the patch draws at a point: none by its edge, all of it inside,
    /// easing in across a band measured from the edge with rounded corners and moved by low noise.
    /// </summary>
    private double EdgeFade(double worldX, double worldZ) => !InDetail(worldX, worldZ) ? 0
        : WindowFade(worldX, worldZ, detailMinimum, detailMaximum, EdgeMarginMetres, EdgeBandMetres, EdgeNoiseMetres, EdgeNoiseWavelength, EdgeCornerMetres);

    /// <summary>Kept clear of the noise's bound, so a rounding at its extreme never changes a fade the early outs give.</summary>
    private const double FadeNoiseSlackMetres = 1;

    /// <summary>
    /// How fully a window's own ground is drawn at a point: none within a margin of its edge, all of it past
    /// a band beyond, measured with the corners rounded and the boundary moved in and out by low noise.
    /// </summary>
    private double WindowFade(double worldX, double worldZ, Vector2 minimum, Vector2 maximum, double margin, double band, double noise, double wavelength, double corner)
    {
        double insetX = Math.Min(worldX - minimum.X, maximum.X - worldX), insetZ = Math.Min(worldZ - minimum.Y, maximum.Y - worldZ);
        double inset = insetX < corner && insetZ < corner
            ? corner - Math.Sqrt(Math.Pow(corner - insetX, 2) + Math.Pow(corner - insetZ, 2))
            : Math.Min(insetX, insetZ);
        // The noise moves the boundary by at most its amplitude, so beyond its reach the fade is whole or none without it (#10346).
        if (ExactShift && inset - margin - (2 * noise) >= band + FadeNoiseSlackMetres) return 1;
        if (ExactShift && inset <= margin - FadeNoiseSlackMetres) return 0;
        double wander = noise * MapNoise.Fbm(map.Configuration.Contract.GeographyNoiseSeed ^ EdgeNoiseSalt, worldX / wavelength, worldZ / wavelength, 2, 0.5);
        return WorldMap.Smooth(Math.Clamp((inset - margin - noise + wander) / band, 0, 1));
    }

    /// <summary>A point on the surface as drawn: the detail patch where it covers, else the coarse map.</summary>
    private Vector3 Surface(double worldX, double worldZ)
    {
        bool inDetail = InDetail(worldX, worldZ);
        // On a continent, region ground is read only where its tier is drawn, so it is always built.
        MapSample sample = regions is not null && (inDetail || InRegion(worldX, worldZ)) ? RegionGround(worldX, worldZ) : map.Sample(worldX, worldZ);
        // Over water, markers stand on its surface, not its bed.
        return Position(worldX, Math.Max(Top(sample, worldX, worldZ, inDetail), WaterRules.Surface(sample)), worldZ);
    }

    private Vector3 Position(double worldX, double elevation, double worldZ)
    {
        double metresPerUnit = CellMetres / VerticalExaggeration;
        return new((float)(worldX / CellMetres), (float)(OriginY + Math.Max(elevation, GenerationConstants.WaterLevel) / metresPerUnit),
            (float)(worldZ / CellMetres));
    }

    /// <summary>A flat palette colour, decoded once and given once (a white tint), so the Engine draws it as authored.</summary>
    private Material Flat(Color color) =>
        engine.Graphics.CreateMaterial(new MaterialRequest(color.Linear(), default, 1f, ColourSpace.White, Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));

    private static uint Slot(MapBiome biome, int tone = Tones / 2) => FirstBiomeSlot + (uint)((int)biome * Tones + tone);

    private int Tone(double x, double z)
    {
        double noise = MapNoise.Fbm(map.Configuration.Contract.GeographyNoiseSeed ^ ToneSalt, x / ToneWavelength, z / ToneWavelength, 2, 0.5);
        return Math.Clamp((int)Math.Floor((noise * 1.6 + 1) / 2 * Tones), 0, Tones - 1);
    }

    private static Color Shade(Color color, float factor) =>
        new(Math.Min(1, color.R * factor), Math.Min(1, color.G * factor), Math.Min(1, color.B * factor), 1);
}
