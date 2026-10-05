using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// Prototype faceted map view (#9436): the generated map voxelized as coarse dual-contoured
/// terrain in its own spatial session, one voxel per <see cref="CellMetres"/> of map. It samples
/// the same <see cref="WorldMap"/> walking terrain does, so the two agree by construction. Rivers
/// narrower than a voxel are painted onto the surface rather than carved. It stands apart from
/// the mesh overview with its own camera, so the two can be compared by switching cameras.
/// </summary>
internal sealed class WorldMapVoxelView : IDisposable
{
    internal const double CellMetres = 32;
    private const double VoxelSize = 1;
    private const int OriginY = -20000;
    private const int ChunksPerUpdate = 24;
    private const float MinimumDensity = 0.001f;
    private const double ExposedRock = 0.6;
    private const float GroundCreaseDegrees = 40f;
    private const float GroundRoughness = 0f;
    private const float MaterialRoughness = 1f;
    private const float MarkerScale = 3;
    private const float MarkerLift = 2;
    private const int EdgeLength = TerrainConstants.ChunkEdgeLength;
    private const uint RiverSlot = 1;
    private const uint RockSlot = 2;
    private const uint FirstBiomeSlot = 3;

    private readonly IEngineContext engine;
    private readonly WorldMap map;
    private readonly int cells;
    private readonly double[] surface;
    private readonly uint[] columnMaterial;
    private readonly Queue<(long X, long Y, long Z)> pending = new();
    private readonly Dictionary<uint, Material> materials = [];
    private readonly Appearance marker;
    private readonly Camera camera;
    private readonly int totalChunks;
    private VoxelScenePresentation? projection;
    private long workTicks;
    private long startedAt;
    private double loadMilliseconds;

