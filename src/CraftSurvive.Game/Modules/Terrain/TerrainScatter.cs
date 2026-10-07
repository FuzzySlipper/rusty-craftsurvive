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

    /// <summary>
    /// The grass clump variants: each samples one cell of the painted 2x2 atlas (scripts/make-grass-atlas.py,
    /// content/grass.sources.json) and takes its share of the grass density; the seed-head tuft stands taller.
    /// </summary>
    private sealed record GrassVariant(uint Id, int Cell, float Share, float HeightScale);
    private static readonly GrassVariant[] GrassVariants =
    [
        new(0, 0, 0.35f, 1f),
        new(8, 1, 0.35f, 1f),
        new(9, 2, 0.15f, 1.35f),
        new(10, 3, 0.15f, 0.85f),
    ];
    private const int AtlasCells = 2;

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
    private const uint GrassSeedOffset = 100;
    private static readonly Vector3 GrassTintLow = new(0.78f, 0.82f, 0.68f), GrassTintHigh = new(1.05f, 1.05f, 0.92f);

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
    private const float CardWidth = 0.6f, CardHeight = 0.65f, ClumpSpread = 0.35f;
    private const int ClumpLayoutSeed = 9546;
    private const float GrassWindBend = 0.08f, GrassWindFlutter = 0.06f;
    private const float GrassAlphaCutoff = 0.5f;
    private const float GrassRoughness = 0.9f;
    private static readonly Color GrassColor = new(1, 1, 1, 1);

    private readonly IEngineContext engine;
    private readonly RenderResource grassTexture;
    private readonly Material grass, plant, stone;
    private readonly List<MeshResource> grassMeshes = [];
    private readonly Dictionary<uint, Appearance> grassLooks = [];
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
        foreach (GrassVariant variant in GrassVariants)
        {
            MeshResource mesh = engine.Graphics.CreateMeshResource(Clump(grass, variant));
            grassMeshes.Add(mesh);
            grassLooks[variant.Id] = engine.Graphics.CreateMeshAppearance(mesh);
        }
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
        foreach (GrassVariant variant in GrassVariants)
        {
            float density = grassDensity * variant.Share;
            Set(projection, variant.Id, density, new VoxelSceneScatterRequest(projection, variant.Id, grassLooks[variant.Id], grass, onGrass, density, Reach) with
            {
                Fade = FadeMetres,
                ScaleMin = GrassScaleMin,
                ScaleMax = GrassScaleMax,
                TintLow = GrassTintLow,
                TintHigh = GrassTintHigh,
                SlopeLimitDegrees = GrassSlopeDegrees,
                Align = GrassLean,
                MaximumInstances = (uint)(MaximumGrass * variant.Share),
                Seed = variant.Id + GrassSeedOffset,
            });
        }

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
        foreach (Appearance look in grassLooks.Values) look.Dispose();
        grassLooks.Clear();
        foreach (MeshResource mesh in grassMeshes) mesh.Dispose();
        grassMeshes.Clear();
        foreach (MeshResource mesh in propMeshes) mesh.Dispose();
        propMeshes.Clear();
        grass.Dispose();
        plant.Dispose();
        stone.Dispose();
        grassTexture.Dispose();
    }

    /// <summary>
    /// A clump: <see cref="GrassCards"/> cards crossed at even angles near its origin, each showing the
    /// variant's atlas cell; vertex alpha 0 at the root and 1 at the tip weights the wind's flutter.
    /// </summary>
    private static MeshResourceCreateRequest Clump(Material material, GrassVariant variant)
    {
        float u0 = (float)(variant.Cell % AtlasCells) / AtlasCells, v0 = (float)(variant.Cell / AtlasCells) / AtlasCells;
        float u1 = u0 + (1f / AtlasCells), v1 = v0 + (1f / AtlasCells);
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<Color> colors = [];
        List<uint> indices = [];
        Random layout = new(ClumpLayoutSeed + variant.Cell);
        Vector3 up = new(0, CardHeight * variant.HeightScale, 0);
        for (int card = 0; card < GrassCards; card++)
        {
            float angle = card * MathF.PI / GrassCards;
            Vector3 across = new(MathF.Cos(angle) * CardWidth / 2, 0, MathF.Sin(angle) * CardWidth / 2);
            Vector3 normal = new(-MathF.Sin(angle), 0, MathF.Cos(angle));
            Vector3 root = new(((float)layout.NextDouble() - 0.5f) * ClumpSpread, 0, ((float)layout.NextDouble() - 0.5f) * ClumpSpread);
            uint first = (uint)positions.Count;
            positions.AddRange([root - across, root + across, root + across + up, root - across + up]);
            normals.AddRange([normal, normal, normal, normal]);
            uvs.AddRange([new(u0, v1), new(u1, v1), new(u1, v0), new(u0, v0)]);
            colors.AddRange([new(1, 1, 1, 0), new(1, 1, 1, 0), new(1, 1, 1, 1), new(1, 1, 1, 1)]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), colors.ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });
    }
}
