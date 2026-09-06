using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

internal readonly record struct CourtyardSettings(
    string Treatment, float Width, float DoorWidth, float DoorOffset, ulong Seed,
    float CellSize, float CreaseDegrees)
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
    private const float CourtyardFloor = 3f;
    private const float RaisedFloor = 5f;
    private const float PassageStart = 10f;
    private const float PassageEnd = 22f;
    private const float ChamberEnd = 32f;
    private const float WallThickness = 0.6f;
    private const float WallSectionLength = 4f;
    private const float CourseHeight = 0.6f;
    private const float StoneLength = 0.95f;
    private const float JointHalfWidth = 0.045f;
    private const float StoneRelief = 0.09f;
    private const float UvRepeatsPerMeter = 0.6f;
    private const float DomainPadding = 0.25f;
    private readonly IEngineContext engine;
    private readonly CourtyardMaterials materials;
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

    internal CourtyardScene(IEngineContext engine)
    {
        this.engine = engine;
        materials = new CourtyardMaterials(engine);
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
        FirstObjectId + (ulong)index, false, 0, new Transform(translation, Quaternion.Identity, Vector3.One),
        part.Appearance, true, RenderLayer.Scene));

    internal string QueueTreatment(string treatment)
    {
        pending = (pending ?? settings).WithTreatment(treatment);
        return $"queued courtyard treatment={treatment}";
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
        $"generation={generation};treatment={settings.Treatment};width={settings.Width:F1};doorWidth={settings.DoorWidth:F1};doorOffset={settings.DoorOffset:F2};seed={settings.Seed};courtyardDepth=20;passageLength=12;chamber=12x10;parts={parts.Count};triangles={triangleCount};vertices={vertexCount};seconds={generationSeconds:F3};cellSize={settings.CellSize:F3};crease={settings.CreaseDegrees:F0};reorientedTriangles={correctionCount};shadows={shadows};collision=generated-mesh-copy");

    private void Build(CourtyardSettings next)
    {
        Stopwatch watch = Stopwatch.StartNew();
        List<Part> replacement = [];
        try
        {
            float halfWidth = next.Width * 0.5f;
            // Floors and the raised route are authored solids; texture density
            // stays constant when dimensions or extraction sampling change.
            BoxPart("courtyard floor", new(-halfWidth, CourtyardFloor - 0.5f, -10f), new(halfWidth, CourtyardFloor, PassageStart), materials.Ground, next, replacement);
            BoxPart("passage floor", new(-2.6f, RaisedFloor - 0.5f, PassageStart), new(2.6f, RaisedFloor, PassageEnd), materials.Stone, next, replacement);
            BoxPart("chamber floor", new(-6f, RaisedFloor - 0.5f, PassageEnd), new(6f, RaisedFloor, ChamberEnd), materials.Stone, next, replacement);
            // Union before extraction so hidden overlapping risers never enter
            // the collision world as separate surfaces.
            using (Recipe stairs = new(engine.ImplicitSurfaces))
            {
                const int stairCount = 6;
                const float stairRun = 4f;
                ImplicitNode solid = default;
                for (int step = 0; step < stairCount; step++)
                {
                    float top = CourtyardFloor + (RaisedFloor - CourtyardFloor) * (step + 1) / stairCount;
                    float front = PassageStart - stairRun + stairRun * step / stairCount;
                    ImplicitNode riser = stairs.Box(new(-2f, CourtyardFloor - 0.1f, front), new(2f, top, PassageStart + 0.08f));
                    // Each visible nose is a 45-degree cut, followed by a flat
                    // tread. Collision copies this exact surface.
                    Vector3 noseNormal = Vector3.Normalize(new Vector3(0f, 1f, -1f));
                    float noseRun = (RaisedFloor - CourtyardFloor) / stairCount;
                    ImplicitNode nose = stairs.Plane(noseNormal, Vector3.Dot(noseNormal, new Vector3(0f, top, front + noseRun)));
                    riser = stairs.Intersect(riser, nose);
                    solid = step == 0 ? riser : stairs.Union(solid, riser);
                }
                AddPart("stair flight", stairs, solid, new(-2f, CourtyardFloor - 0.1f, PassageStart - stairRun),
                    new(2f, RaisedFloor, PassageStart + 0.08f), materials.Plaster, [], next, replacement);
            }

            WallRun("south", new(-halfWidth, CourtyardFloor, -10f), new(halfWidth, 7.2f, -10f + WallThickness), false, next, replacement);
            WallRun("west", new(-halfWidth, CourtyardFloor, -10f), new(-halfWidth + WallThickness, 7.6f, PassageStart), false, next, replacement);
            WallRun("east", new(halfWidth - WallThickness, CourtyardFloor, -10f), new(halfWidth, 6.4f, PassageStart), false, next, replacement);
            WallRun("gateway", new(-halfWidth, CourtyardFloor, PassageStart - WallThickness), new(halfWidth, 8.1f, PassageStart), true, next, replacement);
            WallRun("passage west", new(-2.6f, RaisedFloor, PassageStart), new(-2f, 8.6f, PassageEnd), false, next, replacement);
            WallRun("passage east", new(2f, RaisedFloor, PassageStart), new(2.6f, 8.6f, PassageEnd), false, next, replacement);
            BoxPart("covered passage", new(-2.7f, 8.6f, PassageStart), new(2.7f, 9f, PassageEnd), materials.DarkNeutral, next, replacement);
            WallRun("chamber entrance", new(-6f, RaisedFloor, PassageEnd), new(6f, 9f, PassageEnd + WallThickness), true, next, replacement);
            WallRun("chamber west", new(-6f, RaisedFloor, PassageEnd), new(-5.4f, 9.5f, ChamberEnd), false, next, replacement);
            WallRun("chamber east", new(5.4f, RaisedFloor, PassageEnd), new(6f, 8.8f, ChamberEnd), false, next, replacement);
            WallRun("chamber end", new(-6f, RaisedFloor, ChamberEnd - WallThickness), new(6f, 10f, ChamberEnd), false, next, replacement);

            Frame("gateway frame", PassageStart - WallThickness - 0.12f, next, replacement);
            Frame("chamber frame", PassageEnd - 0.15f, next, replacement);
            for (int beam = 0; beam < 4; beam++)
            {
                float z = PassageStart + 1f + beam * 3f;
                BoxPart($"roof beam {beam}", new(-2f, 8.25f, z), new(2f, 8.62f, z + 0.28f), materials.Wood, next, replacement);
            }
            // A low altar and clipped columns anchor the chamber's long view.
            BoxPart("altar base", new(-2.1f, RaisedFloor, 29.4f), new(2.1f, 5.4f, 31f), materials.DarkNeutral, next, replacement);
            BoxPart("altar cap", new(-2.3f, 5.4f, 29.2f), new(2.3f, 5.65f, 31.2f), materials.Plaster, next, replacement);
            foreach (float x in new[] { -4.3f, 4.3f })
            {
                BoxPart("column foot", new(x - 0.55f, 5f, 28.3f), new(x + 0.55f, 5.5f, 29.4f), materials.Plaster, next, replacement);
                using Recipe column = new(engine.ImplicitSurfaces);
                ImplicitNode shaft = column.Box(new(x - 0.36f, 5.4f, 28.5f), new(x + 0.36f, 8.7f, 29.2f));
                Vector3 cutNormal = Vector3.Normalize(new Vector3(0.35f, 1f, 0.2f));
                ImplicitNode cut = column.Plane(cutNormal, Vector3.Dot(cutNormal, new Vector3(x, 8.35f, 28.85f)));
                shaft = column.Intersect(shaft, cut);
                AddPart("clipped column", column, shaft, new(x - 0.8f, 5.1f, 28.2f), new(x + 0.8f, 9f, 29.6f), materials.Plaster, [], next, replacement);
            }
            OrganicDetails(halfWidth, next, replacement);

            SpatialSession spatial = session ?? throw new InvalidOperationException("Courtyard collision session unavailable.");
            StaticMeshAsset[] assets = replacement.Select((p, i) => new StaticMeshAsset(
                FirstObjectId + (ulong)i, new MeshResourceReference(p.Mesh), 0, 0, 0, 0)).ToArray();
            StaticMeshInstance[] instances = replacement.Select((p, i) => new StaticMeshInstance(
                FirstObjectId + (ulong)i, FirstObjectId + (ulong)i, new Transform(translation, Quaternion.Identity, Vector3.One))).ToArray();
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

    private void WallRun(string name, Vector3 min, Vector3 max, bool doorway, CourtyardSettings next, List<Part> output)
    {
        bool alongX = max.X - min.X > max.Z - min.Z;
        float start = alongX ? min.X : min.Z;
        float end = alongX ? max.X : max.Z;
        for (float position = start; position < end - 0.01f; position += WallSectionLength)
        {
            Vector3 a = min;
            Vector3 b = max;
            if (alongX) { a.X = position; b.X = MathF.Min(end, position + WallSectionLength); }
            else { a.Z = position; b.Z = MathF.Min(end, position + WallSectionLength); }
            using Recipe recipe = new(engine.ImplicitSurfaces);
            ImplicitNode wall = recipe.Box(a, b);
            // Courses are globally phased inside each architectural wall frame,
            // then intersected with its section. Sections share solid backing.
            for (int course = 0; min.Y + course * CourseHeight < max.Y; course++)
            {
                float bottom = min.Y + course * CourseHeight;
                float phase = (course % 2) * StoneLength * 0.5f;
                for (float p = MathF.Floor((position - phase) / StoneLength) * StoneLength + phase; p < b[alongX ? 0 : 2]; p += StoneLength)
                {
                    float left = MathF.Max(position, p + JointHalfWidth);
                    float right = MathF.Min(b[alongX ? 0 : 2], p + StoneLength - JointHalfWidth);
                    if (right <= left) continue;
                    Vector3 stoneMin = a - new Vector3(StoneRelief, 0, StoneRelief);
                    Vector3 stoneMax = b + new Vector3(StoneRelief, 0, StoneRelief);
                    stoneMin.Y = bottom + JointHalfWidth;
                    stoneMax.Y = MathF.Min(max.Y, bottom + CourseHeight - JointHalfWidth);
                    if (alongX) { stoneMin.X = left; stoneMax.X = right; }
                    else { stoneMin.Z = left; stoneMax.Z = right; }
                    if (stoneMax.Y > stoneMin.Y) wall = recipe.Union(wall, recipe.Box(stoneMin, stoneMax));
                }
            }
            const float capDepth = 0.14f;
            ImplicitNode cap = recipe.Box(new(a.X - capDepth, b.Y - 0.22f, a.Z - capDepth), new(b.X + capDepth, b.Y + 0.14f, b.Z + capDepth));
            wall = recipe.Union(wall, cap);
            // Deliberate broken tops: sparse larger chips, not even surface noise.
            float chipPosition = position + WallSectionLength * 0.65f;
            long chipVariation = engine.Random.DrawKeyed(new KeyedRngRequest(next.Seed, "courtyard.chips", FormattableString.Invariant($"{name}:{position:R}"), 0, 3)).Value;
            float chipRadius = 0.22f + chipVariation * 0.11f;
            Vector3 chip = alongX ? new(chipPosition, b.Y + 0.08f, (a.Z + b.Z) * 0.5f) : new((a.X + b.X) * 0.5f, b.Y + 0.08f, chipPosition);
            wall = recipe.Subtract(wall, recipe.Sphere(chip, chipRadius));
            if (doorway)
            {
                float doorHalf = next.DoorWidth * 0.5f;
                ImplicitNode opening = recipe.Box(new(next.DoorOffset - doorHalf, min.Y - 1f, min.Z - 1f), new(next.DoorOffset + doorHalf, 7.75f, max.Z + 1f));
                wall = recipe.Subtract(wall, opening);
            }
            ImplicitNode mossRegion = recipe.Box(a - Vector3.One, new(b.X + 1f, min.Y + 0.38f, b.Z + 1f));
            AddPart(name, recipe, wall, a - new Vector3(0.3f), b + new Vector3(0.35f), materials.Stone,
                [new ImplicitMaterialRegion(recipe.Offset(cap, 0.03f), materials.Plaster), new ImplicitMaterialRegion(mossRegion, materials.Moss)], next, output);
        }
    }

    private void Frame(string name, float z, CourtyardSettings next, List<Part> output)
    {
        float half = next.DoorWidth * 0.5f;
        float center = next.DoorOffset;
        const float trimWidth = 0.25f;
        foreach (float x in new[] { center - half - trimWidth, center + half })
            BoxPart(name + " jamb", new(x, RaisedFloor, z), new(x + trimWidth, 7.85f, z + 0.28f), materials.Plaster, next, output);
        BoxPart(name + " lintel", new(center - half - trimWidth, 7.75f, z), new(center + half + trimWidth, 8.1f, z + 0.28f), materials.Plaster, next, output);
    }

    private void OrganicDetails(float halfWidth, CourtyardSettings next, List<Part> output)
    {
        foreach ((Vector3 center, Vector3 radii) in new[] {
            (new Vector3(-8.5f, 3.25f, -2f), new Vector3(1.8f, 1.1f, 1.4f)),
            (new Vector3(8.5f, 3.2f, 3f), new Vector3(1.5f, 0.8f, 2f)),
            (new Vector3(-7f, 3.15f, 6f), new Vector3(1.1f, 0.65f, 1.2f)) })
        {
            using Recipe recipe = new(engine.ImplicitSurfaces);
            ImplicitNode rock = recipe.Ellipsoid(center, radii);
            Vector3 clipNormal = Vector3.Normalize(new Vector3(0.3f, 1f, 0.15f));
            ImplicitNode clip = recipe.Plane(clipNormal, Vector3.Dot(clipNormal, center + new Vector3(0f, radii.Y * 0.6f, 0f)));
            rock = recipe.Intersect(rock, clip);
            ImplicitNode moss = recipe.Box(center - radii, center + new Vector3(radii.X, 0.15f, radii.Z));
            AddPart("broken rock", recipe, rock, center - radii, center + radii, materials.Stone, [new ImplicitMaterialRegion(moss, materials.Moss)], next, output);
        }
        using Recipe roots = new(engine.ImplicitSurfaces);
        Vector3 origin = new(-halfWidth + 0.9f, 3.2f, 2f);
        ImplicitNode root = roots.Capsule(origin, origin + new Vector3(2f, -0.08f, 1f), 0.23f);
        for (int branch = 0; branch < 4; branch++)
        {
            Vector3 bend = origin + new Vector3(1.2f + branch * 0.45f, 0.15f, branch - 1f);
            Vector3 tip = bend + new Vector3(1.4f, -0.17f, 0.7f);
            root = roots.Blend(root, roots.Capsule(origin, bend, 0.18f), 0.12f);
            root = roots.Blend(root, roots.Capsule(bend, tip, 0.12f), 0.1f);
        }
        AddPart("root cluster", roots, root, origin - new Vector3(0.4f, 0.4f, 2f), origin + new Vector3(5f, 1f, 4f), materials.Wood, [], next, output);
    }

    private void BoxPart(string name, Vector3 min, Vector3 max, Material material, CourtyardSettings next, List<Part> output)
    {
        using Recipe recipe = new(engine.ImplicitSurfaces);
        Vector3 extent = max - min;
        float narrowest = MathF.Min(extent.X, MathF.Min(extent.Y, extent.Z));
        CourtyardSettings planar = next with { CellSize = MathF.Min(0.5f, narrowest * 0.75f) };
        AddPart(name, recipe, recipe.Box(min, max), min, max, material, [], planar, output);
    }

    private void AddPart(string name, Recipe recipe, ImplicitNode root, Vector3 min, Vector3 max, Material material,
        ImplicitMaterialRegion[] regions, CourtyardSettings next, List<Part> output)
    {
        MeshResource mesh = engine.ImplicitSurfaces.Generate(new ImplicitGenerateRequest(recipe.Field, root,
            min - new Vector3(DomainPadding), max + new Vector3(DomainPadding), next.CellSize, next.CreaseDegrees,
            UvRepeatsPerMeter, material, regions));
        try
        {
            Appearance appearance = engine.Graphics.CreateMeshAppearance(mesh);
            output.Add(new Part(name, mesh, appearance, engine.ImplicitSurfaces.ReadGeneration(recipe.Field)));
        }
        catch { mesh.Dispose(); throw; }
    }

    private void AddLight(LightKind kind, Vector3 color, float intensity, Vector3 position, Vector3 direction)
    {
        LightDescriptor descriptor = new(kind, color, intensity, true, position, direction,
            kind == LightKind.Point, 14f, 2f, 0.6f, 0f,
            kind == LightKind.Directional ? LightShadowIntent.Requested : LightShadowIntent.Disabled);
        Light owner = engine.Graphics.CreateLight(new LightRequest(FirstLightId + (ulong)lights.Count, false, 0, descriptor));
        lights.Add((owner, descriptor));
    }

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
        materials.Dispose();
    }

    private sealed record Part(string Name, MeshResource Mesh, Appearance Appearance, ImplicitGenerationReadout Stats) : IDisposable
    {
        public void Dispose() { Appearance.Dispose(); Mesh.Dispose(); }
    }

    // This helper only composes named Engine operations; it evaluates no fields
    // and generates no geometry in the product.
    private sealed class Recipe(IImplicitSurfacesService service) : IDisposable
    {
        internal ImplicitField Field { get; } = service.CreateField();
        internal ImplicitNode Box(Vector3 min, Vector3 max) => service.AddBox(new(Field, min, max));
        internal ImplicitNode Sphere(Vector3 center, float radius) => service.AddSphere(new(Field, center, radius));
        internal ImplicitNode Ellipsoid(Vector3 center, Vector3 radii) => service.AddEllipsoid(new(Field, center, radii));
        internal ImplicitNode Capsule(Vector3 start, Vector3 end, float radius) => service.AddCapsule(new(Field, start, end, radius));
        internal ImplicitNode Plane(Vector3 normal, float offset) => service.AddPlane(new(Field, normal, offset));
        internal ImplicitNode Union(ImplicitNode a, ImplicitNode b) => service.Union(new(Field, a, b));
        internal ImplicitNode Intersect(ImplicitNode a, ImplicitNode b) => service.Intersection(new(Field, a, b));
        internal ImplicitNode Subtract(ImplicitNode a, ImplicitNode b) => service.Difference(new(Field, a, b));
        internal ImplicitNode Offset(ImplicitNode source, float amount) => service.Offset(new(Field, source, amount));
        internal ImplicitNode Blend(ImplicitNode a, ImplicitNode b, float radius) => service.SmoothUnion(new(Field, a, b, radius));
        public void Dispose() => Field.Dispose();
    }
}
