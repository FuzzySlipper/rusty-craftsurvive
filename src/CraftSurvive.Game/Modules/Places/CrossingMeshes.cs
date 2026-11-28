using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Building;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Horizon;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// Overworld river crossings as parametric textured bridge meshes with walkable static collision (#11186).
/// Replaces voxel <c>Planks</c>/<c>Cobblestone</c> fills so bridges orient perpendicularly across meandering
/// rivers at any yaw angle, keep continuous sloped ramps onto both riverbanks, and expose a single mesh
/// builder seam ready for authored bridge kit assets.
/// </summary>
internal sealed class CrossingMeshes : IDisposable
{
    private const float Roughness = 0.90f;
    private const double LayoutStepMetres = 32;
    private const double CollisionMetres = 64, CollisionStepMetres = 16;

    /// <summary>Vertex colour: white with alpha 0 (solid bridge parts never sway in wind).</summary>
    private static readonly Color SolidVertex = new(1f, 1f, 1f, 0f);

    /// <summary>Static collision id range, disjoint from built pieces, ice (0x4000_0000), deadwood (0x5000_0000), and places (0x6000_0000).</summary>
    private const ulong CollisionAssetBase = 0x7000_0000UL, CollisionInstanceBase = 0x7000_0000UL;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly CrossingPlacement placement;
    private readonly Func<double, double, double> groundTop;
    private readonly PropMaterials materials;

    private readonly Dictionary<string, BuiltBridge> cache = [];
    private readonly List<BuiltBridge> standing = [];
    private readonly List<BuiltBridge> retired = [];
    private readonly List<CrossingSite> gathered = [];
    private readonly HashSet<string> standingIds = [];

    private (double X, double Z) gatheredAt = (double.NaN, double.NaN);
    private double gatheredEdge = double.NaN;
    private (double X, double Z) laidOutAt = (double.NaN, double.NaN);
    private (double X, double Z) collidedAt = (double.NaN, double.NaN);
    private (long X, long Y, long Z) collidedOrigin;
    private ulong nextAssetId = CollisionAssetBase;
    private int layoutRevision, collidedRevision = -1;

