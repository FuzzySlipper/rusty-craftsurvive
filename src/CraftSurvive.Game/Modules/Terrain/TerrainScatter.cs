using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// What grows on the overworld's grass (Engine #9546): clumps of grass cards and low-poly bushes
/// the Engine scatters on the grass block's ground around the camera. The product decides what
/// grows, where and how densely; the Engine places, draws, fades and re-places the copies.
/// </summary>
internal sealed class TerrainScatter : IDisposable
{
    internal const string GrassContentPath = "textures/grass-blades.png";

    private const uint GrassScatter = 0, BushScatter = 1;

    /// <summary>Clumps per square metre of grass ground, and how far from the camera they grow.</summary>
    internal const float DefaultGrassDensity = 2.5f;
    internal const float DefaultBushDensity = 0.03f;
    /// <summary>Inside the coarse distance (<see cref="TerrainPresentation.CoarseBeyondMetres"/>): coarse chunks grow nothing.</summary>
    private const float Reach = 44f;
    private const float FadeMetres = 12f;
    private const float GrassSlopeDegrees = 38f, BushSlopeDegrees = 28f;
    private const float GrassLean = 0.35f;
    private const float GrassScaleMin = 0.75f, GrassScaleMax = 1.3f;
    private const float BushScaleMin = 0.7f, BushScaleMax = 1.6f;
    private const uint MaximumGrass = 60_000, MaximumBushes = 1_500;
    private const uint BushSeed = 1;
    private static readonly Vector3 GrassTintLow = new(0.62f, 0.66f, 0.5f), GrassTintHigh = new(0.95f, 0.95f, 0.82f);
    private static readonly Vector3 BushTintLow = new(0.75f, 0.8f, 0.7f), BushTintHigh = new(1.15f, 1.1f, 0.9f);

    // The clump: cards crossed at even angles around its origin, blades from the texture on them.
    private const int GrassCards = 4;
    private const float CardWidth = 0.5f, CardHeight = 0.55f, ClumpSpread = 0.35f;
    private const int ClumpLayoutSeed = 9546;
    private const float GrassWindBend = 0.08f, GrassWindFlutter = 0.06f;
    private const float GrassAlphaCutoff = 0.5f;
    private const float GrassRoughness = 0.9f;
    private static readonly Color GrassColor = new(1, 1, 1, 1);

    // The bush: a squashed low-poly sphere standing on its origin, flat shaded.
    private const int BushRings = 4, BushSegments = 7;
    private const float BushWidth = 0.7f, BushHeight = 0.5f, BushFootDepth = 0.7f;
    private const float BushRoughness = 0.85f;
    private static readonly Color BushColor = new(0.1f, 0.17f, 0.07f, 1);

    private readonly IEngineContext engine;
    private readonly RenderResource grassTexture;
    private readonly Material grass, bush;
    private readonly MeshResource grassMesh, bushMesh;
    private readonly Appearance grassLook, bushLook;
    private float grassDensity = DefaultGrassDensity, bushDensity = DefaultBushDensity;

    internal TerrainScatter(IEngineContext engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Color white = new(1, 1, 1, 1);
        RenderResourceInfo texture = engine.Graphics.OpenResource(new RenderResourceRequest(GrassContentPath, TextureFilter.Linear, TextureWrap.Clamp));
        if (texture.Kind != RenderResourceKind.Texture || texture.ByteLength == 0)
        {
            throw new InvalidOperationException($"CraftSurvive grass '{GrassContentPath}' must be a non-empty Engine texture.");
        }

        grassTexture = texture.Handle;
        grass = engine.Graphics.CreateMaterial(new MaterialRequest(GrassColor, grassTexture, GrassRoughness, white, Vector3.Zero, 0, true) with
        {
            AlphaMode = MaterialAlphaMode.Mask,
            AlphaCutoff = GrassAlphaCutoff,
            WindBend = GrassWindBend,
            WindFlutter = GrassWindFlutter,
        });
        bush = engine.Graphics.CreateMaterial(new MaterialRequest(BushColor, default(RenderResourceReference), BushRoughness, white, Vector3.Zero, 0, false) with
        {
            FlatShading = true,
        });
        grassMesh = engine.Graphics.CreateMeshResource(Clump(grass));
        bushMesh = engine.Graphics.CreateMeshResource(Bush(bush));
        grassLook = engine.Graphics.CreateMeshAppearance(grassMesh);
        bushLook = engine.Graphics.CreateMeshAppearance(bushMesh);
    }

