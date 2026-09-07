using System.Numerics;
using CraftSurvive.Game.Modules.LevelGeneration;
using Rusty.Engine;
using Rusty.Engine.Implicit;

namespace CraftSurvive.Game.Modules.Terrain;

internal static class GeneratedCaveRecipe
{
    private const float BlendRadius = 0.55f;
    private const float TextureRepeats = 0.65f;
    private const uint WeatheredExtractionCapacity = 1_048_576;
    private const float MacroAmplitude = 0.19f;
    private const float FineAmplitude = 0.035f;
    private const uint MacroOctaves = 4;
    private const uint FineOctaves = 2;
    private static readonly Vector3 MacroFrequency = new(0.10f, 0.18f, 0.10f);
    private static readonly Vector3 FineFrequency = new(0.85f, 1.3f, 0.85f);
    private const float ProtectedRouteRadius = 1.25f;
    private const float DrawResolution = 10000f;
    private static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    internal static bool IsStudy(string study) => study is "level" or "level-layout" or "level-weathered" or "level-weathered-strata" or "level-disrupted";
    internal static CaveLevelPlan Plan(IEngineContext engine, ulong seed) => CaveLevelPlan.Create(seed,
        key => engine.Random.DrawKeyed(new KeyedRngRequest(seed, "cave.level", key, 0, (long)DrawResolution)).Value / DrawResolution);

    internal static (CaveLevelPlan Plan, string Probes) Compose(IEngineContext engine, StoneworksMaterials materials,
        CourtyardSettings settings, Action<RecipeSurface> emit)
    {
        CaveLevelPlan plan = Plan(engine, settings.Seed);
        bool disrupted = settings.Study == "level-disrupted";
        bool weathered = settings.Study is "level-weathered" or "level-weathered-strata" || disrupted;
        RecipeWriter writer = new(engine.ImplicitSurfaces,
            new(VolumeCaveRecipe.Cell(settings.Detail), settings.CreaseDegrees, TextureRepeats,
                settings.MaterialBoundaryMode, settings.MaterialSampleSpacing,
                weathered ? WeatheredExtractionCapacity : 0, weathered ? WeatheredExtractionCapacity : 0), emit);
        using ImplicitRecipe field = writer.Begin();
        ImplicitNode air = Carve(field, plan);
        bool dressed = settings.Study != "level-layout";
        if (settings.Study is "level" or "level-weathered-strata") air = Treat(field, plan, air);
        if (disrupted) air = Disrupt(field, air, plan, settings.Seed);
        else if (weathered) air = Weather(field, air, settings.Seed);
        ImplicitNode entrance = field.Box(new(-2, CaveLevelPlan.Floor, -10), new(2, 7.2f, 0));
        ImplicitNode rock = ArchitecturalRecipes.Enclosure(field, new(CaveLevelPlan.Min, CaveLevelPlan.Max),
            new(CaveLevelPlan.AirMin, CaveLevelPlan.AirMax), air, new ImplicitNode[] { entrance });
        ImplicitMaterialRegion[] regions = dressed ? Materials(field, materials, weathered, settings.Seed) : [];
        string probes = CheckClearance(field, rock, plan);
        probes += disrupted
            ? FormattableString.Invariant($";weathering=disrupted;displacementBands={DisruptionBands.Length};weatherOctaves={DisruptionBands.Sum(band => (long)band.Octaves)};protectedRouteRadius={ProtectedRouteRadius}")
            : weathered
                ? FormattableString.Invariant($";weathering=spectral;macroAmplitude={MacroAmplitude};fineAmplitude={FineAmplitude};weatherOctaves={MacroOctaves}+{FineOctaves}")
                : ";weathering=none";
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

    // Field amplitudes are deliberately small: ellipsoid fields are normalized,
    // whereas capsule fields use distance. They are not metre displacements.
    private static ImplicitNode Weather(ImplicitRecipe field, ImplicitNode air, ulong seed)
    {
        const float Lacunarity = 2.1f;
        const float Gain = 0.5f;
        const ulong FineSeed = 0x9e3779b97f4a7c15UL;
        air = field.DisplaceWaves(air, MacroFrequency, MacroAmplitude, MacroOctaves, Lacunarity, Gain, seed);
        return field.DisplaceWaves(air, FineFrequency, FineAmplitude, FineOctaves, Lacunarity, Gain, seed ^ FineSeed);
    }

    private readonly record struct DisplacementBand(Vector3 Frequency, float Amplitude, uint Octaves, ulong SeedSalt);
    // These bands deliberately affect chamber-scale shape, not just surface finish.
    // Amplitudes are field values: normalized chambers react more than distance passages.
    private static readonly DisplacementBand[] DisruptionBands = [
        new(new(0.035f, 0.055f, 0.035f), 1.15f, 3, 0x243f6a8885a308d3UL),
        new(new(0.09f, 0.14f, 0.09f), 0.65f, 4, 0x13198a2e03707344UL),
        new(new(0.22f, 0.32f, 0.22f), 0.30f, 3, 0xa4093822299f31d0UL),
        new(new(0.55f, 0.8f, 0.55f), 0.12f, 3, 0x082efa98ec4e6c89UL),
        new(new(1.3f, 1.7f, 1.3f), 0.035f, 2, 0x452821e638d01377UL)];

    private static ImplicitNode Disrupt(ImplicitRecipe field, ImplicitNode air, CaveLevelPlan plan, ulong seed)
    {
        const float Lacunarity = 1.9f;
        const float Gain = 0.55f;
        const float ProtectedCenterHeight = CaveLevelPlan.Floor + 1.05f;
        foreach (DisplacementBand band in DisruptionBands)
            air = field.DisplaceWaves(air, band.Frequency, band.Amplitude, band.Octaves,
                Lacunarity, Gain, seed ^ band.SeedSalt);
        // Keep only a narrow walking core after disruption, allowing the surrounding
        // chambers to expand, pinch and merge. The enclosure still clips every carve.
        foreach (CaveRoute route in plan.Routes)
            for (int index = 0; index < route.Points.Length - 1; index++)
                air = field.Union(air, ArchitecturalRecipes.Passage(field,
                    new(route.Points[index] with { Y = ProtectedCenterHeight },
                        route.Points[index + 1] with { Y = ProtectedCenterHeight }, ProtectedRouteRadius, ProtectedRouteRadius)));
        return air;
    }

    private static ImplicitMaterialRegion[] Materials(ImplicitRecipe field, StoneworksMaterials materials,
        bool weathered, ulong seed)
    {
        ImplicitNode pale = field.Union(field.Box(new(-20, 6.8f, -9), new(20, 7.35f, 40)),
            field.Box(new(-20, 10.1f, -9), new(20, 10.65f, 40)));
        if (weathered)
        {
            const float MineralAmplitude = 0.22f;
            const uint MineralOctaves = 3;
            const float MineralLacunarity = 2f;
            const float MineralGain = 0.5f;
            Vector3 mineralFrequency = new(0.12f, 0.09f, 0.12f);
            pale = field.DisplaceWaves(pale, mineralFrequency, MineralAmplitude, MineralOctaves,
                MineralLacunarity, MineralGain, seed);
        }
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
