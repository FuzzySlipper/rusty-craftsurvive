using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The overgrowth drawn (#10096): each kind's prop mesh (content/game/models/dungeon-overgrowth, painted by the texturing
/// pillar) loaded once, one appearance per kind, an appearance fact for every placement - instanced, as the kit is - under
/// the kit stone shader. Nothing collides. The space owns this for as long as it is loaded.
/// </summary>
internal sealed class DungeonOvergrowth : IDisposable
{
    private const string ShaderPath = "shaders/kit-stone.wgsl";

    /// <summary>Wet leaves are a little less matte than the stone.</summary>
    private const float Roughness = 0.75f;

    /// <summary>As the kit's textures: 512 texels across (32 texels/m over the 16 m an atlas spans); the giant vents' are 1024 (overgrowth_paint.sh).</summary>
    private const float TextureTexels = 512f, GiantTextureTexels = 1024f;

    // kit-stone.wgsl's stain: none on the leaves (their tint is white, so no dampness either), as the kit's facing limits.
    private const float MossStrength = 0f, StainDepth = 1f, SideShare = 0.85f, LowestFacing = -0.25f;

    /// <summary>How far an overgrowth is turned about its surface's normal, at most, either way (radians), so repeats differ.</summary>
    private const float MostTwist = 0.12f;

    private readonly IEngineContext engine;
    private readonly RenderResource shader;
    private readonly List<RenderResource> textures = [];
    private readonly List<Material> materials = [];
    private readonly Dictionary<OvergrowthKind, (MeshResource Mesh, Appearance Look)> kinds = [];
    private readonly List<(OvergrowthKind Kind, Transform Place)> placed = [];

    internal DungeonOvergrowth(IEngineContext engine, ProductContent content, IReadOnlyList<OvergrowthPlacement> placements)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(content);
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
        foreach (OvergrowthKind kind in placements.Select(p => p.Kind).Distinct())
        {
            (MeshResourceCreateRequest model, string[] parts) = Model(content, ModelPath(kind));
            float texels = kind is OvergrowthKind.VentGiant or OvergrowthKind.VentGiantCluster ? GiantTextureTexels : TextureTexels;
            MeshMaterialBinding[] bindings = [.. parts.Select((texture, slot) => new MeshMaterialBinding((uint)slot, Material(texture, texels)))];
            MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(model.Positions, model.Normals, model.Uvs, model.Colors,
                model.Indices, model.Groups, bindings));
            kinds[kind] = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
        }

        foreach (OvergrowthPlacement p in placements.Take(ProductIds.DungeonOvergrowthObjects))
        {
            placed.Add((p.Kind, new Transform(DungeonCollision.InSession(p.At), Turn(p), new Vector3(p.Scale))));
        }
    }

    /// <summary>
    /// How a placement is turned: its frame's +z to its outward and +y to its up - a floor piece square to its up and turned
    /// about it, a wall piece facing its outward exactly - and twisted a little about its up by its pick.
    /// </summary>
    private static Quaternion Turn(OvergrowthPlacement p)
    {
        Vector3 up = Vector3.Normalize(p.Up ?? Vector3.UnitY), facing = Vector3.Normalize(p.Out);
        Vector3 y, z;
        if (OvergrowthPlacement.OnFloor(p.Kind) || MathF.Abs(Vector3.Dot(up, facing)) > 0.98f)
        {
            y = up;
            Vector3 flat = facing - (y * Vector3.Dot(facing, y));
            z = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.Normalize(Vector3.Cross(Vector3.UnitX, y));
        }
        else
        {
            z = facing;
            y = Vector3.Normalize(up - (z * Vector3.Dot(up, z)));
        }

        // System.Numerics transforms row vectors: the matrix's rows are where the frame's axes go.
        Vector3 x = Vector3.Cross(y, z);
        Quaternion frame = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f));
        // Twisted about its up: a drape keeps hanging and a mound keeps its foot on the floor.
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(y, (p.Pick - 0.5f) * 2f * MostTwist) * frame);
    }

    /// <summary>A kind's prop mesh.</summary>
    internal static string ModelPath(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VineDrape => "models/dungeon-overgrowth/vine-drape.prop-mesh.json",
        OvergrowthKind.VineDrapeShort => "models/dungeon-overgrowth/vine-drape-short.prop-mesh.json",
        OvergrowthKind.IvyMound => "models/dungeon-overgrowth/ivy-mound.prop-mesh.json",
        OvergrowthKind.IvyClump => "models/dungeon-overgrowth/ivy-clump.prop-mesh.json",
        OvergrowthKind.RootTangle => "models/dungeon-overgrowth/root-tangle.prop-mesh.json",
        OvergrowthKind.GrassTuft => "models/dungeon-overgrowth/grass-tuft.prop-mesh.json",
        OvergrowthKind.GrassStrip => "models/dungeon-overgrowth/grass-strip.prop-mesh.json",
        OvergrowthKind.VentTall => "models/dungeon-overgrowth/vent-tall.prop-mesh.json",
        OvergrowthKind.VentCluster => "models/dungeon-overgrowth/vent-cluster.prop-mesh.json",
        OvergrowthKind.VentShort => "models/dungeon-overgrowth/vent-short.prop-mesh.json",
        OvergrowthKind.VentBed => "models/dungeon-overgrowth/vent-bed.prop-mesh.json",
        OvergrowthKind.VentGiant => "models/dungeon-overgrowth/vent-giant.prop-mesh.json",
        OvergrowthKind.VentGiantCluster => "models/dungeon-overgrowth/vent-giant-cluster.prop-mesh.json",
        _ => "models/dungeon-overgrowth/mushrooms.prop-mesh.json",
    };

    // Each kind's model parsed once for the process (as the kit's): every dungeon reuses its geometry with its own materials.
    private static readonly Dictionary<string, (MeshResourceCreateRequest Mesh, string[] Textures)> Models = [];

    private static (MeshResourceCreateRequest Mesh, string[] Textures) Model(ProductContent content, string path)
    {
        if (!Models.TryGetValue(path, out (MeshResourceCreateRequest Mesh, string[] Textures) model))
        {
            List<string> parts = [];
            MeshResourceCreateRequest mesh = PropMesh.Read(content, path, (role, texture) =>
            {
                parts.Add(texture ?? throw new InvalidOperationException($"Overgrowth model {path} has no texture for {role}."));
                return null!;
            });
            model = (mesh, [.. parts]);
            Models[path] = model;
        }

        return model;
    }

    /// <summary>A material for an overgrowth texture: nearest with mipmaps, under the kit stone shader at the kit's settings.</summary>
    private Material Material(string path, float texels)
    {
        RenderResource texture = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
        textures.Add(texture);
        DungeonKitLook.Look look = DungeonKitLook.Look.Default;
        Color white = new(1f, 1f, 1f, 1f);
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(white, texture, Roughness, white, Vector3.Zero, 0f, false)
        {
            Shader = new MaterialShader(shader, new Vector4(texels, look.FacetStrength, look.FacetSteps, look.FacetCoherence),
                new Vector4(look.CalmFrom, look.CalmFull, look.CalmMip, look.Calm), new Vector4(0f, 0f, 0f, MossStrength),
                new Vector4(StainDepth, SideShare, LowestFacing, 0f), texture, default(RenderResourceReference)),
        });
        materials.Add(material);
        return material;
    }

    /// <summary>Whether a dungeon loaded from now draws its overgrowth (a study's before and after; on by default).</summary>
    internal static bool Shown { get; set; } = true;

    internal int Count => placed.Count;

    /// <summary>How many of each kind are drawn, for readouts.</summary>
    internal string Counts => string.Join(" ", placed.GroupBy(p => p.Kind).Select(g => $"{g.Key}={g.Count()}"));

    internal IEnumerable<AppearanceFact> Facts => placed.Select((p, index) => new AppearanceFact(ProductIds.DungeonOvergrowth + (ulong)index, false, 0UL,
        p.Place, kinds[p.Kind].Look, true, RenderLayer.Scene, ShadowCasting.Cast));

    // The vent trunks' colliders (#10096): each trunk a closed prism of so many sides, as overgrowth_authored.py builds the
    // tubes (their feet and radii, metres, in the piece's frame); the plumes, beds and the rest do not collide. The asset and
    // instance ids, clear of the kit's and the rock's.
    private const int TrunkSides = 8;
    private const ulong TrunkAssetBase = 0x5000UL, TrunkInstanceBase = 0x30_0000UL;

    private static (float X, float Z, float Radius, float Height)[] Trunks(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VentTall => [(0f, 0f, 0.32f, 2.6f)],
        OvergrowthKind.VentCluster => [(0f, 0f, 0.33f, 3f), (0.42f, 0.2f, 0.27f, 2.1f), (-0.3f, 0.38f, 0.22f, 1.6f), (0.1f, -0.42f, 0.2f, 1.4f)],
        OvergrowthKind.VentGiant => [(0f, 0f, 1.1f, 10f)],
        OvergrowthKind.VentGiantCluster => [(0f, 0f, 1.15f, 11f), (1.6f, 0.8f, 0.95f, 8f), (-1.1f, 1.4f, 0.78f, 6f)],
        _ => [],
    };

    /// <summary>Whether any placement has a trunk that collides.</summary>
    internal bool Collides => placed.Any(p => Trunks(p.Kind).Length > 0);

    /// <summary>
    /// The vent trunks as the space's static collision, one asset per kind and an instance per placement, as the kit
    /// collides; only where nothing else holds the space's static collision (a natural cave's rock is voxels).
    /// </summary>
    internal void Collide(SpatialSession session)
    {
        List<OvergrowthKind> kinds = [.. placed.Select(p => p.Kind).Where(k => Trunks(k).Length > 0).Distinct()];
        List<Vector3> vertices = [];
        List<Triangle> triangles = [];
        List<StaticMeshAsset> assets = [];
        foreach (OvergrowthKind kind in kinds)
        {
            uint firstVertex = (uint)vertices.Count, firstTriangle = (uint)triangles.Count;
            foreach ((float x, float z, float radius, float height) in Trunks(kind))
            {
                uint b = (uint)vertices.Count - firstVertex;
                for (int k = 0; k < TrunkSides; k++)
                {
                    float a = MathF.Tau * k / TrunkSides;
                    Vector3 rim = new(x + (radius * MathF.Cos(a)), 0f, z + (radius * MathF.Sin(a)));
                    vertices.Add(rim);
                    vertices.Add(rim + new Vector3(0f, height, 0f));
                }

                for (int k = 0; k < TrunkSides; k++)
                {
                    uint low = b + (uint)(2 * k), high = low + 1, nextLow = b + (uint)(2 * ((k + 1) % TrunkSides)), nextHigh = nextLow + 1;
                    triangles.Add(new Triangle(low, nextLow, nextHigh));
                    triangles.Add(new Triangle(low, nextHigh, high));
                    if (k > 0 && k < TrunkSides - 1)
                    {
                        // The caps, fans from the first rim vertex.
                        triangles.Add(new Triangle(b, nextLow, low));
                        triangles.Add(new Triangle(b + 1, high, nextHigh));
                    }
                }
            }

            assets.Add(new StaticMeshAsset(TrunkAssetBase + (ulong)assets.Count, firstVertex, (uint)vertices.Count - firstVertex, firstTriangle, (uint)triangles.Count - firstTriangle));
        }

        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(session, assets.ToArray(), vertices.ToArray(), triangles.ToArray(),
            placed.Where(p => Trunks(p.Kind).Length > 0)
                .Select((p, i) => new StaticMeshInstance(TrunkInstanceBase + (ulong)i, TrunkAssetBase + (ulong)kinds.IndexOf(p.Kind), p.Place)).ToArray()));
    }

    /// <summary>Disposes the meshes after the snapshot that last named them, then their materials.</summary>
    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in kinds.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        kinds.Clear();
        foreach (Material material in materials)
        {
            material.Dispose();
        }

        materials.Clear();
        foreach (RenderResource texture in textures)
        {
            texture.Dispose();
        }

        textures.Clear();
        shader.Dispose();
    }
}
