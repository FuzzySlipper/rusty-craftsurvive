using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Interaction;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>
/// A town visit (#9895): the composed view of a town standing in the world, its selectable service
/// buildings and their conversations. It owns visit identity, the camera policy and selection; the
/// town's art is <see cref="TownPresence"/>'s, and the expedition's state is the product's, which
/// applies a service's real action and hands back its outcome. A visit is never a place to walk.
/// </summary>
internal sealed class TownModule : IWorldInteractionScene, IDisposable
{
    private const double MaximumPresentationStep = .25;
    private const float LabelHeight = 1.8f;
    private const double Near = .1;
    private const float HorizontalShift = .65f, VerticalShift = .18f, HorizontalLook = .28f, VerticalLook = .10f;
    private const float ResponsePerSecond = 8, SettleThreshold = .0001f, CloseResponsePerSecond = 2.5f;
    private const float SelectionAngle = .002f, SelectionReach = 110, VisibilityClearance = .01f;
    private const double SpatialVoxelMetres = 1, CameraDelay = 0;
    private const uint SpatialChunkSize = 16, SelectionGroup = 1;
    private const double DefaultAspect = 16d / 9;

    private readonly IEngineContext engine;
    private readonly TownContent content;
    private readonly Action<TownUiFacts> publish;
    private TownPresence? presence;
    private readonly Dictionary<string, UiImage> portraits = [];
    private TownServices? services;
    private TownAudio? audio;
    private Camera? camera;
    private SpatialSession? session;
    private SpatialEntityCollider[] colliders = [];
    private WorldFrame? frame;
    private TownSite? site;
    private TownConditions conditions;
    private TownView? lastClose;
    private double far;
    private double aspect = DefaultAspect;
    private double cameraSeconds;
    private ulong visit;
    private ulong hover;
    private string focused = "";
    private string prompt = "";
    private TownUiFacts? lastFacts;
    private Vector2 cursor = new(.5f), offset;
    private float closeness;

    internal TownModule(IEngineContext engine, TownContent content, Action<TownUiFacts> publish)
    {
        this.engine = engine;
        this.content = content;
        this.publish = publish;
        Interactions = new(this);
    }

    internal WorldInteraction Interactions { get; }
    internal bool Open { get; private set; }
    internal bool Motion { get; private set; } = true;
    internal TownSite? Site => site;
    internal PlacedService? Selected { get; private set; }
    internal TownConditions Conditions => conditions;
    internal ulong Visit => visit;
    internal Vector2 CameraOffset => offset;
    internal float OutdoorGain => 1 - (audio?.Presence ?? 0);
    internal int AudioVoices => audio?.Voices ?? 0;

    /// <summary>The product's live reasons a service choice cannot be taken (a full pack), for the menu.</summary>
    internal Func<ServiceChoice, string?> Blocked { get; set; } = _ => null;

    /// <summary>The offer shown at a town's doorstep (outside its bubble, or on arriving by map), or none.</summary>
    internal void Offer(string text)
    {
        if (prompt == text) return;
        prompt = text;
        Publish();
    }

    /// <summary>Opens the composed view of the town standing in the world; the traveller stands at its arrival view.</summary>
    internal bool Enter(TownSite town, TownPresence placed, TownConditions now, WorldFrame drawing, double farMetres)
    {
        if (Open || placed.Site?.Key != town.Key) return false;
        presence = placed;
        site = town;
        conditions = now;
        frame = drawing;
        far = farMetres;
        services = new(content, town);
        visit++;
        Selected = null;
        lastClose = null;
        focused = "";
        hover = 0;
        cursor = new(.5f);
        offset = Vector2.Zero;
        closeness = 0;
        Interactions.Focus.Clear();
        session = engine.Spatial.CreateSession(new(SpatialVoxelMetres, SpatialChunkSize, VoxelSurfaceMode.GreedyCubes));
        colliders = [.. placed.Services.Select(service => Collider(service.Id, service.Min, service.Max)),
            .. placed.Blockers.Select((box, i) => Collider(ProductIds.TownBlockerBase + (ulong)i, box.Min, box.Max))];
        ReadAspect();
        camera = engine.CameraView.CreateCamera(Descriptor());
        engine.CameraView.SetActiveCamera(camera);
        audio ??= new(engine);
        services.Reset();
        Open = true;
        prompt = "";
        Publish();
        return true;
    }

