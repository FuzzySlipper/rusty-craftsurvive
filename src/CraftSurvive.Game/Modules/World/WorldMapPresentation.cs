using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>A coarse geographic mesh and its overview camera, entirely realized by Engine services.</summary>
internal sealed class WorldMapPresentation : IDisposable
{
    private const float MapWidth = 100;
    /// <summary>Relief is drawn taller than true scale so a 300 m range reads on a 10 km sheet.</summary>
    private const float VerticalExaggeration = 3;
    private const int MaximumMeshSegments = 256;
    private static readonly Color RiverColor = new(0.22f, 0.42f, 0.72f, 1);
    private const float MapY = -10000;
    private const float EyeHeight = 112;
    private const float EyeBack = 65;
    private const float Pitch = -60;
    private const double FieldOfView = 55;
    private const double Near = 0.1;
    private const double Far = 300;
    private const uint MaterialSlot = 0;
    private readonly IEngineContext engine;
    private readonly Material material;
    private readonly MeshResource mesh;
    private readonly Appearance appearance;
    private readonly Camera camera;
    private readonly Appearance marker;
    private readonly WorldMap map;

    internal WorldMapPresentation(IEngineContext engine, WorldMap map)
    {
        this.engine = engine;
        this.map = map;
        // Large maps are shown at a bounded vertex count; a river anywhere in a block marks it.
        int stride = Math.Max(1, (map.Segments + MaximumMeshSegments - 1) / MaximumMeshSegments);
        int segments = map.Segments / stride, side = segments + 1;
        Vector3[] positions = new Vector3[side * side];
        Vector3[] normals = new Vector3[positions.Length];
        Vector2[] uvs = new Vector2[positions.Length];
        Color[] colors = new Color[positions.Length];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int i = z * side + x, node = z * stride * map.Side + x * stride;
            MapSample sample = map.Node(node);
            positions[i] = Point(map.Coordinate(x * stride), sample.Elevation, map.Coordinate(z * stride));
            uvs[i] = new(x / (float)segments, z / (float)segments);
            colors[i] = RiverIn(map, x * stride, z * stride, stride) ? RiverColor : Tint(sample);
        }
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            Vector3 dx = positions[z * side + Math.Min(segments, x + 1)] - positions[z * side + Math.Max(0, x - 1)];
            Vector3 dz = positions[Math.Min(segments, z + 1) * side + x] - positions[Math.Max(0, z - 1) * side + x];
            normals[z * side + x] = Vector3.Normalize(Vector3.Cross(dz, dx));
        }
        List<uint> indices = [];
        for (int z = 0; z < segments; z++)
        for (int x = 0; x < segments; x++)
        {
            uint a = (uint)(z * side + x), b = a + 1, c = a + (uint)side, d = c + 1;
            indices.AddRange([a, c, b, b, c, d]);
        }
        Color white = new(1, 1, 1, 1);
        material = engine.Graphics.CreateMaterial(new MaterialRequest(white, default, 1, white, Vector3.Zero, 0, true));
        mesh = engine.Graphics.CreateMeshResource(new(positions, normals, uvs, colors, indices.ToArray(),
            new MeshGroup[] { new(MaterialSlot, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(MaterialSlot, material) }));
        appearance = engine.Graphics.CreateMeshAppearance(mesh);
        marker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, new Color(1, 0.38f, 0.1f, 1)));
        camera = engine.CameraView.CreateCamera(new(new CameraPose(new(0, MapY + EyeHeight, EyeBack), Pitch, 0), CameraBasisMode.Derived, default,
            new(CameraProjectionKind.Perspective, FieldOfView, 0, Near, Far), CameraViewports.Full));
    }

    internal AppearanceFact[] Facts => [new(ProductIds.WorldMapObject, false, 0,
        new(Vector3.Zero, Quaternion.Identity, Vector3.One), appearance, true, RenderLayer.Scene),
        .. map.Sites.Select((site, i) => new AppearanceFact(ProductIds.WorldMapSiteBase + (ulong)i, false, 0,
            new(Point(site.X, site.Geography.Elevation, site.Z) + Vector3.UnitY, Quaternion.Identity, Vector3.One), marker, true, RenderLayer.Scene))];

    internal void Activate() => engine.CameraView.SetActiveCamera(camera);

    private Vector3 Point(double x, double elevation, double z) => new(
        (float)(x / map.Configuration.Size * MapWidth),
        MapY + (float)(elevation / map.Configuration.Size * MapWidth) * VerticalExaggeration,
        (float)(z / map.Configuration.Size * MapWidth));

    private static bool RiverIn(WorldMap map, int x, int z, int stride)
    {
        int reach = stride / 2;
        for (int dz = -reach; dz <= reach; dz++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            int nx = Math.Clamp(x + dx, 0, map.Segments), nz = Math.Clamp(z + dz, 0, map.Segments);
            int node = nz * map.Side + nx;
            if (map.Fields.Elevation[node] >= GenerationConstants.WaterLevel
                && MapRivers.CatchmentSquareKilometres(map.Grid, map.Fields.Discharge[node]) >= MapRivers.SourceCatchment) return true;
        }
        return false;
    }

    private static Color Tint(MapSample s)
    {
        Color baseColor = WorldMap.Biome(s) switch
        {
            MapBiome.Sea => new(0.16f, 0.31f, 0.45f, 1),
            MapBiome.IceField => new(0.92f, 0.94f, 0.96f, 1),
            MapBiome.Tundra => new(0.66f, 0.68f, 0.62f, 1),
            MapBiome.BorealForest => new(0.24f, 0.37f, 0.31f, 1),
            MapBiome.ColdSteppe => new(0.55f, 0.55f, 0.43f, 1),
            MapBiome.TemperateForest => new(0.2f, 0.41f, 0.19f, 1),
            MapBiome.Grassland => new(0.43f, 0.55f, 0.27f, 1),
            MapBiome.Shrubland => new(0.67f, 0.62f, 0.35f, 1),
            MapBiome.Desert => new(0.84f, 0.71f, 0.47f, 1),
            MapBiome.Rainforest => new(0.12f, 0.35f, 0.18f, 1),
            _ => new(0.52f, 0.5f, 0.48f, 1),
        };
        const float RockBlend = 0.45f;
        Color stone = new(0.5f, 0.48f, 0.46f, 1);
        float r = (float)s.Rock * RockBlend;
        return new(baseColor.R + (stone.R - baseColor.R) * r, baseColor.G + (stone.G - baseColor.G) * r,
            baseColor.B + (stone.B - baseColor.B) * r, 1);
    }

    public void Dispose()
    {
        camera.Dispose(); marker.Dispose(); appearance.Dispose(); mesh.Dispose(); material.Dispose();
    }
}
