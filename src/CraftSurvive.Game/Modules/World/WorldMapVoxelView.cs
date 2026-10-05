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
    private readonly Vector3 partyPosition;
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
        .. map.Sites.Select((site, i) => Marker(ProductIds.WorldMapSiteBase + (ulong)i,
            Surface(site.X, site.Z), MarkerScalePerDistance, marker))];

    /// <summary>Whether the camera has zoomed since the markers were last published.</summary>
    internal bool MarkersStale => rig.Distance != publishedDistance;

    private AppearanceFact Marker(ulong id, Vector3 ground, float scalePerDistance, Appearance appearance)
    {
        publishedDistance = rig.Distance;
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
