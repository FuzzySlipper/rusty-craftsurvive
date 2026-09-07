using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain.Recipes;

namespace CraftSurvive.Game.Modules.Terrain;

// Product-owned composition: fixed broad landforms and separately sampled
// incisions. Every surface goes through the same Engine field/mesh path.
internal static class CaveRecipe
{
    internal const float Floor = 3f;
    private const float RockCell = 0.30f;
    private const float Roof = 12.4f;
    private const float CourseGap = 0.055f;
    private const float CarvingWidth = 0.12f;
    private static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    private static readonly float[] CourseHeights = [0.75f, 1.6f, 0.95f, 1.8f, 1.25f, 1.45f, 1.65f];
    private static readonly float[] WallDepth = [7.1f, 7.8f, 7.4f, 8.15f, 7.55f, 7.05f, 6.65f];
    private static readonly float[] ColumnRadii = [2.0f, 1.7f, 1.37f, 1.48f, 1.12f, 1.28f, 1.53f, 1.8f];
    private static readonly float[] ColumnHeights = [0.65f, 1.2f, 1.4f, 1.1f, 1.65f, 1.25f, 1.1f, 1.2f];

    internal static void Compose(IEngineContext engine, StoneworksMaterials materials, CourtyardSettings settings,
        Action<RecipeSurface> emit)
    {
        RecipeWriter writer = new(engine.ImplicitSurfaces,
            new(RockCell, settings.CreaseDegrees, 0.65f, settings.MaterialBoundaryMode), emit);
        writer.Box("cave chamber floor", new(-12f, Floor - 0.65f, -11f), new(12f, Floor, 16f), materials.Mortar, Identity);
        Walls(writer, materials);
        Terraces(writer, materials);
        Column(writer, materials, new(-3.0f, Floor, 4.4f), 1f, "hero", settings);
        Column(writer, materials, new(4.7f, Floor, 10.2f), 0.72f, "rear", settings);
        Ceiling(writer, materials);
        Incisions(writer, materials, settings);
        Scatter(writer, materials);
    }

