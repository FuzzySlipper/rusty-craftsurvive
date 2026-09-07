using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Implicit;

namespace CraftSurvive.Game.Modules.Terrain;

// A compact, walk-up study for judging the relationship between authored
// feature size and field sampling. Each panel remains an independent field so
// the 0.02m experiment cannot accidentally turn into a detailed courtyard.
internal static class DetailStudyRecipe
{
    internal const float FloorY = 3f;
    internal const float CoarseCellSize = 0.16f;
    internal const float NormalCellSize = 0.08f;
    internal const float FineCellSize = 0.02f;
    internal const float PanelWidth = 1.4f;
    internal const float PanelHeight = 1.70f;
    internal const float FixedStrokeWidth = 0.08f;
    internal const float FineStrokeWidth = 0.02f;

    // Faces look south over the court. These anchors are also stable camera
    // targets for the product UI; local geometry starts at FloorY.
    internal static readonly Vector3[] PanelStations =
    [
        new(4.8f, FloorY, -3.0f),
        new(1.6f, FloorY, -3.0f),
        new(-1.6f, FloorY, -3.0f),
        new(-4.8f, FloorY, -3.0f),
    ];

    private const float MasonryHeight = 0.62f;
    private const float FaceBottom = MasonryHeight + 0.12f;
    private const float FaceDepthFront = -0.13f;
    private const float FaceDepthBack = 0.10f;
    private const float BackingDepthFront = 0.16f;
    private const float BackingDepthBack = 0.38f;
    private const float MotifBottom = FaceBottom + 0.22f;
    private const float MotifTop = FaceBottom + PanelHeight - 0.20f;
    private const float MotifHalfWidth = PanelWidth * 0.31f;
    private const float BackingCellSize = 0.08f;
    private static readonly float[] BlockWidths = [0.64f, 0.32f, 0.16f, 0.08f];
    private static readonly float[] StrokeWidths = [0.16f, FixedStrokeWidth, 0.04f, FineStrokeWidth];

    internal static void Build(RecipeWriter writer, StoneworksMaterials materials, CourtyardSettings settings)
    {
        float cellSize = SamplingCell(settings.Detail);
        for (int panel = 0; panel < PanelStations.Length; panel++)
        {
            string scale = ScaleLabel(StrokeWidths[panel], cellSize);
            Transform placement = new(PanelStations[panel], Quaternion.Identity, Vector3.One);
            Material masonryMaterial = panel % 2 == 0 ? materials.Limestone : materials.Brick;

            BuildBacking(writer, materials, placement, panel, StrokeWidths[panel], cellSize, scale);
            BuildMasonry(writer, masonryMaterial, placement, panel, BlockWidths[panel], cellSize);
            BuildCarvedFace(writer, materials, placement, panel, StrokeWidths[panel], cellSize, scale);
        }
    }

    internal static void BuildFlatComparison(RecipeWriter writer, StoneworksMaterials materials, CourtyardSettings settings)
    {
        for (int panel = 0; panel < PanelStations.Length; panel++)
        {
            using ImplicitRecipe field = writer.Begin();
            Transform placement = new(PanelStations[panel], Quaternion.Identity, Vector3.One);
            ImplicitNode slab = field.Box(new(-PanelWidth / 2, FaceBottom, FaceDepthFront),
                new(PanelWidth / 2, FaceBottom + PanelHeight, FaceDepthBack));
            ImplicitNode motif = WovenDiamond(field, StrokeWidths[panel], FaceDepthFront - 0.08f, FaceDepthFront + 0.08f);
            writer.Surface($"detail flat motif panel-{panel} stroke-{StrokeWidths[panel]:F2}", field, slab,
                new(-PanelWidth / 2, FaceBottom, FaceDepthFront), new(PanelWidth / 2, FaceBottom + PanelHeight, FaceDepthBack),
                materials.Limestone, placement, [new(motif, materials.Bronze)], cellSize: NormalCellSize);
        }
    }

    private static void BuildBacking(RecipeWriter writer, StoneworksMaterials materials, Transform placement, int panel,
        float strokeWidth, float cellSize, string scale)
    {
        using ImplicitRecipe field = writer.Begin();
        ImplicitNode backing = field.Box(new(-PanelWidth * 0.5f - 0.07f, 0f, BackingDepthFront),
            new(PanelWidth * 0.5f + 0.07f, FaceBottom + PanelHeight + 0.10f, BackingDepthBack));
        // The last panel holds a flat bronze material region in the dark mortar
        // backing. It exercises the material split on an otherwise planar face
        // while the physical 0.06m recess remains visible in every panel.
        ImplicitMaterialRegion[] regions = panel == PanelStations.Length - 1
            ? [new(WovenDiamond(field, strokeWidth, BackingDepthFront - 0.08f, BackingDepthFront + 0.08f), materials.Bronze)]
            : [];
        string treatment = regions.Length == 0 ? "mortar" : "mortar-bronze-flat-region";
        float backingCellSize = regions.Length == 0 ? BackingCellSize : cellSize;
        writer.Surface($"detail backing panel-{panel} {treatment} {scale}", field, backing,
            new(-PanelWidth * 0.5f - 0.07f, 0f, BackingDepthFront),
            new(PanelWidth * 0.5f + 0.07f, FaceBottom + PanelHeight + 0.10f, BackingDepthBack), materials.Mortar, placement,
            regions, backingCellSize);
    }