    /// <summary>Grows grass and bushes on the projection's grass ground at the current densities.</summary>
    internal void Grow(VoxelScenePresentation projection)
    {
        ReadOnlyMemory<uint> onGrass = new uint[] { BlockRegistry.Get(BlockId.Grass).Slot };
        Set(projection, GrassScatter, grassDensity, new VoxelSceneScatterRequest(projection, GrassScatter, grassLook, grass, onGrass, grassDensity, Reach) with
        {
            Fade = FadeMetres,
            ScaleMin = GrassScaleMin,
            ScaleMax = GrassScaleMax,
            TintLow = GrassTintLow,
            TintHigh = GrassTintHigh,
            SlopeLimitDegrees = GrassSlopeDegrees,
            Align = GrassLean,
            MaximumInstances = MaximumGrass,
        });
        Set(projection, BushScatter, bushDensity, new VoxelSceneScatterRequest(projection, BushScatter, bushLook, bush, onGrass, bushDensity, Reach) with
        {
            Fade = FadeMetres,
            ScaleMin = BushScaleMin,
            ScaleMax = BushScaleMax,
            TintLow = BushTintLow,
            TintHigh = BushTintHigh,
            SlopeLimitDegrees = BushSlopeDegrees,
            CastsShadows = true,
            MaximumInstances = MaximumBushes,
            Seed = BushSeed,
        });
    }

    /// <summary>Sets the densities (0 removes that scatter) and grows again.</summary>
    internal void Tune(VoxelScenePresentation projection, float grassPerSquareMetre, float bushesPerSquareMetre)
    {
        grassDensity = grassPerSquareMetre;
        bushDensity = bushesPerSquareMetre;
        Grow(projection);
    }

    internal string Readout(VoxelScenePresentationReadout current) => FormattableString.Invariant(
        $"scatter grass={grassDensity} bushes={bushDensity} patches={current.ScatterPatchCount} copies={current.ScatterInstanceCount} overBudget={current.ScatterOverBudgetCount}");

    private void Set(VoxelScenePresentation projection, uint scatter, float density, VoxelSceneScatterRequest request)
    {
        if (density > 0) engine.VoxelScenePresentation.SetScatter(request);
        else engine.VoxelScenePresentation.RemoveScatter(new(projection, scatter));
    }

    public void Dispose()
    {
        grassLook.Dispose();
        bushLook.Dispose();
        grassMesh.Dispose();
        bushMesh.Dispose();
        grass.Dispose();
        bush.Dispose();
        grassTexture.Dispose();
    }

    /// <summary>
    /// A clump: <see cref="GrassCards"/> cards crossed at even angles near its origin; vertex alpha
    /// 0 at the root and 1 at the tip weights the wind's flutter.
    /// </summary>
    private static MeshResourceCreateRequest Clump(Material material)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<Color> colors = [];
        List<uint> indices = [];
        Random layout = new(ClumpLayoutSeed);
        Vector3 up = new(0, CardHeight, 0);
        for (int card = 0; card < GrassCards; card++)
        {
            float angle = card * MathF.PI / GrassCards;
            Vector3 across = new(MathF.Cos(angle) * CardWidth / 2, 0, MathF.Sin(angle) * CardWidth / 2);
            Vector3 normal = new(-MathF.Sin(angle), 0, MathF.Cos(angle));
            Vector3 root = new(((float)layout.NextDouble() - 0.5f) * ClumpSpread, 0, ((float)layout.NextDouble() - 0.5f) * ClumpSpread);
            uint first = (uint)positions.Count;
            positions.AddRange([root - across, root + across, root + across + up, root - across + up]);
            normals.AddRange([normal, normal, normal, normal]);
            uvs.AddRange([new(0, 1), new(1, 1), new(1, 0), new(0, 0)]);
            colors.AddRange([new(1, 1, 1, 0), new(1, 1, 1, 0), new(1, 1, 1, 1), new(1, 1, 1, 1)]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), colors.ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });
    }

    /// <summary>A bush: a squashed sphere of few rings whose lower part sinks below its origin.</summary>
    private static MeshResourceCreateRequest Bush(Material material)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<uint> indices = [];
        for (int ring = 0; ring <= BushRings; ring++)
        for (int segment = 0; segment <= BushSegments; segment++)
        {
            float theta = MathF.PI * ring / BushRings, phi = MathF.Tau * segment / BushSegments;
            Vector3 n = new(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
            positions.Add(new(n.X * BushWidth / 2, (n.Y + BushFootDepth) * BushHeight / (1 + BushFootDepth), n.Z * BushWidth / 2));
            normals.Add(n);
            uvs.Add(new((float)segment / BushSegments, (float)ring / BushRings));
        }

        const uint across = BushSegments + 1;
        for (uint ring = 0; ring < BushRings; ring++)
        for (uint segment = 0; segment < BushSegments; segment++)
        {
            uint a = ring * across + segment, b = a + across;
            indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), ReadOnlyMemory<Color>.Empty, indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });
    }
}
