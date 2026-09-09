using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Procgen.Artifacts;
using CraftSurvive.Procgen.Workbench;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.LevelGeneration;

/// <summary>One bounded experiment session. Commands name the observed revision; only Update applies them.</summary>
internal sealed class ProcgenWorkbench(IEngineContext engine, ProductContent content, TerrainWorld terrain, PlayerController player)
{
    private WorkbenchCandidate? candidate;
    private WorkbenchState? replay;
    private WorkbenchState physical = new("none", false);
    private WorkbenchLayoutData layout = new([], [], "");
    private WorkbenchAnalysis? analysis;
    private string[] witness = [];
    private readonly List<string> history = [];
    private string identity = "none", source = "none", error = "", mode = "inspection";
    private string replayLabel = "Completing witness";
    private string routes = "not run", separations = "not run", information = "not run";
    private int cursor;
    private long revision;
    private (string Action, string Argument)? pending;
    private const float UseDistance = 3f;
    private const int MaxHistory = 24;
    private const float SightTargetMargin = 0.5f;
    internal bool Active => candidate is not null && ReferenceEquals(terrain.ActiveWorkbench, candidate);

    internal string QueueLoad(string path)
    {
        if (path.Length > 160 || !path.StartsWith("procgen/", StringComparison.Ordinal)
            || !path.EndsWith(".json", StringComparison.Ordinal) || path.Contains("..") || path.Any(char.IsWhiteSpace))
            throw new ArgumentException("Choose an admitted procgen/*.json content path.");
        return Queue("load", path);
    }

    internal string QueueAction(string action, long expectedRevision)
    {
        if (expectedRevision != revision) throw new InvalidOperationException("Candidate changed; refresh before acting.");
        if (!Active) throw new InvalidOperationException("Load a candidate before acting; another study may be active.");
        return Queue(action, "");
    }

    private string Queue(string action, string argument)
    {
        if (pending is not null) throw new InvalidOperationException("A workbench command is already pending.");
        pending = (action, argument);
        return $"queued {action}; read applied status before the next action";
    }

    internal void Update(ProductUpdate update)
    {
        if (Active && pending is null)
            foreach (ProductInputEvent input in update.Input)
                if (input.Edge == InputEdge.Pressed &&
                    ((input.Kind == InputEventKind.Key && input.Keyboard == KeyboardControl.KeyE)
                     || (input.Kind == InputEventKind.ControllerButton && input.ControllerButton == ControllerButton.Button3)))
                { pending = ("use", ""); break; }
        if (pending is not { } command) return;
        pending = null;
        try
        {
            switch (command.Action)
            {
                case "load": Load(command.Argument); break;
                case "enter":
                case "reset": Enter(); break;
                case "step": Step(); break;
                case "counterexample": SelectTrace(true); break;
                case "witness": SelectTrace(false); break;
                case "use": Use(); break;
                default: throw new InvalidOperationException("Unknown workbench action.");
            }
            revision++;
            error = "";
            Record($"r{revision}: {command.Action}");
        }
        catch (Exception failure)
        {
            error = failure.Message;
            Record($"{command.Action} failed: {error}");
        }
    }

    private void Load(string path)
    {
        ProductContentFile? found = null;
        foreach (ProductContentFile file in content.Files.Span)
            if (Encoding.UTF8.GetString(file.Path.Span) == path) { found = file; break; }
        ProductContentFile selected = found ?? throw new InvalidOperationException($"Content path {path} was not staged. Generate the offline artifact, then reload the product.");
        if (selected.Bytes.Length > 65536) throw new InvalidOperationException("Workbench candidate exceeds 64 KiB.");
        WorkbenchCandidate next = WorkbenchCandidateJson.Deserialize(selected.Bytes.Span);
        string nextIdentity = WorkbenchCandidateJson.Identity(next);
        WorkbenchAnalysis nextAnalysis = WorkbenchExperiment.Analyze(next);
        WorkbenchLayoutData nextLayout = WorkbenchLayout.Resolve(next);
        terrain.ApplyWorkbench(next, false);
        candidate = next;
        source = path;
        identity = nextIdentity;
        analysis = nextAnalysis;
        layout = nextLayout;
        witness = nextAnalysis.Witness;
        replayLabel = "Completing witness";
        replay = WorkbenchExperiment.Initial(next);
        physical = WorkbenchExperiment.Initial(next);
        cursor = 0;
        mode = "inspection";
        history.Clear();
        CheckRealization();
    }

    private void Enter()
    {
        WorkbenchCandidate plan = RequireActive();
        if (physical.SwitchOpen) terrain.ApplyWorkbench(plan, false);
        physical = WorkbenchExperiment.Initial(plan);
        replay = WorkbenchExperiment.Initial(plan);
        witness = analysis!.Witness;
        replayLabel = "Completing witness";
        cursor = 0;
        mode = "walking";
        WorkbenchRoom start = plan.Rooms.Single(r => r.Id == plan.StartRoom);
        WorkbenchRoute first = plan.Routes.First(r => !r.RequiresSwitch && (r.From == start.Id || r.To == start.Id));
        WorkbenchRoom next = plan.Rooms.Single(r => r.Id == (first.From == start.Id ? first.To : first.From));
        Vector3 eye = WorkbenchRecipe.Center(start) with { Y = start.Minimum.Y + WorkbenchLayout.StandingEyeHeight };
        Vector3 target = plan.Motif == WorkbenchExperiment.PreviewMotif
            ? WorkbenchRecipe.Point(layout.Markers.Single(m => m.Id == "goal").Position)
            : WorkbenchRecipe.Center(next);
        player.ViewFrom(eye, target with { Y = eye.Y });
        CheckRealization();
    }