    /// <summary>Withdraws the visit; the product restores the traveller's own camera.</summary>
    internal void Leave()
    {
        if (!Open) return;
        Open = false;
        Selected = null;
        hover = 0;
        Interactions.Focus.Clear();
        camera?.Dispose();
        camera = null;
        session?.Dispose();
        session = null;
        colliders = [];
        Publish();
    }

    /// <summary>Audio fades on host time, which keeps passing while a menu holds the game.</summary>
    internal void Present(double seconds)
    {
        if (audio is null) return;
        audio.Advance(Math.Clamp(seconds, 0, MaximumPresentationStep), Open, Selected is not null, site);
        if (!Open && audio.Silent)
        {
            audio.Dispose();
            audio = null;
        }
    }

    internal void ClearInput()
    {
        focused = "";
        cursor = new(.5f);
        hover = 0;
        Interactions.Focus.Clear();
    }

    internal void SetMotion(bool enabled)
    {
        Motion = enabled;
        cursor = new(.5f);
        Publish();
    }

    internal void Back()
    {
        if (!Open) return;
        Selected = null;
        services?.Greet();
        ClearInput();
        Publish();
    }

    internal void FocusDestination(string service)
    {
        focused = Open && Selected is null && presence!.Services.Any(s => s.Service == service) ? service : "";
        cursor = new(.5f);
        Publish();
    }

    internal void Select(string service)
    {
        if (!Open || presence!.Services.FirstOrDefault(s => s.Service == service) is not PlacedService place) return;
        Interactions.UseTarget(new(place.Id, visit));
    }

    /// <summary>A service choice the product should apply, or none when it only talks or is not on offer.</summary>
    internal TownServiceRequest? Choose(string service, string key, ulong revision)
    {
        // A delayed choice must not act at a different counter, or in a later visit.
        if (!Open || revision != visit || Selected?.Service != service || services is null) return null;
        if (services.Offered(service, key, conditions) is not ServiceChoice choice) return null;
        if (choice.Action == TownServices.Talk)
        {
            services.Record(service, choice, conditions, null);
            Publish();
            return null;
        }

        return services.Unavailable(service, choice, Blocked).Length == 0 ? new(service, choice) : null;
    }

    /// <summary>Records what the product did for a choice, under the conditions after it.</summary>
    internal void Record(TownServiceRequest request, TownServiceOutcome outcome, TownConditions now)
    {
        conditions = now;
        services?.Record(request.Service, request.Choice, conditions, outcome);
        Publish();
    }

    internal void Update(ReadOnlySpan<ProductInputEvent> input, double seconds)
    {
        if (!Open) return;
        ReadAspect();
        foreach (ProductInputEvent fact in input)
        {
            if (fact.Kind == InputEventKind.Clear) ClearInput();
            else if (fact.Kind == InputEventKind.PointerPosition && Selected is null)
            {
                focused = "";
                cursor = Vector2.Clamp(new(fact.X, fact.Y), Vector2.Zero, Vector2.One);
            }
            else if (fact.Kind == InputEventKind.PointerButton && fact.PointerButton == PointerButton.Primary
                && fact.Edge == InputEdge.Pressed && fact.HasPosition && Selected is null)
            {
                Click(new(fact.X, fact.Y));
            }
        }

        Advance(seconds);
    }

    /// <summary>Uses the currently published pose for the click, before the eased camera moves on.</summary>
    internal void Click(Vector2 point)
    {
        if (!Open || Selected is not null || session is null) return;
        cursor = Vector2.Clamp(point, Vector2.Zero, Vector2.One);
        SpatialHit hit = Pick(CameraQueries.Ray(Descriptor(), aspect, cursor));
        Interactions.Update();
        // A cone alone must not open an entrance beside the clicked building, or through the foreground.
        if (hit.Present && Interactions.Focus.Selected?.Id == hit.Entity) Interactions.UseFocused();
    }

