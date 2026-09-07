using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Implicit;

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
    internal static bool IsStudy(string study) => study is "volume" or "volume-passages" or "volume-chambers" or "volume-sampled";
    internal static float SampledCell(string detail) => detail switch { "coarse" => 0.60f, "fine" => 0.25f, _ => 0.40f };
    internal static float Cell(string detail) => detail switch { "coarse" => 0.50f, "fine" => 0.16f, _ => 0.28f };

    internal static string Compose(IEngineContext engine, StoneworksMaterials materials, CourtyardSettings settings,
        Action<RecipeSurface> emit, Action<SampledRecipeSurface> emitSampled)
    {
        RecipeWriter writer = new(engine.ImplicitSurfaces,
            new(Cell(settings.Detail), settings.CreaseDegrees, 0.65f, settings.MaterialBoundaryMode), emit);
        using ImplicitRecipe field = writer.Begin();
        // Small deterministic layout variations keep every centerline inside the
        // protected bounds. No unbounded random search or erosion simulation.
        float bend = ((int)(settings.Seed % 7) - 3) * 0.18f;
        Vector3[] spine = [new(0, 5.3f, -4), new(0, 5.3f, 1), new(-3 + bend, 5.6f, 6),
            new(2 + bend, 5.4f, 12), new(0, 5.6f, 19)];
        ImplicitNode air = ArchitecturalRecipes.Passage(field, new(spine[0], spine[1], PassageRadius, PassageRadius));
        for (int i = 1; i < spine.Length - 1; i++)
            air = ArchitecturalRecipes.Join(field, air, ArchitecturalRecipes.Passage(field, new(spine[i], spine[i + 1], PassageRadius, PassageRadius)), BlendRadius);
        Vector3 branch = new(-6, 5.6f, 13);
        air = ArchitecturalRecipes.Join(field, air, ArchitecturalRecipes.Passage(field, new(spine[2], branch, 2.7f, 2.7f)), BlendRadius);
        if (settings.Study != "volume-passages")
        {
            air = ArchitecturalRecipes.Join(field, air, ArchitecturalRecipes.Chamber(field, new(spine[2] + new Vector3(0, 1, 0), new(4.8f, 5.2f, 5))), BlendRadius);
            air = ArchitecturalRecipes.Join(field, air, ArchitecturalRecipes.Chamber(field, new(spine[4] + new Vector3(0, 0.6f, 0), new(5.5f, 4.6f, 4.5f))), BlendRadius);
            air = ArchitecturalRecipes.Join(field, air, ArchitecturalRecipes.Chamber(field, new(branch, new(3.3f, 3.8f, 3.6f))), BlendRadius);
        }
        if (settings.Study is "volume" or "volume-sampled")
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
        // Only this portal bypasses the wall margin. Its floor matches the
        // internal carve; the outer landing ends at the portal, never under it.
        ImplicitNode entrance = field.Box(new(-2, 3, -8), new(2, 7.2f, -3.5f));
        ImplicitNode rock = ArchitecturalRecipes.Enclosure(field, new(Min, Max), new(AirMin, AirMax), air, new ImplicitNode[] { entrance });
        ImplicitNode pale = field.Union(field.Box(new(-13, 6.7f, -7), new(13, 7.25f, 25)),
            field.Box(new(-13, 10.2f, -7), new(13, 10.7f, 25)));
        ImplicitNode moss = field.Box(new(-13, 2.9f, -7), new(13, 3.18f, 25));
        string probes = Probe(engine.ImplicitSurfaces, field, rock, spine, branch);
        ImplicitMaterialRegion[] regions = [new(pale, materials.Limestone), new(moss, materials.Moss)];
        if (settings.Study == "volume-sampled")
        {
            // Lattice points include exterior air, so the solid shell's outer
            // surface is sampled as well. The Engine never invents end caps.
            const float ExteriorMargin = 0.5f;
            float spacing = SampledCell(settings.Detail);
            Vector3 origin = Min - new Vector3(ExteriorMargin);
            Vector3 extent = Max + new Vector3(ExteriorMargin) - origin;
            uint Count(float length) => checked((uint)MathF.Ceiling(length / spacing) + 1);
            using SampledVolume volume = engine.ImplicitSurfaces.CreateSampledVolume(
                new(origin, spacing, Count(extent.X), Count(extent.Y), Count(extent.Z), 1f));
            engine.ImplicitSurfaces.RasterizeSampledVolume(new(volume, field.Field, rock));
            SampledVolumeDescriptor grid = engine.ImplicitSurfaces.DescribeSampledVolume(volume);
            emitSampled(new("volume sampled rock", new(volume, field.Field, 0f,
                settings.CreaseDegrees, 0.65f, materials.Mortar, regions,
                settings.MaterialBoundaryMode, settings.MaterialSampleSpacing), Identity));
            probes += FormattableString.Invariant($";source=sampled;grid={grid.Width}x{grid.Height}x{grid.Depth};gridSpacing={grid.Spacing:F3};volumeRevision={grid.Revision}");
        }
        else
        {
            writer.Surface("volume carved rock", field, rock, Min, Max, materials.Mortar, Identity, regions);
            probes += ";source=analytic";
        }
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
