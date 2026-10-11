using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Heightfields;

namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>
/// The map on the skyline (#9751, H1 #9779): the world map's terrain, at its true heights, drawn in
/// the Engine's backdrop layer behind the first-person world, so a range on the map stands on the
/// horizon in the direction it lies. The Engine's backdrop camera stands where the player stands,
/// scaled down by <see cref="Scale"/>, so the whole map fits in a few hundred units.
/// <para>
/// The ground is one Engine heightfield (#9848, rusty-engine #9842) in the backdrop, sampled in world
/// metres by <see cref="HorizonGround"/> on the streamer's workers, its rings centred on the
/// first-person camera and geomorphed one onto the next by the Engine. It is sunk about the player
/// within the far field's reach (<see cref="HorizonSink"/>), so the 8 m far field covers the near range
/// and the backdrop takes over beyond it under the haze.
/// </para>
/// It is linked to the first-person camera alone, so the map screen's camera never sees it, and is
/// unlinked while the player is in a separate space (a dungeon).
/// </summary>
internal sealed class HorizonBackdrop : IProductModule
{
    /// <summary>An ordinary map's backdrop unit: the map view's coarse layer.</summary>
    internal const double CellMetres = 32;
    /// <summary>A continent's backdrop unit: the map view's kilometre tier.</summary>
    internal const double ContinentCellMetres = 1000;
    /// <summary>How many sampled tiles are committed to the Engine an update, by default: 64 settles a 130 km jump in about 4 s (8 took 19 s).</summary>
    private const int DefaultCommitsPerUpdate = 64;
    private int commitsPerUpdate = DefaultCommitsPerUpdate;
    private static readonly Vector3 BackdropOrigin = Vector3.Zero;
    private static readonly Color White = new(1, 1, 1, 1);
    private const float GroundRoughness = 1f;

    private readonly IEngineContext engine;
    private readonly WorldMap map;
    private readonly Func<SpatialSession> walking;
    private readonly Func<Vector3> playerWorld;
    private readonly Func<Camera?> camera;
    private readonly Func<bool> inTheWorld;
    private readonly Func<double> reach;
    private readonly Action<bool>? shownChanged;
    private readonly MapRegions? regions;
    private readonly HorizonGround ground;
    /// <summary>The ground's one material: matte white, tinted by the samples' linear vertex colours.</summary>
    private readonly Material material;
    private Heightfield? heightfield;
    private HeightfieldStreamer? streamer;
    /// <summary>The camera the rings follow; the heightfield is made again for another.</summary>
    private Camera? focusCamera;
    /// <summary>For measuring what the horizon holds: the heightfield destroyed until built again.</summary>
    private bool released;
    /// <summary>Region tiles about the player that were built when last looked at; a change resamples the ground over them.</summary>
    private readonly HashSet<(int X, int Z)> readyRegions = [];
    private (double X, double Z) sinkCentre = (double.NaN, double.NaN);
    private double reachMetres;
    private Camera? linked;
    private bool shown;
    private long linkChanges;
    private double exaggeration = 1, exaggerationBase;

    /// <param name="walking">The walking session: the backdrop's anchor is resolved in its frame, so a rebase moves nothing.</param>
    /// <param name="camera">The first-person camera, once it exists: the only view linked to the backdrop, and the rings' focus.</param>
    /// <param name="inTheWorld">Whether the player is in the open world, not a separate space such as a dungeon.</param>
    /// <param name="reach">The far field's reach from the player, in metres: the backdrop is sunk within it.</param>
    /// <param name="shownChanged">Told each update whether the backdrop is shown, so the sky can thin its fog.</param>
    internal HorizonBackdrop(IEngineContext engine, WorldMap map, Func<SpatialSession> walking, Func<Vector3> playerWorld,
        Func<Camera?> camera, Func<bool> inTheWorld, Func<double> reach, Action<bool>? shownChanged = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        this.walking = walking ?? throw new ArgumentNullException(nameof(walking));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        this.inTheWorld = inTheWorld ?? throw new ArgumentNullException(nameof(inTheWorld));
        this.reach = reach ?? throw new ArgumentNullException(nameof(reach));
        this.shownChanged = shownChanged;
        Scale = map.Scale.Continental ? ContinentCellMetres : CellMetres;
        ErodedHeights? eroded = null;
        if (map.Scale.Continental)
        {
            regions = MapRegions.For(map);
            eroded = new(map, new ErosionFilter(map.Configuration.Contract.GeographyNoiseSeed, ErosionFilterSettings.Continent), ContinentDesign.For(map.Configuration.Size));
        }

        ground = new HorizonGround(map, regions, eroded, FarField.ChunkMetres);
        material = engine.Graphics.CreateMaterial(new MaterialRequest(White, default, GroundRoughness, White, Vector3.Zero, 0f, false));
    }

