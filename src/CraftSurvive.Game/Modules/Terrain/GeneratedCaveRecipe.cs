using System.Numerics;
using CraftSurvive.Game.Modules.LevelGeneration;
using Rusty.Engine;
using Rusty.Engine.Implicit;

namespace CraftSurvive.Game.Modules.Terrain;

internal static class GeneratedCaveRecipe
{
    private const float BlendRadius = 0.55f;
    private const float TextureRepeats = 0.65f;
    private const float DrawResolution = 10000f;
    private static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    internal static bool IsStudy(string study) => study is "level" or "level-layout";
    internal static CaveLevelPlan Plan(IEngineContext engine, ulong seed) => CaveLevelPlan.Create(seed,
        key => engine.Random.DrawKeyed(new KeyedRngRequest(seed, "cave.level", key, 0, (long)DrawResolution)).Value / DrawResolution);

    internal static (CaveLevelPlan Plan, string Probes) Compose(IEngineContext engine, StoneworksMaterials materials,
        CourtyardSettings settings, Action<RecipeSurface> emit)
    {
        CaveLevelPlan plan = Plan(engine, settings.Seed);
        RecipeWriter writer = new(engine.ImplicitSurfaces,
            new(VolumeCaveRecipe.Cell(settings.Detail), settings.CreaseDegrees, TextureRepeats,
                settings.MaterialBoundaryMode, settings.MaterialSampleSpacing), emit);
        using ImplicitRecipe field = writer.Begin();
        ImplicitNode air = Carve(field, plan);
        bool dressed = settings.Study == "level";
        if (dressed) air = Treat(field, plan, air);
        ImplicitNode entrance = field.Box(new(-2, CaveLevelPlan.Floor, -10), new(2, 7.2f, 0));
        ImplicitNode rock = ArchitecturalRecipes.Enclosure(field, new(CaveLevelPlan.Min, CaveLevelPlan.Max),
            new(CaveLevelPlan.AirMin, CaveLevelPlan.AirMax), air, new ImplicitNode[] { entrance });
        ImplicitMaterialRegion[] regions = dressed ? Materials(field, materials) : [];
        string probes = CheckClearance(field, rock, plan);
        writer.Surface("level carved rock", field, rock, CaveLevelPlan.Min, CaveLevelPlan.Max, materials.Mortar, Identity, regions);
        writer.Box("level entrance landing", new(-5, 2.4f, -14), new(5, CaveLevelPlan.Floor, -8), materials.Paving, Identity);
        return (plan, probes);
    }

    // The physical stage consumes only rooms and route polylines. Changing its
    // meshing/material choices never changes the intended graph or keyed draws.
    private static ImplicitNode Carve(ImplicitRecipe field, CaveLevelPlan plan)
    {
        CaveRoom first = plan.Rooms[0];
        ImplicitNode air = ArchitecturalRecipes.Chamber(field, new(first.Center, first.Radii));
        foreach (CaveRoom room in plan.Rooms.Skip(1))
            air = ArchitecturalRecipes.Join(field, air, ArchitecturalRecipes.Chamber(field, new(room.Center, room.Radii)), BlendRadius);
        foreach (CaveRoute route in plan.Routes)
            for (int index = 0; index < route.Points.Length - 1; index++)
                air = ArchitecturalRecipes.Join(field, air, ArchitecturalRecipes.Passage(field,
                    new(route.Points[index], route.Points[index + 1], route.Radius, route.Radius)), BlendRadius);
        return air;
    }

    private static ImplicitNode Treat(ImplicitRecipe field, CaveLevelPlan plan, ImplicitNode air)
    {
        const int CourseCount = 4;
        const float FirstCourse = 4.3f;
        const float CoursePitch = 1.65f;
        const float RecessHeight = 0.7f;
        foreach (CaveRoom room in plan.Rooms.Where(room => room.Id is "left" or "goal" or "hub"))
            for (int course = 0; course < CourseCount; course++)
            {
                float expansion = course % 2 == 0 ? 0.55f : 0.25f;
                air = field.Union(air, field.Ellipsoid(room.Center with { Y = FirstCourse + CoursePitch * course },
                    new(room.Radii.X + expansion, RecessHeight, room.Radii.Z + expansion)));
            }
        return air;
    }

    private static ImplicitMaterialRegion[] Materials(ImplicitRecipe field, StoneworksMaterials materials)
    {
        ImplicitNode pale = field.Union(field.Box(new(-20, 6.8f, -9), new(20, 7.35f, 40)),
            field.Box(new(-20, 10.1f, -9), new(20, 10.65f, 40)));
        ImplicitNode moss = field.Box(new(-20, 2.9f, -9), new(20, 3.18f, 40));
        return [new(pale, materials.Limestone), new(moss, materials.Moss)];
    }

    private static string CheckClearance(ImplicitRecipe field, ImplicitNode rock, CaveLevelPlan plan)
    {
        const int SegmentSteps = 8;
        const float FootHeight = 0.15f;
        const float HeadHeight = 1.9f;
        const float BodyRadius = 0.4f;
        int clear = 0, count = 0;
        foreach (CaveRoute route in plan.Routes)
            for (int segment = 0; segment < route.Points.Length - 1; segment++)
                for (int step = 0; step <= SegmentSteps; step++)
                {
                    Vector3 point = Vector3.Lerp(route.Points[segment], route.Points[segment + 1], step / (float)SegmentSteps);
                    foreach (float height in new[] { FootHeight, HeadHeight })
                        foreach (Vector3 offset in new[] { Vector3.UnitX * BodyRadius, -Vector3.UnitX * BodyRadius,
                            Vector3.UnitZ * BodyRadius, -Vector3.UnitZ * BodyRadius })
                        {
                            count++;
                            float value = field.Sample(rock, (point with { Y = CaveLevelPlan.Floor + height }) + offset).Value;
                            if (float.IsFinite(value) && value > 0) clear++;
                        }
                }
        if (clear != count) throw new InvalidOperationException($"Cave route body clearance failed: {clear}/{count} witnesses.");
        // These are source-field clearance witnesses, not mesh traversal or a
        // navigation certificate. The playable collision copy is tested separately.
        return $"levelRooms={plan.Rooms.Length};levelRoutes={plan.Routes.Length};levelLoops={plan.IndependentLoops};levelClearance={clear}/{count};levelPlanHash={CraftSurvive.Procgen.CanonicalIdentity.Hash(plan.Intent)};portalCount=1;floorThickness=3";
    }
}
