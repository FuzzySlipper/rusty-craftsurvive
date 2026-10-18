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
    /// <summary>Strength: 0 none, 1 accents, 2 moss, 3 overgrown.</summary>
    internal const float MaximumStrength = 3;
    private const float DefaultStrength = 2;
    /// <summary>Metres per clump on a place's walls and stones; a small prop asks for its own.</summary>
    internal const float PlaceClumpMetres = 0.9f;
    /// <summary>Metres per clump on a field stone (about a metre across), so a stone holds a few patches.</summary>
    internal const float FieldStoneClumpMetres = 0.18f;
    /// <summary>The moss's texels per metre on a place (the crunch rule), and on a small prop, where 32 leaves a patch a handful of texels.</summary>
    internal const int PlaceTexelsPerMetre = 32, SmallPropTexelsPerMetre = 64;
    /// <summary>How far down a surface may face and still hold moss (its normal's y).</summary>
    private const float LowestFacing = -0.25f;
    /// <summary>How much a vertical face holds, as a share of a top's.</summary>
    private const float SideShare = 0.85f;

    /// <summary>Lichen's amount (surface-moss.wgsl): pale crusty spots on dry, sunny stone, the moss's opposite.</summary>
    private const float LichenAmount = 1f;

    /// <summary>How much of the stain's tint (surface-moss.wgsl's STAIN_TINT) the fullest stain takes.</summary>
    private const float StainDepth = 1.0f;

    private readonly IEngineContext engine;
    private readonly RenderResource shader, blanketShader;
    private readonly Dictionary<Material, (MaterialRequest Request, Grain Grain, float Overgrowth, bool Blanket)> materials = [];
    private (long X, long Y, long Z) origin;

    internal SurfaceMoss(IEngineContext engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
        blanketShader = engine.Graphics.OpenResource(new RenderResourceRequest(BlanketShaderPath)).Handle;
    }

    internal float Strength { get; private set; } = DefaultStrength;

    /// <summary>
    /// How a material reads the field (#10044): its texels per metre, its clump size, and whether its vertex colour's
    /// alpha is the vertex's moisture (the place kit; a field stone's alpha is its wind weight, so no).
    /// </summary>
    internal readonly record struct Grain(float ClumpMetres, int TexelsPerMetre, bool VertexMoisture)
    {
        /// <summary>The field's lattice, in texels per cell: broad clumps across and down a side, and the fine octave.</summary>
        internal (int Broad, int BroadDown, int Fine, int FineDown) Lattice
        {
            get
            {
                int broad = Math.Max(1, (int)Math.Round(ClumpMetres * TexelsPerMetre, MidpointRounding.ToEven));
                int fine = Math.Max(1, (int)Math.Round(broad / FineRatio, MidpointRounding.ToEven));
                return (broad, Math.Max(1, (int)Math.Round(broad * Streak, MidpointRounding.ToEven)), fine,
                    Math.Max(1, (int)Math.Round(fine * Streak, MidpointRounding.ToEven)));
            }
        }

        /// <summary>
        /// The whole metres the origin is wrapped by: the least period every lattice divides (in texels), so the
        /// wrapped pattern is the world's pattern exactly and never jumps when the origin moves.
        /// </summary>
        internal long PeriodMetres
        {
            get
            {
                (int a, int b, int c, int d) = Lattice;
                long cells = Lcm(Lcm(a, b), Lcm(c, d));
                return cells / Gcd(cells, TexelsPerMetre);
            }
        }

        private static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);

        private static long Lcm(long a, long b) => a / Gcd(a, b) * b;
    }

    /// <summary>The place kit's grain (stone pieces), and a field stone's.</summary>
    internal static readonly Grain PlaceGrain = new(PlaceClumpMetres, PlaceTexelsPerMetre, true);
    internal static readonly Grain FieldStoneGrain = new(FieldStoneClumpMetres, SmallPropTexelsPerMetre, false);

    private Vector4[] Parameters(Grain grain, float overgrowth)
    {
        long period = grain.PeriodMetres;
        (int a, int b, int c, int d) = grain.Lattice;
        return
        [
            new(Wrap(origin.X, period), Wrap(origin.Y, period), Wrap(origin.Z, period), Strength + overgrowth),
            new(a, b, c, d),
            new(grain.TexelsPerMetre, SideShare, LowestFacing, grain.VertexMoisture ? 1 : 0),
            new(period, LichenAmount, 0, StainDepth),
        ];
    }

    /// <summary>The stain as a material carries it now; <paramref name="overgrowth"/> adds to the strength for one place.</summary>
    internal MaterialShader Shader(Grain grain, float overgrowth = 0)
    {
        Vector4[] v = Parameters(grain, overgrowth);
        return new MaterialShader(shader, v[0], v[1], v[2], v[3]);
    }

    /// <summary>The ivy blanket as a material carries it now, for a place with <paramref name="overgrowth"/>.</summary>
    internal MaterialShader BlanketShader(Grain grain, float overgrowth)
    {
        Vector4[] v = Parameters(grain, overgrowth);
        return new MaterialShader(blanketShader, v[0], v[1], v[2], v[3]);
    }

    private static float Wrap(long metres, long period) => (float)(((metres % period) + period) % period);

    /// <summary>Keeps a material's shader current from now on; <paramref name="request"/> is the material as created.</summary>
    internal void Register(Material material, MaterialRequest request, Grain grain, float overgrowth = 0, bool blanket = false) =>
        materials[material] = (request, grain, overgrowth, blanket);

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

    /// <summary>
    /// The snow lying where the party stands (#10068), in metres (frost as a thin depth), for the moss cards: the Engine's
    /// scene-wide cover draws the snow itself, and the stain shader keeps off it.
    /// </summary>
    internal float Snow { get; set; }

    private void Refresh()
    {
        foreach ((Material material, (MaterialRequest request, Grain grain, float overgrowth, bool blanket)) in materials)
        {
            MaterialShader now = blanket ? BlanketShader(grain, overgrowth) : Shader(grain, overgrowth);
            engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(material, request with { Shader = now }));
        }
    }

    // The shaders' own constants (surface-moss.wgsl, ivy-blanket.wgsl), mirrored for Field.
    private const float Streak = 2.2f, FineRatio = 2.7f, EdgeFray = 0.04f, ThresholdAtNone = 0.74f, ThresholdPerStrength = 0.09f;
    private const float SunDrying = 0.15f;

    /// <summary>The vertex moisture is stored in the colour alpha divided by this, so it stays within 0..1.</summary>
    internal const float MoistureScale = 1.25f;
    private const float BlanketAtNone = 1.40f, BlanketPerStrength = 0.12f;

    /// <summary>The threshold the shader draws moss above at the current strength.</summary>
    internal float Threshold => ThresholdAt(0);

    /// <summary>The moss threshold for a place with <paramref name="overgrowth"/> on top of the strength.</summary>
    internal float ThresholdAt(float overgrowth) => ThresholdAtNone - ((Strength + overgrowth) * ThresholdPerStrength);

    /// <summary>The field the ivy blanket shows above, for a place with <paramref name="overgrowth"/>.</summary>
    internal float BlanketThreshold(float overgrowth) => BlanketAtNone - ((Strength + overgrowth) * BlanketPerStrength);

    /// <summary>
    /// Moisture by height above the ground (#10044), around 1: wet feet and rising damp (1.2 at the ground), drier up a
    /// wall and on a crown (0.8 from 4 m). It redistributes the moss rather than thinning it.
    /// </summary>
    internal static float HeightMoisture(float metresAboveGround) => 1.2f - (0.4f * SmoothStep(0.5f, 4f, metresAboveGround));

    /// <summary>
    /// The shaders' moss field at a world point on a surface facing <paramref name="normal"/>, before the threshold
    /// (mirrored exactly): so product geometry (the moss cards) grows where the painted moss is.
    /// <paramref name="vertexMoisture"/> is what the vertex's colour alpha carries for a grain that reads it.
    /// </summary>
    internal float Field(double worldX, double worldY, double worldZ, Vector3 normal, Grain grain, float vertexMoisture = 1) =>
        FieldAt(origin, worldX, worldY, worldZ, normal, grain, vertexMoisture);

    /// <summary>The field as the shaders read it with the frame origin at <paramref name="frameOrigin"/>; the same for every origin.</summary>
    internal static float FieldAt((long X, long Y, long Z) frameOrigin, double worldX, double worldY, double worldZ, Vector3 normal, Grain grain,
        float vertexMoisture = 1)
    {
        (long X, long Y, long Z) origin = frameOrigin;
        long period = grain.PeriodMetres;
        double px = worldX - (period * Math.Floor(origin.X / (double)period));
        double py = worldY - (period * Math.Floor(origin.Y / (double)period));
        double pz = worldZ - (period * Math.Floor(origin.Z / (double)period));
        Vector3 n = Vector3.Normalize(normal), a = Vector3.Abs(n);
        (double qx, double qy, bool top) = a.Y >= a.X && a.Y >= a.Z ? (px, pz, true) : a.X >= a.Z ? (pz, py, false) : (px, py, false);
        long tx = (long)Math.Floor(qx * grain.TexelsPerMetre), ty = (long)Math.Floor(qy * grain.TexelsPerMetre);
        (int b, int bd, int f, int fd) = grain.Lattice;
        (int bx, int by) = top ? (b, b) : (b, bd);
        (int fx, int fy) = top ? (f, f) : (f, fd);
        long periodTexels = period * grain.TexelsPerMetre;
        float clump = (LatticeNoise(tx, ty, bx, by, periodTexels) * 0.7f) + (LatticeNoise(tx + (fx * 17L), ty + (fy * 5L), fx, fy, periodTexels) * 0.3f);
        float fray = (Hash(WrapIndex(tx, periodTexels) + 1013, WrapIndex(ty, periodTexels) + 7919) - 0.5f) * EdgeFray;
        float facing = n.Y >= LowestFacing ? Lerp(SideShare, 1, SmoothStep(0, 0.8f, n.Y)) : 0;
        float sun = 1 - (SunDrying * n.Z);
        // As the shader reads it: stored as an alpha (moisture / MoistureScale) and scaled back.
        float own = grain.VertexMoisture ? (vertexMoisture / MoistureScale) * MoistureScale : 1;
        return (clump + fray) * facing * sun * own;
    }

    /// <summary>As the shader's hash: the cell's two 32-bit coordinates, wrapping as WGSL's u32 arithmetic does.</summary>
    private static float Hash(long x, long y)
    {
        uint h = unchecked(((uint)(int)x * 374761393u) + ((uint)(int)y * 668265263u));
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return (h & 0x00ffffffu) / 16777215f;
    }

    private static long FloorDiv(long a, long b) => (a / b) - ((a % b != 0) && (a < 0) ? 1 : 0);

    private static long WrapIndex(long a, long n) => ((a % n) + n) % n;

    private static float LatticeNoise(long tx, long ty, int cx, int cy, long periodTexels)
    {
        long ix = FloorDiv(tx, cx), iy = FloorDiv(ty, cy);
        float fx = ((tx - (ix * cx)) + 0.5f) / cx, fy = ((ty - (iy * cy)) + 0.5f) / cy;
        float ux = fx * fx * (3 - (2 * fx)), uy = fy * fy * (3 - (2 * fy));
        long nx = periodTexels / cx, ny = periodTexels / cy;
        long x0 = WrapIndex(ix, nx), x1 = WrapIndex(ix + 1, nx), y0 = WrapIndex(iy, ny), y1 = WrapIndex(iy + 1, ny);
        return Lerp(Lerp(Hash(x0, y0), Hash(x1, y0), ux), Lerp(Hash(x0, y1), Hash(x1, y1), ux), uy);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - (2 * t));
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture, $"moss strength={Strength:F1} snow={Snow:F3} materials={materials.Count} origin={origin.X},{origin.Y},{origin.Z}");

    /// <summary>Disposes the shader; the materials are their owners' to dispose.</summary>
    public void Dispose()
    {
        materials.Clear();
        shader.Dispose();
        blanketShader.Dispose();
    }
}
