using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
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
    private WorkbenchReadout? reference;
    private WorkbenchCandidate? referenceCandidate;
    private WorkbenchRepairReceipt? repairReceipt;
    private WorkbenchBankEntry[]? bank;
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
    private const string ContentDirectory = "procgen";
    private const string ContentIndexName = "_index.json";
    private const string IndexFirstProperty = "first";
    private const int MaxArtifactBytes = 64 * 1024;
    private const int MaxBankEntries = 16;
    private const int MaxReadoutProbes = 64;
    private const float SightTargetMargin = 0.5f;
    private string treatment = WorkbenchRealization.Intact;
    private WorkbenchProbeResult[] probes = [];
    private string realizationSummary = "not run";
    private string spatialRevision = "unavailable";
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

    internal string QueueMend(long expectedRevision, string operation)
    {
        if (expectedRevision != revision || !Active) throw new InvalidOperationException("Candidate changed; refresh before acting.");
        if (!WorkbenchRepair.Operations(RequireActive()).Contains(operation, StringComparer.Ordinal))
            throw new InvalidOperationException("This repair is not applicable to the current candidate.");
        return Queue("mend", operation);
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
                case "reference": PinReference(); break;
                case "mend": Mend(command.Argument); break;
                case "enter":
                case "reset": Enter(); break;
                case "step": Step(); break;
                case "counterexample": SelectTrace(true); break;
                case "witness": SelectTrace(false); break;
                case "use": Use(); break;
                case "breach": Treat(WorkbenchRealization.SideBreach); break;
                case "repair": Treat(WorkbenchRealization.Intact); break;
                case "check": CheckRealization(); break;
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
        if (!content.TryReadFile(path, out ProductContentFile selected))
            throw new InvalidOperationException($"Content path {path} was not staged. Generate the offline artifact, then reload the product.");
        (WorkbenchCandidate next, WorkbenchRepairReceipt? retained) = ReadArtifact(selected.Bytes.Span);
        ApplyCandidate(next, path);
        repairReceipt = retained;
        if (retained is not null)
        {
            referenceCandidate = retained.Parent;
            WorkbenchState initial = WorkbenchExperiment.Initial(retained.Parent);
            WorkbenchAnalysis parentAnalysis = WorkbenchExperiment.Analyze(retained.Parent);
            reference = ReadFacts() with
            {
                Identity = retained.ParentIdentity, Source = path + " (retained parent)",
                Seed = retained.Parent.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Active = false, Status = "offline parent", Mode = "inspection", State = initial, ModelState = initial,
                PhysicalState = new("unobserved", false), PhysicalRoom = "unobserved", PhysicalSwitchOpen = false,
                PlayerPosition = new(0, 0, 0), Completed = false, LegalActions = WorkbenchExperiment.LegalActions(retained.Parent, initial),
                Witness = parentAnalysis.Witness, Rooms = retained.Parent.Rooms, Routes = retained.Parent.Routes,
                Layout = WorkbenchLayout.Resolve(retained.Parent), Analysis = parentAnalysis, Motif = retained.Parent.Motif,
                History = [], Realization = new("unobserved", "unobserved", "unobserved", "unobserved", "unobserved", "unobserved"),
                Checks = new("Retained offline analysis; see contracts", "unobserved", "unobserved", "unobserved",
                    "No physical observations were retained in the semantic receipt.", "unobserved", "UNAVAILABLE: offline parent", [], 0, 0)
            };
        }
    }

    private void ApplyCandidate(WorkbenchCandidate next, string path)
    {
        string nextIdentity = WorkbenchCandidateJson.Identity(next);
        WorkbenchAnalysis nextAnalysis = WorkbenchExperiment.Analyze(next);
        WorkbenchLayoutData nextLayout = WorkbenchLayout.Resolve(next);
        terrain.ApplyWorkbench(next, false);
        candidate = next;
        source = path;
        identity = nextIdentity;
        analysis = nextAnalysis;
        layout = nextLayout;
        treatment = WorkbenchRealization.Intact;
        witness = nextAnalysis.Witness;
        replayLabel = "Completing witness";
        replay = WorkbenchExperiment.Initial(next);
        physical = WorkbenchExperiment.Initial(next);
        cursor = 0;
        mode = "inspection";
        history.Clear();
        // Replacing the collision world can turn the previous player position
        // into solid rock. Establish the new entrance before the next solver step.
        PlaceAtEntrance(next);
        CheckRealization();
    }

    private void Enter()
    {
        WorkbenchCandidate plan = RequireActive();
        if (physical.SwitchOpen) terrain.ApplyWorkbench(plan, false, treatment);
        physical = WorkbenchExperiment.Initial(plan);
        replay = WorkbenchExperiment.Initial(plan);
        witness = analysis!.Witness;
        replayLabel = "Completing witness";
        cursor = 0;
        mode = "walking";
        PlaceAtEntrance(plan);
        CheckRealization();
    }

    private void Treat(string nextTreatment)
    {
        WorkbenchCandidate plan = RequireActive();
        WorkbenchLayoutData nextLayout = WorkbenchRealization.Resolve(plan, nextTreatment);
        terrain.ApplyWorkbench(plan, false, nextTreatment);
        treatment = nextTreatment;
        layout = nextLayout;
        physical = WorkbenchExperiment.Initial(plan);
        replay = WorkbenchExperiment.Initial(plan);
        witness = analysis!.Witness;
        replayLabel = "Completing witness";
        cursor = 0;
        mode = "walking";
        PlaceAtEntrance(plan);
        CheckRealization();
    }

    private void PlaceAtEntrance(WorkbenchCandidate plan)
    {
        WorkbenchRoom start = plan.Rooms.Single(r => r.Id == plan.StartRoom);
        WorkbenchRoute first = plan.Routes.First(r => !r.RequiresSwitch && (r.From == start.Id || r.To == start.Id));
        WorkbenchRoom next = plan.Rooms.Single(r => r.Id == (first.From == start.Id ? first.To : first.From));
        Vector3 eye = WorkbenchRecipe.Center(start) with { Y = start.Minimum.Y + WorkbenchLayout.StandingEyeHeight };
        Vector3 target = plan.Motif == WorkbenchExperiment.PreviewMotif
            ? WorkbenchRecipe.Point(layout.Markers.Single(m => m.Id == "goal").Position)
            : WorkbenchRecipe.Center(next);
        player.ViewFrom(eye, target with { Y = eye.Y });
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
        if (after.SwitchOpen != physical.SwitchOpen) terrain.ApplyWorkbench(plan, after.SwitchOpen, treatment);
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
        probes = [];
        realizationSummary = "unavailable";
        spatialRevision = "unavailable";
        routes = separations = information = "not run";
        try
        {
            WorkbenchCandidate plan = RequireActive();
            int clear = 0, clearTotal = 0, blocked = 0, blockedTotal = 0;
            var failures = new List<string>();
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
                        if (hit != expectedBlocked && failures.Count < 12) failures.Add($"{a.Id}–{b.Id}@{height:F2}/{offset:F1}");
                        if (expectedBlocked) { blockedTotal++; if (hit) blocked++; }
                        else { clearTotal++; if (!hit) clear++; }
                    }
            }
            foreach (WorkbenchRoute route in plan.Routes)
                Probe(plan.Rooms.Single(r => r.Id == route.From), plan.Rooms.Single(r => r.Id == route.To), route.RequiresSwitch && !physical.SwitchOpen);
            for (int a = 0; a < plan.Rooms.Length; a++)
                for (int b = a + 1; b < plan.Rooms.Length; b++)
                    if ((plan.Motif != WorkbenchExperiment.LargeMotif || WorkbenchProbePlan.NeighboringGridRooms(plan, plan.Rooms[a], plan.Rooms[b]))
                        && !plan.Routes.Any(r => (r.From == plan.Rooms[a].Id && r.To == plan.Rooms[b].Id)
                        || (r.To == plan.Rooms[a].Id && r.From == plan.Rooms[b].Id))) Probe(plan.Rooms[a], plan.Rooms[b], true);
            routes = $"{clear}/{clearTotal} clear mesh rays ({(clear == clearTotal ? "pass" : "FAIL")})";
            separations = $"{blocked}/{blockedTotal} blocked mesh rays ({(blocked == blockedTotal ? "pass" : "FAIL")}); gates {(physical.SwitchOpen ? "open" : "closed")}; failures: {string.Join(", ", failures)}";
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
        try
        {
            probes = WorkbenchSpatialChecks.Run(engine, terrain.Session, RequireActive(), physical.SwitchOpen);
            SpatialProjectionReadout projection = engine.Spatial.ReadProjection(new(terrain.Session));
            CollisionReplaceReceipt collision = terrain.WorkbenchCollision;
            spatialRevision = FormattableString.Invariant($"collision={collision.RevisionAfter}; projectionHash={collision.ProjectionHash}; assets={collision.AssetCount}; instances={collision.InstanceCount}; currentCollision={projection.CollisionRevision}; currentStaticMesh={projection.StaticMeshRevision}");
            if (projection.StaticMeshRevision != collision.RevisionAfter)
                throw new InvalidOperationException("Collision projection changed since realization; checks are not bound to the current mesh.");
            int failures = probes.Count(p => p.Passed == false), unknown = probes.Count(p => p.Passed is null);
            realizationSummary = $"{(unknown > 0 ? "UNAVAILABLE" : failures > 0 ? "FAIL" : "PASS")}: {probes.Count(p => p.Passed == true)}/{probes.Length} scoped body checks; {failures} failures; {unknown} unavailable";
        }
        catch (Exception failure) { realizationSummary = "UNAVAILABLE: " + failure.Message; }
    }

    private void Record(string value)
    {
        history.Add(value);
        if (history.Count > MaxHistory) history.RemoveAt(0);
    }

    internal string Readout() => JsonSerializer.Serialize(ReadFacts(), WorkbenchUiJson.Default.WorkbenchReadout);

    private WorkbenchReadout ReadFacts()
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
                routes, separations, information, WorkbenchSpatialChecks.Coverage, terrain.ReadWorkbenchBuild(), realizationSummary,
                probes.OrderBy(p => p.Passed == false ? 0 : p.Passed is null ? 1 : 2).Take(MaxReadoutProbes).ToArray(),
                probes.Length, Math.Max(0, probes.Length - MaxReadoutProbes)), history.ToArray(), candidate?.Motif ?? "none", world,
                new(player.WorldPosition.X, player.WorldPosition.Y, player.WorldPosition.Z), layout, analysis, replayLabel, replay ?? new("none", false),
                new(RealizationIdentity(), treatment, candidate is null ? "none" : WorkbenchRealization.BreachRoute(candidate), physical.SwitchOpen ? "open" : "closed", spatialRevision,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"workbench-route-requirements.v1|{identity}|switch={physical.SwitchOpen}")))));
        return result;
    }

    private void PinReference()
    {
        referenceCandidate = RequireActive();
        reference = ReadFacts();
    }

    private void Mend(string operation)
    {
        WorkbenchCandidate parent = RequireActive();
        WorkbenchRepairReceipt receipt = WorkbenchRepairJson.Create(parent, operation);
        WorkbenchReadout before = ReadFacts();
        string parentSource = source;
        ApplyCandidate(receipt.Result, parentSource + " (session repair)");
        // Publish the comparison only after the replacement world succeeds.
        referenceCandidate = parent;
        reference = before;
        repairReceipt = receipt;
        Record($"semantic repair: {operation}; cost {receipt.Cost}; parent {receipt.ParentIdentity}");
    }

    internal string ExportRepair()
    {
        if (pending is not null) throw new InvalidOperationException("Wait for the pending command before exporting.");
        if (repairReceipt is null || !Active || repairReceipt.ResultIdentity != identity)
            throw new InvalidOperationException("There is no applied semantic repair to export.");
        return Encoding.UTF8.GetString(WorkbenchRepairJson.Serialize(repairReceipt));
    }

    internal string Comparison()
    {
        WorkbenchComparison value = new(revision, identity, reference,
            referenceCandidate is null || candidate is null ? [] : CompareCandidates(referenceCandidate, candidate),
            Active ? WorkbenchRepair.Operations(candidate!) : [],
            repairReceipt is null ? null : new(repairReceipt.ParentIdentity, repairReceipt.ResultIdentity,
                repairReceipt.Operation, repairReceipt.Cost, repairReceipt.ChangedField));
        return JsonSerializer.Serialize(value, WorkbenchUiJson.Default.WorkbenchComparison);
    }

    private static string[] CompareCandidates(WorkbenchCandidate before, WorkbenchCandidate after)
    {
        List<string> differences = [];
        void Field<T>(string label, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b)) differences.Add($"{label}: {a} → {b}");
        }
        Field("Seed", before.Seed, after.Seed);
        Field("Motif", before.Motif, after.Motif);
        Field("Start room", before.StartRoom, after.StartRoom);
        Field("Goal room", before.GoalRoom, after.GoalRoom);
        Field("Switch room", before.SwitchRoom, after.SwitchRoom);
        Field("Switch enabled", before.SwitchEnabled, after.SwitchEnabled);
        Field("Recovery enabled", before.RecoveryEnabled, after.RecoveryEnabled);
        Field("Preview opening", before.PreviewOpening, after.PreviewOpening);
        foreach (string id in before.Rooms.Select(r => r.Id).Union(after.Rooms.Select(r => r.Id)).Order(StringComparer.Ordinal))
        {
            WorkbenchRoom? a = before.Rooms.SingleOrDefault(r => r.Id == id), b = after.Rooms.SingleOrDefault(r => r.Id == id);
            if (a != b) differences.Add($"Room {id}: {(a is null ? "added" : b is null ? "removed" : "bounds changed")}");
        }
        foreach (string id in before.Routes.Select(r => r.Id).Union(after.Routes.Select(r => r.Id)).Order(StringComparer.Ordinal))
        {
            WorkbenchRoute? a = before.Routes.SingleOrDefault(r => r.Id == id), b = after.Routes.SingleOrDefault(r => r.Id == id);
            if (a != b) differences.Add($"Route {id}: {(a is null ? "added" : b is null ? "removed" : "endpoints, width or condition changed")}");
        }
        if (differences.Count == 0) differences.Add("No resolved candidate decisions changed.");
        return differences.ToArray();
    }

    private static (WorkbenchCandidate Candidate, WorkbenchRepairReceipt? Receipt) ReadArtifact(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxArtifactBytes) throw new InvalidOperationException("Workbench artifact exceeds the runtime limit of 64 KiB.");
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("schema", out JsonElement schema)
            && schema.ValueKind == JsonValueKind.String && schema.GetString() == WorkbenchRepairReceipt.CurrentSchema)
        {
            WorkbenchRepairReceipt receipt = WorkbenchRepairJson.Deserialize(bytes);
            return (receipt.Result, receipt);
        }
        return (WorkbenchCandidateJson.Deserialize(bytes), null);
    }

    internal string Bank()
    {
        if (bank is null)
        {
            List<WorkbenchBankEntry> entries = [];
            // Admitted, resolved artifacts only. Receipts and unrelated JSON are not candidates.
            foreach (ProductContentFile file in ReadBankFiles())
            {
                string path = file.RelativePath;
                if (entries.Count == MaxBankEntries) break;
                try
                {
                    WorkbenchCandidate item = ReadArtifact(file.Bytes.Span).Candidate;
                    WorkbenchAnalysis report = WorkbenchExperiment.Analyze(item);
                    entries.Add(new(path, WorkbenchCandidateJson.Identity(item), item.Motif,
                        item.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Rooms.Length, item.Routes.Length,
                        report.Completable, report.Contracts.Where(c => !c.Passed).Select(c => c.Requirement).ToArray(), ""));
                }
                catch (Exception failure) { entries.Add(new(path, "unavailable", "unknown", "unknown", 0, 0, false, [], failure.Message)); }
            }
            bank = entries.ToArray();
        }
        return JsonSerializer.Serialize(bank, WorkbenchUiJson.Default.WorkbenchBankEntryArray);
    }

    private ProductContentFile[] ReadBankFiles()
    {
        ProductContentFile[] files = content.ReadDirectory(ContentDirectory, recursive: true)
            .Where(file => file.Name != ContentIndexName && file.Name.EndsWith(".json", StringComparison.Ordinal)
                && !file.Name.EndsWith(".receipt.json", StringComparison.Ordinal)).ToArray();
        if (!content.TryReadFile($"{ContentDirectory}/{ContentIndexName}", out ProductContentFile index)) return files;

        // Curation lives in authored content. New unlisted artifacts remain discoverable
        // after the preferred entries, in Engine's deterministic directory order.
        using JsonDocument document = JsonDocument.Parse(index.Bytes);
        Dictionary<string, int> priority = new(StringComparer.Ordinal);
        HashSet<string> available = files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (JsonElement entry in document.RootElement.GetProperty(IndexFirstProperty).EnumerateArray())
        {
            string path = $"{ContentDirectory}/{entry.GetString()}";
            if (!available.Contains(path)) throw new InvalidOperationException($"Workbench index references an unavailable artifact: {path}");
            priority.Add(path, priority.Count);
        }
        return files.OrderBy(file => priority.GetValueOrDefault(file.RelativePath, int.MaxValue)).ToArray();
    }

    private string RealizationIdentity() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{WorkbenchRealization.RecipeVersion}|{identity}|{treatment}|{(physical.SwitchOpen ? "open" : "closed")}")));
}

