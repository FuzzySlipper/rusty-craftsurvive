using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>
/// The map on the skyline (#9751, H1 #9779): the world map's terrain, at its true heights, drawn in
/// the Engine's backdrop layer behind the first-person world, so a range on the map stands on the
/// horizon in the direction it lies. The Engine's backdrop camera stands where the player stands,
/// scaled down by <see cref="Scale"/>, so the whole map fits in a few hundred units.
/// <para>
/// The ground is dual-contoured voxel sessions sampling the map as the map view does, in tiers:
/// <list type="bullet">
/// <item>an ordinary map is one 32 m layer over the whole map;</item>
/// <item>a continent is a kilometre tier over the whole continent, with a 32 m window of region ground
/// about the player (about 8 km across, admitted as its region tiles are ready and easing onto the
/// kilometre tier over its outer band), the kilometre tier sinking beneath it.</item>
/// </list>
/// Every tier is sunk within the far field's reach, so the 8 m far field covers the near range and
/// the backdrop takes over beyond it under the haze.
/// </para>
/// It is linked to the first-person camera alone, so the map screen's camera never sees it, and is
/// unlinked while the player is in a separate space (a dungeon).
/// </summary>
internal sealed class HorizonBackdrop : IProductModule
{
    /// <summary>The fine tier's cell: the map view's coarse layer, a whole ordinary map's backdrop unit.</summary>
    internal const double CellMetres = 32;
    /// <summary>A continent's broad tier, and its backdrop unit: the map view's kilometre tier.</summary>
    internal const double ContinentCellMetres = 1000;
    private const int EdgeLength = MapVoxelLayer.EdgeLength;
    private const double CellChunkMetres = HorizonTiers.RegionChunkMetres;
    private const double ContinentChunkMetres = EdgeLength * ContinentCellMetres;
    /// <summary>How many backdrop chunks each tier admits or replaces an update.</summary>
    private const int ChunksPerUpdate = 8;
    /// <summary>Chunks farther than these from the backdrop camera, in metres, are drawn from coarse meshes.</summary>
    private const double CellCoarseBeyondMetres = 3_000, ContinentCoarseBeyondMetres = 60_000;
    /// <summary>A continent's region window: chunk columns of the fine tier each side of the player's (one is 512 m).</summary>
    private const int RegionRadiusChunks = 8;
    /// <summary>Chunk columns over which the window's ground eases onto the kilometre tier, so the tiers meet flush.</summary>
    private const int RegionFadeChunks = HorizonTiers.RegionFadeChunks;
    private const double RegionFadeMetres = HorizonTiers.RegionFadeMetres;
    /// <summary>The kilometre tier sinks this far beneath the window's ground past its fade band.</summary>
    private const double UnderRegionSinkMetres = 1_500;
    /// <summary>
    /// A continent's middle tier (#9822): 250 m cells with the erosion filter cut in, within this many chunk
    /// columns (4 km each, so about 64 km) of the player's, easing onto the kilometre tier over its outer band.
    /// </summary>
    internal const double MidCellMetres = 250;
    private const double MidChunkMetres = HorizonTiers.MidChunkMetres;
    private const int MidRadiusChunks = 16, MidFadeChunks = 2;
    private const double MidFadeMetres = MidFadeChunks * MidChunkMetres;
    private const double MidCoarseBeyondMetres = 16_000;
    /// <summary>The kilometre tier sinks this far beneath the middle tier past its fade band: below the deepest gully the filter cuts.</summary>
    private const double UnderMidSinkMetres = 2_500;
    /// <summary>The middle tier's base samples reach this many cells beyond a chunk, for the slope at its edge.</summary>
    private static readonly int SlopeCells = (int)Math.Round(ErodedHeights.SlopeReachMetres / MidCellMetres);
    /// <summary>Each tier samples for at most this long an update (#9822): sampling never stalls an update.</summary>
    private const double SampleBudgetMilliseconds = 2;
    private const double ExposedRock = 0.6;
    private const double RiverReach = 0.5, ContinentRiverReach = 0.71;
    private const uint RiverSlot = 1, RockSlot = 2, FirstBiomeSlot = 3;
    private static readonly Vector3 BackdropOrigin = Vector3.Zero;