    internal void Advance(double seconds)
    {
        if (!Open || camera is null) return;
        bool settled = Selected is not null || focused.Length > 0;
        Vector2 target = Motion && !settled ? (cursor - new Vector2(.5f)) * 2 : Vector2.Zero;
        float step = (float)Math.Max(0, seconds);
        offset = Vector2.Lerp(offset, target, 1 - MathF.Exp(-ResponsePerSecond * step));
        if (Vector2.DistanceSquared(offset, target) < SettleThreshold * SettleThreshold) offset = target;
        float close = Selected?.Close is null ? 0 : 1;
        closeness += (close - closeness) * (1 - MathF.Exp(-CloseResponsePerSecond * step));
        if (MathF.Abs(close - closeness) < SettleThreshold) closeness = close;
        cameraSeconds += step;
        engine.CameraView.UpdateCameraSample(new(camera, Descriptor(), cameraSeconds, CameraDelay, CameraInterpolation.Latest, 1));
        hover = Selected is null ? Interactions.Update().Selected?.Id ?? 0 : 0;
        Publish();
    }

    public InteractionSceneSnapshot ReadInteraction()
    {
        if (!Open || session is null || frame is null || site is null)
        {
            InteractionQuery idle = new(Vector3.Zero, -Vector3.UnitZ, SelectionAngle, SelectionAngle, SelectionReach, SelectionReach, 1, 0, null);
            return new(idle, ReadOnlyMemory<InteractionCandidate>.Empty, $"town:closed:{visit}", "visit entrance");
        }

        CameraRay ray = CameraQueries.Ray(Descriptor(), aspect, cursor);
        InteractionQuery query = new(ray.Origin, ray.Direction, SelectionAngle, SelectionAngle, SelectionReach, SelectionReach, 1, 0, null);
        SpatialHit hit = Pick(ray);
        InteractionCandidate[] candidates = [.. presence!.Services.Select(place =>
        {
            Vector3 entrance = Local(place.Entrance);
            return new InteractionCandidate(new(place.Id, visit), place.Name, hit.Present && hit.Entity == place.Id ? hit.Point : entrance,
                SelectionReach, InteractionVisibilityQuery.Cast(engine.Spatial, session, ray.Origin, entrance, new(uint.MaxValue, uint.MaxValue),
                    colliders, new ulong[] { place.Id }, VisibilityClearance),
                Selected is null ? InteractionAvailability.Available : InteractionAvailability.Unavailable, 0, InteractionRoute.Unknown);
        })];
        return new(query, candidates, $"town:{visit}:{Selected?.Service ?? "overview"}", "visit entrance");
    }

    public InteractionActionResult UseInteraction(InteractionTarget target)
    {
        if (!Open || Selected is not null || target.Revision != visit || presence!.Services.FirstOrDefault(s => s.Id == target.Id) is not PlacedService place)
            return new(false, "That entrance is not available in this view.");
        Selected = place;
        if (place.Close is TownView close) lastClose = close;
        services?.Greet();
        ClearInput();
        Publish();
        return new(true, $"Entered {place.Name}");
    }

    private SpatialHit Pick(CameraRay ray) => engine.Spatial.CastRay(new(session!, ray.Origin, ray.Direction, SelectionReach,
        new(uint.MaxValue, uint.MaxValue), colliders, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));

    private SpatialEntityCollider Collider(ulong id, (double X, double Y, double Z) min, (double X, double Y, double Z) max) =>
        new(id, Local(min), Local(max), SelectionGroup, uint.MaxValue, true, true, false);

    private Vector3 Local((double X, double Y, double Z) world) => frame!.ToLocal(world.X, world.Y, world.Z);

    /// <summary>The composed pose: the wide view, eased toward a selected building's close view, nudged by the pointer.</summary>
    private CameraDescriptor Descriptor()
    {
        TownView wide = site!.Layout.View;
        TownView view = lastClose is TownView close && closeness > 0 ? Blend(wide, close, closeness) : wide;
        Vector3 eye = Point(view.Eye), focus = Point(view.Focus);
        Vector3 forward = Vector3.Normalize(focus - eye), right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        eye += (right * offset.X * HorizontalShift) + (Vector3.UnitY * offset.Y * VerticalShift);
        focus += (right * offset.X * HorizontalLook) + (Vector3.UnitY * offset.Y * VerticalLook);
        // The authored points never coincide.
        CameraQueries.TryLookAtPose(eye, focus, 0, out CameraPose pose);
        return new(pose, CameraBasisMode.Derived, default,
            new(CameraProjectionKind.Perspective, view.FieldOfView, 0, Near, far), CameraViewports.Full);

        Vector3 Point(Vector3 local)
        {
            (double x, double y, double z) = site.Frame.World(local);
            return Local((x, presence!.Pad + (y - site.Frame.Y), z));
        }
    }

