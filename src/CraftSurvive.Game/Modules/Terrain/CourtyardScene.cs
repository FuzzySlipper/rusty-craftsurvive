using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.LevelGeneration;
using CraftSurvive.Procgen.Workbench;
using Rusty.Engine;
using Rusty.Engine.Implicit;

namespace CraftSurvive.Game.Modules.Terrain;

internal readonly record struct CourtyardSettings(
    string Treatment, float Width, float DoorWidth, float DoorOffset, ulong Seed,
    float CellSize, float CreaseDegrees, string Masonry = "layered",
    ImplicitMaterialBoundaryMode MaterialBoundaryMode = ImplicitMaterialBoundaryMode.Interpolated,
    float MaterialCutoff = 0f, string Study = "stoneworks", string Detail = "normal", float MaterialSampleSpacing = 0f,
    WorkbenchCandidate? Workbench = null, bool SwitchOpen = false)
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
    private string generationError = "none";
    private CaveLevelPlan? levelPlan;
    private DungeonLevelPlan? dungeonPlan;
    private string volumeProbes = "not-built";
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
        ApplyStudyLighting();
    }

    internal bool IsGeneratedLevel => settings.Study == "workbench" || GeneratedCaveRecipe.IsStudy(settings.Study) || GeneratedDungeonRecipe.IsStudy(settings.Study);
    internal WorkbenchCandidate? ActiveWorkbench => settings.Study == "workbench" ? settings.Workbench : null;

    internal void ApplyWorkbench(WorkbenchCandidate candidate, bool switchOpen)
    {
        CourtyardSettings next = settings with { Study = "workbench", Workbench = candidate, SwitchOpen = switchOpen };
        Build(next);
        settings = next;
        pending = null;
        generationError = "none";
        ApplyStudyLighting();
    }

    internal IEnumerable<AppearanceFact> Facts => parts.SelectMany(part => part.Visuals.Select(visual => (part, visual)))
        .Select((entry, index) => new AppearanceFact(FirstObjectId + (ulong)index, false, 0,
            entry.part.Placement with { Translation = entry.part.Placement.Translation + translation },
            entry.visual.Appearance, true, RenderLayer.Scene));

    internal string QueueStudy(string study)
    {
        if (study is not ("stoneworks" or "reference" or "sampling" or "detail" or "motifs" or "cave" or "volume" or "volume-passages" or "volume-chambers" or "volume-sampled" or "level" or "level-layout" or "level-weathered" or "level-weathered-strata" or "level-disrupted" or "dungeon" or "dungeon-layout" or "dungeon-split"))
            throw new ArgumentException("Study must be stoneworks, reference, sampling, detail, motifs, cave, volume-passages, volume-chambers, volume, volume-sampled, level, level-layout, level-weathered, level-weathered-strata, level-disrupted, dungeon, dungeon-split, or dungeon-layout.");
        pending = (pending ?? settings) with { Study = study };
        return $"queued environment study={study}";
    }

    internal string QueueSeed(ulong seed)
    {
        pending = (pending ?? settings) with { Seed = seed };
        return $"queued environment seed={seed}";
    }

    internal string QueueMaterialSamples(float spacing)
    {
        if (!float.IsFinite(spacing) || spacing < 0f || (spacing > 0f && spacing < 0.02f) || spacing > 0.5f)
            throw new ArgumentException("Material sample spacing must be 0 (off), or 0.02 to 0.5 metres.");
        pending = (pending ?? settings) with { MaterialSampleSpacing = spacing,
            MaterialBoundaryMode = ImplicitMaterialBoundaryMode.Interpolated };
        return FormattableString.Invariant($"queued material sample spacing={spacing:F2}m");
    }

    internal string QueueDetail(string detail)
    {
        if (detail is not ("coarse" or "normal" or "fine"))
            throw new ArgumentException("Detail must be coarse, normal, or fine.");
        pending = (pending ?? settings) with { Detail = detail };
        return $"queued detail sampling={detail}";
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
        if (angle.StartsWith("dungeon-", StringComparison.Ordinal))
        {
            DungeonLevelPlan plan = dungeonPlan ?? GeneratedDungeonRecipe.Plan(engine, settings.Seed);
            string station = angle[8..];
            if (station == "entry") return (new(0, 4.55f, -10), plan.Rooms[0].Eye);
            if (station == "roof") return (new(24, 18, -12), new(24, 7, 14));
            int index = int.Parse(station, CultureInfo.InvariantCulture);
            DungeonRoom room = plan.Rooms[index];
            DungeonRoute route = plan.Routes.First(route => route.From == index || route.To == index);
            Vector3 target = plan.Rooms[route.From == index ? route.To : route.From].Eye;
            return (room.Eye, target with { Y = room.Eye.Y });
        }
        if (angle.StartsWith("level-", StringComparison.Ordinal))
        {
            CaveLevelPlan plan = levelPlan ?? GeneratedCaveRecipe.Plan(engine, settings.Seed);
            string room = angle[6..];
            if (room == "entry") return (new(0, 4.55f, -12), plan.Eye("start"));
            if (room == "roof") return (new(15, 21, -10), new(0, 9, 20));
            Vector3 eye = plan.Eye(room);
            return (eye, room == "goal" ? plan.Eye("right") : plan.Eye(room == "hub" ? "left" : "goal"));
        }
        float face = -settings.Width * 0.5f + WallThickness;
        return angle switch
        {
            "volume-cut-left" => (new(-2, 4.55f, 3), new(-3.8f, 4.3f, 1)),
            "volume-cut-right" => (new(0, 4.55f, -1), new(-3.8f, 4.3f, 1)),
            "volume-entry" => (new(0, 4.55f, -9), new(0, 5.5f, 0)),
            "volume-room" => (new(-2, 4.55f, 4), new(-5, 7.5f, 8)),
            "volume-back" => (new(0, 4.55f, 18), new(2, 7, 21)),
            "volume-roof" => (new(6, 15.55f, -3), new(0, 14, 15)),
            "cave" => (new(0, 4.55f, -8f), new(-1, 7.5f, 6f)),
            "column" => (new(0, 4.55f, 0.2f), new(-3, 6.2f, 4.4f)),
            "runes" => (new(3.8f, 4.55f, 6f), new(6.85f, 5.8f, 6f)),
            "terraces" => (new(0, 4.55f, 7f), new(-6, 5f, 10f)),
            "arrival" => (new(0, 4.55f, -7), new(0, 6.6f, 10)),
            "plaster" => (new(-settings.Width * 0.5f + 4f, 4.8f, -5f), new(-settings.Width * 0.5f, 5.6f, -3f)),
            "arcade" => (new(0, 6.55f, 11.5f), new(-2f, 7f, 19.5f)),
            "carving" => (new(0, 6.55f, 26.5f), new(0, 7.6f, 30.6f)),
            "details" => (new(0, 4.6f, -9f), new(0, 4.4f, -3f)),
            "detail1" => DetailView(0, false),
            "detail2" => DetailView(1, false),
            "detail3" => DetailView(2, false),
            "detail4" => DetailView(3, false),
            "tiny" => DetailView(3, true),
            "bricks" => (new(-4.8f, 4.55f, -4.6f), new(-4.8f, 3.35f, -3.13f)),
            "samples" => (new(-3.5f, 5f, -7.5f), new(-2f, 4.5f, -3f)),
            "front" => (new(face + 3f, 4.55f, 0f), new(face, 5.1f, 0f)),
            "grazing" => (new(face + 0.8f, 4.55f, -3.2f), new(face, 4.9f, 1.3f)),
            _ => throw new ArgumentException("Inspection view must be arrival, plaster, arcade, carving, samples, front or grazing."),
        };
    }

    private static (Vector3 Eye, Vector3 Target) DetailView(int panel, bool close)
    {
        Vector3 station = DetailStudyRecipe.PanelStations[panel];
        Vector3 target = station + new Vector3(0, 1.6f, -0.13f);
        return (target + new Vector3(0, 0, close ? -0.6f : -2f), target);
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
        pending = null;
        try
        {
            Build(next);
            settings = next;
            ApplyStudyLighting();
            generationError = "none";
        }
        catch (EngineCallException error) when (error.Service == "ImplicitSurfaces" && error.Operation is "Generate" or "CreateSampledVolume" or "RasterizeSampledVolume" or "GenerateSampledVolume")
        {
            // Build disposes the unpublished replacement on failure. A rejected
            // bounded recipe leaves the applied scene/settings in place and is
            // visible in the readout, rather than escaping the product callback.
            string reason = error.Diagnostics.IsEmpty ? $"status {error.Status}" :
                string.Join(" | ", error.Diagnostics.ToArray().Select(diagnostic => diagnostic.Message));
            generationError = $"{next.Study} generation rejected: {reason}; previous scene retained";
        }
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
        $"volumeProbes={volumeProbes};volumeCell={(settings.Study == "volume-sampled" ? VolumeCaveRecipe.SampledCell(settings.Detail) : VolumeCaveRecipe.Cell(settings.Detail)):F3};boundedLeafVertices={parts.Sum(p => (long)p.Stats.BoundedLeafVertices)};boundaryEdges={parts.Sum(p => (long)p.Stats.BoundaryEdges)};nonManifoldEdges={parts.Sum(p => (long)p.Stats.NonManifoldEdges)};inconsistentWindingEdges={parts.Sum(p => (long)p.Stats.InconsistentWindingEdges)};caveCarvingCell={CaveRecipe.CarvingCell(settings.Detail):F3};caveTriangles={parts.Where(p => p.Name.StartsWith("cave ", StringComparison.Ordinal)).Sum(p => (long)p.Stats.Triangles)};generationError={generationError};study={settings.Study};materialSampleSpacing={settings.MaterialSampleSpacing:F2};detailCell={DetailStudyRecipe.SamplingCell(settings.Detail):F2};stoneWidths=0.64/0.32/0.16/0.08;carvedStrokes=0.16/0.08/0.04/0.02;detailTriangles={parts.Where(p => p.Name.StartsWith("detail ", StringComparison.Ordinal)).Sum(p => (long)p.Stats.Triangles)};detail={settings.Detail};sampleCell={SampleCell(settings.Detail):F2};sampleWidths=0.04/0.08/0.16/0.32;sampleAngles=0/45/90;sampleTriangles={parts.Where(p => p.Name.StartsWith("sampling panel", StringComparison.Ordinal)).Sum(p => (long)p.Stats.Triangles)};generation={generation};treatment={settings.Treatment};width={settings.Width:F1};doorWidth={settings.DoorWidth:F1};doorOffset={settings.DoorOffset:F2};seed={settings.Seed};courtyardDepth=20;passageLength=12;chamber=12x10;parts={parts.Count};renderSections={parts.Sum(p => p.Visuals.Count)};partitionCell={(settings.Study == "dungeon-split" ? 14 : 0)};triangles={triangleCount};vertices={vertexCount};seconds={generationSeconds:F3};cellSize={settings.CellSize:F3};crease={settings.CreaseDegrees:F0};materialBoundaries={BoundaryModeName(settings.MaterialBoundaryMode)};materialCutoff={settings.MaterialCutoff:F2};reorientedTriangles={correctionCount};degenerateTriangles={parts.Sum(p => (long)p.Stats.DegenerateTriangles)};shadows={shadows};collision=generated-mesh-copy;masonry={settings.Masonry};testParts={TestParts.Count()};testTriangles={TestParts.Sum(p => (long)p.Stats.Triangles)};testVertices={TestParts.Sum(p => (long)p.Stats.Vertices)};testSeconds={testGenerationSeconds:F3};testWall=west;testZ=-2..2");

    private IEnumerable<Part> TestParts => parts.Where(p => p.Name.StartsWith(TestPartPrefix, StringComparison.Ordinal));

    internal string ReadLevelPlan() => dungeonPlan is { } dungeon ? string.Join("\n",
        dungeon.Rooms.Select(room => $"room={room.Id};min={room.Minimum};max={room.Maximum}")
        .Concat(dungeon.Routes.Select(route => $"corridor={route.From}->{route.To};start={route.Start};end={route.End}"))) : levelPlan is not { } plan ? "level inactive" : string.Join("\n",
        plan.Rooms.Select(room => FormattableString.Invariant($"room={room.Id};center={room.Center.X:F2},{room.Center.Y:F2},{room.Center.Z:F2};radii={room.Radii.X:F2},{room.Radii.Y:F2},{room.Radii.Z:F2}"))
        .Concat(plan.Routes.Select(route => $"route={route.Id};from={route.From};to={route.To};points=" + string.Join("/", route.Points.Select(point => FormattableString.Invariant($"{point.X:F2},{point.Y:F2},{point.Z:F2}"))))));

    internal string ReadDetailParts() => string.Join("\n", parts.Where(p => p.Name.StartsWith("detail ", StringComparison.Ordinal)
        || p.Name.StartsWith("dungeon ", StringComparison.Ordinal) || p.Name.StartsWith("level ", StringComparison.Ordinal) || p.Name.StartsWith("volume ", StringComparison.Ordinal) || p.Name.StartsWith("cave ", StringComparison.Ordinal) || p.Name.StartsWith("sampling panel", StringComparison.Ordinal)).Select(p => FormattableString.Invariant(
            $"{p.Name};triangles={p.Stats.Triangles};vertices={p.Stats.Vertices};boundaryEdges={p.Stats.BoundaryEdges};nonManifoldEdges={p.Stats.NonManifoldEdges};inconsistentWindingEdges={p.Stats.InconsistentWindingEdges};groups={p.Stats.MaterialGroups};actualCell={p.Stats.SampleSpacing:F5};seconds={p.Stats.GenerationSeconds:F4}")));

    private void Build(CourtyardSettings next)
    {
        Stopwatch watch = Stopwatch.StartNew();
        List<Part> replacement = [];
        try
        {
            string nextVolumeProbes = "not-built";
            CaveLevelPlan? nextLevelPlan = null;
            DungeonLevelPlan? nextDungeonPlan = null;
            testGenerationSeconds = 0;
            if (next.Study == "workbench")
            {
                WorkbenchRecipe.Compose(engine, stoneworksMaterials,
                    next.Workbench ?? throw new InvalidOperationException("No resolved workbench candidate."),
                    next.SwitchOpen, surface => AddPart(surface, replacement));
            }
            else if (next.Study == "reference")
            {
                CourtyardRecipe recipe = new(engine, materials);
                recipe.Compose(next, surface => AddPart(surface, replacement));
                testGenerationSeconds = recipe.TestGenerationSeconds;
            }
            else if (next.Study is "detail" or "motifs")
            {
                RecipeWriter writer = new(engine.ImplicitSurfaces,
                    new(next.CellSize, next.CreaseDegrees, 0.45f, next.MaterialBoundaryMode, next.MaterialSampleSpacing),
                    surface => AddPart(surface, replacement));
                writer.Box("detail study floor", new(-10, 2.5f, -12), new(10, 3, 1), stoneworksMaterials.Paving,
                    new(Vector3.Zero, Quaternion.Identity, Vector3.One));
                if (next.Study == "detail") DetailStudyRecipe.Build(writer, stoneworksMaterials, next);
                else DetailStudyRecipe.BuildFlatComparison(writer, stoneworksMaterials, next);
            }
            else if (GeneratedDungeonRecipe.IsStudy(next.Study))
            {
                var built = GeneratedDungeonRecipe.Compose(engine, stoneworksMaterials, next, surface => AddPart(surface, replacement, next.Study == "dungeon-split"));
                nextVolumeProbes = built.Probes;
                nextDungeonPlan = built.Plan;
            }
            else if (GeneratedCaveRecipe.IsStudy(next.Study))
            {
                var built = GeneratedCaveRecipe.Compose(engine, stoneworksMaterials, next, surface => AddPart(surface, replacement));
                nextVolumeProbes = built.Probes;
                nextLevelPlan = built.Plan;
            }
            else if (VolumeCaveRecipe.IsStudy(next.Study)) nextVolumeProbes = VolumeCaveRecipe.Compose(engine, stoneworksMaterials, next, surface => AddPart(surface, replacement), surface => AddSampledPart(surface, replacement));
            else if (next.Study == "cave") CaveRecipe.Compose(engine, stoneworksMaterials, next, surface => AddPart(surface, replacement));
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
            volumeProbes = nextVolumeProbes;
            levelPlan = nextLevelPlan;
            dungeonPlan = nextDungeonPlan;
            generation++;
        }
        catch
        {
            foreach (Part part in replacement) part.Dispose();
            throw;
        }
    }

    private void AddPart(RecipeSurface surface, List<Part> output, bool partition = false)
    {
        MeshResource mesh = engine.ImplicitSurfaces.Generate(new ImplicitGenerateRequest(surface.Field, surface.Root,
            surface.Min - new Vector3(DomainPadding), surface.Max + new Vector3(DomainPadding),
            surface.Sampling.CellSize, surface.Sampling.CreaseDegrees, surface.Sampling.TextureRepeats,
            surface.Material, surface.Regions, MaterialBoundaryMode: surface.Sampling.MaterialBoundaries, MaterialSampleSpacing:
                surface.Regions.Length > 0 && surface.Sampling.MaterialBoundaries == ImplicitMaterialBoundaryMode.Interpolated
                    ? surface.Sampling.MaterialSampleSpacing : 0f,
            MaxExtractionVertices: surface.Sampling.MaxExtractionVertices, MaxExtractionTriangles: surface.Sampling.MaxExtractionTriangles));
        PublishPart(surface.Name, mesh, () => engine.ImplicitSurfaces.ReadGeneration(surface.Field), surface.Placement, output, partition);
    }

    private void AddSampledPart(SampledRecipeSurface surface, List<Part> output)
    {
        MeshResource mesh = engine.ImplicitSurfaces.GenerateSampledVolume(surface.Request);
        PublishPart(surface.Name, mesh, () => engine.ImplicitSurfaces.ReadSampledVolumeGeneration(surface.Request.Volume), surface.Placement, output);
    }

    private void PublishPart(string name, MeshResource mesh, Func<ImplicitGenerationReadout> readGeneration,
        Transform placement, List<Part> output, bool partition = false)
    {
        List<VisualPart> visuals = [];
        try
        {
            if (partition)
            {
                // Partition presentation only. The original extraction remains
                // the collision source, with identical topology and statistics.
                using MeshPartition sections = engine.Graphics.PartitionMesh(new MeshPartitionRequest(
                    mesh, DungeonPartitionOrigin, DungeonPartitionSize));
                uint count = engine.Graphics.ReadMeshPartition(sections).PartCount;
                for (uint i = 0; i < count; i++)
                {
                    MeshResource section = engine.Graphics.TakeMeshPartitionPart(new MeshPartitionPartRequest(sections, i));
                    try { visuals.Add(new VisualPart(engine.Graphics.CreateMeshAppearance(section), section)); }
                    catch { section.Dispose(); throw; }
                }
            }
            else visuals.Add(new VisualPart(engine.Graphics.CreateMeshAppearance(mesh), null));
            output.Add(new Part(name, mesh, visuals, readGeneration(), placement));
        }
        catch { foreach (VisualPart visual in visuals) visual.Dispose(); mesh.Dispose(); throw; }
    }

    private void ApplyStudyLighting()
    {
        if (lights.Count == 0) return;
        bool cave = settings.Study == "cave" || VolumeCaveRecipe.IsStudy(settings.Study) || GeneratedCaveRecipe.IsStudy(settings.Study);
        // Product lighting profiles share the Engine's existing retained lights.
        (Vector3 Color, float Intensity, Vector3 Position)[] profile = GeneratedCaveRecipe.IsStudy(settings.Study)
            ? [(new(0.60f, 0.73f, 1f), 0.38f, Vector3.Zero),
               (new(0.85f, 0.92f, 1f), 2.1f, new(-12f, 22f, -8f)),
               (new(1f, 0.55f, 0.23f), 65f, new(-6f, 7f, 16f)),
               (new(0.42f, 0.66f, 1f), 65f, new(5f, 7f, 27f))]
            : VolumeCaveRecipe.IsStudy(settings.Study)
            ? [(new(0.60f, 0.73f, 1f), 0.26f, Vector3.Zero),
               (new(0.85f, 0.92f, 1f), 2.1f, new(-12f, 22f, -8f)),
               (new(1f, 0.55f, 0.23f), 40f, new(-3f, 7f, 6f)),
               (new(0.42f, 0.66f, 1f), 45f, new(0f, 7f, 18f))]
            : cave
            ? [(new(0.60f, 0.73f, 1f), 0.32f, Vector3.Zero),
               (new(0.85f, 0.92f, 1f), 2.1f, new(-12f, 22f, -8f)),
               (new(1f, 0.48f, 0.16f), 25f, new(3.5f, 5.8f, 7f)),
               (new(0.42f, 0.66f, 1f), 18f, new(-3f, 9f, 5f))]
            : [(new(0.77f, 0.83f, 1f), 0.75f, Vector3.Zero),
               (new(1f, 0.84f, 0.62f), 2.1f, new(-12f, 22f, -8f)),
               (new(1f, 0.49f, 0.19f), 16f, new(-1.8f, 7.4f, 17f)),
               (new(0.55f, 0.74f, 1f), 20f, new(0f, 8.6f, 28f))];
        if (GeneratedDungeonRecipe.IsStudy(settings.Study) && dungeonPlan is { } dungeon)
        {
            // Ambient fill keeps an enclosed inspection useful. Room lights
            // provide depth cues without assigning shadows to every point light.
            profile = new (Vector3 Color, float Intensity, Vector3 Position)[] {
                (new(0.76f, 0.80f, 0.92f), 0.65f, Vector3.Zero),
                (Vector3.One, 0f, new(-12, 22, -8)) }
                .Concat(dungeon.Rooms.Select(room => (room.Index % 3 == 0 ? new Vector3(1f, 0.68f, 0.35f) : new Vector3(0.68f, 0.79f, 1f),
                    42f, room.Center with { Y = room.Maximum.Y - 0.7f }))).ToArray();
        }
        if (settings.Study == "workbench" && settings.Workbench is { } workbench)
        {
            profile = new (Vector3 Color, float Intensity, Vector3 Position)[] {
                (new(0.76f, 0.80f, 0.92f), 0.7f, Vector3.Zero),
                (Vector3.One, 0f, new(-12, 22, -8)) }
                .Concat(workbench.Rooms.Select(room => (room.Id == workbench.SwitchRoom
                    ? new Vector3(1f, 0.72f, 0.4f) : new Vector3(0.68f, 0.79f, 1f),
                    32f, WorkbenchRecipe.Center(room) with { Y = room.Maximum.Y - 0.7f }))).ToArray();
        }
        while (lights.Count < profile.Length)
            AddLight(LightKind.Point, profile[lights.Count].Color, profile[lights.Count].Intensity, profile[lights.Count].Position, Vector3.Zero);
        for (int index = 0; index < lights.Count; index++)
        {
            (Light owner, LightDescriptor descriptor) = lights[index];
            descriptor = index < profile.Length
                ? descriptor with { Enabled = true, Color = profile[index].Color, Intensity = profile[index].Intensity, Position = profile[index].Position }
                : descriptor with { Enabled = false };
            lights[index] = (owner, descriptor);
            engine.Graphics.UpdateLight(new LightUpdateRequest(owner, new LightRequest(FirstLightId + (ulong)index,
                false, 0, descriptor with { Position = descriptor.Position + translation })));
        }
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

    private static readonly Vector3 DungeonPartitionSize = new(14f, 14f, 14f);
    private static readonly Vector3 DungeonPartitionOrigin = new(-7f, -1f, -7f);

    private sealed record VisualPart(Appearance Appearance, MeshResource? Mesh) : IDisposable
    {
        public void Dispose() { Appearance.Dispose(); Mesh?.Dispose(); }
    }

    private sealed record Part(string Name, MeshResource Mesh, List<VisualPart> Visuals, ImplicitGenerationReadout Stats, Transform Placement) : IDisposable
    {
        public void Dispose() { foreach (VisualPart visual in Visuals) visual.Dispose(); Mesh.Dispose(); }
    }

}
