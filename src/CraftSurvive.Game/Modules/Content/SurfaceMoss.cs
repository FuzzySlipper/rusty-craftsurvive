using System.Globalization;
using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// Surface moss (#10044): the one product shader that grows moss on stone places and field stones
/// (content/game/shaders/surface-moss.wgsl), and the materials that draw with it. Its strength and colours
/// are this owner's, so one change restyles every place. The shader reads world points (renderer-local
/// position plus the frame origin, wrapped), so the materials are refreshed whenever the origin moves.
/// </summary>
internal sealed class SurfaceMoss : IDisposable
{
    private const string ShaderPath = "shaders/surface-moss.wgsl";
    /// <summary>The origin is wrapped to this, as the shader's noise repeats every 4096 cells of a metre.</summary>
    private const long WrapMetres = 4096;
    /// <summary>Strength: 0 none, 1 accents, 2 moss, 3 overgrown.</summary>
    internal const float MaximumStrength = 3;
    private const float DefaultStrength = 2;
    /// <summary>Metres per clump on a place's walls and stones; a small prop asks for its own.</summary>
    internal const float PlaceClumpMetres = 0.9f;
    /// <summary>Metres per clump on a field stone (about a metre across), so a stone holds a few patches.</summary>
    internal const float FieldStoneClumpMetres = 0.3f;
    /// <summary>How far down a surface may face and still hold moss (its normal's y).</summary>
    private const float LowestFacing = -0.25f;
    /// <summary>How much a vertical face holds, as a share of a top's.</summary>
    private const float SideShare = 0.85f;

    // Between the palette's foliage and sage ramps (content/style/palette.json): an olive moss that sits in
    // the warm stone rather than a grass green painted on it; dark and light moss, and a darker rim.
    private static readonly Color Dark = Color.FromSrgbHex(0x515e36);
    private static readonly Color Light = Color.FromSrgbHex(0x737c48);
    private static readonly Color Rim = Color.FromSrgbHex(0x3a432b);

    private readonly IEngineContext engine;
    private readonly RenderResource shader;
    private readonly Dictionary<Material, (MaterialRequest Request, float Clump)> materials = [];
    private (long X, long Y, long Z) origin;

    internal SurfaceMoss(IEngineContext engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
    }

    internal float Strength { get; private set; } = DefaultStrength;

    /// <summary>The shader as a material carries it now.</summary>
    internal MaterialShader Shader(float clumpMetres)
    {
        Vector4 at = new(Wrap(origin.X), Wrap(origin.Y), Wrap(origin.Z), Strength);
        return new MaterialShader(shader, at, new(Dark.R, Dark.G, Dark.B, clumpMetres),
            new(Light.R, Light.G, Light.B, LowestFacing), new(Rim.R, Rim.G, Rim.B, SideShare));
    }

    private static float Wrap(long metres) => ((metres % WrapMetres) + WrapMetres) % WrapMetres;

    /// <summary>Keeps a material's shader current from now on; <paramref name="request"/> is the material as created.</summary>
    internal void Register(Material material, MaterialRequest request, float clumpMetres) => materials[material] = (request, clumpMetres);

    internal void Unregister(Material material) => materials.Remove(material);

    /// <summary>Refreshes the materials when the frame origin has moved.</summary>
    internal void Follow((long X, long Y, long Z) frameOrigin)
    {
        if (frameOrigin == origin) return;
        origin = frameOrigin;
        Refresh();
    }

    /// <summary>Sets the moss's strength for every place at once (0 to <see cref="MaximumStrength"/>).</summary>
    internal string SetStrength(float strength)
    {
        Strength = Math.Clamp(strength, 0, MaximumStrength);
        Refresh();
        return Readout();
    }

    private void Refresh()
    {
        foreach ((Material material, (MaterialRequest request, float clump)) in materials)
        {
            engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(material, request with { Shader = Shader(clump) }));
        }
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture, $"moss strength={Strength:F1} materials={materials.Count} origin={origin.X},{origin.Y},{origin.Z}");

    /// <summary>Disposes the shader; the materials are their owners' to dispose.</summary>
    public void Dispose()
    {
        materials.Clear();
        shader.Dispose();
    }
}