    private readonly IEngineContext engine;
    private readonly WorldMap map;
    private readonly Func<SpatialSession> walking;
    private readonly Func<Vector3> playerWorld;
    private readonly Func<Camera?> camera;
    private readonly Func<bool> inTheWorld;
    private readonly Func<double> reach;
    private readonly Action<bool>? shownChanged;
    private readonly Dictionary<uint, Material> materials = [];
    /// <summary>The 32 m tier: the whole of an ordinary map, or a continent's region window.</summary>
    private MapVoxelLayer cells = null!;
    /// <summary>A continent's kilometre tier; null on an ordinary map.</summary>
    private MapVoxelLayer? continent;
    /// <summary>A continent's middle tier about the player, with the erosion filter (#9822); null on an ordinary map.</summary>
    private MapVoxelLayer? mid;
    private readonly ErodedHeights? eroded;
    private readonly long firstMidChunk, lastMidChunk;
    private Vector2 midMinimum, midMaximum;
    /// <summary>For measuring what the horizon holds: its sessions disposed until rebuilt.</summary>
    private bool released;
    private readonly MapRegions? regions;
    private readonly HashSet<(long X, long Z)> regionCovered = [];
    private readonly long firstCellChunk, lastCellChunk;
    private Vector2 regionMinimum, regionMaximum;
    private (double X, double Z) sinkCentre = (double.NaN, double.NaN);
    private double reachMetres;
    private Camera? linked;
    private bool shown;
    private long linkChanges;