    /// <summary>Weather fronts on the horizon (H2 #9780), drawn while the backdrop is shown.</summary>
    internal HorizonWeather? Weather { get; set; }

    /// <summary>Known places on the horizon (H3 #9781), placed while the backdrop is shown.</summary>
    internal HorizonLandmarks? Landmarks { get; set; }

    /// <summary>The fronts' masses and the places' silhouettes for the appearance snapshot while the backdrop is shown.</summary>
    internal IEnumerable<AppearanceFact> Facts => !shown ? [] : [.. Weather?.Facts(Scale) ?? [], .. Landmarks?.Facts() ?? []];

    /// <summary>For comparison captures: off unlinks the backdrop, leaving the far field over the panorama.</summary>
    internal bool Enabled { get; set; } = true;

    /// <summary>Metres per backdrop unit: the Engine draws the backdrop from the eye's position divided by this.</summary>
    internal double Scale { get; }

    /// <summary>
    /// For aimed captures only (#9779): heights above the ground under the player are multiplied by
    /// this, so a gentle map's ranges read on the skyline beside the map view. Play is at true scale (1).
    /// The whole ground is sampled again.
    /// </summary>
    internal string Exaggerate(double factor)
    {
        exaggeration = Math.Clamp(factor, 1, 20);
        Vector3 at = playerWorld();
        exaggerationBase = map.Sample(at.X, at.Z).Elevation;
        Publish();
        Invalidate(-map.Radius, -map.Radius, map.Radius, map.Radius);
        return string.Create(CultureInfo.InvariantCulture, $"exaggeration={exaggeration:F1} above {exaggerationBase:F0}m; ") + Readout();
    }

    /// <summary>
    /// For measuring what the horizon holds (R9779-1): destroys the heightfield (its samples, meshes and
    /// draws) and unlinks it, or builds it again. The difference in the host's memory is the horizon's cost.
    /// </summary>
    internal string Hold(bool held)
    {
        if (held == !released) return Readout();
        if (!held)
        {
            Link(null);
            Release();
            released = true;
            return "horizon released";
        }

        released = false;
        return Readout();
    }

    /// <summary>
    /// For tuning (#9848): the middle and coarsest rings' reaches in kilometres and the tiles committed an
    /// update; the heightfield is made again with them on the next update.
    /// </summary>
    internal string Tune(double midKilometres, double coarsestKilometres, long commits)
    {
        if (commits < 1) return "refused: at least one tile a commit";
        ground.RingsReaching(midKilometres * 1000, coarsestKilometres * 1000);
        commitsPerUpdate = (int)Math.Min(commits, 1024);
        Release();
        return string.Create(CultureInfo.InvariantCulture,
            $"rings={string.Join(",", ground.Rings.Select(ring => $"{ring.CellSize}m@{ring.Reach / 1000:F0}km"))} commits={commitsPerUpdate}");
    }

    public void Start() => Follow();

    public void Update(ProductStep step) => Follow();

    public void Restart() => Follow();

    /// <summary>
    /// Keeps the backdrop with the player: sinks the ground under the far field about them, resamples it
    /// where region tiles were built, links it to the first-person camera while they are in the open
    /// world, and streams a bounded number of tiles an update.
    /// </summary>
    private void Follow()
    {
        if (released)
        {
            shownChanged?.Invoke(false);
            Weather?.Update(false);
            Weather?.UpdateBodies(Scale, false);
            Landmarks?.Update(Scale, false, reachMetres, GroundMetres);
            return;
        }

        Vector3 at = playerWorld();
        Camera? view = inTheWorld() && Enabled ? camera() : null;
        Build(camera());
        FollowSink(at);
        FollowRegions(at);
        streamer?.Update();
        Link(view);
        shownChanged?.Invoke(shown);
        Weather?.Update(shown);
        Weather?.UpdateBodies(Scale, shown);
        Landmarks?.Update(Scale, shown, reachMetres, GroundMetres);
    }

    /// <summary>Makes the heightfield once the first-person camera exists, following it; again if the camera changes.</summary>
    private void Build(Camera? view)
    {
        if (view is null || (heightfield is not null && ReferenceEquals(view, focusCamera))) return;
        Release();
        focusCamera = view;
        heightfield = engine.Heightfield.Create(new HeightfieldCreateRequest(HorizonGround.TileCells, ground.Rings,
            new HeightfieldMaterial[] { new(0, material) }, RenderLayer.Backdrop, BackdropOrigin, (float)(1 / Scale),
            HeightfieldFocus.Following(view)) with { ShadowCasting = ShadowCasting.None });
        streamer = new HeightfieldStreamer(engine, heightfield, ground, commitsPerUpdate);
        readyRegions.Clear();
        sinkCentre = (double.NaN, double.NaN);
        sinkMoves = 0;
        exposedChange = 0;
    }

