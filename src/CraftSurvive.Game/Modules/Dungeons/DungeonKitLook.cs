using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The dungeon kit drawn and collided (#10089): each piece's prop mesh loaded once (content/game/models/dungeon-kit),
/// an appearance fact for every placement, and the placements as the space's static collision, each piece one asset
/// and every placement an instance of it, as the overworld's places collide. The space owns this for as long as it is
/// loaded.
/// </summary>
internal sealed class DungeonKitLook : IDisposable
{
    private const string Manifest = "models/dungeon-kit/kit.json";

    /// <summary>Kit stone is matte.</summary>
    private const float Roughness = 0.9f;

    /// <summary>The kit's collision asset and instance ids, clear of the rock's.</summary>
    private const ulong AssetBase = 0x4000UL, InstanceBase = 0x10_0000UL;

    private const string ShaderPath = "shaders/kit-stone.wgsl";

    /// <summary>
    /// The kit stone shader's settings (kit-stone.wgsl): the texture's width in texels, the texel facets' strength, snap
    /// steps and how much neighbours lean together; then where calming begins and is full (texels per screen pixel), the
    /// mip the neighbourhood's mean is read from, and how far fine contrast is turned down at full calm.
    /// </summary>
    internal readonly record struct Look(float FacetStrength, float FacetSteps, float FacetCoherence, float CalmFrom, float CalmFull, float CalmMip, float Calm)
    {
        internal static Look Default { get; } = new(0.6f, 0f, 0.4f, 0.35f, 1.2f, 2f, 0.7f);
    }

    /// <summary>The kit's textures are this many texels across (512 at 32 texels/m over the 16 m an atlas spans).</summary>
    private const float TextureTexels = 512f;

    private readonly IEngineContext engine;
    private readonly RenderResource shader;
    private readonly Dictionary<string, RenderResource> textures = [];
    private readonly List<(Material Material, RenderResource Texture)> materials = [];
    private Look look;
    private readonly Dictionary<KitPiece, (MeshResource Mesh, Appearance Look)> pieces = [];
    private readonly List<(KitPiece Piece, Transform Place)> placed = [];

    /// <param name="paint">A study's folder of the kit's textures beside its own (#10089: "plain", painted from the geometry), or null for its own.</param>
    /// <param name="look">The kit stone shader's settings (a study may tune them).</param>
    internal DungeonKitLook(IEngineContext engine, ProductContent content, IReadOnlyList<KitPlacement> placements, Look look, string? paint = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(content);
        this.look = look;
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
        using JsonDocument manifest = JsonDocument.Parse(content.ReadText(Manifest));
        Dictionary<string, JsonElement> entries = manifest.RootElement.GetProperty("pieces").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.Clone());
        foreach (KitPiece piece in placements.Select(p => p.Piece).Concat(placements.Select(p => DungeonKit.Collider(p.Piece))).Distinct())
        {
            JsonElement entry = entries[DungeonKit.Name(piece)];
            // A glowing piece (the relic's glow) draws in a plain emissive material of its own colour, not the stone.
            Vector3? glow = entry.TryGetProperty("emission", out JsonElement e)
                ? new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle()) : null;
            MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, entry.GetProperty("model").GetString()!, (role, texture) =>
                glow is Vector3 light ? Glowing(light)
                    : Material(Painted(texture ?? throw new InvalidOperationException($"Dungeon kit piece {piece} has no texture for {role}."), paint))));
            pieces[piece] = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
        }

        foreach (KitPlacement p in placements.Take(ProductIds.DungeonKitObjects))
        {
            (Vector3 at, Quaternion turn, Vector3 scale) = DungeonKit.Place(p);
            placed.Add((p.Piece, new Transform(at, turn, scale)));
        }
    }

    /// <summary>A material for a kit texture: the texture as its base colour, nearest with mipmaps, under the kit stone shader.</summary>
    private Material Material(string path)
    {
        if (!textures.TryGetValue(path, out RenderResource? texture))
        {
            texture = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
            textures[path] = texture;
        }

        Material material = engine.Graphics.CreateMaterial(Request(texture));
        materials.Add((material, texture));
        return material;
    }

    /// <summary>A plain emissive material, for a piece that is a light's visible source.</summary>
    private Material Glowing(Vector3 emission)
    {
        Color pale = new(emission.X, emission.Y, emission.Z, 1f);
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(pale, default(RenderResourceReference), 1f, pale, emission, GlowIntensity, false));
        glowing.Add(material);
        return material;
    }

    /// <summary>How brightly a glowing piece shines in its emission colour.</summary>
    private const float GlowIntensity = 2.5f;

    private readonly List<Material> glowing = [];

    private MaterialRequest Request(RenderResource texture)
    {
        Color white = new(1f, 1f, 1f, 1f);
        return new MaterialRequest(white, texture, Roughness, white, Vector3.Zero, 0f, false)
        {
            Shader = new MaterialShader(shader, new Vector4(TextureTexels, look.FacetStrength, look.FacetSteps, look.FacetCoherence),
                new Vector4(look.CalmFrom, look.CalmFull, look.CalmMip, look.Calm), Vector4.Zero, Vector4.Zero, texture, default(RenderResourceReference)),
        };
    }

    /// <summary>Retunes the kit stone shader on every piece now (a study).</summary>
    internal void Tune(Look next)
    {
        look = next;
        foreach ((Material material, RenderResource texture) in materials)
        {
            engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(material, Request(texture)));
        }
    }

    /// <summary>A kit texture's path in a study's paint folder (beside it, the same name), or as it is.</summary>
    private static string Painted(string texture, string? paint) =>
        paint is null ? texture : $"{Path.GetDirectoryName(texture)!.Replace('\\', '/')}/{paint}/{Path.GetFileName(texture)}";

    internal int Count => placed.Count;

    internal IEnumerable<AppearanceFact> Facts => placed.Select((p, index) => new AppearanceFact(ProductIds.DungeonKitObject + (ulong)index, false, 0UL,
        p.Place, pieces[p.Piece].Look, true, RenderLayer.Scene, ShadowCasting.Cast));

    /// <summary>The pieces that collide (<see cref="DungeonKit.Collides"/>) as static collision: one asset per piece, an instance per placement.</summary>
    internal void Collide(SpatialSession session)
    {
        List<KitPiece> kinds = [.. placed.Where(p => DungeonKit.Collides(p.Piece)).Select(p => DungeonKit.Collider(p.Piece)).Distinct()];
        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(
            session,
            kinds.Select((kind, i) => new StaticMeshAsset(AssetBase + (ulong)i, new MeshResourceReference(pieces[kind].Mesh), 0, 0, 0, 0)).ToArray(),
            ReadOnlyMemory<Vector3>.Empty,
            ReadOnlyMemory<Triangle>.Empty,
            placed.Where(p => DungeonKit.Collides(p.Piece))
                .Select((p, i) => new StaticMeshInstance(InstanceBase + (ulong)i, AssetBase + (ulong)kinds.IndexOf(DungeonKit.Collider(p.Piece)), p.Place)).ToArray()));
    }

    /// <summary>Disposes the pieces after the snapshot that last named them, then their materials.</summary>
    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in pieces.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        pieces.Clear();
        foreach ((Material material, RenderResource _) in materials)
        {
            material.Dispose();
        }

        materials.Clear();
        foreach (Material material in glowing)
        {
            material.Dispose();
        }

        glowing.Clear();
        foreach (RenderResource texture in textures.Values)
        {
            texture.Dispose();
        }

        textures.Clear();
        shader.Dispose();
    }
}
