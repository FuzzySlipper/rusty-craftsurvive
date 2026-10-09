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
/// horizon in the direction it lies. One backdrop unit is one map cell of <see cref="CellMetres"/>; the
/// Engine's backdrop camera stands where the player stands, scaled down, so the whole map fits in a
/// few hundred units. The ground is a dual-contoured voxel session sampling <see cref="WorldMap"/> as
/// the map view does, sunk within the far field's reach so the 8 m far field covers the near range
/// and the backdrop takes over beyond it under the haze. On a continent it draws the kilometre tier.
/// It is linked to the first-person camera alone, so the map screen's camera never sees it, and is
/// unlinked while the player is in a separate space (a dungeon).
/// </summary>
internal sealed class HorizonBackdrop : IProductModule
{
    /// <summary>One backdrop unit: a map cell on an ordinary map, as the map view's coarse layer.</summary>
    internal const double CellMetres = 32;
    /// <summary>On a continent the backdrop draws the map view's kilometre tier.</summary>
    internal const double ContinentCellMetres = 1000;
    private const int EdgeLength = MapVoxelLayer.EdgeLength;
    /// <summary>The backdrop's ground is this far below the true ground within the far field's reach: below the far field's own error.</summary>
    private const double SinkMetres = 60;
    /// <summary>Past the far field's edge the backdrop rises back to its true height over this far.</summary>
    private const double RiseMetres = 384;
    /// <summary>The sunk zone moves onto the player once they have walked this far from its centre.</summary>
    private const double SinkFollowMetres = 256;
    /// <summary>How many backdrop chunks are admitted or replaced an update.</summary>
    private const int ChunksPerUpdate = 8;
    /// <summary>Chunks farther than this from the backdrop camera, in backdrop units, are drawn from coarse meshes.</summary>
    private const double CoarseBeyondUnits = 96;
    private const double ExposedRock = 0.6;
    private const double RiverReach = 0.5;
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
    private readonly double cellMetres;
    private readonly Dictionary<uint, Material> materials = [];
    private readonly MapVoxelLayer ground;
    private (double X, double Z) sinkCentre = (double.NaN, double.NaN);
    private double reachMetres;
    private Camera? linked;
    private bool shown;
    private long linkChanges;