    private void Release()
    {
        streamer?.Dispose();
        streamer = null;
        heightfield?.Dispose();
        heightfield = null;
        focusCamera = null;
    }

    /// <summary>
    /// Moves the sunk zone onto the player once they have walked the follow step from its centre, and
    /// samples again the ground it sank before and sinks now.
    /// </summary>
    private void FollowSink(Vector3 at)
    {
        double reachNow = reach();
        if (reachNow == reachMetres && !double.IsNaN(sinkCentre.X)
            && Math.Max(Math.Abs(at.X - sinkCentre.X), Math.Abs(at.Z - sinkCentre.Z)) < HorizonSink.FollowMetres) return;
        (double X, double Z) previous = sinkCentre;
        double previousExtent = HorizonSink.CoveredMetres(reachMetres, FarField.ChunkMetres);
        if (!double.IsNaN(previous.X) && reachNow == reachMetres) MeasureExposedChange(previous, (at.X, at.Z));
        sinkCentre = (at.X, at.Z);
        reachMetres = reachNow;
        // The frame the workers sample by changes first, so nothing invalidated is sampled again by the old one.
        Publish();
        if (!double.IsNaN(previous.X)) Invalidate(previous.X - previousExtent, previous.Z - previousExtent, previous.X + previousExtent, previous.Z + previousExtent);
        double extent = HorizonSink.CoveredMetres(reachMetres, FarField.ChunkMetres);
        Invalidate(sinkCentre.X - extent, sinkCentre.Z - extent, sinkCentre.X + extent, sinkCentre.Z + extent);
    }

    /// <summary>
    /// A continent's region tiles about the player: those the finest ring reaches are built ahead of need,
    /// and wherever one was built (or let go) since last looked at, the ground over it is sampled again.
    /// </summary>
    private void FollowRegions(Vector3 at)
    {
        if (regions is null) return;
        double extent = HorizonGround.RegionReachMetres + (HorizonGround.RegionCellMetres * HorizonGround.TileCells);
        regions.Prefetch(at.X - extent, at.Z - extent, at.X + extent, at.Z + extent);
        HashSet<(int X, int Z)> near = [.. MapRegions.TilesNear(at.X, at.Z, extent)];
        foreach ((int X, int Z) tile in near)
        {
            bool ready = regions.IsReady(tile);
            if (ready == readyRegions.Contains(tile)) continue;
            if (ready) readyRegions.Add(tile);
            else readyRegions.Remove(tile);
            (double x, double z) = MapRegions.Centre(tile);
            double half = (MapRegions.TileSpacing / 2) + MapRegions.BlendHalfWidth;
            Invalidate(x - half, z - half, x + half, z + half);
        }

        readyRegions.IntersectWith(near);
    }

    private void Publish() => ground.Current = new HorizonGround.Frame(sinkCentre, reachMetres, exaggeration, exaggerationBase);

    private void Invalidate(double minX, double minZ, double maxX, double maxZ)
    {
        if (heightfield is not null) engine.Heightfield.Invalidate(new HeightfieldInvalidateRequest(heightfield, minX, minZ, maxX, maxZ));
    }

    private void Link(Camera? view)
    {
        bool want = view is not null;
        if (want == shown && ReferenceEquals(view, linked)) return;
        if (shown && linked is not null) engine.CameraView.ClearBackdrop(new(linked));
        if (view is not null)
        {
            // Anchored at the world's origin in the walking session: the backdrop's origin is the map's,
            // so a map point stands at its world position divided by the scale, rebase or not.
            engine.CameraView.SetBackdrop(new(view, walking(), new WorldOriginGlobalPosition(0, 0, 0, 0, 0, 0), BackdropOrigin, (float)Scale));
        }

        linked = view;
        shown = want;
        linkChanges++;
    }

    private int sinkMoves;
    private double exposedChange;

