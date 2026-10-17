using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// Materials for textured prop parts (scripts/generate-flora.py, #9685), one for each role and texture,
/// owned and disposed together. Textures are magnified nearest so their low-resolution texels read as
/// one chunky style up close, and mipmapped at distance so they do not sparkle (the crunch rule). A "leaves" part is alpha-cut cards built with both windings, so its material is
/// one-sided and both sides keep the crown-facing normals they were built with; other roles (bark)
/// are opaque and smooth-shaded by their own normals. The owner gives each role's finish, and may
/// choose another texture filter.
/// </summary>
internal sealed class PropMaterials : IDisposable
{
    internal const string LeavesRole = "leaves";
    private const float CardAlphaCutoff = 0.5f;
    private static readonly Color White = new(1, 1, 1, 1);

    /// <summary>
    /// A role's surface and how the wind moves it (flutter scales by each vertex's wind weight); a mossy
    /// role grows the surface moss (#10044) when its owner gave one, in clumps of <paramref name="MossClumpMetres"/>
    /// (0 for none) at <paramref name="MossTexelsPerMetre"/>: a small prop needs smaller clumps and a finer grid
    /// than a wall, or it is all moss or bare, in a handful of blocks.
    /// </summary>
    internal readonly record struct Finish(float Roughness, float WindBend, float WindFlutter, float MossClumpMetres = 0,
        int MossTexelsPerMetre = SurfaceMoss.PlaceTexelsPerMetre);

    private readonly IEngineContext engine;
    private readonly Func<string, Finish> finishFor;
    private readonly TextureFilter filter;
    private readonly SurfaceMoss? moss;
    private readonly Dictionary<(string Role, string Texture), Material> materials = [];
    private readonly Dictionary<string, RenderResource> textures = [];

    internal PropMaterials(IEngineContext engine, Func<string, Finish> finishFor, TextureFilter filter = TextureFilter.NearestMipmapped, SurfaceMoss? moss = null)
    {
        this.filter = filter;
        this.moss = moss;
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.finishFor = finishFor ?? throw new ArgumentNullException(nameof(finishFor));
    }

    internal Material For(string role, string texturePath)
    {
        if (materials.TryGetValue((role, texturePath), out Material? material)) return material;
        if (!textures.TryGetValue(texturePath, out RenderResource? texture))
        {
            RenderResourceInfo info = engine.Graphics.OpenResource(new RenderResourceRequest(texturePath, filter, TextureWrap.Repeat));
            if (info.Kind != RenderResourceKind.Texture || info.ByteLength == 0)
            {
                throw new InvalidOperationException($"CraftSurvive prop texture '{texturePath}' must be a non-empty Engine texture.");
            }

            texture = info.Handle;
            textures[texturePath] = texture;
        }

        bool cards = role == LeavesRole;
        Finish finish = finishFor(role);
        MaterialRequest request = new MaterialRequest(White, texture, finish.Roughness, White, Vector3.Zero, 0, false) with
        {
            AlphaMode = cards ? MaterialAlphaMode.Mask : MaterialAlphaMode.Opaque,
            AlphaCutoff = cards ? CardAlphaCutoff : 0,
            WindBend = finish.WindBend,
            WindFlutter = finish.WindFlutter,
        };
        bool mossy = finish.MossClumpMetres > 0 && moss is not null;
        if (mossy) request = request with { Shader = moss!.Shader(finish.MossClumpMetres, finish.MossTexelsPerMetre) };
        material = engine.Graphics.CreateMaterial(request);
        if (mossy) moss!.Register(material, request, finish.MossClumpMetres, finish.MossTexelsPerMetre);
        materials[(role, texturePath)] = material;
        return material;
    }

    public void Dispose()
    {
        foreach (Material material in materials.Values)
        {
            moss?.Unregister(material);
            material.Dispose();
        }

        materials.Clear();
        foreach (RenderResource texture in textures.Values)
        {
            texture.Dispose();
        }

        textures.Clear();
    }
}