    /// <param name="walking">The walking session: the backdrop's anchor is resolved in its frame, so a rebase moves nothing.</param>
    /// <param name="camera">The first-person camera, once it exists: the only view linked to the backdrop.</param>
    /// <param name="inTheWorld">Whether the player is in the open world, not a separate space such as a dungeon.</param>
    /// <param name="reach">The far field's reach from the player, in metres: the backdrop is sunk within it.</param>
    internal HorizonBackdrop(IEngineContext engine, WorldMap map, Func<SpatialSession> walking, Func<Vector3> playerWorld,
        Func<Camera?> camera, Func<bool> inTheWorld, Func<double> reach, Action<bool>? shownChanged = null)
    {
        this.shownChanged = shownChanged;
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        this.walking = walking ?? throw new ArgumentNullException(nameof(walking));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        this.inTheWorld = inTheWorld ?? throw new ArgumentNullException(nameof(inTheWorld));
        this.reach = reach ?? throw new ArgumentNullException(nameof(reach));
        cellMetres = map.Scale.Continental ? ContinentCellMetres : CellMetres;
        try
        {
            materials[RiverSlot] = Flat(MapPalette.River);
            materials[RockSlot] = Flat(MapPalette.Stone);
            foreach (MapBiome biome in Enum.GetValues<MapBiome>()) materials[Slot(biome)] = Flat(MapPalette.For(biome));
            ground = new MapVoxelLayer(engine, 1, Sample, materials, coarseBeyond: CoarseBeyondUnits, layer: RenderLayer.Backdrop);
            double chunkMetres = EdgeLength * cellMetres;
            long first = (long)Math.Floor(-map.Radius / chunkMetres), last = (long)Math.Floor((map.Radius - 1) / chunkMetres);
            ground.Want(from z in Range(first, last) from x in Range(first, last) select (x, z));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>For comparison captures: off unlinks the backdrop, leaving the far field over the panorama.</summary>
    internal bool Enabled { get; set; } = true;

    /// <summary>Metres per backdrop unit: the Engine draws the backdrop from the eye's position divided by this.</summary>
    internal double Scale => cellMetres;

    public void Start() => Follow();

    public void Update(ProductStep step) => Follow();

    public void Restart() => Follow();

    /// <summary>
    /// Keeps the backdrop with the player: sinks it under the far field about them, links it to the
    /// first-person camera while they are in the open world, and streams a bounded number of chunks.
    /// </summary>
    private void Follow()
    {
        Vector3 playerWorld = this.playerWorld();
        double reach = this.reach();
        if (reach != reachMetres || double.IsNaN(sinkCentre.X)
            || Math.Max(Math.Abs(playerWorld.X - sinkCentre.X), Math.Abs(playerWorld.Z - sinkCentre.Z)) >= SinkFollowMetres)
        {
            HashSet<(long X, long Z)> changed = double.IsNaN(sinkCentre.X) ? [] : [.. SunkColumns()];
            sinkCentre = (playerWorld.X, playerWorld.Z);
            reachMetres = reach;
            changed.UnionWith(SunkColumns());
            ground.Invalidate(changed);
        }

        ground.Advance(ChunksPerUpdate);
        Link(inTheWorld() && Enabled ? camera() : null);
        shownChanged?.Invoke(shown);
    }

    private void Link(Camera? camera)
    {
        bool want = camera is not null;
        if (want == shown && ReferenceEquals(camera, linked)) return;
        if (shown && linked is not null) engine.CameraView.ClearBackdrop(new(linked));
        if (camera is not null)
        {
            // Anchored at the world's origin in the walking session: the backdrop's origin is the map's,
            // so a map point stands at its world position divided by the scale, rebase or not.
            engine.CameraView.SetBackdrop(new(camera, walking(), new WorldOriginGlobalPosition(0, 0, 0, 0, 0, 0), BackdropOrigin, (float)cellMetres));
        }

        linked = camera;
        shown = want;
        linkChanges++;
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"horizon shown={shown} scale={cellMetres:F0}m chunks={ground.ResidentChunks}+{ground.PendingChunks}pending settled={ground.Settled} workMs={ground.WorkMilliseconds:F0} sink={sinkCentre.X:F0},{sinkCentre.Z:F0} reach={reachMetres:F0}m links={linkChanges}");

    public void Dispose()
    {
        if (shown && linked is not null)
        {
            engine.CameraView.ClearBackdrop(new(linked));
            shown = false;
        }

        ground?.Dispose();
        foreach (Material material in materials.Values) material.Dispose();
        materials.Clear();
    }

    /// <summary>The map's ground at its true height in backdrop units (one per cell), sunk about the player.</summary>
    private void Sample(long chunkX, long chunkZ, Span<double> surface, Span<uint> material)
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

            MapSample sample = map.Sample(worldX, worldZ);
            double top = Math.Max(sample.InRiver ? Math.Max(sample.Elevation, sample.RiverSurface) : sample.Elevation, GenerationConstants.WaterLevel);
            surface[i] = (top - Sink(worldX, worldZ)) / cellMetres;
            material[i] = sample.Elevation < GenerationConstants.WaterLevel ? Slot(MapBiome.Sea)
                : map.Rivers.Nearest(worldX, worldZ) is RiverInfluence river && river.Distance < river.HalfWidth + (cellMetres * RiverReach) ? RiverSlot
                : sample.Rock >= ExposedRock ? RockSlot
                : Slot(WorldMap.Biome(sample));
        }
    }

    /// <summary>How far the backdrop is sunk at a world point: fully within the far field's reach, rising past it.</summary>
    private double Sink(double worldX, double worldZ)
    {
        if (double.IsNaN(sinkCentre.X)) return 0;
        double away = Math.Max(Math.Abs(worldX - sinkCentre.X), Math.Abs(worldZ - sinkCentre.Z));
        double t = Math.Clamp((away - reachMetres) / RiseMetres, 0d, 1d);
        return SinkMetres * (1d - (t * t * (3d - (2d * t))));
    }

    /// <summary>The backdrop's chunk columns the sunk zone about its centre touches.</summary>
    private IEnumerable<(long X, long Z)> SunkColumns()
    {
        double chunkMetres = EdgeLength * cellMetres, reach = reachMetres + RiseMetres + SinkFollowMetres;
        long minX = (long)Math.Floor((sinkCentre.X - reach) / chunkMetres), maxX = (long)Math.Floor((sinkCentre.X + reach) / chunkMetres);
        long minZ = (long)Math.Floor((sinkCentre.Z - reach) / chunkMetres), maxZ = (long)Math.Floor((sinkCentre.Z + reach) / chunkMetres);
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
