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

    /// <summary>As the kit's textures: 512 texels across (32 texels/m over the 16 m an atlas spans).</summary>
    private const float TextureTexels = 512f;

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
            MeshMaterialBinding[] bindings = [.. parts.Select((texture, slot) => new MeshMaterialBinding((uint)slot, Material(texture)))];
            MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(model.Positions, model.Normals, model.Uvs, model.Colors,
                model.Indices, model.Groups, bindings));
            kinds[kind] = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
        }

        foreach (OvergrowthPlacement p in placements.Take(ProductIds.DungeonOvergrowthObjects))
        {
            float twist = (p.Pick - 0.5f) * 2f * MostTwist;
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(p.Out.X, p.Out.Z) + twist);
            placed.Add((p.Kind, new Transform(DungeonCollision.InSession(p.At), turn, new Vector3(p.Scale))));
        }
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
    private Material Material(string path)
    {
        RenderResource texture = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
        textures.Add(texture);
        DungeonKitLook.Look look = DungeonKitLook.Look.Default;
        Color white = new(1f, 1f, 1f, 1f);
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(white, texture, Roughness, white, Vector3.Zero, 0f, false)
        {
            Shader = new MaterialShader(shader, new Vector4(TextureTexels, look.FacetStrength, look.FacetSteps, look.FacetCoherence),
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
