using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain.Recipes;

namespace CraftSurvive.Game.Modules.Terrain;

internal readonly record struct CourtyardSettings(
    string Treatment, float Width, float DoorWidth, float DoorOffset, ulong Seed,
    float CellSize, float CreaseDegrees, string Masonry = "layered",
    ImplicitMaterialBoundaryMode MaterialBoundaryMode = ImplicitMaterialBoundaryMode.Interpolated,
    float MaterialCutoff = 0f, string Study = "stoneworks", string Detail = "normal")
{
    internal static CourtyardSettings Default => new("soft", 24f, 3.4f, 0f, 0x4352414654UL, 0.20f, 110f);

    internal CourtyardSettings WithTreatment(string name) => name switch
    {
        "balanced" => this with { Treatment = name, CellSize = 0.22f, CreaseDegrees = 38f },
        "faceted" => this with { Treatment = name, CellSize = 0.26f, CreaseDegrees = 0f },
        "soft" => this with { Treatment = name, CellSize = 0.20f, CreaseDegrees = 110f },
        _ => throw new ArgumentException("Treatment must be balanced, faceted, or soft."),
    };
}

/// <summary>Product recipe and retained owners for the playable implicit courtyard.</summary>
internal sealed class CourtyardScene : IDisposable
{
    private const ulong FirstObjectId = 1000;
    private const ulong FirstLightId = 5000;
    private const float WallThickness = 0.6f;
    private const float DomainPadding = 0.25f;
    private const string TestPartPrefix = "masonry test";
    private const float MaxMaterialCutoff = 0.15f;
    private readonly IEngineContext engine;
    private readonly CourtyardMaterials materials;
    private readonly StoneworksMaterials stoneworksMaterials;
    private readonly List<Part> parts = [];
    private readonly List<Part> retired = [];
    private readonly List<(Light Owner, LightDescriptor Descriptor)> lights = [];
    private CourtyardSettings settings = CourtyardSettings.Default;
    private CourtyardSettings? pending;
    private bool shadows = true;
    private bool? pendingShadows;
    private SpatialSession? session;
    private Vector3 translation;
    private double generationSeconds;
    private ulong triangleCount;
    private ulong vertexCount;
    private uint correctionCount;
    private int generation;
    private double testGenerationSeconds;

    internal CourtyardScene(IEngineContext engine)
    {
        this.engine = engine;
        materials = new CourtyardMaterials(engine);
        try { stoneworksMaterials = new StoneworksMaterials(engine); }
        catch { materials.Dispose(); throw; }
    }

    internal void Start(SpatialSession spatial)
    {
        session = spatial;
        Build(settings);
        AddLight(LightKind.Ambient, new Vector3(0.77f, 0.83f, 1f), 0.75f, Vector3.Zero, Vector3.Zero);
        AddLight(LightKind.Directional, new Vector3(1f, 0.84f, 0.62f), 2.1f,
            new Vector3(-12f, 22f, -8f), Vector3.Normalize(new Vector3(0.45f, -1f, 0.4f)));
        AddLight(LightKind.Point, new Vector3(1f, 0.49f, 0.19f), 16f,
            new Vector3(-1.8f, 7.4f, 17f), Vector3.Zero);
        AddLight(LightKind.Point, new Vector3(0.55f, 0.74f, 1f), 20f,
            new Vector3(0f, 8.6f, 28f), Vector3.Zero);
    }

    internal IEnumerable<AppearanceFact> Facts => parts.Select((part, index) => new AppearanceFact(
        FirstObjectId + (ulong)index, false, 0, part.Placement with { Translation = part.Placement.Translation + translation },
        part.Appearance, true, RenderLayer.Scene));

    internal string QueueStudy(string study)
    {
        if (study is not ("stoneworks" or "reference" or "sampling"))
            throw new ArgumentException("Study must be stoneworks, reference, or sampling.");
        pending = (pending ?? settings) with { Study = study };
        return $"queued environment study={study}";
    }

    internal string QueueDetail(string detail)
    {
        if (detail is not ("coarse" or "normal" or "fine"))
            throw new ArgumentException("Detail must be coarse, normal, or fine.");
        pending = (pending ?? settings) with { Detail = detail };
        return $"queued sampling panel detail={detail}";
    }

    internal string QueueTreatment(string treatment)
    {
        pending = (pending ?? settings).WithTreatment(treatment);
        return $"queued courtyard treatment={treatment}";
    }

    internal string QueueMasonry(string mode)
    {
        if (mode is not ("original" or "regions" or "layered"))
            throw new ArgumentException("Masonry must be original, regions, or layered.");
        pending = (pending ?? settings) with { Masonry = mode };
        return $"queued west-wall masonry test={mode}";
    }

    internal string QueueMaterialBoundaries(string mode)
    {
        ImplicitMaterialBoundaryMode boundaryMode = mode switch
        {
            "centroid" => ImplicitMaterialBoundaryMode.Centroid,
            "interpolated" => ImplicitMaterialBoundaryMode.Interpolated,
            _ => throw new ArgumentException("Material boundaries must be centroid or interpolated."),
        };
        pending = (pending ?? settings) with { MaterialBoundaryMode = boundaryMode };
        return $"queued courtyard materialBoundaries={mode}";
    }