    private void Step()
    {
        WorkbenchCandidate plan = RequireActive();
        if (cursor >= witness.Length) throw new InvalidOperationException("Trace has no remaining actions; choose a trace to begin again.");
        replay = WorkbenchExperiment.Apply(plan, replay ?? WorkbenchExperiment.Initial(plan), witness[cursor]);
        Record($"model: {witness[cursor]}");
        cursor++;
        mode = "model replay (world unchanged)";
    }

    private void SelectTrace(bool failure)
    {
        WorkbenchCandidate plan = RequireActive();
        if (failure)
        {
            WorkbenchCounterexample? example = analysis!.Counterexamples.OrderByDescending(e => e.Actions.Length > 0).FirstOrDefault();
            if (example is null) throw new InvalidOperationException("No failing model contract for this candidate.");
            witness = example.Actions;
            replayLabel = "Failure trace: " + example.Requirement;
        }
        else
        {
            witness = analysis!.Witness;
            replayLabel = "Completing witness";
        }
        replay = WorkbenchExperiment.Initial(plan);
        cursor = 0;
        mode = "model replay (world unchanged)";
    }

    private void Use()
    {
        WorkbenchCandidate plan = RequireActive();
        string room = PhysicalRoom();
        WorkbenchMarker? marker = layout.Markers.Where(m => m.Action.Length > 0 && m.Room == room)
            .OrderBy(m => Vector3.DistanceSquared(player.WorldPosition, WorkbenchRecipe.Point(m.Position)))
            .FirstOrDefault();
        if (marker is null || Vector3.Distance(player.WorldPosition, WorkbenchRecipe.Point(marker.Position)) > UseDistance)
            throw new InvalidOperationException("Approach a station shown on the plan, then press E / Y or Interact.");
        WorkbenchState before = physical with { Room = room };
        if (!WorkbenchExperiment.LegalActions(plan, before).Contains(marker.Action, StringComparer.Ordinal))
            throw new InvalidOperationException($"{marker.Label} is unavailable in the physical state; inspect the motif contracts and legal actions.");
        if (marker.Action == "observe" && !GoalVisibleFrom(player.WorldEyePosition))
            throw new InvalidOperationException("The goal is occluded from this lookout position; acknowledge it through the opening.");
        WorkbenchState after = WorkbenchExperiment.Apply(plan, before, marker.Action);
        if (after.SwitchOpen != physical.SwitchOpen) terrain.ApplyWorkbench(plan, after.SwitchOpen);
        physical = after;
        Record("physical: " + marker.Action);
        mode = "walking";
        CheckRealization();
    }

    private bool GoalVisibleFrom(Vector3 from)
    {
        WorkbenchMarker marker = layout.Markers.Single(m => m.Id == "goal");
        Vector3 target = WorkbenchRecipe.Point(marker.Position) + Vector3.UnitY * WorkbenchLayout.StandingEyeHeight;
        return !RayHits(from, target, SightTargetMargin);
    }

