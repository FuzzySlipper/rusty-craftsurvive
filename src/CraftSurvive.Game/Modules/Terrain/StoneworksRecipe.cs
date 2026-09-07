using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain.Recipes;

namespace CraftSurvive.Game.Modules.Terrain;

// A second consumer of recipe composition. Dimensions and art policy belong
// here; the writer and architectural recipes know nothing about debug UI,
// player state, resource publication or this particular scene.
internal static class StoneworksRecipe
{
    private const float Floor = 3f;
    private const float UpperFloor = 5f;
    private const float GatewayZ = 10f;
    private const float HallZ = 22f;
    private const float EndZ = 32f;
    private const float Thickness = 0.6f;
    private static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    internal static void Compose(IEngineContext engine, StoneworksMaterials materials, CourtyardSettings settings,
        Action<RecipeSurface> emit)
    {
        RecipeWriter writer = new(engine.ImplicitSurfaces,
            new(settings.CellSize, settings.CreaseDegrees, 0.45f, settings.MaterialBoundaryMode, settings.MaterialSampleSpacing), emit);
        ArchitecturalRecipes architecture = new(writer);
        WallPalette masonry = new(materials.Limestone, materials.Mortar, materials.Limestone, materials.Plaster);
        WallPalette plaster = masonry with { Body = materials.Brick };
        float half = settings.Width * 0.5f;
        Func<int, float> Wear(string name) => bay => engine.Random.DrawKeyed(new(settings.Seed,
            "stoneworks.wear", $"{name}:{bay}", 0, 100)).Value / 100f;

        writer.Box("garden grounds", new(-half - 6f, 1.8f, -16f), new(half + 6f, 2.5f, 38f), materials.Moss, Identity);
        writer.Box("passage foundation", new(-3.6f, 2.5f, GatewayZ), new(3.6f, UpperFloor - 0.5f, HallZ), materials.Mortar, Identity);
        writer.Box("hall foundation", new(-6.5f, 2.5f, HallZ), new(6.5f, UpperFloor - 0.5f, EndZ + 0.5f), materials.Mortar, Identity);
        writer.Box("stoneworks court", new(-half - 0.5f, Floor - 0.5f, -10.5f), new(half + 0.5f, Floor, GatewayZ), materials.Paving, Identity);
        writer.Box("stoneworks passage", new(-3.6f, UpperFloor - 0.5f, GatewayZ), new(3.6f, UpperFloor, HallZ), materials.Paving, Identity);
        writer.Box("stoneworks hall", new(-6.5f, UpperFloor - 0.5f, HallZ), new(6.5f, UpperFloor, EndZ + 0.5f), materials.Paving, Identity);
        Stairs(writer, materials.Limestone);

        architecture.Wall("south dressed wall", new(new(half, Floor, -10), 180, settings.Width, 3.8f, Thickness), WallFinish.DressedStone, masonry, Wear("south dressed wall"));
        architecture.Wall("west plaster", new(new(-half, Floor, -10), -90, 20, 4.8f, Thickness), WallFinish.BrokenPlaster, plaster, Wear("west plaster"));
        architecture.Wall("east masonry", new(new(half, Floor, GatewayZ), 90, 20, 3.6f, Thickness), WallFinish.Masonry, masonry, Wear("east masonry"));
        WallOpening gate = new(half + settings.DoorOffset, settings.DoorWidth, 4.0f, true);
        architecture.Wall("arched gateway", new(new(-half, Floor, GatewayZ), 0, settings.Width, 6.7f, Thickness, gate), WallFinish.BrokenPlaster, plaster, Wear("arched gateway"));

        // Repeated open arches establish a readable rhythm through the passage.
        for (int bay = 0; bay < 3; bay++)
        {
            const float bayLength = 4f;
            float z = GatewayZ + bay * bayLength;
            WallOpening arch = new(bayLength * 0.5f, 2.5f, 1.9f, true);
            architecture.Wall("west arcade", new(new(-3.3f, UpperFloor, z), -90, bayLength, 4.2f, Thickness, arch), WallFinish.DressedStone, masonry, Wear("west arcade"));
            architecture.Wall("east arcade", new(new(3.3f, UpperFloor, z + bayLength), 90, bayLength, 4.2f, Thickness, arch), WallFinish.DressedStone, masonry, Wear("east arcade"));
            writer.Box("arcade beam", new(-3.6f, 9.15f, z + 0.15f), new(3.6f, 9.5f, z + 0.5f), materials.Timber, Identity);
        }
        writer.Box("passage canopy", new(-3.7f, 9.5f, GatewayZ), new(3.7f, 9.8f, HallZ), materials.Timber, Identity);
        architecture.Wall("hall west", new(new(-6.2f, UpperFloor, HallZ), -90, 10, 4.5f, Thickness), WallFinish.BrokenPlaster, plaster, Wear("hall west"));
        architecture.Wall("hall east", new(new(6.2f, UpperFloor, EndZ), 90, 10, 4.5f, Thickness), WallFinish.DressedStone, masonry, Wear("hall east"));
        architecture.Wall("hall rear", new(new(-6.2f, UpperFloor, EndZ), 0, 12.4f, 5.8f, Thickness), WallFinish.DressedStone, masonry, Wear("hall rear"));
        // Short front returns leave a generous, aligned transition from arcade.
        foreach (float x in new[] { -6.2f, 3.6f })
            architecture.Wall("hall return", new(new(x, UpperFloor, HallZ), 0, 2.6f, 4.5f, Thickness), WallFinish.DressedStone, masonry, Wear("hall return"));

        // A lighter central route, inset joints and low garden beds divide the
        // court into deliberate masses rather than an uninterrupted noisy plane.
        for (int tile = 0; tile < 7; tile++)
        {
            float z = -8f + tile * 1.9f;
            writer.Box("path slab", new(-1.6f, Floor - 0.05f, z), new(1.6f, Floor + 0.035f, z + 1.78f), materials.Limestone, Identity);
        }
        foreach (float side in new[] { -1f, 1f })
        {
            float x = side * (half - 2.5f);
            writer.Box("raised bed stone", new(x - 1.5f, Floor, 2.5f), new(x + 1.5f, Floor + 0.42f, 5.5f), materials.Mortar, Identity);
            writer.Box("raised bed moss", new(x - 1.28f, Floor + 0.3f, 2.72f), new(x + 1.28f, Floor + 0.46f, 5.28f), materials.Moss, Identity);
            for (int stone = 0; stone < 3; stone++)
            {
                float z = 2.5f + stone;
                writer.Box("bed rim", new(x - 1.55f, Floor + 0.3f, z), new(x - 1.3f, Floor + 0.58f, z + 0.92f), materials.Limestone, Identity);
                writer.Box("bed rim", new(x + 1.3f, Floor + 0.3f, z), new(x + 1.55f, Floor + 0.58f, z + 0.92f), materials.Limestone, Identity);
            }
        }
        Carving(writer, materials);
        if (settings.Study == "sampling") SamplingPanels(writer, materials, settings);
    }