internal sealed record WorkbenchRealizationReadout(string Identity, string Treatment, string BreachRoute, string GateState, string SpatialRevision, string StateIdentity);
internal sealed record WorkbenchChecks(string Model, string Routes, string Separations, string Information, string Coverage, string Build,
    string Realization, WorkbenchProbeResult[] Probes, int ProbeCount, int OmittedProbes);
internal sealed record WorkbenchReadout(long Revision, string Identity, string Source, string Seed, string Status, string Error,
    bool Active, string Mode, WorkbenchState State, string PhysicalRoom, bool PhysicalSwitchOpen, string[] LegalActions,
    string[] Witness, int Cursor, bool Completed, WorkbenchRoom[] Rooms, WorkbenchRoute[] Routes, WorkbenchChecks Checks, string[] History,
    string Motif, WorkbenchState PhysicalState, WorkbenchPoint PlayerPosition, WorkbenchLayoutData Layout, WorkbenchAnalysis? Analysis, string ReplayLabel,
    WorkbenchState ModelState, WorkbenchRealizationReadout Realization);
internal sealed record WorkbenchBankEntry(string Path, string Identity, string Motif, string Seed, int Rooms, int Routes,
    bool Completable, string[] Failures, string Error);
internal sealed record WorkbenchRepairSummary(string ParentIdentity, string ResultIdentity, string Operation, int Cost, string ChangedField);
internal sealed record WorkbenchComparison(long Revision, string Identity, WorkbenchReadout? Reference, string[] Differences, string[] Operations, WorkbenchRepairSummary? Repair);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkbenchReadout))]
[JsonSerializable(typeof(WorkbenchComparison))]
[JsonSerializable(typeof(WorkbenchBankEntry[]))]
internal partial class WorkbenchUiJson : JsonSerializerContext;
