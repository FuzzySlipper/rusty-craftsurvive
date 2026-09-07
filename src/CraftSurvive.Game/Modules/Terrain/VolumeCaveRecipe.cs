using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain.Recipes;

namespace CraftSurvive.Game.Modules.Terrain;

// Product level recipe. The Engine owns all field evaluation and extraction.
// Each stage enlarges connected air; clipping protects the enclosing solid.
internal static class VolumeCaveRecipe
{
    internal static readonly Vector3 Min = new(-12, 0, -6);
    internal static readonly Vector3 Max = new(12, 14, 24);
    private static readonly Vector3 AirMin = new(-10.8f, 3, -4.8f);
    private static readonly Vector3 AirMax = new(10.8f, 12.8f, 22.8f);
    private static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    private const float PassageRadius = 3.1f;
    private const float BlendRadius = 0.65f;
    internal static bool IsStudy(string study) => study is "volume" or "volume-passages" or "volume-chambers";
    internal static float Cell(string detail) => detail switch { "coarse" => 0.50f, "fine" => 0.16f, _ => 0.28f };

    internal static string Compose(IEngineContext engine, StoneworksMaterials materials, CourtyardSettings settings,
        Action<RecipeSurface> emit)
    {
        RecipeWriter writer = new(engine.ImplicitSurfaces,
            new(Cell(settings.Detail), settings.CreaseDegrees, 0.65f, settings.MaterialBoundaryMode), emit);
        using ImplicitRecipe field = writer.Begin();
        // Small deterministic layout variations keep every centerline inside the
        // protected bounds. No unbounded random search or erosion simulation.
        float bend = ((int)(settings.Seed % 7) - 3) * 0.18f;
        Vector3[] spine = [new(0, 5.3f, -4), new(0, 5.3f, 1), new(-3 + bend, 5.6f, 6),
            new(2 + bend, 5.4f, 12), new(0, 5.6f, 19)];
        ImplicitNode air = field.Capsule(spine[0], spine[1], PassageRadius);
        for (int i = 1; i < spine.Length - 1; i++)
            air = field.Blend(air, field.Capsule(spine[i], spine[i + 1], PassageRadius), BlendRadius);
        Vector3 branch = new(-6, 5.6f, 13);
        air = field.Blend(air, field.Capsule(spine[2], branch, 2.7f), BlendRadius);
        if (settings.Study != "volume-passages")
        {
            air = field.Blend(air, field.Ellipsoid(spine[2] + new Vector3(0, 1, 0), new(4.8f, 5.2f, 5)), BlendRadius);
            air = field.Blend(air, field.Ellipsoid(spine[4] + new Vector3(0, 0.6f, 0), new(5.5f, 4.6f, 4.5f)), BlendRadius);
            air = field.Blend(air, field.Ellipsoid(branch, new(3.3f, 3.8f, 3.6f)), BlendRadius);
        }
        if (settings.Study == "volume")
        {
            // Layered lateral dissolution: shallow lobes widen alternating rock
            // courses. A tall recess makes the carve visibly three dimensional.
            for (int course = 0; course < 5; course++)
            {
                float y = 4.2f + course * 1.35f;
                float width = 4.7f + (course % 2) * 0.65f;
                air = field.Union(air, field.Ellipsoid(new(-3 + bend, y, 6), new(width, 0.48f, 4.6f)));
                air = field.Union(air, field.Ellipsoid(new(0, y, 19), new(width + 0.6f, 0.40f, 3.9f)));
            }
            air = field.Blend(air, field.Ellipsoid(new(-3 + bend, 9.8f, 6), new(1.8f, 4, 2)), 0.35f);
        }
        air = field.Intersect(air, field.Box(AirMin, AirMax));
        // Only this portal bypasses the wall margin. Its floor matches the
        // internal carve; the outer landing ends at the portal, never under it.
        ImplicitNode entrance = field.Box(new(-2, 3, -8), new(2, 7.2f, -3.5f));
        air = field.Union(air, entrance);
        ImplicitNode rock = field.Subtract(field.Box(Min, Max), air);
        ImplicitNode pale = field.Union(field.Box(new(-13, 6.7f, -7), new(13, 7.25f, 25)),
            field.Box(new(-13, 10.2f, -7), new(13, 10.7f, 25)));
        ImplicitNode moss = field.Box(new(-13, 2.9f, -7), new(13, 3.18f, 25));
        string probes = Probe(engine.ImplicitSurfaces, field, rock, spine, branch);
        writer.Surface("volume carved rock", field, rock, Min, Max, materials.Mortar, Identity,
            [new(pale, materials.Limestone), new(moss, materials.Moss)]);
        writer.Box("volume exterior landing", new(-5, 2.4f, -11), new(5, 3, -6), materials.Paving, Identity);
        return probes;
    }

    private static string Probe(IImplicitSurfacesService service, ImplicitRecipe field, ImplicitNode rock,
        Vector3[] spine, Vector3 branch)
    {
        int solid = 0, solidCount = 0, air = 0, airCount = 0;
        void Check(Vector3 point, bool expectSolid)
        {
            float value = service.Sample(new(field.Field, rock, point)).Value;
            if (expectSolid) { solidCount++; if (float.IsFinite(value) && value < 0) solid++; }
            else { airCount++; if (float.IsFinite(value) && value > 0) air++; }
        }
        // Witnesses inside the retained shell, not on its zero surface. The
        // clipping construction provides the margin; these are not a flood fill.
        foreach (float z in new[] { -5.7f, 6f, 18f, 23.7f })
        {
            Check(new(-11.7f, 6, z), true); Check(new(11.7f, 6, z), true);
            Check(new(0, 0.3f, z), true); Check(new(0, 13.7f, z), true);
        }
        Check(new(-5, 5, -5.7f), true); Check(new(5, 5, -5.7f), true);
        Check(new(0, 5, 23.7f), true); Check(new(0, 5, -6), false);
        for (int i = 0; i < spine.Length - 1; i++)
            for (int step = 0; step <= 4; step++) Check(Vector3.Lerp(spine[i], spine[i + 1], step / 4f), false);
        for (int step = 0; step <= 4; step++) Check(Vector3.Lerp(spine[2], branch, step / 4f), false);
        return $"shellWitnesses={solid}/{solidCount};airWitnesses={air}/{airCount};protectedMargin=1.2;floorThickness=3;portalCount=1";
    }
}