    internal string QueueMaterialCutoff(float cutoff)
    {
        if (!float.IsFinite(cutoff) || MathF.Abs(cutoff) > MaxMaterialCutoff)
            throw new ArgumentException("Material cutoff must be finite and between -0.15m and 0.15m.");
        pending = (pending ?? settings) with { MaterialCutoff = cutoff };
        return FormattableString.Invariant($"queued courtyard materialCutoff={cutoff:F2}m");
    }

    internal (Vector3 Eye, Vector3 Target) InspectionView(string angle)
    {
        float face = -settings.Width * 0.5f + WallThickness;
        return angle switch
        {
            "arrival" => (new(0, 4.55f, -7), new(0, 6.6f, 10)),
            "plaster" => (new(-settings.Width * 0.5f + 4f, 4.8f, -5f), new(-settings.Width * 0.5f, 5.6f, -3f)),
            "arcade" => (new(0, 6.55f, 11.5f), new(-2f, 7f, 19.5f)),
            "carving" => (new(0, 6.55f, 26.5f), new(0, 7.6f, 30.6f)),
            "samples" => (new(-3.5f, 5f, -7.5f), new(-2f, 4.5f, -3f)),
            "front" => (new(face + 3f, 4.55f, 0f), new(face, 5.1f, 0f)),
            "grazing" => (new(face + 0.8f, 4.55f, -3.2f), new(face, 4.9f, 1.3f)),
            _ => throw new ArgumentException("Inspection view must be arrival, plaster, arcade, carving, samples, front or grazing."),
        };
    }

    internal string QueueLayout(float width, float doorWidth, float doorOffset, ulong seed)
    {
        if (!float.IsFinite(width) || width < 20f || width > 30f
            || !float.IsFinite(doorWidth) || doorWidth < 2.4f || doorWidth > 4.2f
            || !float.IsFinite(doorOffset) || MathF.Abs(doorOffset) > 0.3f)
            throw new ArgumentException("Width: 20..30m; doorway: 2.4..4.2m; doorway offset: -0.3..0.3m.");
        pending = (pending ?? settings) with { Width = width, DoorWidth = doorWidth, DoorOffset = doorOffset, Seed = seed };
        return "queued courtyard dimensions, opening and detail seed";
    }

    internal string QueueShadows(bool enabled)
    {
        pendingShadows = enabled;
        return $"queued courtyard shadows={enabled}";
    }

    internal void Update()
    {
        if (pendingShadows is { } requested)
        {
            shadows = requested;
            pendingShadows = null;
            for (int index = 0; index < lights.Count; index++)
            {
                (Light owner, LightDescriptor descriptor) = lights[index];
                descriptor = descriptor with { ShadowIntent = shadows && descriptor.Kind == LightKind.Directional
                    ? LightShadowIntent.Requested : LightShadowIntent.Disabled };
                lights[index] = (owner, descriptor);
                engine.Graphics.UpdateLight(new LightUpdateRequest(owner, new LightRequest(FirstLightId + (ulong)index,
                    false, 0, descriptor with { Position = descriptor.Position + translation })));
            }
        }
        if (pending is not { } next) return;
        Build(next);
        settings = next;
        pending = null;
    }

    internal void Translate(Vector3 delta)
    {
        translation += delta;
        for (int index = 0; index < lights.Count; index++)
        {
            (Light owner, LightDescriptor descriptor) = lights[index];
            engine.Graphics.UpdateLight(new LightUpdateRequest(owner, new LightRequest(FirstLightId + (ulong)index, false, 0, descriptor with { Position = descriptor.Position + translation })));
        }
    }

    internal string Readout() => FormattableString.Invariant(
        $"study={settings.Study};detail={settings.Detail};sampleCell={SampleCell(settings.Detail):F2};sampleWidths=0.04/0.08/0.16/0.32;sampleAngles=0/45/90;sampleTriangles={parts.Where(p => p.Name.StartsWith("sampling panel", StringComparison.Ordinal)).Sum(p => (long)p.Stats.Triangles)};generation={generation};treatment={settings.Treatment};width={settings.Width:F1};doorWidth={settings.DoorWidth:F1};doorOffset={settings.DoorOffset:F2};seed={settings.Seed};courtyardDepth=20;passageLength=12;chamber=12x10;parts={parts.Count};triangles={triangleCount};vertices={vertexCount};seconds={generationSeconds:F3};cellSize={settings.CellSize:F3};crease={settings.CreaseDegrees:F0};materialBoundaries={BoundaryModeName(settings.MaterialBoundaryMode)};materialCutoff={settings.MaterialCutoff:F2};reorientedTriangles={correctionCount};degenerateTriangles={parts.Sum(p => (long)p.Stats.DegenerateTriangles)};shadows={shadows};collision=generated-mesh-copy;masonry={settings.Masonry};testParts={TestParts.Count()};testTriangles={TestParts.Sum(p => (long)p.Stats.Triangles)};testVertices={TestParts.Sum(p => (long)p.Stats.Vertices)};testSeconds={testGenerationSeconds:F3};testWall=west;testZ=-2..2");