    private static void Stairs(RecipeWriter writer, Material material)
    {
        using ImplicitRecipe field = writer.Begin();
        const int count = 6;
        const float run = 4f;
        float rise = (UpperFloor - Floor) / count;
        ImplicitNode stairs = default;
        for (int step = 0; step < count; step++)
        {
            float top = Floor + rise * (step + 1);
            float front = GatewayZ - run + run * step / count;
            ImplicitNode tread = field.Box(new(-2f, Floor - 0.1f, front), new(2f, top, GatewayZ + 0.08f));
            Vector3 normal = Vector3.Normalize(new Vector3(0, 1, -1));
            tread = field.Intersect(tread, field.Plane(normal, Vector3.Dot(normal, new(0, top, front + rise))));
            stairs = step == 0 ? tread : field.Union(stairs, tread);
        }
        writer.Surface("stone stair flight", field, stairs, new(-2, Floor - 0.1f, 6), new(2, UpperFloor, 10.08f), material, Identity);
    }

    private static void Carving(RecipeWriter writer, StoneworksMaterials materials)
    {
        Transform placement = new(new(0, UpperFloor, 30.8f), Quaternion.Identity, Vector3.One);
        writer.Box("carved shrine plinth", new(-2.4f, 0, -1f), new(2.4f, 0.4f, 0.8f), materials.Mortar, placement);
        writer.Box("carved shrine lip", new(-2.5f, 0.4f, -1.1f), new(2.5f, 0.65f, 0.9f), materials.Limestone, placement);
        using ImplicitRecipe field = writer.Begin();
        ImplicitNode slab = field.Box(new(-1.65f, 0.6f, -0.5f), new(1.65f, 4.1f, 0.35f));
        // A round-backed recess and deeply cut rays preserve the graphic motif
        // under motion; metal center and quiet surrounding stone carry contrast.
        slab = field.Subtract(slab, field.Ellipsoid(new(0, 2.6f, -0.55f), new(1.15f, 1.15f, 0.32f)));
        for (int ray = 0; ray < 8; ray++)
        {
            float angle = ray * MathF.Tau / 8;
            ImplicitNode slot = field.Box(new(-0.06f, 0.58f, -0.8f), new(0.06f, 1.02f, -0.12f));
            slot = field.Place(slot, new(new(0, 2.6f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), Vector3.One));
            slab = field.Subtract(slab, slot);
        }
        writer.Surface("sun carved stone", field, slab, new(-1.65f, 0.6f, -0.5f), new(1.65f, 4.1f, 0.35f), materials.Limestone, placement, cellSize: 0.10f);
        ImplicitNode boss = field.Ellipsoid(new(0, 2.6f, -0.38f), new(0.40f, 0.40f, 0.18f));
        writer.Surface("bronze sun", field, boss, new(-0.4f, 2.2f, -0.56f), new(0.4f, 3f, -0.2f), materials.Bronze, placement, cellSize: 0.10f);
        foreach (float x in new[] { -3.8f, 3.8f })
        {
            Transform column = new(new(x, UpperFloor, 29.8f), Quaternion.Identity, Vector3.One);
            writer.Box("column base", new(-0.65f, 0, -0.65f), new(0.65f, 0.4f, 0.65f), materials.Limestone, column);
            ImplicitNode shaft = field.Capsule(new(0, 0.6f, 0), new(0, 3.6f, 0), 0.38f);
            shaft = field.Intersect(shaft, field.Box(new(-0.5f, 0.4f, -0.5f), new(0.5f, 3.8f, 0.5f)));
            writer.Surface("round column", field, shaft, new(-0.4f, 0.4f, -0.4f), new(0.4f, 3.8f, 0.4f), materials.Limestone, column);
            writer.Box("column capital", new(-0.6f, 3.8f, -0.6f), new(0.6f, 4.12f, 0.6f), materials.Limestone, column);
        }
    }