    private static TownView Blend(TownView a, TownView b, float t) =>
        new(Vector3.Lerp(a.Eye, b.Eye, t), Vector3.Lerp(a.Focus, b.Focus, t), a.FieldOfView + ((b.FieldOfView - a.FieldOfView) * t));

    private void ReadAspect()
    {
        CameraSurfaceReadout surface = engine.CameraView.ReadSurface();
        if (surface.Reported && surface.CssWidth > 0 && surface.CssHeight > 0) aspect = surface.CssWidth / surface.CssHeight;
    }

    private UiImage? Portrait(string? path)
    {
        if (path is null) return null;
        if (portraits.TryGetValue(path, out UiImage? image)) return image;
        using ContentReference source = engine.Content.OpenReference(new(path));
        image = engine.Ui.OpenImage(new(source));
        portraits[path] = image;
        return image;
    }

    private void Publish()
    {
        string anchors = "";
        TownServiceUiFacts? service = null;
        if (Open && site is not null && camera is not null)
        {
            CameraDescriptor pose = Descriptor();
            anchors = string.Join(';', presence!.Services.Select(place =>
            {
                CameraProjectionResult projected = CameraQueries.Project(pose, aspect, Local(place.Entrance) + (Vector3.UnitY * LabelHeight));
                return string.Create(CultureInfo.InvariantCulture,
                    $"{place.Service}|{place.Name}|{content.Services[place.Service].Summary}|{projected.NormalizedPoint.X:F5}|{projected.NormalizedPoint.Y:F5}|{(projected.InClip ? 1 : 0)}");
            }));
            if (Selected is PlacedService selected && services is not null)
                service = services.Describe(selected.Service, conditions, Portrait(site.Keepers[selected.Service].Portrait)?.Url() ?? "", Blocked);
        }

        string hovered = focused.Length > 0 ? focused : presence?.Services.FirstOrDefault(s => s.Id == hover)?.Service ?? "";
        TownUiFacts facts = new(Open, Open ? site?.Name ?? "" : "", Open && site is not null ? Subtitle(site) : "", Selected?.Service ?? "",
            Selected?.Name ?? "", hovered, Motion, anchors, visit.ToString(CultureInfo.InvariantCulture), service, Open ? "" : prompt);
        if (facts == lastFacts) return;
        lastFacts = facts;
        publish(facts);
    }

    private string Subtitle(TownSite town) =>
        (town.Port ? "Sea port" : "River town") + (conditions.Wet ? " · rain" : "") + (conditions.Night ? " · night" : "");

    /// <summary>The open service's conversation as published: speaker, speech, outcome and choices.</summary>
    internal string ServiceReadout => lastFacts?.Service is TownServiceUiFacts service
        ? $"speaker={service.Speaker};said={service.Text};outcome={(service.Outcome.Length > 0 ? service.Outcome : "none")};choices={service.Choices}"
        : "service=none";

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"open={Open};visit={visit};town={site?.Key ?? "none"};recipe={site?.Recipe ?? "none"};selected={Selected?.Service ?? "none"};" +
        $"hover={presence?.Services.FirstOrDefault(s => s.Id == hover)?.Service ?? "none"};motion={Motion};offset={offset.X:F5},{offset.Y:F5};" +
        $"closeness={closeness:F3};wet={conditions.Wet};night={conditions.Night};prompt={(prompt.Length > 0 ? prompt : "none")};" +
        $"{audio?.Readout ?? "voices=0;presence=0"}");

    public void Dispose()
    {
        Leave();
        audio?.Dispose();
        audio = null;
        foreach (UiImage image in portraits.Values) image.Dispose();
        portraits.Clear();
    }
}