    private IEnumerable<Part> TestParts => parts.Where(p => p.Name.StartsWith(TestPartPrefix, StringComparison.Ordinal));

    private void Build(CourtyardSettings next)
    {
        Stopwatch watch = Stopwatch.StartNew();
        List<Part> replacement = [];
        try
        {
            testGenerationSeconds = 0;
            if (next.Study == "reference")
            {
                CourtyardRecipe recipe = new(engine, materials);
                recipe.Compose(next, surface => AddPart(surface, replacement));
                testGenerationSeconds = recipe.TestGenerationSeconds;
            }
            else StoneworksRecipe.Compose(engine, stoneworksMaterials, next, surface => AddPart(surface, replacement));

            SpatialSession spatial = session ?? throw new InvalidOperationException("Courtyard collision session unavailable.");
            StaticMeshAsset[] assets = replacement.Select((p, i) => new StaticMeshAsset(
                FirstObjectId + (ulong)i, new MeshResourceReference(p.Mesh), 0, 0, 0, 0)).ToArray();
            StaticMeshInstance[] instances = replacement.Select((p, i) => new StaticMeshInstance(
                FirstObjectId + (ulong)i, FirstObjectId + (ulong)i, p.Placement with { Translation = p.Placement.Translation + translation })).ToArray();
            engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(spatial, assets,
                ReadOnlyMemory<Vector3>.Empty, ReadOnlyMemory<Triangle>.Empty, instances));
            retired.AddRange(parts);
            parts.Clear();
            parts.AddRange(replacement);
            vertexCount = (ulong)parts.Sum(p => (long)p.Stats.Vertices);
            triangleCount = (ulong)parts.Sum(p => (long)p.Stats.Triangles);
            correctionCount = (uint)parts.Sum(p => (long)p.Stats.ReorientedTriangles);
            generationSeconds = watch.Elapsed.TotalSeconds;
            generation++;
        }
        catch
        {
            foreach (Part part in replacement) part.Dispose();
            throw;
        }
    }

    private void AddPart(RecipeSurface surface, List<Part> output)
    {
        MeshResource mesh = engine.ImplicitSurfaces.Generate(new ImplicitGenerateRequest(surface.Field, surface.Root,
            surface.Min - new Vector3(DomainPadding), surface.Max + new Vector3(DomainPadding),
            surface.Sampling.CellSize, surface.Sampling.CreaseDegrees, surface.Sampling.TextureRepeats,
            surface.Material, surface.Regions, MaterialBoundaryMode: surface.Sampling.MaterialBoundaries));
        Appearance? appearance = null;
        try
        {
            appearance = engine.Graphics.CreateMeshAppearance(mesh);
            output.Add(new Part(surface.Name, mesh, appearance, engine.ImplicitSurfaces.ReadGeneration(surface.Field), surface.Placement));
        }
        catch { appearance?.Dispose(); mesh.Dispose(); throw; }
    }

    private void AddLight(LightKind kind, Vector3 color, float intensity, Vector3 position, Vector3 direction)
    {
        LightDescriptor descriptor = new(kind, color, intensity, true, position, direction,
            kind == LightKind.Point, 14f, 2f, 0.6f, 0f,
            kind == LightKind.Directional ? LightShadowIntent.Requested : LightShadowIntent.Disabled);
        Light owner = engine.Graphics.CreateLight(new LightRequest(FirstLightId + (ulong)lights.Count, false, 0, descriptor));
        lights.Add((owner, descriptor));
    }

    private static float SampleCell(string detail) => detail switch { "coarse" => 0.32f, "fine" => 0.08f, _ => 0.16f };

    private static string BoundaryModeName(ImplicitMaterialBoundaryMode mode) => mode switch
    {
        ImplicitMaterialBoundaryMode.Centroid => "centroid",
        ImplicitMaterialBoundaryMode.Interpolated => "interpolated",
        _ => "unknown",
    };

    // Called by the product root after publishing facts for the replacement.
    internal void ReleaseRetired()
    {
        foreach (Part part in retired) part.Dispose();
        retired.Clear();
    }

    public void Dispose()
    {
        ReleaseRetired();
        foreach ((Light owner, _) in lights) owner.Dispose();
        lights.Clear();
        foreach (Part part in parts) part.Dispose();
        parts.Clear();
        stoneworksMaterials.Dispose();
        materials.Dispose();
    }

    private sealed record Part(string Name, MeshResource Mesh, Appearance Appearance, ImplicitGenerationReadout Stats, Transform Placement) : IDisposable
    {
        public void Dispose() { Appearance.Dispose(); Mesh.Dispose(); }
    }

}
