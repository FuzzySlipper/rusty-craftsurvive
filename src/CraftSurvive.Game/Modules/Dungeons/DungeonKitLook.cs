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
    private readonly List<(Material Material, RenderResource Texture, RenderResource? Emission, bool Mosaic)> materials = [];
    private Look look;
    // Each piece drawn plain, and lit (its inlays glowing through its emission map) where a placement is lit.
    private readonly Dictionary<(KitPiece Piece, bool Lit), (MeshResource Mesh, Appearance Look, int Slots, bool Mossy)> pieces = [];
    private readonly List<(KitPiece Piece, bool Lit, int Wet, Transform Place)> placed = [];

    // Moss (#10092): each placement draws through an appearance of its piece's mesh whose texture tint carries its dampness
    // in one of so many steps (kit-stone.wgsl reads it); the moss's strength and stain as SurfaceMoss's.
    private const int WetSteps = 16;
    private const float MossStrength = 2f, StainDepth = 1f, SideShare = 0.85f, LowestFacing = -0.25f, WetInBlue = 0.45f;
    private readonly Dictionary<(KitPiece Piece, bool Lit, int Wet), Appearance> damp = [];

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
        foreach (KitPiece kind in placements.Select(p => DungeonKit.Collider(p.Piece)).Distinct())
        {
            if (entries[DungeonKit.Name(kind)].TryGetProperty("collider", out JsonElement collider))
            {
                colliders[kind] = Collision(content, collider.GetString()!);
            }
        }

        foreach ((KitPiece piece, bool wantsLit) in placements.Select(p => (p.Piece, p.Lit)).Concat(placements.Select(p => (DungeonKit.Collider(p.Piece), false))).Distinct())
        {
            JsonElement entry = entries[DungeonKit.Name(piece)];
            // A glowing piece (the relic's glow) draws in a plain emissive material of its own colour, not the stone.
            Vector3? glow = entry.TryGetProperty("emission", out JsonElement e)
                ? new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle()) : null;
            // A piece may name an emission map: its painted inlays glow, lighting the room through the probe volume (the ruin light study).
            string? emissionMap = wantsLit && entry.TryGetProperty("emissionMap", out JsonElement m) ? m.GetString() : null;
            bool lit = emissionMap is not null;
            if (pieces.ContainsKey((piece, lit)))
            {
                continue;
            }

            (MeshResourceCreateRequest model, (string Role, string? Texture)[] parts) = Model(content, entry.GetProperty("model").GetString()!);
            MeshMaterialBinding[] bindings = [.. parts.Select((part, slot) => new MeshMaterialBinding((uint)slot,
                glow is Vector3 light ? Glowing(light)
                    : Material(Painted(part.Texture ?? throw new InvalidOperationException($"Dungeon kit piece {piece} has no texture for {part.Role}."), paint), emissionMap,
                        piece is KitPiece.WallRoundel or KitPiece.WallTallRoundel)))];
            MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(model.Positions, model.Normals, model.Uvs, model.Colors,
                model.Indices, model.Groups, bindings));
            pieces[(piece, lit)] = (mesh, engine.Graphics.CreateMeshAppearance(mesh), bindings.Length, glow is null);
        }

        foreach (KitPlacement p in placements.Take(ProductIds.DungeonKitObjects))
        {
            (Vector3 at, Quaternion turn, Vector3 scale) = DungeonKit.Place(p);
            bool litHere = p.Lit && pieces.ContainsKey((p.Piece, true));
            int wet = Math.Clamp((int)MathF.Round(KitMoss.Dampness(p.Wet) / KitMoss.MostDampness * (WetSteps - 1)), 0, WetSteps - 1);
            placed.Add((p.Piece, litHere, wet, new Transform(at, turn, scale)));
            if (!damp.ContainsKey((p.Piece, litHere, wet)) && pieces[(p.Piece, litHere)] is { Mossy: true } drawn)
            {
                // The piece's mesh again, its every slot's texture tint carrying this dampness in its alpha.
                Appearance wetLook = engine.Graphics.CreateMeshAppearance(drawn.Mesh);
                // The dampness in the tint's blue (kit-stone.wgsl's WET_IN_BLUE): an alpha below 1 would draw it transparent.
                Color dampness = new(1f, 1f, 1f - (WetInBlue * KitMoss.MostDampness * wet / (float)(WetSteps - 1)), 1f);
                engine.Graphics.UpdateStaticMeshMaterialFactors(new StaticMeshMaterialFactorsRequest(wetLook,
                    Enumerable.Range(0, drawn.Slots).Select(slot => new MeshMaterialFactors((uint)slot, false, default, false, Vector3.Zero, 0f, true, dampness)).ToArray()));
                damp[(p.Piece, litHere, wet)] = wetLook;
            }
        }
    }

    // The pieces' collision meshes (a manifest's collider: the shape decimated), by piece; a piece without one collides as drawn.
    private readonly Dictionary<KitPiece, (Vector3[] Vertices, Triangle[] Triangles)> colliders = [];

    private static readonly Dictionary<string, (Vector3[] Vertices, Triangle[] Triangles)> Collisions = [];

    /// <summary>A collider's vertices and triangles, read once for the process.</summary>
    private static (Vector3[] Vertices, Triangle[] Triangles) Collision(ProductContent content, string path)
    {
        if (!Collisions.TryGetValue(path, out (Vector3[] Vertices, Triangle[] Triangles) mesh))
        {
            using JsonDocument document = JsonDocument.Parse(content.ReadText(path));
            JsonElement part = document.RootElement.GetProperty("parts")[0];
            float[] p = [.. part.GetProperty("positions").EnumerateArray().Select(v => v.GetSingle())];
            uint[] index = [.. part.GetProperty("indices").EnumerateArray().Select(v => v.GetUInt32())];
            mesh = ([.. Enumerable.Range(0, p.Length / 3).Select(i => new Vector3(p[3 * i], p[(3 * i) + 1], p[(3 * i) + 2]))],
                [.. Enumerable.Range(0, index.Length / 3).Select(i => new Triangle(index[3 * i], index[(3 * i) + 1], index[(3 * i) + 2]))]);
            Collisions[path] = mesh;
        }

        return mesh;
    }

    // Each piece's model parsed once for the process: a piece's lit and plain meshes, and every later dungeon, reuse its
    // geometry with their own materials (the content does not change while the product runs).
    private static readonly Dictionary<string, (MeshResourceCreateRequest Mesh, (string Role, string? Texture)[] Parts)> Models = [];

    private static (MeshResourceCreateRequest Mesh, (string Role, string? Texture)[] Parts) Model(ProductContent content, string path)
    {
        if (!Models.TryGetValue(path, out (MeshResourceCreateRequest Mesh, (string Role, string? Texture)[] Parts) model))
        {
            List<(string Role, string? Texture)> parts = [];
            // The materials are bound per kit; parsing only records each part's role and texture.
            MeshResourceCreateRequest mesh = PropMesh.Read(content, path, (role, texture) =>
            {
                parts.Add((role, texture));
                return null!;
            });
            model = (mesh, [.. parts]);
            Models[path] = model;
        }

        return model;
    }

    /// <summary>A material for a kit texture: the texture as its base colour, nearest with mipmaps, under the kit stone shader.</summary>
    private Material Material(string path, string? emissionPath, bool mosaic)
    {
        RenderResource texture = Texture(path);
        RenderResource? emission = emissionPath is null ? null : Texture(emissionPath);
        Material material = engine.Graphics.CreateMaterial(Request(texture, emission, mosaic));
        materials.Add((material, texture, emission, mosaic));
        return material;
    }

    private RenderResource Texture(string path)
    {
        if (!textures.TryGetValue(path, out RenderResource? texture))
        {
            texture = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
            textures[path] = texture;
        }

        return texture;
    }

    /// <summary>
    /// How brightly a lit piece's inlays glow through its emission map (the ruin light; the study may change it, 0 none): a
    /// powered room's inlays faintly, so the room fills softly, and a roundel's great mosaic brightly, a room's light alone.
    /// </summary>
    internal static float InlayGlow { get; set; } = DefaultInlayGlow;

    /// <inheritdoc cref="InlayGlow"/>
    internal static float MosaicGlow { get; set; } = DefaultMosaicGlow;

    private const float DefaultInlayGlow = 0.1f, DefaultMosaicGlow = 2f;

    /// <summary>A plain emissive material, for a piece that is a light's visible source.</summary>
    private Material Glowing(Vector3 emission)
    {
        Material material = engine.Graphics.CreateMaterial(GlowRequest(emission));
        glowing.Add((material, emission));
        return material;
    }

    private static MaterialRequest GlowRequest(Vector3 emission)
    {
        Color pale = new(emission.X, emission.Y, emission.Z, 1f);
        return new MaterialRequest(pale, default(RenderResourceReference), 1f, pale, emission, GlowIntensity, false);
    }

    /// <summary>Retunes the glowing pieces to <see cref="GlowIntensity"/> now (a study).</summary>
    internal void TuneGlow()
    {
        Tune(look);
        foreach ((Material material, Vector3 emission) in glowing)
        {
            engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(material, GlowRequest(emission)));
        }
    }

    /// <summary>How brightly a glowing piece shines in its emission colour (the ruin light study may change it).</summary>
    internal static float GlowIntensity { get; set; } = DefaultGlowIntensity;

    // Bright enough that a relic's glow, with the inlays it powers, lights its room through the probe volume.
    private const float DefaultGlowIntensity = 3f;

    private readonly List<(Material Material, Vector3 Emission)> glowing = [];

    private MaterialRequest Request(RenderResource texture, RenderResource? emission, bool mosaic)
    {
        Color white = new(1f, 1f, 1f, 1f);
        float glow = mosaic ? MosaicGlow : InlayGlow;
        bool glows = emission is not null && glow > 0f;
        return new MaterialRequest(white, texture, Roughness, white, glows ? Vector3.One : Vector3.Zero, glows ? glow : 0f, false)
        {
            EmissionMap = glows ? emission! : default(RenderResourceReference),
            Shader = new MaterialShader(shader, new Vector4(TextureTexels, look.FacetStrength, look.FacetSteps, look.FacetCoherence),
                new Vector4(look.CalmFrom, look.CalmFull, look.CalmMip, look.Calm), new Vector4(0f, 0f, 0f, MossStrength),
                new Vector4(StainDepth, SideShare, LowestFacing, 0f), texture, default(RenderResourceReference)),
        };
    }

    /// <summary>Retunes the kit stone shader on every piece now (a study).</summary>
    internal void Tune(Look next)
    {
        look = next;
        foreach ((Material material, RenderResource texture, RenderResource? emission, bool mosaic) in materials)
        {
            engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(material, Request(texture, emission, mosaic)));
        }
    }

    /// <summary>A kit texture's path in a study's paint folder (beside it, the same name), or as it is.</summary>
    private static string Painted(string texture, string? paint) =>
        paint is null ? texture : $"{Path.GetDirectoryName(texture)!.Replace('\\', '/')}/{paint}/{Path.GetFileName(texture)}";

    internal int Count => placed.Count;

    /// <summary>How many piece meshes the kit built (each piece plain, and lit where a placement glows).</summary>
    internal int Meshes => pieces.Count;

    internal IEnumerable<AppearanceFact> Facts => placed.Select((p, index) => new AppearanceFact(ProductIds.DungeonKitObject + (ulong)index, false, 0UL,
        p.Place, damp.TryGetValue((p.Piece, p.Lit, p.Wet), out Appearance? wet) ? wet : pieces[(p.Piece, p.Lit)].Look, true, RenderLayer.Scene, ShadowCasting.Cast));

    /// <summary>The pieces that collide (<see cref="DungeonKit.Collides"/>) as static collision: one asset per piece, an instance per placement.</summary>
    internal void Collide(SpatialSession session)
    {
        List<KitPiece> kinds = [.. placed.Where(p => DungeonKit.Collides(p.Piece)).Select(p => DungeonKit.Collider(p.Piece)).Distinct()];
        // A piece with a collider collides as its triangles, given inline; one without, as its drawn mesh.
        List<Vector3> vertices = [];
        List<Triangle> triangles = [];
        List<StaticMeshAsset> assets = [];
        foreach (KitPiece kind in kinds)
        {
            ulong id = AssetBase + (ulong)assets.Count;
            if (colliders.TryGetValue(kind, out (Vector3[] Vertices, Triangle[] Triangles) collider))
            {
                uint firstVertex = (uint)vertices.Count, firstTriangle = (uint)triangles.Count;
                vertices.AddRange(collider.Vertices);
                triangles.AddRange(collider.Triangles);
                assets.Add(new StaticMeshAsset(id, firstVertex, (uint)collider.Vertices.Length, firstTriangle, (uint)collider.Triangles.Length));
            }
            else
            {
                assets.Add(new StaticMeshAsset(id, new MeshResourceReference(pieces[(kind, false)].Mesh), 0, 0, 0, 0));
            }
        }

        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(
            session,
            assets.ToArray(),
            vertices.ToArray(),
            triangles.ToArray(),
            placed.Where(p => DungeonKit.Collides(p.Piece))
                .Select((p, i) => new StaticMeshInstance(InstanceBase + (ulong)i, AssetBase + (ulong)kinds.IndexOf(DungeonKit.Collider(p.Piece)), p.Place)).ToArray()));
    }

    /// <summary>Disposes the pieces after the snapshot that last named them, then their materials.</summary>
    public void Dispose()
    {
        foreach (Appearance look in damp.Values)
        {
            look.Dispose();
        }

        damp.Clear();
        foreach ((MeshResource mesh, Appearance look, int _, bool _) in pieces.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        pieces.Clear();
        foreach ((Material material, RenderResource _, RenderResource? _, bool _) in materials)
        {
            material.Dispose();
        }

        materials.Clear();
        foreach ((Material material, Vector3 _) in glowing)
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
