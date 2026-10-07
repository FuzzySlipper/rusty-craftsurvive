using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// What grows on the overworld's ground (Engine #9546): clumps of grass cards, and generated low-poly
/// bushes, ferns, flowers and field stones (#9668)
/// the Engine scatters on the grass block's ground around the camera. The product decides what
/// grows, where and how densely; the Engine places, draws, fades and re-places the copies.
/// </summary>
internal sealed class TerrainScatter : IDisposable
{
    internal const string GrassContentPath = "textures/grass-blades.png";

    private const uint GrassScatter = 0;

    /// <summary>Clumps per square metre of grass ground, and how far from the camera they grow.</summary>
    internal const float DefaultGrassDensity = 2.5f;
    internal const float DefaultBushDensity = 0.03f;
    internal const string PropFolder = "models/scatter/";
    /// <summary>Inside the coarse distance (<see cref="TerrainPresentation.CoarseBeyondMetres"/>): coarse chunks grow nothing.</summary>
    private const float Reach = 44f;
    private const float FadeMetres = 12f;
    private const float GrassSlopeDegrees = 38f;
    private const float GrassLean = 0.35f;
    private const float GrassScaleMin = 0.75f, GrassScaleMax = 1.3f;
    private const uint MaximumGrass = 60_000;
    private static readonly Vector3 GrassTintLow = new(0.62f, 0.66f, 0.5f), GrassTintHigh = new(0.95f, 0.95f, 0.82f);

    /// <summary>
    /// What else grows or lies on the ground, each a generated prop mesh (scripts/stylise-mesh.py,
    /// content/scatter.sources.json) scattered on its own: bushes, a fern, flower clumps and field
    /// stones. Density is per square metre of the named ground; the bushes' share follows the bush
    /// density the debug command tunes.
    /// </summary>
    private sealed record Layer(uint Id, string Mesh, bool Sways, float Density, float ScaleMin, float ScaleMax,
        float SlopeDegrees, float Align, bool CastsShadows, uint Maximum, Vector3 TintLow, Vector3 TintHigh, BlockId[] Ground,
        bool Bush = false);

    private static readonly Vector3 PlantTintLow = new(0.8f, 0.85f, 0.75f), PlantTintHigh = new(1.12f, 1.08f, 0.92f);
    private static readonly Vector3 StoneTintLow = new(0.85f, 0.85f, 0.85f), StoneTintHigh = new(1.1f, 1.05f, 1f);
    private static readonly BlockId[] Meadow = [BlockId.Grass];
    private static readonly BlockId[] Stony = [BlockId.Grass, BlockId.Dirt, BlockId.Stone, BlockId.Gravel, BlockId.Snow];
    private static readonly Layer[] Layers =
    [
        new(1, "bush-1", true, DefaultBushDensity / 2, 0.7f, 1.4f, 28f, 0f, true, 1_500, PlantTintLow, PlantTintHigh, Meadow, Bush: true),
        new(2, "bush-2", true, DefaultBushDensity / 2, 0.7f, 1.4f, 28f, 0f, true, 1_500, PlantTintLow, PlantTintHigh, Meadow, Bush: true),
        new(3, "fern-1", true, 0.02f, 0.7f, 1.3f, 32f, 0.2f, false, 2_000, PlantTintLow, PlantTintHigh, Meadow),
        new(4, "flowers-1", true, 0.05f, 0.7f, 1.2f, 30f, 0.3f, false, 4_000, PlantTintLow, PlantTintHigh, Meadow),
        new(5, "flowers-2", true, 0.05f, 0.7f, 1.2f, 30f, 0.3f, false, 4_000, PlantTintLow, PlantTintHigh, Meadow),
        new(6, "stones-1", false, 0.008f, 0.6f, 1.6f, 45f, 0.8f, true, 1_000, StoneTintLow, StoneTintHigh, Stony),
        new(7, "stones-2", false, 0.008f, 0.6f, 1.6f, 45f, 0.8f, true, 1_000, StoneTintLow, StoneTintHigh, Stony),
    ];

    // Plants flutter by their vertex wind weight and lean a little; stones hold still.
    private const float PlantWindBend = 0.05f, PlantWindFlutter = 0.05f;
    private const float PlantRoughness = 0.85f, StoneRoughness = 0.95f;

    // The clump: cards crossed at even angles around its origin, blades from the texture on them.
    private const int GrassCards = 4;
    private const float CardWidth = 0.5f, CardHeight = 0.55f, ClumpSpread = 0.35f;
    private const int ClumpLayoutSeed = 9546;
    private const float GrassWindBend = 0.08f, GrassWindFlutter = 0.06f;
    private const float GrassAlphaCutoff = 0.5f;
    private const float GrassRoughness = 0.9f;
    private static readonly Color GrassColor = new(1, 1, 1, 1);

    private readonly IEngineContext engine;
    private readonly RenderResource grassTexture;
    private readonly Material grass, plant, stone;
    private readonly MeshResource grassMesh;
    private readonly Appearance grassLook;
    private readonly List<MeshResource> propMeshes = [];
    private readonly Dictionary<uint, Appearance> looks = [];
    private float grassDensity = DefaultGrassDensity, bushDensity = DefaultBushDensity;

    internal TerrainScatter(IEngineContext engine, ProductContent content)
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
        plant = engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), PlantRoughness, white, Vector3.Zero, 0, true) with
        {
            FlatShading = true,
            WindBend = PlantWindBend,
            WindFlutter = PlantWindFlutter,
        });
        stone = engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), StoneRoughness, white, Vector3.Zero, 0, false) with
        {
            FlatShading = true,
        });
        grassMesh = engine.Graphics.CreateMeshResource(Clump(grass));
        grassLook = engine.Graphics.CreateMeshAppearance(grassMesh);
        foreach (Layer layer in Layers)
        {
            Material material = layer.Sways ? plant : stone;
            MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, PropFolder + layer.Mesh + PropMesh.Suffix, _ => material));
            propMeshes.Add(mesh);
            looks[layer.Id] = engine.Graphics.CreateMeshAppearance(mesh);
        }
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
        foreach (Layer layer in Layers)
        {
            float density = layer.Bush ? bushDensity / 2 : layer.Density * (grassDensity > 0 ? 1 : 0);
            ReadOnlyMemory<uint> ground = layer.Ground.Select(block => (uint)BlockRegistry.Get(block).Slot).ToArray();
            Set(projection, layer.Id, density, new VoxelSceneScatterRequest(projection, layer.Id, looks[layer.Id], layer.Sways ? plant : stone,
                ground, density, Reach) with
            {
                Fade = FadeMetres,
                ScaleMin = layer.ScaleMin,
                ScaleMax = layer.ScaleMax,
                TintLow = layer.TintLow,
                TintHigh = layer.TintHigh,
                SlopeLimitDegrees = layer.SlopeDegrees,
                Align = layer.Align,
                CastsShadows = layer.CastsShadows,
                MaximumInstances = layer.Maximum,
                Seed = layer.Id,
            });
        }
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
        foreach (Appearance look in looks.Values) look.Dispose();
        looks.Clear();
        grassLook.Dispose();
        grassMesh.Dispose();
        foreach (MeshResource mesh in propMeshes) mesh.Dispose();
        propMeshes.Clear();
        grass.Dispose();
        plant.Dispose();
        stone.Dispose();
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
}
