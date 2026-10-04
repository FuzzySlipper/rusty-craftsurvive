using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>A coarse geographic mesh and its overview camera, entirely realized by Engine services.</summary>
internal sealed class WorldMapPresentation : IDisposable
{
    private const float MapWidth = 100;
    private const float HeightScale = 0.25f;
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
        int side = map.Side;
        Vector3[] positions = new Vector3[side * side];
        Vector3[] normals = new Vector3[positions.Length];
        Vector2[] uvs = new Vector2[positions.Length];
        Color[] colors = new Color[positions.Length];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int i = z * side + x;
            MapSample sample = map.Nodes[i];
            positions[i] = Point(map.Coordinate(x), sample.Elevation, map.Coordinate(z));
            normals[i] = Vector3.UnitY;
            uvs[i] = new(x / (float)map.Segments, z / (float)map.Segments);
            colors[i] = Tint(sample);
        }
        List<uint> indices = [];
        for (int z = 0; z < map.Segments; z++)
        for (int x = 0; x < map.Segments; x++)
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
        (float)(x / map.Configuration.Size * MapWidth), MapY + (float)elevation * HeightScale,
        (float)(z / map.Configuration.Size * MapWidth));

    private static Color Tint(MapSample s)
    {
        if (s.Elevation < GenerationConstants.WaterLevel) return new(0.18f, 0.35f, 0.42f, 1);
        Color baseColor = WorldMap.Frozen(s) ? new(0.72f, 0.81f, 0.82f, 1)
            : WorldMap.Arid(s) ? new(0.64f, 0.44f, 0.25f, 1) : new(0.39f, 0.48f, 0.31f, 1);
        const float RockDarkening = 0.28f;
        const float DrainageDarkening = 0.40f;
        float shade = 1 - (float)s.Rock * RockDarkening - (float)s.Drainage * DrainageDarkening;
        return new(baseColor.R * shade, baseColor.G * shade, baseColor.B * shade, 1);
    }

    public void Dispose()
    {
        camera.Dispose(); marker.Dispose(); appearance.Dispose(); mesh.Dispose(); material.Dispose();
    }
}