    /// <summary>
    /// The live check of R9779-2: when the sunk zone moves, the largest change in sink over ground
    /// beyond the far field's reach about the player (which they can see). It should stay zero.
    /// </summary>
    private void MeasureExposedChange((double X, double Z) from, (double X, double Z) to)
    {
        const double StepMetres = 32;
        double extent = reachMetres + HorizonSink.FollowMetres + FarField.ChunkMetres;
        for (double z = to.Z - extent; z <= to.Z + extent; z += StepMetres)
        for (double x = to.X - extent; x <= to.X + extent; x += StepMetres)
        {
            if (Math.Max(Math.Abs(x - to.X), Math.Abs(z - to.Z)) <= reachMetres) continue;
            double before = HorizonSink.At(Math.Max(Math.Abs(x - from.X), Math.Abs(z - from.Z)), reachMetres, FarField.ChunkMetres);
            double after = HorizonSink.At(Math.Max(Math.Abs(x - to.X), Math.Abs(z - to.Z)), reachMetres, FarField.ChunkMetres);
            exposedChange = Math.Max(exposedChange, Math.Abs(after - before));
        }

        sinkMoves++;
    }

    /// <summary>Whether every tile the rings want is drawn and nothing waits to be sampled or committed.</summary>
    internal bool Settled => heightfield is not null && streamer is not null && streamer.InFlight == 0
        && engine.Heightfield.Read(heightfield).Settled;

    internal string Readout()
    {
        string groundReadout = "ground=none";
        if (heightfield is not null && streamer is not null)
        {
            HeightfieldReadout read = engine.Heightfield.Read(heightfield);
            groundReadout = string.Create(CultureInfo.InvariantCulture,
                $"tiles={read.DrawnTiles}/{read.ResidentTiles}+{read.PendingTiles}pending vertices={read.Vertices} sampleMB={read.SampleBytes / 1048576.0:F1} meshMB={read.MeshBytes / 1048576.0:F1} inFlight={streamer.InFlight} committed={streamer.Committed} ignored={streamer.Ignored} sampleMs={streamer.SampleMilliseconds:F0} focus={read.FocusX:F0},{read.FocusZ:F0}");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"horizon shown={shown} scale={Scale:F0}m {groundReadout} regionsReady={readyRegions.Count} {Weather?.Readout() ?? "fronts=off"} {Landmarks?.Readout() ?? "landmarks=off"} sinkMoves={sinkMoves} exposedChange={exposedChange:F3}m settled={Settled} sink={sinkCentre.X:F0},{sinkCentre.Z:F0} reach={reachMetres:F0}m links={linkChanges}");
    }

    /// <summary>
    /// For aimed captures: the highest ground on the map within <paramref name="kilometres"/> of the player,
    /// one per compass sector, with its height, distance and bearing (degrees clockwise from north, -Z).
    /// </summary>
    internal string Peaks(double kilometres)
    {
        const double StepMetres = 500;
        const int Sectors = 8;
        Vector3 at = playerWorld();
        double radius = kilometres * 1000;
        (double Height, double X, double Z)[] best = [.. Enumerable.Repeat((double.MinValue, 0d, 0d), Sectors)];
        for (double z = at.Z - radius; z <= at.Z + radius; z += StepMetres)
        for (double x = at.X - radius; x <= at.X + radius; x += StepMetres)
        {
            double dx = x - at.X, dz = z - at.Z;
            double distance = Math.Sqrt((dx * dx) + (dz * dz));
            if (distance > radius || distance < 2000 || Math.Abs(x) > map.Radius || Math.Abs(z) > map.Radius) continue;
            double bearing = ((Math.Atan2(dx, -dz) * 180 / Math.PI) + 360) % 360;
            int sector = (int)Math.Round(bearing / (360.0 / Sectors)) % Sectors;
            double height = map.Sample(x, z).Elevation;
            if (height > best[sector].Height) best[sector] = (height, x, z);
        }

        return string.Join(" | ", best.Select(peak => string.Create(CultureInfo.InvariantCulture,
            $"{peak.Height:F0}m at {peak.X:F0},{peak.Z:F0} {Math.Sqrt(Math.Pow(peak.X - at.X, 2) + Math.Pow(peak.Z - at.Z, 2)) / 1000:F1}km bearing {((Math.Atan2(peak.X - at.X, -(peak.Z - at.Z)) * 180 / Math.PI) + 360) % 360:F0}")))
            + string.Create(CultureInfo.InvariantCulture, $" | here {map.Sample(at.X, at.Z).Elevation:F0}m");
    }

    /// <summary>The backdrop's ground at a world point, as the ring covering it there draws it, sunk as that is: where a place on the horizon stands.</summary>
    internal double GroundMetres(double worldX, double worldZ)
    {
        Vector3 at = playerWorld();
        return ground.GroundMetres(worldX, worldZ, (at.X, at.Z));
    }

    public void Dispose()
    {
        if (shown && linked is not null)
        {
            engine.CameraView.ClearBackdrop(new(linked));
            shown = false;
        }

        Release();
        material.Dispose();
        Weather?.Dispose();
        Landmarks?.Dispose();
    }
}