    private static void SamplingPanels(RecipeWriter writer, StoneworksMaterials materials, CourtyardSettings settings)
    {
        // Identical local designs at perpendicular and oblique orientations.
        // Increasing sample density is deliberate and confined to these panels.
        float cell = settings.Detail switch { "coarse" => 0.32f, "fine" => 0.08f, _ => 0.16f };
        for (int panel = 0; panel < 3; panel++)
        {
            Transform placement = new(new(-6 + panel * 4f, Floor + 0.4f, -3f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, panel * MathF.PI / 4), Vector3.One);
            using ImplicitRecipe field = writer.Begin();
            ImplicitNode slab = field.Box(new(-1.3f, 0, -0.2f), new(1.3f, 2.4f, 0.2f));
            List<ImplicitMaterialRegion> regions = [];
            float[] widths = [0.04f, 0.08f, 0.16f, 0.32f];
            for (int motif = 0; motif < widths.Length; motif++)
            {
                float x = -0.9f + motif * 0.6f;
                float width = widths[motif];
                slab = field.Subtract(slab, field.Box(new(x - width / 2, 0.2f, -0.3f), new(x + width / 2, 0.95f, -0.10f)));
                slab = field.Union(slab, field.Box(new(x - width / 2, 2.35f, -0.12f), new(x + width / 2, 2.7f, 0.12f)));
                ImplicitNode stripe = field.Box(new(x - width / 2, 1.15f, -0.5f), new(x + width / 2, 1.65f, 0.5f));
                regions.Add(new(stripe, materials.Bronze));
                ImplicitNode patch = field.Sphere(new(x, 2f, -0.2f), width * 0.6f);
                regions.Add(new(patch, materials.Brick));
            }
            writer.Surface($"sampling panel {panel}", field, slab, new(-1.3f, 0, -0.2f), new(1.3f, 2.7f, 0.2f),
                materials.Limestone, placement, regions.ToArray(), cell);
        }
    }
}