    private static void BuildMasonry(RecipeWriter writer, Material material, Transform placement, int panel,
        float blockWidth, float cellSize)
    {
        // The recessed mortar plane remains visible through real gaps. Its
        // front is 0.06m behind the block backs, so no generated faces coincide.
        using ImplicitRecipe field = writer.Begin();
        float blockHeight = blockWidth * 0.48f;
        float joint = blockWidth * 0.12f;
        int rows = Math.Max(2, (int)MathF.Floor((MasonryHeight - 0.10f) / (blockHeight + joint)));
        int columns = Math.Max(2, (int)MathF.Floor((PanelWidth - joint) / (blockWidth + joint)));
        // A continuous footer remains extractable even when all tiny bricks
        // are missed by coarse sampling; missing bricks are an observable result.
        ImplicitNode blocks = field.Box(new(-PanelWidth * 0.5f, 0, FaceDepthFront),
            new(PanelWidth * 0.5f, 0.12f, FaceDepthBack));
        for (int row = 0; row < rows; row++)
        {
            float bottom = 0.16f + row * (blockHeight + joint);
            if (bottom + blockHeight > MasonryHeight - 0.03f) break;
            float offset = row % 2 == 0 ? 0f : (blockWidth + joint) * 0.5f;
            for (int column = -1; column <= columns; column++)
            {
                float left = -PanelWidth * 0.5f + column * (blockWidth + joint) + offset;
                float right = left + blockWidth;
                if (right <= -PanelWidth * 0.5f || left >= PanelWidth * 0.5f) continue;
                ImplicitNode block = field.Box(new(MathF.Max(left, -PanelWidth * 0.5f), bottom, FaceDepthFront),
                    new(MathF.Min(right, PanelWidth * 0.5f), bottom + blockHeight, FaceDepthBack));
                blocks = field.Union(blocks, block);
            }
        }

        string scale = ScaleLabel(blockWidth, cellSize);
        writer.Surface($"detail masonry panel-{panel} block-{scale}", field, blocks,
            new(-PanelWidth * 0.5f, 0f, FaceDepthFront), new(PanelWidth * 0.5f, MasonryHeight, FaceDepthBack),
            material, placement, cellSize: cellSize);
    }

    private static void BuildCarvedFace(RecipeWriter writer, StoneworksMaterials materials, Transform placement,
        int panel, float strokeWidth, float cellSize, string scale)
    {
        using ImplicitRecipe field = writer.Begin();
        ImplicitNode face = field.Box(new(-PanelWidth * 0.5f, FaceBottom, FaceDepthFront),
            new(PanelWidth * 0.5f, FaceBottom + PanelHeight, FaceDepthBack));
        ImplicitNode motif = WovenDiamond(field, strokeWidth, FaceDepthFront - 0.08f, FaceDepthBack + 0.08f);
        face = field.Subtract(face, motif);
        writer.Surface($"detail carving woven-diamond panel-{panel} {scale}", field, face,
            new(-PanelWidth * 0.5f, FaceBottom, FaceDepthFront),
            new(PanelWidth * 0.5f, FaceBottom + PanelHeight, FaceDepthBack), materials.Limestone, placement,
            cellSize: cellSize);
    }

    private static ImplicitNode WovenDiamond(ImplicitRecipe field, float strokeWidth, float depthFront, float depthBack)
    {
        // Two families of three clipped bands cross diagonally to leave a rune-
        // like diamond lattice. The cutter passes fully through the limestone
        // face but stops before its physically separate backing material.
        ImplicitNode window = field.Box(new(-MotifHalfWidth, MotifBottom, depthFront),
            new(MotifHalfWidth, MotifTop, depthBack));
        ImplicitNode motif = default;
        bool hasBand = false;
        foreach (float angle in new[] { MathF.PI / 4f, -MathF.PI / 4f })
        {
            foreach (float offset in new[] { -0.34f, 0f, 0.34f })
            {
                ImplicitNode band = field.Box(new(-0.88f, -strokeWidth * 0.5f, depthFront),
                    new(0.88f, strokeWidth * 0.5f, depthBack));
                band = field.Place(band, new(new(0f, (MotifBottom + MotifTop) * 0.5f + offset, 0f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), Vector3.One));
                band = field.Intersect(band, window);
                motif = hasBand ? field.Union(motif, band) : band;
                hasBand = true;
            }
        }

        return motif;
    }

    internal static float SamplingCell(string detail) => detail switch
    {
        "coarse" => CoarseCellSize,
        "fine" => FineCellSize,
        _ => NormalCellSize,
    };

    private static string ScaleLabel(float featureSize, float cellSize) => FormattableString.Invariant(
        $"feature-{featureSize:F2}m-cell-{cellSize:F2}m");
}
