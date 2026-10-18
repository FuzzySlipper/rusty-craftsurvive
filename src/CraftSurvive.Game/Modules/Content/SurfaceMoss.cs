using System.Globalization;
using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// Surface moss (#10044): the one product shader that marks where moss grows on stone places and field stones,
/// as a soft damp stain under the painted moss cards
/// (content/game/shaders/surface-moss.wgsl), and the materials that draw with it. Its strength and colours
/// are this owner's, so one change restyles every place. The shader reads world points (renderer-local
/// position plus the frame origin, wrapped), so the materials are refreshed whenever the origin moves.
/// </summary>
internal sealed class SurfaceMoss : IDisposable
{
    private const string ShaderPath = "shaders/surface-moss.wgsl";
    /// <summary>The ivy blanket over an overgrown place's walls (#10044), reading the same field.</summary>
    private const string BlanketShaderPath = "shaders/ivy-blanket.wgsl";
    /// <summary>A place's overgrowth adds to the strength, up to this much: 5 more covers most of a ruin's walls.</summary>
    internal const float MaximumOvergrowth = 6;
    /// <summary>The origin is wrapped to this, as the shader's noise repeats every 4096 cells of a metre.</summary>
    private const long WrapMetres = 4096;
    /// <summary>Strength: 0 none, 1 accents, 2 moss, 3 overgrown.</summary>
    internal const float MaximumStrength = 3;
    private const float DefaultStrength = 2;
    /// <summary>Metres per clump on a place's walls and stones; a small prop asks for its own.</summary>
    internal const float PlaceClumpMetres = 0.9f;
    /// <summary>Metres per clump on a field stone (about a metre across), so a stone holds a few patches.</summary>
    internal const float FieldStoneClumpMetres = 0.18f;
    /// <summary>The moss's texels per metre on a place (the crunch rule), and on a small prop, where 32 leaves a patch a handful of texels.</summary>
    internal const int PlaceTexelsPerMetre = 32, SmallPropTexelsPerMetre = 64;
    /// <summary>The shader reads texels per metre packed above the side share (parameters[3].w).</summary>
    private const float Pack = 1000;
    /// <summary>How far down a surface may face and still hold moss (its normal's y).</summary>
    private const float LowestFacing = -0.25f;
    /// <summary>How much a vertical face holds, as a share of a top's.</summary>
    private const float SideShare = 0.85f;

    /// <summary>
    /// The damp stain's tint, multiplying the stone where the stain is fullest (linear): darker and a little green,
    /// so warm stone goes olive-grey under the moss without losing its painted marks; and how much of it the fullest stain takes.
    /// </summary>
    private static readonly Vector3 StainTint = new(0.5f, 0.64f, 0.4f);
    private const float StainDepth = 1.0f;

    private readonly IEngineContext engine;
    private readonly RenderResource shader, blanketShader;
    private readonly Dictionary<Material, (MaterialRequest Request, float Clump, int Texels, float Overgrowth, bool Blanket)> materials = [];
    private (long X, long Y, long Z) origin;

    internal SurfaceMoss(IEngineContext engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
        blanketShader = engine.Graphics.OpenResource(new RenderResourceRequest(BlanketShaderPath)).Handle;
    }

    internal float Strength { get; private set; } = DefaultStrength;

    /// <summary>The stain as a material carries it now; <paramref name="overgrowth"/> adds to the strength for one place.</summary>
    internal MaterialShader Shader(float clumpMetres, int texelsPerMetre, float overgrowth = 0)
    {
        Vector4 at = new(Wrap(origin.X), Wrap(origin.Y), Wrap(origin.Z), Strength + overgrowth);
        return new MaterialShader(shader, at, new(StainTint, clumpMetres),
            new(StainDepth, 0, 0, LowestFacing), new(0, 0, 0, (texelsPerMetre * Pack) + SideShare));
    }

    /// <summary>The ivy blanket as a material carries it now, for a place with <paramref name="overgrowth"/>.</summary>
    internal MaterialShader BlanketShader(float clumpMetres, int texelsPerMetre, float overgrowth)
    {
        Vector4 at = new(Wrap(origin.X), Wrap(origin.Y), Wrap(origin.Z), Strength + overgrowth);
        return new MaterialShader(blanketShader, at, new(clumpMetres, texelsPerMetre, SideShare, LowestFacing), Vector4.Zero, Vector4.Zero);
    }

    private static float Wrap(long metres) => ((metres % WrapMetres) + WrapMetres) % WrapMetres;

