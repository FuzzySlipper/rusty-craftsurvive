using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain.Recipes;

internal enum WallFinish { Masonry, BrokenPlaster, DressedStone }

internal readonly record struct WallPalette(Material Body, Material Mortar, Material Trim, Material Plaster);

// Reusable composition policy stays product-local while its vocabulary is
// being tested. No resource handles are retained here and no field is evaluated.
internal sealed class ArchitecturalRecipes(RecipeWriter writer)
{
    private const float BayLength = 4f;
    private const float Joint = 0.045f;
    private const float Relief = 0.10f;
    private const float TrimDepth = 0.16f;
    private const float TrimWidth = 0.25f;
    private const float RevealClearance = 0.08f;

    internal void Wall(string name, WallLayout wall, WallFinish finish, WallPalette palette, Func<int, float> wearAt)
    {
        Transform placement = new(wall.Origin, wall.Rotation, Vector3.One);
        float half = wall.Thickness * 0.5f;
        for (float start = 0; start < wall.Length; start += BayLength)
        {
            float end = MathF.Min(wall.Length, start + BayLength);
            float wearPhase = wearAt((int)(start / BayLength));
            using ImplicitRecipe field = writer.Begin();
            ImplicitNode backing = CutOpening(field, field.Box(new(start, 0, -half), new(end, wall.Height, half)), wall, RevealClearance * 3);
            writer.Surface(name + " mortar", field, backing, new(start, 0, -half), new(end, wall.Height, half), palette.Mortar, placement);
            bool hasStone = false;
            ImplicitNode stones = default;
            float courseHeight = finish == WallFinish.DressedStone ? 0.8f : 0.55f;
            float stoneLength = finish == WallFinish.DressedStone ? 1.35f : 1.05f;
            foreach (MasonryCourse stone in MasonryLayout.Courses(start, end, wall.Height, stoneLength, courseHeight, Joint))
            {
                ImplicitNode block = field.Box(new(stone.Left, stone.Bottom, -half - Relief), new(stone.Right, stone.Top, half + Relief));
                stones = hasStone ? field.Union(stones, block) : block;
                hasStone = true;
            }
            if (hasStone)
            {
                stones = CutOpening(field, stones, wall, RevealClearance * 2);
                writer.Surface(name + " courses", field, stones, new(start, 0, -half - Relief),
                    new(end, wall.Height, half + Relief), palette.Body, placement);
            }

            if (finish == WallFinish.BrokenPlaster)
            {
                // Physical plaster shell over the same courses, with broad
                // missing patches. Real depth separates it from exposed brick.
                const float plasterDepth = 0.16f;
                ImplicitNode plaster = field.Box(new(start, 0.8f, -half - Relief - plasterDepth), new(end, wall.Height - 0.35f, -half));
                float center = start + 1.3f + wearPhase * 1.4f;
                float height = 1.5f + wearPhase * 1.7f;
                Vector2[] outline = [new(center - 1.3f, -1), new(center - 1.1f, height * 0.65f),
                    new(center - 0.4f, height), new(center + 0.35f, height * 0.86f),
                    new(center + 1.25f, height * 0.3f), new(center + 1.35f, -1)];
                ImplicitNode missing = field.Box(new(start - 1, -1, -1), new(end + 1, wall.Height, 1));
                for (int edge = 0; edge < outline.Length; edge++)
                {
                    Vector2 a = outline[edge], b = outline[(edge + 1) % outline.Length];
                    Vector3 normal = Vector3.Normalize(new Vector3(a.Y - b.Y, b.X - a.X, 0));
                    missing = field.Intersect(missing, field.Plane(normal, Vector3.Dot(normal, new(a.X, a.Y, 0))));
                }
                plaster = field.Subtract(plaster, missing);
                plaster = CutOpening(field, plaster, wall, RevealClearance);
                writer.Surface(name + " broken plaster", field, plaster, new(start, 0.8f, -half - Relief - plasterDepth),
                    new(end, wall.Height - 0.35f, -half), palette.Plaster, placement);
            }
        }
        writer.Box(name + " coping", new(0, wall.Height - 0.1f, -half - TrimDepth),
            new(wall.Length, wall.Height + 0.22f, half + TrimDepth), palette.Trim, placement);
        // The same opening owns the structural subtraction and trim curve.
        if (wall.Opening is { } opening)
        {
            using ImplicitRecipe field = writer.Begin();
            WallOpening outer = opening with { Width = opening.Width + TrimWidth * 2 };
            ImplicitNode trim = field.Subtract(Opening(field, outer, half + TrimDepth), Opening(field, opening, half + TrimDepth + Relief));
            trim = field.Intersect(trim, field.Box(new(outer.Left, 0, -half - TrimDepth), new(outer.Right, outer.Top, -half + Relief)));
            writer.Surface(name + " arch surround", field, trim, new(outer.Left, 0, -half - TrimDepth),
                new(outer.Right, outer.Top, -half + Relief), palette.Trim, placement, cellSize: 0.12f);
        }
    }

    private static ImplicitNode CutOpening(ImplicitRecipe field, ImplicitNode source, WallLayout wall, float clearance)
        // Each underlying layer retreats farther from the shared opening.
        // Mortar, courses, plaster and trim must not expose coincident cut faces.
        => wall.Opening is { } opening ? field.Subtract(source, Opening(field,
            opening with { Width = opening.Width + clearance * 2, SpringHeight = opening.SpringHeight + clearance }, wall.Thickness + 1f)) : source;

    internal static ImplicitNode Opening(ImplicitRecipe field, WallOpening opening, float depth)
    {
        ImplicitNode lower = field.Box(new(opening.Left, -1f, -depth), new(opening.Right, opening.SpringHeight, depth));
        if (!opening.Arched) return lower;
        ImplicitNode round = field.Capsule(new(opening.Center, opening.SpringHeight, -depth),
            new(opening.Center, opening.SpringHeight, depth), opening.Width * 0.5f);
        return field.Union(lower, round);
    }
}