    private bool RayHits(Vector3 from, Vector3 to, float endMargin = 0f)
    {
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        Vector3 local = PlayerWorldPosition.FromWorld(from).ToLocal(origin);
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(terrain.Session, local, Vector3.Normalize(to - from),
            Vector3.Distance(from, to) - endMargin, new SpatialQueryFilter(uint.MaxValue, uint.MaxValue),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present;
    }

    private WorkbenchCandidate RequireActive() => Active ? candidate! : throw new InvalidOperationException("The resolved candidate is no longer the active study.");

    private string PhysicalRoom()
    {
        if (!Active) return "none";
        Vector3 position = player.WorldPosition;
        return candidate!.Rooms.FirstOrDefault(r => position.X >= r.Minimum.X && position.X <= r.Maximum.X
            && position.Z >= r.Minimum.Z && position.Z <= r.Maximum.Z
            && position.Y >= r.Minimum.Y - 0.25f && position.Y < r.Maximum.Y)?.Id ?? "passage / outside rooms";
    }

    private void CheckRealization()
    {
        routes = separations = information = "not run";
        try
        {
            WorkbenchCandidate plan = RequireActive();
            int clear = 0, clearTotal = 0, blocked = 0, blockedTotal = 0;
            void Probe(WorkbenchRoom a, WorkbenchRoom b, bool expectedBlocked)
            {
                Vector3 from = WorkbenchRecipe.Center(a), to = WorkbenchRecipe.Center(b);
                Vector3 direction = Vector3.Normalize(to - from);
                Vector3 lateral = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
                foreach (float height in new[] { 0.25f, 1f, 1.85f })
                    foreach (float offset in new[] { -0.4f, 0f, 0.4f })
                    {
                        Vector3 start = new Vector3(from.X, a.Minimum.Y + height, from.Z) + lateral * offset;
                        bool hit = RayHits(start, start + direction * Vector3.Distance(from, to));
                        if (expectedBlocked) { blockedTotal++; if (hit) blocked++; }
                        else { clearTotal++; if (!hit) clear++; }
                    }
            }
            foreach (WorkbenchRoute route in plan.Routes)
                Probe(plan.Rooms.Single(r => r.Id == route.From), plan.Rooms.Single(r => r.Id == route.To), route.RequiresSwitch && !physical.SwitchOpen);
            for (int a = 0; a < plan.Rooms.Length; a++)
                for (int b = a + 1; b < plan.Rooms.Length; b++)
                    if (!plan.Routes.Any(r => (r.From == plan.Rooms[a].Id && r.To == plan.Rooms[b].Id)
                        || (r.To == plan.Rooms[a].Id && r.From == plan.Rooms[b].Id))) Probe(plan.Rooms[a], plan.Rooms[b], true);
            routes = $"{clear}/{clearTotal} clear mesh rays ({(clear == clearTotal ? "pass" : "FAIL")})";
            separations = $"{blocked}/{blockedTotal} blocked mesh rays ({(blocked == blockedTotal ? "pass" : "FAIL")}); shortcut {(physical.SwitchOpen ? "open" : "closed")}";
        }
        catch (Exception failure) { routes = separations = $"unavailable: {failure.Message}"; }
        try
        {
            WorkbenchCandidate plan = RequireActive();
            if (plan.Motif == WorkbenchExperiment.PreviewMotif)
            {
                WorkbenchMarker lookout = layout.Markers.Single(m => m.Action == "observe");
                bool visible = GoalVisibleFrom(WorkbenchRecipe.Point(lookout.Position) + Vector3.UnitY * WorkbenchLayout.StandingEyeHeight);
                information = $"Lookout-to-goal mesh sightline: {(visible ? "clear (pass)" : "occluded (FAIL)")}; " +
                    (physical.SwitchOpen ? "gate already open; Enter checks before access" : "gate closed; one eye-height ray, not proof of human recognition");
            }
            else information = "No before-access sightline contract for this motif.";
        }
        catch (Exception failure) { information = $"unavailable: {failure.Message}"; }
    }

    private void Record(string value)
    {
        history.Add(value);
        if (history.Count > MaxHistory) history.RemoveAt(0);
    }

    internal string Readout()
    {
        string room = PhysicalRoom();
        bool model = mode.StartsWith("model", StringComparison.Ordinal) || mode == "inspection";
        WorkbenchState world = physical with { Room = room };
        WorkbenchState state = model ? replay ?? new("none", false) : world;
        string[] legal = candidate is null || !candidate.Rooms.Any(r => r.Id == state.Room) ? [] : WorkbenchExperiment.LegalActions(candidate, state);
        WorkbenchReadout result = new(revision, identity, source, candidate?.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            pending is null ? Active ? "applied" : "inactive" : "pending", error, Active, mode, state, room, physical.SwitchOpen,
            legal, witness, cursor, Active && candidate is not null && WorkbenchExperiment.Complete(candidate, state),
            candidate?.Rooms ?? [], candidate?.Routes ?? [],
            new(analysis is null ? "not run" : $"{(analysis.Completable ? "PASS" : "FAIL")}: completing witness {analysis.Witness.Length} actions; {analysis.ReachableStates} reachable states; {analysis.UnrecoverableStates} cannot complete",
                routes, separations, information, "Realized collision mesh: nine straight rays per route/separation. Not exhaustive navigation, capsule clearance, jump/climb/destruction, or proof of all bypasses. Model stepping never moves the player or opens the world gate."), history.ToArray(), candidate?.Motif ?? "none", world,
                new(player.WorldPosition.X, player.WorldPosition.Y, player.WorldPosition.Z), layout, analysis, replayLabel, replay ?? new("none", false));
        return JsonSerializer.Serialize(result, WorkbenchUiJson.Default.WorkbenchReadout);
    }
}

internal sealed record WorkbenchChecks(string Model, string Routes, string Separations, string Information, string Coverage);
internal sealed record WorkbenchReadout(long Revision, string Identity, string Source, string Seed, string Status, string Error,
    bool Active, string Mode, WorkbenchState State, string PhysicalRoom, bool PhysicalSwitchOpen, string[] LegalActions,
    string[] Witness, int Cursor, bool Completed, WorkbenchRoom[] Rooms, WorkbenchRoute[] Routes, WorkbenchChecks Checks, string[] History,
    string Motif, WorkbenchState PhysicalState, WorkbenchPoint PlayerPosition, WorkbenchLayoutData Layout, WorkbenchAnalysis? Analysis, string ReplayLabel,
    WorkbenchState ModelState);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkbenchReadout))]
internal partial class WorkbenchUiJson : JsonSerializerContext;