    /// <summary>Keeps a material's shader current from now on; <paramref name="request"/> is the material as created.</summary>
    internal void Register(Material material, MaterialRequest request, float clumpMetres, int texelsPerMetre, float overgrowth = 0, bool blanket = false) =>
        materials[material] = (request, clumpMetres, texelsPerMetre, overgrowth, blanket);

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
        foreach ((Material material, (MaterialRequest request, float clump, int texels, float overgrowth, bool blanket)) in materials)
        {
            MaterialShader now = blanket ? BlanketShader(clump, texels, overgrowth) : Shader(clump, texels, overgrowth);
            engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(material, request with { Shader = now }));
        }
    }

    // The shader's own constants (surface-moss.wgsl), mirrored for Field.
    private const int WrapCells = 4096;
    private const float Streak = 2.2f, EdgeFray = 0.04f, ThresholdAtNone = 0.74f, ThresholdPerStrength = 0.09f;

    /// <summary>The threshold the shader draws moss above at the current strength.</summary>
    internal float Threshold => ThresholdAt(0);

    /// <summary>The moss threshold for a place with <paramref name="overgrowth"/> on top of the strength.</summary>
    internal float ThresholdAt(float overgrowth) => ThresholdAtNone - ((Strength + overgrowth) * ThresholdPerStrength);

    // The blanket shader's constants (ivy-blanket.wgsl).
    private const float BlanketAtNone = 1.40f, BlanketPerStrength = 0.12f;

    /// <summary>The field the ivy blanket shows above, for a place with <paramref name="overgrowth"/>.</summary>
    internal float BlanketThreshold(float overgrowth) => BlanketAtNone - ((Strength + overgrowth) * BlanketPerStrength);

    /// <summary>
    /// The shader's moss field at a world point on a surface facing <paramref name="normal"/>, before the threshold
    /// (surface-moss.wgsl, mirrored): so product geometry (the hanging moss cards) can grow exactly where the painted
    /// moss is thick. The shader reads the point as renderer-local plus the wrapped origin; so does this.
    /// </summary>
    internal float Field(double worldX, double worldY, double worldZ, Vector3 normal, float clumpMetres, int texelsPerMetre)
    {
        double px = worldX - (WrapMetres * Math.Floor(origin.X / (double)WrapMetres));
        double py = worldY - (WrapMetres * Math.Floor(origin.Y / (double)WrapMetres));
        double pz = worldZ - (WrapMetres * Math.Floor(origin.Z / (double)WrapMetres));
        Vector3 n = Vector3.Normalize(normal), a = Vector3.Abs(n);
        (double qx, double qy, bool top) = a.Y >= a.X && a.Y >= a.Z ? (px, pz, true) : a.X >= a.Z ? (pz, py, false) : (px, py, false);
        float facing = n.Y >= LowestFacing ? Lerp(SideShare, 1, SmoothStep(0, 0.8f, n.Y)) : 0;
        double tpm = texelsPerMetre;
        double cx = Math.Floor(qx * tpm), cy = Math.Floor(qy * tpm);
        double tx = (cx + 0.5) / tpm, ty = (cy + 0.5) / tpm;
        double sy = top ? 1 : Streak;
        float clump = Clumps((float)(tx / clumpMetres), (float)(ty / (clumpMetres * sy)));
        float fray = (Hash((long)cx, (long)cy) - 0.5f) * EdgeFray;
        return (clump + fray) * facing;
    }

    private static float Hash(long x, long y)
    {
        uint wx = (uint)(((x % WrapCells) + WrapCells) % WrapCells), wy = (uint)(((y % WrapCells) + WrapCells) % WrapCells);
        uint h = unchecked((wx * 374761393u) + (wy * 668265263u));
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return (h & 0x00ffffffu) / 16777215f;
    }

    private static float Noise(float x, float y)
    {
        long ix = (long)Math.Floor(x), iy = (long)Math.Floor(y);
        float fx = x - ix, fy = y - iy;
        float ux = fx * fx * (3 - (2 * fx)), uy = fy * fy * (3 - (2 * fy));
        return Lerp(Lerp(Hash(ix, iy), Hash(ix + 1, iy), ux), Lerp(Hash(ix, iy + 1), Hash(ix + 1, iy + 1), ux), uy);
    }

    private static float Clumps(float x, float y) => (Noise(x, y) * 0.7f) + (Noise((x * 2.7f) + 17.3f, (y * 2.7f) + 5.1f) * 0.3f);

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - (2 * t));
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture, $"moss strength={Strength:F1} materials={materials.Count} origin={origin.X},{origin.Y},{origin.Z}");

    /// <summary>Disposes the shader; the materials are their owners' to dispose.</summary>
    public void Dispose()
    {
        materials.Clear();
        shader.Dispose();
        blanketShader.Dispose();
    }
}