    internal CrossingMeshes(IEngineContext engine, WorldFrame frame, CrossingPlacement placement, Func<double, double, double> groundTop)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.placement = placement ?? throw new ArgumentNullException(nameof(placement));
        this.groundTop = groundTop ?? throw new ArgumentNullException(nameof(groundTop));
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0f, 0f));
    }

    private sealed record BuiltBridge(
        string Id,
        MeshResource Mesh,
        Appearance Appearance,
        Vector3 WorldAnchor,
        Quaternion Rotation,
        Vector3[] CollisionPositions,
        Triangle[] CollisionTriangles,
        ulong AssetId);

    internal void Follow(Vector3 player, double nearEdgeMetres)
    {
        if (double.IsNaN(gatheredAt.X) || nearEdgeMetres != gatheredEdge
            || Math.Max(Math.Abs(player.X - gatheredAt.X), Math.Abs(player.Z - gatheredAt.Z)) >= LayoutStepMetres)
        {
            gatheredAt = (player.X, player.Z);
            gatheredEdge = nearEdgeMetres;
            gathered.Clear();
            placement.CollectSitesNear((long)Math.Floor(player.X), (long)Math.Floor(player.Z),
                (nearEdgeMetres + LayoutStepMetres) * Math.Sqrt(2), gathered);
        }

        List<CrossingSite> near = [];
        foreach (CrossingSite site in gathered)
        {
            double midX = ((site.FromX + site.ToX) * 0.5) + 0.5;
            double midZ = ((site.FromZ + site.ToZ) * 0.5) + 0.5;
            if (HorizonLandmarkRules.OnNearGround(midX - player.X, midZ - player.Z, nearEdgeMetres))
            {
                near.Add(site);
            }
        }

        if (!double.IsNaN(laidOutAt.X) && near.Count == standingIds.Count && near.All(site => standingIds.Contains(site.Id)))
        {
            return;
        }

        laidOutAt = (player.X, player.Z);
        HashSet<string> keep = [];
        standing.Clear();
        standingIds.Clear();
        foreach (CrossingSite site in near)
        {
            keep.Add(site.Id);
            standingIds.Add(site.Id);
            if (!cache.TryGetValue(site.Id, out BuiltBridge? built))
            {
                built = BuildBridge(site);
                cache[site.Id] = built;
            }

            standing.Add(built);
        }

        // Retire meshes that walked out of near range; dispose them after the next appearance snapshot drops them.
        List<string>? evict = null;
        foreach (string key in cache.Keys)
        {
            if (!keep.Contains(key)) (evict ??= []).Add(key);
        }

        if (evict is not null)
        {
            foreach (string key in evict)
            {
                retired.Add(cache[key]);
                cache.Remove(key);
            }
        }

        layoutRevision++;
    }

    internal IEnumerable<AppearanceFact> Facts => standing.Take((int)ProductIds.CrossingMeshLimit).Select((bridge, index) =>
        new AppearanceFact(ProductIds.CrossingMeshBase + (ulong)index, false, 0,
            new Transform(frame.ToLocal(bridge.WorldAnchor.X, bridge.WorldAnchor.Y, bridge.WorldAnchor.Z), bridge.Rotation, Vector3.One),
            bridge.Appearance, true, RenderLayer.Scene, ShadowCasting.Cast));

    internal bool CollisionStale(Vector3 player) =>
        collidedRevision != layoutRevision || collidedOrigin != frame.Origin || double.IsNaN(collidedAt.X)
        || Math.Max(Math.Abs(player.X - collidedAt.X), Math.Abs(player.Z - collidedAt.Z)) >= CollisionStepMetres;

    internal int AddCollision(Vector3 player, List<StaticMeshAsset> assets, List<Vector3> vertices, List<Triangle> triangles, List<StaticMeshInstance> instances)
    {
        collidedAt = (player.X, player.Z);
        collidedRevision = layoutRevision;
        collidedOrigin = frame.Origin;
        int added = 0;
        foreach (BuiltBridge bridge in standing)
        {
            if (Math.Max(Math.Abs(bridge.WorldAnchor.X - player.X), Math.Abs(bridge.WorldAnchor.Z - player.Z)) > CollisionMetres)
            {
                continue;
            }

            assets.Add(new StaticMeshAsset(bridge.AssetId, (uint)vertices.Count, (uint)bridge.CollisionPositions.Length,
                (uint)triangles.Count, (uint)bridge.CollisionTriangles.Length));
            vertices.AddRange(bridge.CollisionPositions);
            triangles.AddRange(bridge.CollisionTriangles);
            instances.Add(new StaticMeshInstance(CollisionInstanceBase + (ulong)added, bridge.AssetId,
                new Transform(frame.ToLocal(bridge.WorldAnchor.X, bridge.WorldAnchor.Y, bridge.WorldAnchor.Z), bridge.Rotation, Vector3.One)));
            added++;
        }

        return added;
    }

    internal void AfterAppearanceSnapshot()
    {
        if (retired.Count == 0) return;
        foreach (BuiltBridge bridge in retired)
        {
            bridge.Appearance.Dispose();
            bridge.Mesh.Dispose();
        }

        retired.Clear();
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture, $"crossings={standing.Count}");

    public void Dispose()
    {
        standing.Clear();
        standingIds.Clear();
        foreach (BuiltBridge bridge in cache.Values.Concat(retired))
        {
            bridge.Appearance.Dispose();
            bridge.Mesh.Dispose();
        }

        cache.Clear();
        retired.Clear();
        materials.Dispose();
    }

    /// <summary>
    /// Builds one bridge's textured render mesh and matching collision mesh from <see cref="CrossingLayout"/>.
    /// When authored bridge kit models land, this is the single seam to swap them in.
    /// </summary>
    private BuiltBridge BuildBridge(CrossingSite site)
    {
        double dx = site.ToX - site.FromX;
        double dz = site.ToZ - site.FromZ;
        double span = Math.Max(1.0, double.Hypot(dx, dz));
        double ux = dx / span, uz = dz / span;
        double nearToeX = site.FromX + 0.5 - (ux * CrossingLayout.RampRun);
        double nearToeZ = site.FromZ + 0.5 - (uz * CrossingLayout.RampRun);
        double farToeX = site.ToX + 0.5 + (ux * CrossingLayout.RampRun);
        double farToeZ = site.ToZ + 0.5 + (uz * CrossingLayout.RampRun);

        double nearTop = groundTop(nearToeX, nearToeZ);
        double farTop = groundTop(farToeX, farToeZ);
        CrossingBridgeGeometry geometry = CrossingLayout.Build(site, nearTop, farTop);

        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<uint> indices = [];
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];

        foreach (CrossingMaterial kind in (ReadOnlySpan<CrossingMaterial>)[CrossingMaterial.Planks, CrossingMaterial.Timber, CrossingMaterial.Masonry])
        {
            uint firstIndex = (uint)indices.Count;
            foreach (CrossingBox box in geometry.Boxes)
            {
                if (box.Material != kind) continue;
                BoxMesh.Add(new PieceBox(box.Centre, box.Half, box.Pitch), positions, normals, uvs, indices);
            }

            uint count = (uint)indices.Count - firstIndex;
            if (count == 0) continue;
            uint slot = (uint)groups.Count;
            groups.Add(new MeshGroup(slot, firstIndex, count));
            bindings.Add(new MeshMaterialBinding(slot, materials.For("solid", TextureFor(kind))));
        }

        Vector3[] posArray = positions.ToArray();
        Triangle[] triangles = new Triangle[indices.Count / 3];
        for (int i = 0; i < triangles.Length; i++)
        {
            triangles[i] = new Triangle(indices[3 * i], indices[(3 * i) + 1], indices[(3 * i) + 2]);
        }

        Color[] colors = new Color[posArray.Length];
        Array.Fill(colors, SolidVertex);
        MeshResourceCreateRequest request = new(posArray, normals.ToArray(), uvs.ToArray(), colors, indices.ToArray(),
            groups.ToArray(), bindings.ToArray());
        MeshResource mesh = EngineClock.Time(() => engine.Graphics.CreateMeshResource(request));
        Appearance appearance = EngineClock.Time(() => engine.Graphics.CreateMeshAppearance(mesh));
        return new BuiltBridge(site.Id, mesh, appearance, geometry.WorldAnchor, geometry.Rotation, posArray, triangles, nextAssetId++);
    }

    private static string TextureFor(CrossingMaterial material) => material switch
    {
        CrossingMaterial.Planks => PieceCatalog.Texture(PieceMaterial.Planks),
        CrossingMaterial.Timber => PieceCatalog.Texture(PieceMaterial.Timber),
        CrossingMaterial.Masonry => PieceCatalog.Texture(PieceMaterial.Masonry),
        _ => PieceCatalog.Texture(PieceMaterial.Timber),
    };
}