    /// <param name="walking">The walking session: the backdrop's anchor is resolved in its frame, so a rebase moves nothing.</param>
    /// <param name="camera">The first-person camera, once it exists: the only view linked to the backdrop.</param>
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
        firstCellChunk = (long)Math.Floor(-map.Radius / CellChunkMetres);
        lastCellChunk = (long)Math.Floor((map.Radius - 1) / CellChunkMetres);
        firstMidChunk = (long)Math.Floor(-map.Radius / MidChunkMetres);
        lastMidChunk = (long)Math.Floor((map.Radius - 1) / MidChunkMetres);
        if (map.Scale.Continental)
            eroded = new(map, new ErosionFilter(map.Configuration.Contract.GeographyNoiseSeed, ErosionFilterSettings.Continent), ContinentDesign.For(map.Configuration.Size));
        try
        {
            materials[RiverSlot] = Flat(MapPalette.River);
            materials[RockSlot] = Flat(MapPalette.Stone);
            foreach (MapBiome biome in Enum.GetValues<MapBiome>()) materials[Slot(biome)] = Flat(MapPalette.For(biome));
            if (map.Scale.Continental) regions = MapRegions.For(map);
            BuildTiers();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private double exaggeration = 1;
    private double exaggerationBase;

    /// <summary>
    /// For aimed captures only (#9779): heights above the ground under the player are multiplied by
    /// this, so a gentle map's ranges read on the skyline beside the map view. Play is at true scale (1).
    /// Every streamed column is sampled again.
    /// </summary>
    internal string Exaggerate(double factor)
    {
        exaggeration = Math.Clamp(factor, 1, 20);
        Vector3 at = playerWorld();
        exaggerationBase = map.Sample(at.X, at.Z).Elevation;
        cells.Invalidate(map.Scale.Continental ? regionCovered : AllColumns(CellChunkMetres));
        continent?.Invalidate(AllColumns(ContinentChunkMetres));
        mid?.Invalidate(MidColumns(midMinimum, midMaximum));
        return string.Create(CultureInfo.InvariantCulture, $"exaggeration={exaggeration:F1} above {exaggerationBase:F0}m; ") + Readout();
    }

    private IEnumerable<(long X, long Z)> AllColumns(double chunkMetres)
    {
        long first = (long)Math.Floor(-map.Radius / chunkMetres), last = (long)Math.Floor((map.Radius - 1) / chunkMetres);
        return from z in Range(first, last) from x in Range(first, last) select (x, z);
    }

    /// <summary>Creates the tiers' sessions and asks for the ground they stream.</summary>
    private void BuildTiers()
    {
        cells = new MapVoxelLayer(engine, CellMetres / Scale, SampleCells, materials,
            coarseBeyond: CellCoarseBeyondMetres / Scale, layer: RenderLayer.Backdrop, sampleBudgetMilliseconds: SampleBudgetMilliseconds);
        if (map.Scale.Continental)
        {
            continent = new MapVoxelLayer(engine, ContinentCellMetres / Scale, SampleContinent, materials,
                coarseBeyond: ContinentCoarseBeyondMetres / Scale, layer: RenderLayer.Backdrop, sampleBudgetMilliseconds: SampleBudgetMilliseconds);
            continent.Want(AllColumns(ContinentChunkMetres));
            mid = new MapVoxelLayer(engine, MidCellMetres / Scale, SampleMid, materials,
                coarseBeyond: MidCoarseBeyondMetres / Scale, layer: RenderLayer.Backdrop, sampleBudgetMilliseconds: SampleBudgetMilliseconds);
        }
        else
        {
            cells.Want(from z in Range(firstCellChunk, lastCellChunk) from x in Range(firstCellChunk, lastCellChunk) select (x, z));
        }

        regionCovered.Clear();
        regionMinimum = regionMaximum = default;
        midMinimum = midMaximum = default;
        sinkCentre = (double.NaN, double.NaN);
    }

    /// <summary>
    /// For measuring what the horizon holds (R9779-1): disposes its sessions (the voxels, meshes and
    /// presentations) and unlinks it, or builds them again. The difference in the host's memory is
    /// the horizon's cost.
    /// </summary>
    internal string Hold(bool held)
    {
        if (held == !released) return Readout();
        if (!held)
        {
            Link(null);
            cells.Dispose();
            continent?.Dispose();
            continent = null;
            mid?.Dispose();
            mid = null;
            released = true;
            return "horizon released";
        }

        released = false;
        BuildTiers();
        return Readout();
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

    public void Start() => Follow();

    public void Update(ProductStep step) => Follow();

    public void Restart() => Follow();

    /// <summary>
    /// Keeps the backdrop with the player: moves a continent's region window, sinks the ground under
    /// the far field about them, links it to the first-person camera while they are in the open world,
    /// and streams a bounded number of chunks per tier.
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
        double reachNow = reach();
        CoverMid(at);
        CoverRegion(at);
        cells.Focus = ((long)Math.Floor(at.X / CellChunkMetres), (long)Math.Floor(at.Z / CellChunkMetres));
        if (mid is not null) mid.Focus = ((long)Math.Floor(at.X / MidChunkMetres), (long)Math.Floor(at.Z / MidChunkMetres));
        if (continent is not null) continent.Focus = ((long)Math.Floor(at.X / ContinentChunkMetres), (long)Math.Floor(at.Z / ContinentChunkMetres));
        if (reachNow != reachMetres || double.IsNaN(sinkCentre.X)
            || Math.Max(Math.Abs(at.X - sinkCentre.X), Math.Abs(at.Z - sinkCentre.Z)) >= HorizonSink.FollowMetres)
        {
            if (!double.IsNaN(sinkCentre.X) && reachNow == reachMetres) MeasureExposedChange(sinkCentre, (at.X, at.Z));
            HashSet<(long X, long Z)> cellsChanged = double.IsNaN(sinkCentre.X) ? [] : [.. SunkColumns(CellChunkMetres)];
            HashSet<(long X, long Z)> continentChanged = double.IsNaN(sinkCentre.X) ? [] : [.. SunkColumns(ContinentChunkMetres)];
            HashSet<(long X, long Z)> midChanged = double.IsNaN(sinkCentre.X) ? [] : [.. SunkColumns(MidChunkMetres)];
            sinkCentre = (at.X, at.Z);
            reachMetres = reachNow;
            cellsChanged.UnionWith(SunkColumns(CellChunkMetres));
            continentChanged.UnionWith(SunkColumns(ContinentChunkMetres));
            midChanged.UnionWith(SunkColumns(MidChunkMetres));
            cells.Invalidate(cellsChanged);
            continent?.Invalidate(continentChanged);
            mid?.Invalidate(midChanged);
        }

        cells.Advance(ChunksPerUpdate);
        mid?.Advance(ChunksPerUpdate);
        continent?.Advance(ChunksPerUpdate);
        Link(inTheWorld() && Enabled ? camera() : null);
        shownChanged?.Invoke(shown);
        Weather?.Update(shown);
        Weather?.UpdateBodies(Scale, shown);
        Landmarks?.Update(Scale, shown, reachMetres, GroundMetres);
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

    /// <summary>Each tier's digest of its sampled ground (R9822-1): equal for a horizon that followed the player and one built fresh where they stand.</summary>
    internal string Digest() => string.Create(CultureInfo.InvariantCulture,
        $"cells={cells.Digest():x16} mid={mid?.Digest() ?? 0:x16} continent={continent?.Digest() ?? 0:x16} settled={cells.Settled && (continent?.Settled ?? true) && (mid?.Settled ?? true)}");

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"horizon shown={shown} scale={Scale:F0}m cells={cells.ResidentChunks}+{cells.PendingChunks}pending")
        + (continent is null ? "" : string.Create(CultureInfo.InvariantCulture,
            $" continent={continent.ResidentChunks}+{continent.PendingChunks}pending regionColumns={regionCovered.Count}"))
        + (mid is null ? "" : string.Create(CultureInfo.InvariantCulture, $" mid={mid.ResidentChunks}+{mid.PendingChunks}pending"))
        + string.Create(CultureInfo.InvariantCulture,
            $" {Weather?.Readout() ?? "fronts=off"} {Landmarks?.Readout() ?? "landmarks=off"} sinkMoves={sinkMoves} exposedChange={exposedChange:F3}m settled={cells.Settled && (continent?.Settled ?? true) && (mid?.Settled ?? true)} workMs={cells.WorkMilliseconds + (continent?.WorkMilliseconds ?? 0) + (mid?.WorkMilliseconds ?? 0):F0} (cells {cells.WorkMilliseconds:F0} mid {mid?.WorkMilliseconds ?? 0:F0} continent {continent?.WorkMilliseconds ?? 0:F0}) sink={sinkCentre.X:F0},{sinkCentre.Z:F0} reach={reachMetres:F0}m links={linkChanges}");

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

    public void Dispose()
    {
        if (shown && linked is not null)
        {
            engine.CameraView.ClearBackdrop(new(linked));
            shown = false;
        }

        if (!released)
        {
            cells?.Dispose();
            continent?.Dispose();
            mid?.Dispose();
        }
        foreach (Material material in materials.Values) material.Dispose();
        materials.Clear();
        Weather?.Dispose();
        Landmarks?.Dispose();
    }

    /// <summary>
    /// A continent's 32 m window about the player: every chunk column whose region tiles are built is
    /// admitted, the rest are prefetched, and the kilometre tier is re-sunk where the cover changed.
    /// </summary>
    private void CoverRegion(Vector3 at)
    {
        if (regions is null || continent is null) return;
        long span = 2 * RegionRadiusChunks;
        long maximumStart = Math.Max(firstCellChunk, lastCellChunk - span + 1);
        long x0 = Math.Clamp((long)Math.Floor(at.X / CellChunkMetres) - RegionRadiusChunks, firstCellChunk, maximumStart);
        long z0 = Math.Clamp((long)Math.Floor(at.Z / CellChunkMetres) - RegionRadiusChunks, firstCellChunk, maximumStart);
        long x1 = Math.Min(lastCellChunk, x0 + span - 1), z1 = Math.Min(lastCellChunk, z0 + span - 1);
        Vector2 minimum = new((float)(x0 * CellChunkMetres), (float)(z0 * CellChunkMetres));
        Vector2 maximum = new((float)((x1 + 1) * CellChunkMetres), (float)((z1 + 1) * CellChunkMetres));
        (Vector2 oldMinimum, Vector2 oldMaximum) = (regionMinimum, regionMaximum);
        bool moved = minimum != regionMinimum || maximum != regionMaximum;
        if (moved)
        {
            // The window moved: the bands beside both its old and new edges carry a changed fade.
            (regionMinimum, regionMaximum) = (minimum, maximum);
            cells.Invalidate(FadeBand(oldMinimum, oldMaximum, CellChunkMetres, RegionFadeChunks).Concat(FadeBand(minimum, maximum, CellChunkMetres, RegionFadeChunks)));
        }

        regions.Prefetch(minimum.X, minimum.Y, maximum.X, maximum.Y);
        HashSet<(long X, long Z)> covered = [];
        for (long cz = z0; cz <= z1; cz++)
        for (long cx = x0; cx <= x1; cx++)
        {
            if (regions.Ready(cx * CellChunkMetres, cz * CellChunkMetres, (cx + 1) * CellChunkMetres, (cz + 1) * CellChunkMetres)) covered.Add((cx, cz));
        }

        bool recovered = !covered.SetEquals(regionCovered);
        // The middle tier sinks beneath the window (R9822-1): whatever moved it or changed its coverage, every
        // middle column under the old window or the new one is sampled again.
        if (moved || recovered) mid?.Invalidate(HorizonTiers.MidColumnsUnder(oldMinimum, oldMaximum).Concat(HorizonTiers.MidColumnsUnder(minimum, maximum)));
        if (!recovered) return;
        HashSet<(long X, long Z)> changed = [.. covered];
        changed.SymmetricExceptWith(regionCovered);
        regionCovered.Clear();
        regionCovered.UnionWith(covered);
        cells.Want(covered);
        continent.Invalidate(changed.Select(column => (
            (long)Math.Floor(column.X * CellChunkMetres / ContinentChunkMetres), (long)Math.Floor(column.Z * CellChunkMetres / ContinentChunkMetres))));
    }

    /// <summary>The 32 m tier: an ordinary map's ground, or a continent's region ground easing onto the kilometre tier at the window's edge.</summary>
    private void SampleCells(long chunkX, long chunkZ, Span<double> surface, Span<uint> material) =>
        Sample(chunkX, chunkZ, CellMetres, surface, material,
            regions is null ? map.Sample : RegionGround,
            regions is null ? map.Rivers.Nearest : regions.RiverNear, RiverReach, sunkUnderRegion: false);

    /// <summary>A continent's kilometre tier, sunk beneath the region window past its fade band.</summary>
    private void SampleContinent(long chunkX, long chunkZ, Span<double> surface, Span<uint> material) =>
        Sample(chunkX, chunkZ, ContinentCellMetres, surface, material, map.Sample, map.Rivers.Nearest, ContinentRiverReach, sunkUnderRegion: true);

    /// <summary>A tier's ground at its true height, in that tier's voxels, sunk about the player.</summary>
    private void Sample(long chunkX, long chunkZ, double cellMetres, Span<double> surface, Span<uint> material,
        Func<double, double, MapSample> geography, Func<double, double, RiverInfluence?> rivers, double riverReach, bool sunkUnderRegion)
    {
        for (int z = 0; z < EdgeLength; z++)
        for (int x = 0; x < EdgeLength; x++)
        {
            int i = (z * EdgeLength) + x;
            double worldX = ((chunkX * EdgeLength) + x) * cellMetres, worldZ = ((chunkZ * EdgeLength) + z) * cellMetres;
            if (Math.Abs(worldX) > map.Radius || Math.Abs(worldZ) > map.Radius)
            {
                surface[i] = double.NaN;
                continue;
            }

            MapSample sample = geography(worldX, worldZ);
            double top = Top(sample);
            double sink = Sink(worldX, worldZ) + (!sunkUnderRegion ? 0
                : mid is not null ? (UnderMid(worldX, worldZ) ? UnderMidSinkMetres : 0)
                : UnderRegion(worldX, worldZ) ? UnderRegionSinkMetres : 0);
            surface[i] = (top - sink) / cellMetres;
            material[i] = sample.Elevation < GenerationConstants.WaterLevel ? Slot(MapBiome.Sea)
                : rivers(worldX, worldZ) is RiverInfluence river && river.Distance < river.HalfWidth + (cellMetres * riverReach) ? RiverSlot
                : sample.Rock >= ExposedRock ? RockSlot
                : Slot(WorldMap.Biome(sample));
        }
    }

    /// <summary>The top of a map sample as the backdrop draws it: water's surface over the ground, and any exaggeration.</summary>
    private double Top(MapSample sample)
    {
        double top = Math.Max(sample.InRiver ? Math.Max(sample.Elevation, sample.RiverSurface) : sample.Elevation, GenerationConstants.WaterLevel);
        return exaggeration == 1 ? top : exaggerationBase + ((top - exaggerationBase) * exaggeration);
    }

    /// <summary>
    /// The backdrop's ground at a world point, as its visible tier draws it (a continent's region ground
    /// inside the window), sunk as that is: where a place on the horizon stands.
    /// </summary>
    internal double GroundMetres(double worldX, double worldZ)
    {
        if (regions is not null && InRegion(worldX, worldZ)) return Top(RegionGround(worldX, worldZ)) - Sink(worldX, worldZ);
        MapSample sample = map.Sample(worldX, worldZ);
        return Top(sample with { Elevation = MidGround(worldX, worldZ, sample) }) - Sink(worldX, worldZ);
    }

    /// <summary>A continent's region ground: the region tiles' geography, easing onto the continent's across the window's outer band.</summary>
    private MapSample RegionGround(double worldX, double worldZ)
    {
        if (!InRegion(worldX, worldZ)) return map.Sample(worldX, worldZ);
        MapSample region = regions!.Sample(worldX, worldZ);
        double fade = WorldMap.Smooth(Math.Clamp(Inset(worldX, worldZ) / RegionFadeMetres, 0, 1));
        if (fade >= 1) return region;
        // The window eases onto the ground the middle tier draws there (#9822), filter and all.
        double broad = MidGround(worldX, worldZ, map.Sample(worldX, worldZ));
        return region with { Elevation = broad + (fade * (region.Elevation - broad)) };
    }

    private double Inset(double worldX, double worldZ) =>
        Math.Min(Math.Min(worldX - regionMinimum.X, regionMaximum.X - worldX), Math.Min(worldZ - regionMinimum.Y, regionMaximum.Y - worldZ));

    /// <summary>Where the kilometre tier sinks: beneath region ground past the window's fade band.</summary>
    private bool UnderRegion(double worldX, double worldZ) => HorizonTiers.UnderRegion(regionCovered, regionMinimum, regionMaximum, worldX, worldZ);

    /// <summary>Where the kilometre tier sinks beneath the middle tier: inside its window past the fade band.</summary>
    private bool UnderMid(double worldX, double worldZ) => InMid(worldX, worldZ) && MidInset(worldX, worldZ) >= MidFadeMetres;

    private bool InRegion(double worldX, double worldZ) =>
        regionCovered.Contains(((long)Math.Floor(worldX / CellChunkMetres), (long)Math.Floor(worldZ / CellChunkMetres)));

    /// <summary>A window's chunk columns within the fade band inside its edges.</summary>
    private static IEnumerable<(long X, long Z)> FadeBand(Vector2 minimum, Vector2 maximum, double chunkMetres, int fadeChunks)
    {
        long x0 = (long)Math.Floor(minimum.X / chunkMetres), x1 = (long)Math.Floor(maximum.X / chunkMetres) - 1;
        long z0 = (long)Math.Floor(minimum.Y / chunkMetres), z1 = (long)Math.Floor(maximum.Y / chunkMetres) - 1;
        return from cz in Range(z0, z1)
               from cx in Range(x0, x1)
               where cx < x0 + fadeChunks || cx > x1 - fadeChunks || cz < z0 + fadeChunks || cz > z1 - fadeChunks
               select (cx, cz);
    }

    /// <summary>The middle tier's chunk columns within a window.</summary>
    private static IEnumerable<(long X, long Z)> MidColumns(Vector2 minimum, Vector2 maximum)
    {
        long x0 = (long)Math.Floor(minimum.X / MidChunkMetres), x1 = (long)Math.Floor(maximum.X / MidChunkMetres) - 1;
        long z0 = (long)Math.Floor(minimum.Y / MidChunkMetres), z1 = (long)Math.Floor(maximum.Y / MidChunkMetres) - 1;
        return from cz in Range(z0, z1) from cx in Range(x0, x1) select (cx, cz);
    }

    /// <summary>
    /// Keeps the middle tier's window about the player (#9822): new columns are wanted (and sampled within the
    /// budget, nearest first), the fade bands at the old and new edges are sampled again, and the kilometre
    /// tier beneath both windows is re-sunk.
    /// </summary>
    private void CoverMid(Vector3 at)
    {
        if (mid is null || continent is null) return;
        long span = 2 * MidRadiusChunks;
        long maximumStart = Math.Max(firstMidChunk, lastMidChunk - span + 1);
        long x0 = Math.Clamp((long)Math.Floor(at.X / MidChunkMetres) - MidRadiusChunks, firstMidChunk, maximumStart);
        long z0 = Math.Clamp((long)Math.Floor(at.Z / MidChunkMetres) - MidRadiusChunks, firstMidChunk, maximumStart);
        long x1 = Math.Min(lastMidChunk, x0 + span - 1), z1 = Math.Min(lastMidChunk, z0 + span - 1);
        Vector2 minimum = new((float)(x0 * MidChunkMetres), (float)(z0 * MidChunkMetres));
        Vector2 maximum = new((float)((x1 + 1) * MidChunkMetres), (float)((z1 + 1) * MidChunkMetres));
        if (minimum == midMinimum && maximum == midMaximum) return;
        (Vector2 oldMinimum, Vector2 oldMaximum) = (midMinimum, midMaximum);
        (midMinimum, midMaximum) = (minimum, maximum);
        mid.Want(MidColumns(minimum, maximum));
        mid.Invalidate(FadeBand(oldMinimum, oldMaximum, MidChunkMetres, MidFadeChunks).Concat(FadeBand(minimum, maximum, MidChunkMetres, MidFadeChunks)));
        continent.Invalidate(MidColumns(oldMinimum, oldMaximum).Concat(MidColumns(minimum, maximum))
            .Select(column => ((long)Math.Floor(column.X * MidChunkMetres / ContinentChunkMetres), (long)Math.Floor(column.Z * MidChunkMetres / ContinentChunkMetres))));
    }

    private bool InMid(double worldX, double worldZ) =>
        mid is not null && worldX >= midMinimum.X && worldX < midMaximum.X && worldZ >= midMinimum.Y && worldZ < midMaximum.Y;

    private double MidInset(double worldX, double worldZ) =>
        Math.Min(Math.Min(worldX - midMinimum.X, midMaximum.X - worldX), Math.Min(worldZ - midMinimum.Y, midMaximum.Y - worldZ));

    /// <summary>How much of the erosion filter the middle tier cuts in at a point: whole inside, easing out over its outer band.</summary>
    private double MidFilter(double worldX, double worldZ) =>
        InMid(worldX, worldZ) ? WorldMap.Smooth(Math.Clamp(MidInset(worldX, worldZ) / MidFadeMetres, 0, 1)) : 0;

    /// <summary>The ground the middle tier draws at a point (the map's, with the filter as far as it reaches there), for the tiers that meet it.</summary>
    private double MidGround(double worldX, double worldZ, MapSample sample)
    {
        double share = eroded is null ? 0 : MidFilter(worldX, worldZ);
        return share <= 0 ? sample.Elevation : sample.Elevation + (share * (eroded!.Height(worldX, worldZ, sample) - sample.Elevation));
    }

    /// <summary>
    /// The middle tier (#9822): the map's ground at 250 m with the erosion filter cut in, the slope taken from
    /// this chunk's own lattice of samples; easing onto the kilometre tier over its window's outer band, and
    /// sunk beneath the region window and about the player as the other tiers are.
    /// </summary>
    private void SampleMid(long chunkX, long chunkZ, Span<double> surface, Span<uint> material)
    {
        int border = SlopeCells, side = EdgeLength + (2 * border);
        MapSample[] lattice = new MapSample[side * side];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
            lattice[(z * side) + x] = map.Sample(((chunkX * EdgeLength) + x - border) * MidCellMetres, ((chunkZ * EdgeLength) + z - border) * MidCellMetres);

        for (int z = 0; z < EdgeLength; z++)
        for (int x = 0; x < EdgeLength; x++)
        {
            int i = (z * EdgeLength) + x;
            double worldX = ((chunkX * EdgeLength) + x) * MidCellMetres, worldZ = ((chunkZ * EdgeLength) + z) * MidCellMetres;
            if (Math.Abs(worldX) > map.Radius || Math.Abs(worldZ) > map.Radius)
            {
                surface[i] = double.NaN;
                continue;
            }

            int at = ((z + border) * side) + x + border;
            MapSample sample = lattice[at];
            RiverInfluence? nearest = map.Rivers.Nearest(worldX, worldZ);
            double share = MidFilter(worldX, worldZ);
            if (share > 0)
            {
                double slopeX = (lattice[at + border].Elevation - lattice[at - border].Elevation) / (2 * ErodedHeights.SlopeReachMetres);
                double slopeZ = (lattice[at + (border * side)].Elevation - lattice[at - (border * side)].Elevation) / (2 * ErodedHeights.SlopeReachMetres);
                sample = sample with { Elevation = sample.Elevation + (share * (eroded!.Height(worldX, worldZ, sample, slopeX, slopeZ, nearest) - sample.Elevation)) };
            }

            double sink = Sink(worldX, worldZ) + (UnderRegion(worldX, worldZ) ? UnderRegionSinkMetres : 0);
            surface[i] = (Top(sample) - sink) / MidCellMetres;
            material[i] = sample.Elevation < GenerationConstants.WaterLevel ? Slot(MapBiome.Sea)
                : nearest is RiverInfluence river && river.Distance < river.HalfWidth + (MidCellMetres * RiverReach) ? RiverSlot
                : sample.Rock >= ExposedRock ? RockSlot
                : Slot(WorldMap.Biome(sample));
        }
    }

    /// <summary>How far the backdrop is sunk at a world point: fully near the player, easing out inside what the far field always covers.</summary>
    private double Sink(double worldX, double worldZ)
    {
        if (double.IsNaN(sinkCentre.X)) return 0;
        double away = Math.Max(Math.Abs(worldX - sinkCentre.X), Math.Abs(worldZ - sinkCentre.Z));
        return HorizonSink.At(away, reachMetres, FarField.ChunkMetres);
    }

    /// <summary>A tier's chunk columns the sunk zone about its centre touches.</summary>
    private IEnumerable<(long X, long Z)> SunkColumns(double chunkMetres)
    {
        double extent = HorizonSink.CoveredMetres(reachMetres, FarField.ChunkMetres);
        long minX = (long)Math.Floor((sinkCentre.X - extent) / chunkMetres), maxX = (long)Math.Floor((sinkCentre.X + extent) / chunkMetres);
        long minZ = (long)Math.Floor((sinkCentre.Z - extent) / chunkMetres), maxZ = (long)Math.Floor((sinkCentre.Z + extent) / chunkMetres);
        for (long z = minZ; z <= maxZ; z++)
        for (long x = minX; x <= maxX; x++)
            yield return (x, z);
    }

    private Material Flat(Color color) =>
        engine.Graphics.CreateMaterial(new MaterialRequest(color, default, 1f, color, Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));

    private static uint Slot(MapBiome biome) => FirstBiomeSlot + (uint)biome;

    private static IEnumerable<long> Range(long first, long last)
    {
        for (long value = first; value <= last; value++) yield return value;
    }
}
