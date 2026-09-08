using System.Numerics;
using CraftSurvive.Game.Modules.LevelGeneration;
using Rusty.Engine;
using Rusty.Engine.Implicit;

namespace CraftSurvive.Game.Modules.Terrain;

internal static class GeneratedDungeonRecipe
{
    private const float DrawResolution = 10000f;
    private const float CoursePitch = 0.85f;
    private const float JointHeight = 0.10f;
    private const float JointDepth = 0.16f;
    private const float TextureRepeats = 0.65f;
    private static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    internal static bool IsStudy(string study) => study is "dungeon" or "dungeon-layout";
    internal static DungeonLevelPlan Plan(IEngineContext engine, ulong seed) => DungeonLevelPlan.Create(seed,
        key => engine.Random.DrawKeyed(new(seed, "dungeon.level", key, 0, (long)DrawResolution)).Value / DrawResolution);

    internal static (DungeonLevelPlan Plan, string Probes) Compose(IEngineContext engine, StoneworksMaterials materials,
        CourtyardSettings settings, Action<RecipeSurface> emit)
    {
        DungeonLevelPlan plan = Plan(engine, settings.Seed);
        float cell = settings.Detail switch { "coarse" => 0.50f, "fine" => 0.16f, _ => 0.28f };
        RecipeWriter writer = new(engine.ImplicitSurfaces,
            new(cell, settings.CreaseDegrees, TextureRepeats, settings.MaterialBoundaryMode, settings.MaterialSampleSpacing), emit);
        using ImplicitRecipe field = writer.Begin();
        bool dressed = settings.Study == "dungeon";
        ImplicitNode air = field.Box(plan.Rooms[0].Minimum, plan.Rooms[0].Maximum);
        foreach (DungeonRoom room in plan.Rooms.Skip(1)) air = field.Union(air, field.Box(room.Minimum, room.Maximum));
        foreach (DungeonRoute route in plan.Routes)
        {
            Vector3 halfWidth = new(DungeonLevelPlan.PassageWidth / 2, 0, DungeonLevelPlan.PassageWidth / 2);
            air = field.Union(air, field.Box(Vector3.Min(route.Start, route.End) - halfWidth,
                Vector3.Max(route.Start, route.End) + halfWidth + Vector3.UnitY * DungeonLevelPlan.PassageHeight));
        }
        List<ImplicitMaterialRegion> regions = [];
        if (dressed)
        {
            // Wide recessed courses survive normal sampling; a separate darker
            // material reveals them even where the lighting is near frontal.
            foreach (DungeonRoom room in plan.Rooms)
                for (float y = DungeonLevelPlan.Floor + CoursePitch; y < room.Maximum.Y - JointHeight; y += CoursePitch)
                {
                    Vector3 minimum = room.Minimum - new Vector3(JointDepth, 0, JointDepth);
                    Vector3 maximum = room.Maximum + new Vector3(JointDepth, 0, JointDepth);
                    ImplicitNode joint = field.Box(minimum with { Y = y }, maximum with { Y = y + JointHeight });
                    air = field.Union(air, joint);
                }
            for (float y = DungeonLevelPlan.Floor + CoursePitch; y < DungeonLevelPlan.AirMaximum.Y; y += CoursePitch)
                regions.Add(new(field.Box(DungeonLevelPlan.Minimum with { Y = y - JointHeight },
                    DungeonLevelPlan.Maximum with { Y = y + JointHeight * 2 }), materials.Mortar));
        }
        regions.Add(new(field.Box(DungeonLevelPlan.Minimum,
            DungeonLevelPlan.Maximum with { Y = DungeonLevelPlan.Floor + 0.06f }), materials.Paving));
        // Only the entrance is allowed through the protective envelope. Roof,
        // floor and all other exterior faces remain solid, including after detail.
        ImplicitNode entrance = field.Box(new(-1.4f, DungeonLevelPlan.Floor, -10),
            new(1.4f, DungeonLevelPlan.Floor + DungeonLevelPlan.PassageHeight, 0));
        ImplicitNode rock = ArchitecturalRecipes.Enclosure(field,
            new(DungeonLevelPlan.Minimum, DungeonLevelPlan.Maximum),
            new(DungeonLevelPlan.AirMinimum, DungeonLevelPlan.AirMaximum), air, new ImplicitNode[] { entrance });
        string probes = Probe(field, rock, plan);
        writer.Surface("dungeon enclosed masonry", field, rock, DungeonLevelPlan.Minimum, DungeonLevelPlan.Maximum,
            materials.Limestone, Identity, regions.ToArray());
        writer.Box("dungeon entrance landing", new(-4, 2.4f, -12), new(4, DungeonLevelPlan.Floor, -8), materials.Paving, Identity);
        return (plan, probes + FormattableString.Invariant($";dungeonCell={cell:F3};dungeonExtent=58x11x44;dungeonDressed={dressed}"));
    }

    private static string Probe(ImplicitRecipe field, ImplicitNode rock, DungeonLevelPlan plan)
    {
        const float BodyRadius = 0.4f, SamplePitch = 0.5f, Foot = 0.15f, Head = 1.9f;
        int clear = 0, shell = 0;
        void Check(Vector3 point, bool solid)
        {
            float value = field.Sample(rock, point).Value;
            if (!float.IsFinite(value) || (solid ? value >= 0 : value <= 0))
                throw new InvalidOperationException($"Dungeon {(solid ? "shell" : "clearance")} witness failed at {point}: {value}.");
            if (solid) shell++; else clear++;
        }
        foreach (DungeonRoute route in plan.Routes)
        {
            int steps = (int)MathF.Ceiling(Vector3.Distance(route.Start, route.End) / SamplePitch);
            for (int step = 0; step <= steps; step++)
                foreach (float height in new[] { Foot, Head })
                    foreach (Vector3 offset in new[] { Vector3.UnitX * BodyRadius, -Vector3.UnitX * BodyRadius,
                        Vector3.UnitZ * BodyRadius, -Vector3.UnitZ * BodyRadius })
                        Check(Vector3.Lerp(route.Start, route.End, step / (float)steps) + Vector3.UnitY * height + offset, false);
        }
        foreach (DungeonRoom room in plan.Rooms)
        {
            Check(room.Eye, false);
            Check(room.Center with { Y = 1.5f }, true);
            Check(room.Center with { Y = 10.5f }, true);
        }
        for (float z = -7; z < 36; z += 2)
        {
            Check(new(-7.5f, 5, z), true); Check(new(49.5f, 5, z), true);
        }
        for (float x = -7; x < 50; x += 2)
        {
            Check(new(x, 5, 35.5f), true);
            if (MathF.Abs(x) > 1.4f) Check(new(x, 5, -7.5f), true);
        }
        return $"levelRooms={plan.Rooms.Length};levelRoutes={plan.Routes.Length};levelLoops={plan.IndependentLoops};levelClearance={clear}/{clear};shellWitnesses={shell}/{shell};portalCount=1;floorThickness=3;levelPlanHash={CraftSurvive.Procgen.CanonicalIdentity.Hash(plan.Intent)}";
    }
}