    internal WorldMapVoxelView(IEngineContext engine, WorldMap map)
    {
        this.engine = engine;
        this.map = map;
        cells = Math.Max(1, (int)Math.Round(map.Configuration.Size / CellMetres));
        Session = engine.Spatial.CreateSession(new SpatialSessionConfig(VoxelSize, TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.DualContouring));
        try
        {
            uint[] slots = [RiverSlot, RockSlot, .. Enum.GetValues<MapBiome>().Select(Slot)];
            engine.Voxel.ConfigureMaterialSurfaces(new VoxelMaterialSurfaceRequest(Session, VoxelSurfaceMode.DualContouring,
                slots.Select(slot => new VoxelMaterialSurface(slot, VoxelSurfaceMode.DualContouring,
                    new SurfaceCharacter(VertexPlacement.Sharp, GroundCreaseDegrees, GroundRoughness))).ToArray()));
            engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(Session,
                slots.Select(slot => new VoxelMaterialCollision(slot, false)).ToArray()));
            engine.Voxel.ConfigureMaterialOcclusion(new VoxelMaterialOcclusionRequest(Session,
                slots.Select(slot => new VoxelMaterialOcclusion(slot, true)).ToArray()));
            materials[RiverSlot] = Flat(MapPalette.River);
            materials[RockSlot] = Flat(MapPalette.Stone);
            foreach (MapBiome biome in Enum.GetValues<MapBiome>()) materials[Slot(biome)] = Flat(MapPalette.For(biome));
            marker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, new Color(1, 0.38f, 0.1f, 1)));
        }
        catch
        {
            foreach (Material material in materials.Values) material.Dispose();
            Session.Dispose();
            throw;
        }

        // One sample per cell: a column's top (the sea's surface where the ground is below it)
        // and what it is made of. Elevation is exaggerated like the mesh overview.
        surface = new double[cells * cells];
        columnMaterial = new uint[cells * cells];
        double metresPerVoxel = CellMetres / WorldMapPresentation.VerticalExaggeration;
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            (double worldX, double worldZ) = CellCentre(x, z);
            MapSample sample = map.Sample(worldX, worldZ);
            double top = Math.Max(sample.Elevation, GenerationConstants.WaterLevel);
            surface[z * cells + x] = OriginY + top / metresPerVoxel;
            columnMaterial[z * cells + x] = sample.Elevation < GenerationConstants.WaterLevel ? Slot(MapBiome.Sea)
                : RiverCovers(worldX, worldZ) ? RiverSlot
                : sample.Rock >= ExposedRock ? RockSlot
                : Slot(WorldMap.Biome(sample));
        }

        // Only chunks a surface passes through, plus one beneath for footing.
        long half = cells / 2;
        for (long cz = Floor(-half); cz <= Floor(cells - 1 - half); cz++)
        for (long cx = Floor(-half); cx <= Floor(cells - 1 - half); cx++)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (long vz = cz * EdgeLength; vz < (cz + 1) * EdgeLength; vz++)
            for (long vx = cx * EdgeLength; vx < (cx + 1) * EdgeLength; vx++)
            {
                if (!TryColumn(vx, vz, out int column)) continue;
                low = Math.Min(low, surface[column]);
                high = Math.Max(high, surface[column]);
            }
            if (low == double.MaxValue) continue;
            for (long cy = Floor((long)Math.Floor(low)) - 1; cy <= Floor((long)Math.Ceiling(high)); cy++) pending.Enqueue((cx, cy, cz));
        }
        totalChunks = pending.Count;
        double width = cells * VoxelSize, scale = width / WorldMapPresentation.MapWidth;
        double eye = OriginY + GenerationConstants.WaterLevel / metresPerVoxel;
        camera = engine.CameraView.CreateCamera(new(new CameraPose(
                new(0, (float)(eye + WorldMapPresentation.EyeHeight * scale), (float)(WorldMapPresentation.EyeBack * scale)), WorldMapPresentation.Pitch, 0),
            CameraBasisMode.Derived, default,
            new(CameraProjectionKind.Perspective, WorldMapPresentation.FieldOfView, 0, WorldMapPresentation.Near * scale, WorldMapPresentation.Far * scale),
            CameraViewports.Full));
    }

    internal SpatialSession Session { get; }
    internal bool Loaded => projection is not null;
    internal string Readout => FormattableString.Invariant(
        $"cells={cells};cellMetres={CellMetres};chunks={totalChunks - pending.Count}/{totalChunks};loaded={Loaded};workMs={Stopwatch.GetElapsedTime(0, workTicks).TotalMilliseconds:F0};wallMs={loadMilliseconds:F0}");

    internal void Activate() => engine.CameraView.SetActiveCamera(camera);

    /// <summary>Site markers placed on the faceted relief.</summary>
    internal AppearanceFact[] Facts => [.. map.Sites.Select((site, i) => new AppearanceFact(ProductIds.WorldMapSiteBase + (ulong)i, false, 0,
        new(Position(site.X, site.Geography.Elevation, site.Z) + Vector3.UnitY * MarkerLift, Quaternion.Identity, Vector3.One * MarkerScale),
        marker, true, RenderLayer.Scene))];

    /// <summary>Admit a bounded batch of chunks; project the scene once the last has landed.</summary>
    internal void Advance()
    {
        if (Loaded) return;
        long started = Stopwatch.GetTimestamp();
        if (startedAt == 0) startedAt = started;
        List<VoxelResidencyOperation> operations = [];
        List<uint> chunkMaterials = [];
        List<float> densities = [];
        while (operations.Count < ChunksPerUpdate && pending.TryDequeue(out (long X, long Y, long Z) address))
        {
            uint offset = (uint)chunkMaterials.Count;
            // X-fastest, the Engine's dense payload order; density is signed distance in voxels.
            for (int z = 0; z < EdgeLength; z++)
            for (int y = 0; y < EdgeLength; y++)
            for (int x = 0; x < EdgeLength; x++)
            {
                long vx = address.X * EdgeLength + x, vy = address.Y * EdgeLength + y, vz = address.Z * EdgeLength + z;
                if (!TryColumn(vx, vz, out int column))
                {
                    chunkMaterials.Add(TerrainConstants.EmptyMaterial);
                    densities.Add(1);
                    continue;
                }
                float distance = (float)(vy + 0.5 - surface[column]);
                bool solid = distance < 0;
                chunkMaterials.Add(solid ? columnMaterial[column] : TerrainConstants.EmptyMaterial);
                densities.Add(solid ? Math.Min(distance, -MinimumDensity) : Math.Max(distance, MinimumDensity));
            }
            operations.Add(new(VoxelResidencyOperationKind.Admit, new(address.X, address.Y, address.Z),
                offset, TerrainConstants.ChunkVolume, offset, TerrainConstants.ChunkVolume));
        }
        if (operations.Count > 0)
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, Session,
                operations.ToArray(), chunkMaterials.ToArray(), densities.ToArray()));
        if (pending.Count == 0)
        {
            projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new(Session,
                materials.Select(pair => new VoxelSceneMaterialBinding(pair.Key, pair.Value)).ToArray(),
                ReadOnlyMemory<VoxelSceneFaceMaterialBinding>.Empty));
            loadMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
        workTicks += Stopwatch.GetTimestamp() - started;
    }

    public void Dispose()
    {
        projection?.Dispose();
        projection = null;
        camera.Dispose();
        marker.Dispose();
        foreach (Material material in materials.Values) material.Dispose();
        Session.Dispose();
    }

    private Vector3 Position(double worldX, double elevation, double worldZ)
    {
        double metresPerVoxel = CellMetres / WorldMapPresentation.VerticalExaggeration;
        return new((float)(worldX / CellMetres), (float)(OriginY + Math.Max(elevation, GenerationConstants.WaterLevel) / metresPerVoxel),
            (float)(worldZ / CellMetres));
    }

    private (double X, double Z) CellCentre(int x, int z) =>
        (-map.Radius + (x + 0.5) * CellMetres, -map.Radius + (z + 0.5) * CellMetres);

    /// <summary>The column index for a voxel position, with the map centred on voxel zero.</summary>
    private bool TryColumn(long vx, long vz, out int column)
    {
        long x = vx + cells / 2, z = vz + cells / 2;
        column = (int)(z * cells + x);
        return x >= 0 && z >= 0 && x < cells && z < cells;
    }

    /// <summary>A river is painted on a cell when its channel reaches within half a cell of the centre.</summary>
    private bool RiverCovers(double x, double z) =>
        map.Rivers.Nearest(x, z) is RiverInfluence river && river.Distance < river.HalfWidth + CellMetres / 2;

    private Material Flat(Color color) =>
        engine.Graphics.CreateMaterial(new MaterialRequest(color, default, MaterialRoughness, color, Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));

    private static uint Slot(MapBiome biome) => FirstBiomeSlot + (uint)biome;
    private static long Floor(long voxel) => GridMath.FloorDivide(voxel, EdgeLength);
}