    private static void Walls(RecipeWriter writer, StoneworksMaterials materials)
    {
        writer.Box("cave left deep rock", new(-12.2f, Floor, -8f), new(-11.4f, Roof + 1f, 16f), materials.Mortar, Identity);
        writer.Box("cave right deep rock", new(11.4f, Floor, -8f), new(12.2f, Roof + 1f, 16f), materials.Mortar, Identity);
        const float bayLength = 5.4f;
        const float outer = 11.5f;
        foreach (float side in new[] { -1f, 1f })
        for (int bay = 0; bay < 4; bay++)
        for (int course = 0; course < WallDepth.Length; course++)
        {
            float inner = WallDepth[(course + bay) % WallDepth.Length];
            float z = -7.5f + bay * bayLength;
            float y = Floor;
            for (int prior = 0; prior < course; prior++) y += CourseHeights[(prior + bay) % CourseHeights.Length];
            float courseHeight = CourseHeights[(course + bay) % CourseHeights.Length];
            float minX = side < 0 ? -outer : inner;
            float maxX = side < 0 ? -inner : outer;
            using ImplicitRecipe field = writer.Begin();
            Vector3 min = new(minX, y, z);
            Vector3 max = new(maxX, y + courseHeight - CourseGap, z + bayLength + 0.05f);
            ImplicitNode root = field.Box(min, max);
            // Broad angular bites, offset between strata, create stepped rock
            // rather than a uniform grid of stones or a smoothly noisy wall.
            Vector3 bite = new(side * (inner + 0.08f), y + 0.65f, z + 1.4f + (course % 3) * 0.85f);
            ImplicitNode notch = field.Box(new(-0.48f, -0.76f, -0.38f), new(0.48f, 0.76f, 0.38f));
            notch = field.Place(notch, new(bite, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.43f), Vector3.One));
            root = field.Subtract(root, notch);
            // Reserve the rock around the right-wall incised face; its backing
            // fills the recess independently, with real clearance at the cuts.
            if (side > 0 && bay == 2)
                root = field.Subtract(root, field.Box(new(6f, 4.45f, 4.2f), new(8.6f, 7f, 7.8f)));
            writer.Surface($"cave strata {side}/{bay}/{course}", field, root, min, max,
                course % 4 == 1 ? materials.Mortar : materials.Paving, Identity);
        }
        // The back wall frames a tall daylight opening instead of closing the
        // whole composition with a flat backdrop.
        foreach (float side in new[] { -1f, 1f })
        for (int level = 0; level < 6; level++)
        {
            float inner = 2.5f + (level % 2) * 0.45f;
            Vector3 min = new(side < 0 ? -10f : inner, Floor + level * 1.55f, 13.2f);
            Vector3 max = new(side < 0 ? -inner : 10f, Floor + (level + 1) * 1.55f - CourseGap, 15.3f);
            writer.Box("cave rear opening strata", min, max, level % 3 == 0 ? materials.Mortar : materials.Paving, Identity);
        }
    }

    private static void Terraces(RecipeWriter writer, StoneworksMaterials materials)
    {
        for (int tier = 0; tier < 4; tier++)
        {
            float inner = -4.4f - tier * 0.78f;
            float top = Floor + (tier + 1) * 0.48f;
            Vector3 min = new(-9.8f, Floor - 0.10f, 6.8f + tier * 0.55f);
            Vector3 max = new(inner, top, 14.8f);
            writer.Box("cave terrace rock", min, max, materials.Paving, Identity);
            writer.Box("cave terrace moss", new(min.X, top - 0.04f, min.Z + 0.14f),
                new(max.X - 0.12f, top + 0.07f, max.Z), materials.Moss, Identity);
        }
        // Low mineral shelves flank a clear, flat walking route through the room.
        writer.Box("cave warm mineral shelf", new(4.5f, Floor, -4.0f), new(8.5f, Floor + 0.38f, 0.5f), materials.Brick, Identity);
        writer.Box("cave shelf pale deposit", new(5.3f, Floor + 0.30f, -3.5f), new(8.7f, Floor + 0.57f, 0.15f), materials.Limestone, Identity);
    }

    private static void Column(RecipeWriter writer, StoneworksMaterials materials, Vector3 origin, float scale, string name, CourtyardSettings settings)
    {
        using (ImplicitRecipe core = writer.Begin())
        {
            const float coreHeight = 9.55f;
            float coreRadius = 0.72f * scale;
            ImplicitNode spine = core.Frustum(Vector3.Zero, new(0, coreHeight, 0), coreRadius, coreRadius);
            writer.Surface($"cave {name} column deep rock", core, spine,
                new(-coreRadius, 0, -coreRadius), new(coreRadius, coreHeight, coreRadius), materials.Mortar,
                new(origin, Quaternion.Identity, Vector3.One));
        }
        float y = 0f;
        for (int level = 0; level < ColumnRadii.Length; level++)
        {
            float radius = ColumnRadii[level] * scale;
            float height = ColumnHeights[level];
            using ImplicitRecipe field = writer.Begin();
            Vector3 drift = new((level % 3 - 1) * 0.13f, 0f, (level % 2) * 0.10f);
            ImplicitNode rock = field.Frustum(drift, drift + new Vector3(0, height - CourseGap, 0), radius, radius * 0.88f);
            // Chisel broad facets into the circular taper. The primitive remains
            // generally useful; clipped geological character is recipe policy.
            for (int face = 0; face < 5; face++)
            {
                float angle = face * MathF.Tau / 5f + level * 0.29f;
                Vector3 normal = new(MathF.Cos(angle), 0, MathF.Sin(angle));
                rock = field.Intersect(rock, field.Plane(normal, radius * 0.91f));
            }
            if (level is 2 or 4 or 6)
            {
                ImplicitNode crack = field.Box(new(-0.09f, height * 0.28f, -radius - 0.2f),
                    new(0.09f, height + 0.1f, -radius * 0.38f));
                rock = field.Subtract(rock, crack);
            }
            bool incised = name == "hero" && level == 2;
            Transform placement = new(origin + new Vector3(0, y, 0), Quaternion.Identity, Vector3.One);
            if (incised)
            {
                // Cut the actual column course, not a planar plaque attached to
                // it. The smaller bronze core shows only through the incisions.
                foreach (float x in new[] { -0.65f, 0f, 0.65f })
                foreach (float angle in new[] { -MathF.PI / 4f, MathF.PI / 4f })
                {
                    ImplicitNode cut = field.Box(new(-CarvingWidth / 2, -0.39f, -radius - 0.2f),
                        new(CarvingWidth / 2, 0.39f, -0.65f));
                    cut = field.Place(cut, new(new(x, height * 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), Vector3.One));
                    rock = field.Subtract(rock, cut);
                }
                using ImplicitRecipe backing = writer.Begin();
                ImplicitNode bronze = backing.Frustum(new(0, 0.06f, 0), new(0, height - 0.12f, 0), 0.94f, 0.88f);
                writer.Surface("cave column recessed bronze", backing, bronze, new(-0.95f, 0, -0.95f), new(0.95f, height, 0.95f),
                    materials.Bronze, placement, cellSize: 0.10f);
            }
            writer.Surface($"cave {name} column course {level}", field, rock,
                new(-radius - 0.2f, 0, -radius - 0.2f), new(radius + 0.2f, height, radius + 0.2f),
                incised || level % 3 == 0 ? materials.Limestone : materials.Paving, placement,
                cellSize: incised ? CarvingCell(settings.Detail) : 0.16f);
            y += height;
        }
    }

    private static void Ceiling(RecipeWriter writer, StoneworksMaterials materials)
    {
        for (int layer = 0; layer < 3; layer++)
        {
            using ImplicitRecipe roof = writer.Begin();
            float y = Roof + layer * 0.62f;
            Vector3 min = new(-12f, y, -8f);
            Vector3 max = new(12f, y + 0.62f, 16f);
            ImplicitNode shell = roof.Box(min, max);
            // Offset overlapping voids make an irregular skylight; successive
            // strata change its silhouette instead of outlining a square hatch.
            ImplicitNode opening = roof.Ellipsoid(new(-0.8f + layer * 0.3f, Roof + 0.8f, 4.5f), new(3.9f + layer * 0.4f, 4f, 6.2f));
            opening = roof.Union(opening, roof.Ellipsoid(new(1.2f, Roof + 0.8f, 6.8f), new(3.2f, 4f, 3.9f)));
            shell = roof.Subtract(shell, opening);
            writer.Surface("cave skylight strata", roof, shell, min, max, layer == 1 ? materials.Mortar : materials.Paving, Identity);
        }
        using (ImplicitRecipe arch = writer.Begin())
        {
            Vector3 min = new(-9.5f, 7.3f, 13.1f);
            Vector3 max = new(9.5f, Roof + 0.5f, 15.4f);
            ImplicitNode mass = arch.Box(min, max);
            mass = arch.Subtract(mass, arch.Ellipsoid(new(0, 7.3f, 14.2f), new(3.7f, 4.8f, 8f)));
            writer.Surface("cave exit arch rock", arch, mass, min, max, materials.Paving, Identity);
        }
        // Asymmetric stepped tapers descend from the roof, clear of the route.
        for (int i = 0; i < 9; i++)
        {
            Vector3 anchor = new(i % 2 == 0 ? -5.5f : 5.9f, Roof + 0.1f, -3.7f + i * 1.8f);
            float length = 1.8f + (i % 3) * 0.65f;
            using ImplicitRecipe field = writer.Begin();
            ImplicitNode taper = field.Frustum(new(0, -length, 0), Vector3.Zero, 0.06f, 0.75f);
            writer.Surface("cave hanging taper", field, taper, new(-0.8f, -length, -0.8f), new(0.8f, 0.1f, 0.8f),
                materials.Paving, new(anchor, Quaternion.Identity, Vector3.One), cellSize: 0.18f);
        }
    }

    internal static float CarvingCell(string detail) => detail switch { "coarse" => 0.14f, "fine" => 0.035f, _ => 0.07f };

    private static void Incisions(RecipeWriter writer, StoneworksMaterials materials, CourtyardSettings settings)
    {
        float cell = CarvingCell(settings.Detail);
        CarvedBand(writer, materials, new(new(6.85f, 4.7f, 6f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.One),
            3.1f, 1.9f, cell, "wall");
    }

    private static void CarvedBand(RecipeWriter writer, StoneworksMaterials materials, Transform placement,
        float width, float height, float cell, string name)
    {
        const float front = -0.16f;
        const float back = 0.16f;
        using ImplicitRecipe field = writer.Begin();
        Vector3 min = new(-width / 2, 0, front);
        Vector3 max = new(width / 2, height, back);
        ImplicitNode face = field.Box(min, max);
        // Three opposed chevrons with an interrupted central stripe are cut
        // all the way through; their bronze backing is physically recessed.
        for (int rune = -1; rune <= 1; rune++)
        foreach (float angle in new[] { -MathF.PI / 4f, MathF.PI / 4f })
        {
            float x = rune * width * 0.27f;
            ImplicitNode cut = field.Box(new(-CarvingWidth / 2, -height * 0.33f, front - 0.1f),
                new(CarvingWidth / 2, height * 0.33f, back + 0.1f));
            cut = field.Place(cut, new(new(x, height * 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), Vector3.One));
            face = field.Subtract(face, cut);
        }
        writer.Surface($"cave {name} incised face", field, face, min, max, materials.Limestone, placement, cellSize: cell);
        writer.Box($"cave {name} bronze recess", new(-width / 2 - 0.08f, -0.08f, back + 0.10f),
            new(width / 2 + 0.08f, height + 0.08f, back + 0.48f), materials.Bronze, placement);
    }

    private static void Scatter(RecipeWriter writer, StoneworksMaterials materials)
    {
        Vector3[] rocks = [new(-5f, Floor, -3.5f), new(4.8f, Floor, 3.1f), new(-5.8f, Floor, 5.5f), new(5.3f, Floor, 12.4f)];
        for (int i = 0; i < rocks.Length; i++)
        {
            using ImplicitRecipe field = writer.Begin();
            ImplicitNode rock = field.Frustum(Vector3.Zero, new(0.15f, 0.8f + i * 0.15f, 0.1f), 0.95f, 0.4f);
            writer.Surface("cave fallen stone", field, rock, new(-1.1f, 0, -1.1f), new(1.1f, 1.5f, 1.1f),
                materials.Paving, new(rocks[i], Quaternion.Identity, Vector3.One));
        }
    }
}
