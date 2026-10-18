using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// What grows on the overworld's ground (Engine #9546): clumps of grass cards, and generated flower
/// clumps and field stones (#9668), which
/// the Engine scatters on the grass block's ground around the camera. The product decides what
/// grows, where and how densely; the Engine places, draws, fades and re-places the copies.
/// Here each kind grows on the full-detail voxel ground out to <see cref="VegetationTiles.VoxelReach"/>,
/// fading out as the vegetation tiles' copies of the same spots fade in (#10016); the tiles carry
/// ground cover on to the player's chosen reach.
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
    internal const float DefaultGrassDensity = 3.5f;

    /// <summary>
    /// The vegetation tiles (past the near 40 m) grow grass at most this thickly, whatever the near density (#10038). The
    /// near band is where bare ground shows between clumps; past it a low view hides the ground, and matching the near
    /// 3.5 there cost about 0.7 ms more of the world pass in open meadow. Every copy is also a row in one renderer buffer
    /// whose ceiling depends on the device: 16.7 million rows on RADV, 2 million where a binding is 128 MiB (Engine
    /// #10040 refuses patches past it rather than failing). At this density the far tiles hold about 1.24 million copies
    /// at the longest Ground cover reach.
    /// </summary>
    private const float TileGrassDensityLimit = 2.5f;
    internal const string PropFolder = "models/scatter/";
    private const float GrassSlopeDegrees = 38f;
    private const float GrassLean = 0.35f;
    private const float GrassScaleMin = 0.75f, GrassScaleMax = 1.3f;
    private const uint MaximumGrass = 60_000;
    private const uint GrassSeedOffset = 100;
    private static readonly Vector3 GrassTintLow = new(0.78f, 0.82f, 0.68f), GrassTintHigh = new(1.05f, 1.05f, 0.92f);

    /// <summary>
    /// What else grows or lies on the ground, each a generated prop mesh (scripts/stylise-mesh.py,
    /// content/scatter.sources.json) scattered on its own: flower clumps and field stones. Density is per
    /// square metre of the named ground. Shrubs and ferns are the forest's to place (PlantTiles, #10032).
    /// </summary>
    private sealed record Layer(uint Id, string Mesh, bool Sways, float Density, float ScaleMin, float ScaleMax,
        float SlopeDegrees, float Align, bool CastsShadows, uint Maximum, Vector3 TintLow, Vector3 TintHigh, BlockId[] Ground);

    private static readonly Vector3 PlantTintLow = new(0.8f, 0.85f, 0.75f), PlantTintHigh = new(1.12f, 1.08f, 0.92f);
    private static readonly Vector3 StoneTintLow = new(0.85f, 0.85f, 0.85f), StoneTintHigh = new(1.1f, 1.05f, 1f);
    private static readonly BlockId[] Meadow = [BlockId.Grass];
    // Bare earth carries no field stones: a town's yard is trodden earth (#9895).
    private static readonly BlockId[] Stony = [BlockId.Grass, BlockId.DryGrass, BlockId.Stone, BlockId.Gravel, BlockId.Snow];
    private static readonly BlockId[] DryMeadow = [BlockId.DryGrass];

    /// <summary>
    /// Dry meadow's grass (#10041): the meadow's clumps in straw, a little sparser, each variant a kind of its own with
    /// these keys (one per grass variant, in order). Its far cards are the meadow's, tinted dry.
    /// </summary>
    private static readonly uint[] DryGrassKeys = [11, 12, 13, 14];
    private const float DryGrassShare = 0.8f;
    private static readonly Color DryCardColor = new(1.3f, 1.05f, 0.55f, 1);
    private static readonly Layer[] Layers =
    [
        new(4, "flowers-1", true, 0.1f, 0.7f, 1.2f, 30f, 0.3f, false, 4_000, PlantTintLow, PlantTintHigh, Meadow),
        new(5, "flowers-2", true, 0.1f, 0.7f, 1.2f, 30f, 0.3f, false, 4_000, PlantTintLow, PlantTintHigh, Meadow),
        new(6, "stones-1", false, 0.008f, 0.6f, 1.6f, 45f, 0.8f, true, 1_000, StoneTintLow, StoneTintHigh, Stony),
        new(7, "stones-2", false, 0.008f, 0.6f, 1.6f, 45f, 0.8f, true, 1_000, StoneTintLow, StoneTintHigh, Stony),
    ];

    // Plants flutter by their vertex wind weight and lean a little; stones hold still.
    private const float PlantWindBend = 0.05f, PlantWindFlutter = 0.05f;
    private const float PlantRoughness = 0.85f, StoneRoughness = 0.95f;
    /// <summary>The role of a stone prop's one part (scripts/stylise-mesh.py's default for a prop that is not foliage).</summary>
    private const string StoneRole = "solid";

    // The clump: cards crossed at even angles around its origin, blades from the texture on them.
    private const int GrassCards = 4;
    private const float CardWidth = 0.6f, CardHeight = 0.65f, ClumpSpread = 0.35f;
    private const int ClumpLayoutSeed = 9546;
    private const float GrassWindBend = 0.08f, GrassWindFlutter = 0.06f;
    private const float GrassAlphaCutoff = 0.5f;
    private const float GrassRoughness = 0.9f;
    private static readonly Color GrassColor = new(1, 1, 1, 1);
    // A clump's blades are white at the root and tip; the wind weight (alpha) rises from 0 to 1.
    private static readonly Color ClumpRoot = new(1, 1, 1, 0), ClumpTip = new(1, 1, 1, 1);

    /// <summary>
    /// Shade grass (#10034): the meadow's clumps under the canopy, a cooler, darker green and darker still at the
    /// root, so a forest floor reads as deep grass in shade. PlantTiles places it as records on the forest floor.
    /// </summary>
    private static readonly Color ShadeGrassColor = new(0.74f, 0.86f, 0.7f, 1);
    private static readonly Color ShadeRoot = new(0.5f, 0.56f, 0.5f, 0), ShadeTip = new(0.95f, 1, 0.92f, 1);

    private readonly IEngineContext engine;
    private readonly RenderResource grassTexture;
    private readonly Material grass, dryCard, shadeGrass, blade, plant, stone;
    /// <summary>Textured props (leaf-card bushes, frond ferns, #9685, and field stones, #9831), and each layer's material.</summary>
    private readonly PropMaterials textured;
    private readonly Dictionary<uint, Material> layerMaterials = [];
    private readonly List<MeshResource> grassMeshes = [];
    private readonly Dictionary<uint, Appearance> grassLooks = [];
    private readonly List<(Appearance Look, float Share)> shadeLooks = [];
    private readonly List<(Appearance Near, Appearance Lite)> meadowBlades = [], dryBlades = [], shadeBlades = [];
    private readonly List<Appearance> bladeLooks = [];
    private readonly List<MeshResource> propMeshes = [];
    private readonly Dictionary<uint, Appearance> looks = [];
    private float grassDensity = DefaultGrassDensity;

    internal TerrainScatter(IEngineContext engine, ProductContent content, SurfaceMoss? moss = null)
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
        dryCard = engine.Graphics.CreateMaterial(new MaterialRequest(DryCardColor, grassTexture, GrassRoughness, white, Vector3.Zero, 0, true) with
        {
            AlphaMode = MaterialAlphaMode.Mask,
            AlphaCutoff = GrassAlphaCutoff,
            WindBend = GrassWindBend,
            WindFlutter = GrassWindFlutter,
        });
        shadeGrass = engine.Graphics.CreateMaterial(new MaterialRequest(ShadeGrassColor, grassTexture, GrassRoughness, white, Vector3.Zero, 0, true) with
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
        // Blades draw from their vertex colours alone, solid (nothing to alpha-cut), moving with the wind as the cards did.
        blade = engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), GrassRoughness, white, Vector3.Zero, 0, true) with
        {
            WindBend = GrassWindBend,
            WindFlutter = GrassWindFlutter,
        });
        for (int index = 0; index < GrassVariants.Length; index++)
        {
            GrassBlades.Style style = GrassBlades.Meadow[index % GrassBlades.Meadow.Length];
            int seed = BladeSeed + (int)GrassVariants[index].Id;
            meadowBlades.Add((BladeLook(style, GrassBlades.MeadowPalette, seed, false), BladeLook(style, GrassBlades.MeadowPalette, seed, true)));
            dryBlades.Add((BladeLook(style, GrassBlades.DryPalette, seed + DrySeedOffset, false), BladeLook(style, GrassBlades.DryPalette, seed + DrySeedOffset, true)));
            shadeBlades.Add((BladeLook(style, GrassBlades.ShadePalette, seed + ShadeSeedOffset, false),
                BladeLook(style, GrassBlades.ShadePalette, seed + ShadeSeedOffset, true)));
        }

        foreach (GrassVariant variant in GrassVariants)
        {
            MeshResource mesh = engine.Graphics.CreateMeshResource(Clump(grass, variant, ClumpRoot, ClumpTip));
            grassMeshes.Add(mesh);
            grassLooks[variant.Id] = engine.Graphics.CreateMeshAppearance(mesh);
            MeshResource shade = engine.Graphics.CreateMeshResource(Clump(shadeGrass, variant, ShadeRoot, ShadeTip));
            grassMeshes.Add(shade);
            shadeLooks.Add((engine.Graphics.CreateMeshAppearance(shade), variant.Share));
        }
        // Field stones grow the surface moss (#10044); plants do not.
        textured = new PropMaterials(engine, role => role == StoneRole
            ? new PropMaterials.Finish(StoneRoughness, 0, 0, SurfaceMoss.FieldStoneGrain)
            : new PropMaterials.Finish(PlantRoughness, PlantWindBend, PlantWindFlutter), moss: moss);
        foreach (Layer layer in Layers)
        {
            // A layer draws with one material: a textured prop is a single leaves or stone part on its texture.
            Material material = layer.Sways ? plant : stone;
            MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, PropFolder + layer.Mesh + PropMesh.Suffix,
                (role, texture) => material = texture is null ? material : textured.For(role, texture)));
            propMeshes.Add(mesh);
            layerMaterials[layer.Id] = material;
            looks[layer.Id] = engine.Graphics.CreateMeshAppearance(mesh);
        }
    }

    /// <summary>
    /// One kind of ground cover at the current densities: what it looks like, where and how thickly it
    /// grows, and the key and seed that fix its spots. The voxel scatter and the vegetation tiles
    /// (<see cref="VegetationTiles"/>, #10016) grow the same kinds, so a kind's copies hand over one for one.
    /// </summary>
    /// <param name="Mid">How the kind looks in the middle distance (the mid tiles), and <paramref name="Far"/> beyond it.</param>
    /// <param name="TileDensity">How thickly it grows on the vegetation tiles, past the near band.</param>
    internal readonly record struct Kind(uint Key, Appearance Look, Material Material, float Density, BlockId[] Ground,
        float SlopeDegrees, float Align, float ScaleMin, float ScaleMax, Vector3 TintLow, Vector3 TintHigh, uint Seed,
        bool CastsShadows, uint Maximum, (Appearance Look, Material Material) Mid, (Appearance Look, Material Material) Far, float TileDensity);

    /// <summary>
    /// Whether grass is blades (#10038), the default, or the painted cards it was. Blades are drawn near and in the middle
    /// distance (a lighter clump there); cards stay beyond, where a blade is under a pixel. The forest floor's shade grass
    /// follows the same choice. A session may switch it to compare (TerrainWorld).
    /// </summary>
    internal bool Blades { get; set; } = true;

    /// <summary>The shade grass's clumps, one per grass variant: how each looks near, in the middle distance (null for cards), and its share.</summary>
    internal IReadOnlyList<(Appearance Look, Appearance? Lite, float Share)> ShadeGrass => Blades
        ? [.. shadeBlades.Select((blade, index) => ((Appearance, Appearance?, float))(blade.Near, blade.Lite, shadeLooks[index].Share))]
        : [.. shadeLooks.Select(card => ((Appearance, Appearance?, float))(card.Look, null, card.Share))];

    internal IEnumerable<Kind> Kinds()
    {
        for (int index = 0; index < GrassVariants.Length; index++)
        {
            GrassVariant variant = GrassVariants[index];
            (Appearance Look, Material Material) card = (grassLooks[variant.Id], grass);
            (Appearance Look, Material Material) near = Blades ? (meadowBlades[index].Near, blade) : card;
            (Appearance Look, Material Material) mid = Blades ? (meadowBlades[index].Lite, blade) : card;
            yield return new(variant.Id, near.Look, near.Material, grassDensity * variant.Share, Meadow, GrassSlopeDegrees,
                GrassLean, GrassScaleMin, GrassScaleMax, GrassTintLow, GrassTintHigh, variant.Id + GrassSeedOffset, false,
                (uint)(MaximumGrass * variant.Share), mid, card, Math.Min(grassDensity, TileGrassDensityLimit) * variant.Share);
        }

        for (int index = 0; index < GrassVariants.Length; index++)
        {
            GrassVariant variant = GrassVariants[index];
            (Appearance Look, Material Material) card = (grassLooks[variant.Id], dryCard);
            (Appearance Look, Material Material) near = Blades ? (dryBlades[index].Near, blade) : card;
            (Appearance Look, Material Material) mid = Blades ? (dryBlades[index].Lite, blade) : card;
            float share = DryGrassShare * variant.Share;
            yield return new(DryGrassKeys[index], near.Look, near.Material, grassDensity * share, DryMeadow, GrassSlopeDegrees,
                GrassLean, GrassScaleMin, GrassScaleMax, GrassTintLow, GrassTintHigh, DryGrassKeys[index] + GrassSeedOffset, false,
                (uint)(MaximumGrass * share), mid, card, Math.Min(grassDensity, TileGrassDensityLimit) * share);
        }

        foreach (Layer layer in Layers)
        {
            float density = layer.Density * (grassDensity > 0 ? 1 : 0);
            (Appearance, Material) look = (looks[layer.Id], layerMaterials[layer.Id]);
            yield return new(layer.Id, looks[layer.Id], layerMaterials[layer.Id], density, layer.Ground, layer.SlopeDegrees,
                layer.Align, layer.ScaleMin, layer.ScaleMax, layer.TintLow, layer.TintHigh, layer.Id, layer.CastsShadows, layer.Maximum, look, look, density);
        }
    }

    /// <summary>Grows every kind on the projection's ground at the current densities, out to the tiles' handover.</summary>
    internal void Grow(VoxelScenePresentation projection)
    {
        foreach (Kind kind in Kinds())
        {
            ReadOnlyMemory<uint> ground = kind.Ground.Select(block => (uint)BlockRegistry.Get(block).Slot).ToArray();
            Set(projection, kind.Key, kind.Density, new VoxelSceneScatterRequest(projection, kind.Key, kind.Look, kind.Material, ground,
                kind.Density, VegetationTiles.VoxelReach) with
            {
                Fade = VegetationTiles.HandoverFade,
                ScaleMin = kind.ScaleMin,
                ScaleMax = kind.ScaleMax,
                TintLow = kind.TintLow,
                TintHigh = kind.TintHigh,
                SlopeLimitDegrees = kind.SlopeDegrees,
                Align = kind.Align,
                CastsShadows = kind.CastsShadows,
                MaximumInstances = kind.Maximum,
                Seed = kind.Seed,
            });
        }
    }

    /// <summary>Sets the densities (0 removes that scatter) and grows again.</summary>
    internal void Tune(VoxelScenePresentation projection, float grassPerSquareMetre)
    {
        grassDensity = grassPerSquareMetre;
        Grow(projection);
    }

    internal string Readout(VoxelScenePresentationReadout current) => FormattableString.Invariant(
        $"scatter grass={grassDensity} reach={VegetationTiles.VoxelReach}m patches={current.ScatterPatchCount} copies={current.ScatterInstanceCount} overBudget={current.ScatterOverBudgetCount}");

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
        foreach ((Appearance look, _) in shadeLooks) look.Dispose();
        shadeLooks.Clear();
        foreach (Appearance look in bladeLooks) look.Dispose();
        bladeLooks.Clear();
        foreach (MeshResource mesh in grassMeshes) mesh.Dispose();
        grassMeshes.Clear();
        foreach (MeshResource mesh in propMeshes) mesh.Dispose();
        propMeshes.Clear();
        grass.Dispose();
        shadeGrass.Dispose();
        dryCard.Dispose();
        blade.Dispose();
        plant.Dispose();
        stone.Dispose();
        textured.Dispose();
        grassTexture.Dispose();
    }

    /// <summary>
    /// A clump: <see cref="GrassCards"/> cards crossed at even angles near its origin, each showing the
    /// variant's atlas cell; vertex alpha 0 at the root and 1 at the tip weights the wind's flutter.
    /// </summary>
    /// <summary>The blade clumps' layouts are seeded from this, and the shade grass's from it offset, so they differ.</summary>
    private const int BladeSeed = 10038, ShadeSeedOffset = 500, DrySeedOffset = 1000;

    private Appearance BladeLook(GrassBlades.Style style, GrassBlades.Palette palette, int seed, bool lite)
    {
        MeshResource mesh = engine.Graphics.CreateMeshResource(GrassBlades.Clump(blade, style, palette, seed, lite));
        grassMeshes.Add(mesh);
        Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
        bladeLooks.Add(look);
        return look;
    }

    private static MeshResourceCreateRequest Clump(Material material, GrassVariant variant, Color rootColor, Color tipColor)
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
            colors.AddRange([rootColor, rootColor, tipColor, tipColor]);
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), colors.ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });
    }
}
