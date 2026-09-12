using System.Text.Json.Nodes;
using CraftSurvive.Procgen.Artifacts;
using CraftSurvive.Procgen.Workbench;

foreach (var motif in new[] { WorkbenchExperiment.CurrentMotif, WorkbenchExperiment.RecoveryMotif, WorkbenchExperiment.PreviewMotif })
    PositiveMotif(motif);

LargeMotifTests();
CounterexamplesFailContracts();
StrictCandidateJson();
WorkbenchRepairTests();
RealizationRequirements();

Console.WriteLine("CraftSurvive workbench checks passed.");

static void RealizationRequirements()
{
    var orientations = new HashSet<bool>();
    foreach (string motif in new[] { WorkbenchExperiment.CurrentMotif, WorkbenchExperiment.RecoveryMotif,
        WorkbenchExperiment.PreviewMotif, WorkbenchExperiment.LargeMotif })
    foreach (ulong seed in new[] { 0UL, 1UL, 29UL, 83UL })
    {
        WorkbenchCandidate candidate = WorkbenchExperiment.Generate(seed, motif);
        if (motif == WorkbenchExperiment.LargeMotif && seed == 0)
        {
            // An equally valid west-entry goal exercises the perpendicular cut.
            string fixtureGateId = WorkbenchRealization.BreachRoute(candidate);
            candidate = candidate with { Routes = candidate.Routes.Select(r => r.Id == fixtureGateId
                ? new WorkbenchRoute("route-34-35", "r34", "goal", r.Width, true) : r).ToArray() };
            True(WorkbenchExperiment.Validate(candidate).Length == 0, "west-entry fixture remains a valid candidate");
        }
        string identity = WorkbenchCandidateJson.Identity(candidate);
        WorkbenchLayoutData intact = WorkbenchRealization.Resolve(candidate, WorkbenchRealization.Intact);
        WorkbenchLayoutData breached = WorkbenchRealization.Resolve(candidate, WorkbenchRealization.SideBreach);
        WorkbenchVolume aperture = breached.Volumes.Single(v => v.Kind == "breach");
        True(intact.Volumes.All(v => v.Kind != "breach"), "intact realization has no fault geometry");
        True(breached.Volumes.Length == intact.Volumes.Length + 1, "breach only adds one treatment aperture");
        Equal(identity, WorkbenchCandidateJson.Identity(candidate), "treatment must not weaken intended candidate graph");
        WorkbenchProbe[] closed = WorkbenchProbePlan.Create(candidate, false, 1.75f, 0.3f);
        WorkbenchProbe[] open = WorkbenchProbePlan.Create(candidate, true, 1.75f, 0.3f);
        True(closed.Select(p => p.Id).Distinct().Count() == closed.Length, "probe IDs must be unique");
        True(closed.Length == open.Length && closed.Zip(open).All(pair => pair.First.Id == pair.Second.Id
            && pair.First.From == pair.Second.From && pair.First.To == pair.Second.To), "state changes requirements, not sample positions");
        True(closed.Where(p => p.Kind == "protected-gate").All(p => p.ExpectedBlocked)
            && open.Where(p => p.Kind == "protected-gate").All(p => !p.ExpectedBlocked), "every gate is blocked before control and clear after control");
        True(open.Where(p => p.Kind == "protected-separation").All(p => p.ExpectedBlocked), "ungated wall requirements survive control activation");
        string target = WorkbenchRealization.BreachRoute(candidate);
        WorkbenchVolume gate = intact.Volumes.Single(v => v.Id == target + "-gate");
        WorkbenchRoute route = candidate.Routes.Single(r => r.Id == target);
        WorkbenchPoint a = WorkbenchLayout.Center(candidate.Rooms.Single(r => r.Id == route.From));
        WorkbenchPoint b = WorkbenchLayout.Center(candidate.Rooms.Single(r => r.Id == route.To));
        bool alongX = a.X != b.X;
        orientations.Add(alongX);
        float center = alongX ? a.Z : a.X;
        True((alongX ? aperture.Minimum.Z : aperture.Minimum.X) > center + 0.4f,
            "deliberate side breach must evade the legacy center-biased +/-0.4 rays");
        True(aperture.Minimum.Y == gate.Minimum.Y && aperture.Maximum.Y - aperture.Minimum.Y > 1.75f,
            "side aperture must reach the floor and provide standing headroom");
        True(closed.Any(p => p.Id.StartsWith(target + "/gate", StringComparison.Ordinal)
            && (alongX ? p.From.Z > aperture.Minimum.Z + 0.3f && p.From.Z < aperture.Maximum.Z - 0.3f
                : p.From.X > aperture.Minimum.X + 0.3f && p.From.X < aperture.Maximum.X - 0.3f)),
            "intent-derived gate samples must include a standing body center inside the aperture");
        Equal(System.Text.Json.JsonSerializer.Serialize(intact),
            System.Text.Json.JsonSerializer.Serialize(WorkbenchRealization.Resolve(candidate, WorkbenchRealization.Intact)),
            "repair returns identical intended geometry");
    }
    True(orientations.Count == 2, "treatment requirements exercise both gate orientations");
}

static void PositiveMotif(string motif)
{
    var candidate = WorkbenchExperiment.Generate(11, motif);
    True(WorkbenchExperiment.Validate(candidate).Length == 0, $"{motif} must remain structurally valid");
    var witness = WorkbenchExperiment.Witness(candidate);
    True(witness.Length > 0, $"{motif} must have a bounded completion witness");
    var completed = ApplyAll(candidate, witness);
    True(WorkbenchExperiment.Complete(candidate, completed), $"{motif} witness must complete");
    True(!WorkbenchExperiment.Complete(candidate, completed with { Room = "passage" }), $"{motif} must treat runtime passage positions as incomplete");
    var analysis = WorkbenchExperiment.Analyze(candidate);
    True(analysis.Completable && analysis.UnrecoverableStates == 0 && analysis.Contracts.All(contract => contract.Passed), $"{motif} contracts must pass with no unrecoverable reachable state");
    ExpectIllegal(() => WorkbenchExperiment.Apply(candidate, WorkbenchExperiment.Initial(candidate), "move:goal"), "locked return shortcut must reject before activation");
    if (StringComparer.Ordinal.Equals(motif, WorkbenchExperiment.RecoveryMotif)) RecoveryRules(candidate);
    if (StringComparer.Ordinal.Equals(motif, WorkbenchExperiment.PreviewMotif)) PreviewRules(candidate);
}

static void RecoveryRules(WorkbenchCandidate candidate)
{
    var state = WorkbenchExperiment.Apply(candidate, WorkbenchExperiment.Initial(candidate), "move:relay");
    state = WorkbenchExperiment.Apply(candidate, state, "spend");
    True(state.Spent && !state.Token, "recovery motif must consume its starting token once");
    ExpectIllegal(() => WorkbenchExperiment.Apply(candidate, state, "spend"), "spent token cannot be spent twice");
    state = WorkbenchExperiment.Apply(candidate, state, "move:control");
    state = WorkbenchExperiment.Apply(candidate, state, "move:goal");
    state = WorkbenchExperiment.Apply(candidate, state, "recover");
    ExpectIllegal(() => WorkbenchExperiment.Apply(candidate, state, "recover"), "recovered token cannot be recovered twice");
}

static void PreviewRules(WorkbenchCandidate candidate)
{
    var state = WorkbenchExperiment.Apply(candidate, WorkbenchExperiment.Initial(candidate), "move:relay");
    state = WorkbenchExperiment.Apply(candidate, state, "move:control");
    True(!WorkbenchExperiment.LegalActions(candidate, state).Contains("move:goal", StringComparer.Ordinal), "preview goal must remain gated before observation and activation");
    ExpectIllegal(() => WorkbenchExperiment.Apply(candidate, state, "activate"), "preview activation must require observation");
    state = WorkbenchExperiment.Apply(candidate, state, "move:relay");
    state = WorkbenchExperiment.Apply(candidate, state, "move:start");
    state = WorkbenchExperiment.Apply(candidate, state, "observe");
    state = WorkbenchExperiment.Apply(candidate, state, "move:relay");
    state = WorkbenchExperiment.Apply(candidate, state, "move:control");
    state = WorkbenchExperiment.Apply(candidate, state, "activate");
    True(WorkbenchExperiment.LegalActions(candidate, state).Contains("move:goal", StringComparer.Ordinal), "preview goal must open after observation and activation");
}

static void CounterexamplesFailContracts()
{
    foreach (var motif in new[] { WorkbenchExperiment.CurrentMotif, WorkbenchExperiment.RecoveryMotif, WorkbenchExperiment.PreviewMotif })
    {
        var candidate = WorkbenchExperiment.Generate(11, motif, counterexample: true);
        True(WorkbenchExperiment.Validate(candidate).Length == 0, $"{motif} intentional bad sample must still be structurally valid");
        candidate = WorkbenchCandidateJson.Deserialize(WorkbenchCandidateJson.Serialize(candidate));
        var analysis = WorkbenchExperiment.Analyze(candidate);
        True(!analysis.Completable && analysis.Contracts.Any(contract => !contract.Passed) && analysis.Counterexamples.Length > 0, $"{motif} intentional bad sample must fail contracts rather than codec validation");
        foreach (var counterexample in analysis.Counterexamples)
        {
            var state = ApplyAll(candidate, counterexample.Actions);
            True(!WorkbenchExperiment.Complete(candidate, state), $"{motif} counterexample '{counterexample.Requirement}' must replay legally to an incomplete state");
        }
        if (StringComparer.Ordinal.Equals(motif, WorkbenchExperiment.RecoveryMotif))
            True(analysis.Counterexamples.Any(counterexample => counterexample.Actions.Contains("spend", StringComparer.Ordinal)), "recovery failure must retain an irreversible spend trace");
    }
}

static void LargeMotifTests()
{
    var seeds = new[] { 0UL, 1UL, 11UL, 29UL, 42UL, 83UL, 99UL, ulong.MaxValue };
    var topologySignatures = new HashSet<string>(StringComparer.Ordinal);
    foreach (var seed in seeds)
    {
        var candidate = WorkbenchExperiment.Generate(seed, WorkbenchExperiment.LargeMotif);
        True(WorkbenchExperiment.Validate(candidate).Length == 0, $"large motif {seed} must be structurally valid");
        True(candidate.Rooms.Length == 36 && candidate.Routes.Length is >= 44 and <= 48, $"large motif {seed} must contain the full grid and about ten loops");
        True(candidate.Rooms.Select(room => room.Id).Distinct(StringComparer.Ordinal).Count() == 36, $"large motif {seed} room IDs must be unique");
        True(candidate.Routes.Where(route => route.From == candidate.GoalRoom || route.To == candidate.GoalRoom).All(route => route.RequiresSwitch), $"large motif {seed} must gate every goal edge");
        True(candidate.Routes.Any(route => route.RequiresSwitch && route.From != candidate.GoalRoom && route.To != candidate.GoalRoom), $"large motif {seed} must include a gated optional shortcut");
        True(ClosedReachable(candidate).Count == 35 && !ClosedReachable(candidate).Contains(candidate.GoalRoom), $"large motif {seed} must leave every non-goal room reachable while the goal stays locked");
        True(OpenReachable(candidate).Count == 36, $"large motif {seed} must become fully connected after activation");
        var repeated = WorkbenchExperiment.Generate(seed, WorkbenchExperiment.LargeMotif);
        Equal(WorkbenchCandidateJson.Identity(candidate), WorkbenchCandidateJson.Identity(repeated), $"large motif {seed} generation must be deterministic");
        topologySignatures.Add(TopologySignature(candidate));
        var encoded = WorkbenchCandidateJson.Serialize(candidate);
        True(encoded.Length <= 64 * 1024, $"large motif {seed} artifact must remain below the 64 KiB workbench limit");
        var roundTrip = WorkbenchCandidateJson.Deserialize(encoded);
        Equal(WorkbenchCandidateJson.Identity(candidate), WorkbenchCandidateJson.Identity(roundTrip), $"large motif {seed} strict v2 roundtrip must preserve identity");

        var witness = WorkbenchExperiment.Witness(candidate);
        True(witness.Length > 0, $"large motif {seed} must have a bounded completion witness");
        True(WorkbenchExperiment.Complete(candidate, ApplyAll(candidate, witness)), $"large motif {seed} witness must complete");
        var analysis = WorkbenchExperiment.Analyze(candidate);
        True(analysis.Completable && analysis.UnrecoverableStates == 0 && analysis.Contracts.All(contract => contract.Passed), $"large motif {seed} contracts must pass without unrecoverable states");
        True(analysis.ReachableStates > 36, $"large motif {seed} bounded search should include switch-state variation");
    }
    True(topologySignatures.Count >= 2, "large seeded samples must vary their route topology");

    var invalid = WorkbenchExperiment.Generate(83, WorkbenchExperiment.LargeMotif);
    var duplicateRoom = invalid with { Rooms = invalid.Rooms.Select((room, index) => index == 1 ? room with { Id = invalid.Rooms[2].Id } : room).ToArray() };
    True(WorkbenchExperiment.Validate(duplicateRoom).Length > 0, "large duplicate room IDs must return validation errors");
    var nullRoom = invalid with { Rooms = invalid.Rooms.Select((room, index) => index == 1 ? null! : room).ToArray() };
    True(WorkbenchExperiment.Validate(nullRoom).Length > 0, "large null rooms must return validation errors");
    var overlapRoom = invalid with { Rooms = invalid.Rooms.Select((room, index) => index == 1 ? room with { Minimum = invalid.Rooms[0].Minimum, Maximum = invalid.Rooms[0].Maximum } : room).ToArray() };
    True(WorkbenchExperiment.Validate(overlapRoom).Length > 0, "large overlapping rooms must return validation errors");
    var duplicateRoute = invalid with { Routes = invalid.Routes.Select((route, index) => index == 1 ? route with { From = invalid.Routes[0].From, To = invalid.Routes[0].To } : route).ToArray() };
    True(WorkbenchExperiment.Validate(duplicateRoute).Length > 0, "large duplicate route pairs must return validation errors");
    var offAxis = invalid with { Routes = invalid.Routes.Select((route, index) => index == 0 ? route with { To = "goal" } : route).ToArray() };
    True(WorkbenchExperiment.Validate(offAxis).Length > 0, "large non-adjacent route endpoints must return validation errors");
    var badWidth = invalid with { Routes = invalid.Routes.Select((route, index) => index == 0 ? route with { Width = 2f } : route).ToArray() };
    True(WorkbenchExperiment.Validate(badWidth).Length > 0, "large narrow routes must return validation errors");

    var counterexample = WorkbenchExperiment.Generate(83, WorkbenchExperiment.LargeMotif, counterexample: true);
    True(WorkbenchExperiment.Validate(counterexample).Length == 0, "large counterexample must remain structurally valid");
    var failure = WorkbenchExperiment.Analyze(counterexample);
    True(!failure.Completable && failure.Counterexamples.Length > 0 && failure.Contracts.Any(contract => !contract.Passed), "large counterexample must fail behavioral contracts");
    foreach (var trace in failure.Counterexamples)
        True(!WorkbenchExperiment.Complete(counterexample, ApplyAll(counterexample, trace.Actions)), $"large counterexample '{trace.Requirement}' must replay to an incomplete state");
}

static void StrictCandidateJson()
{
    var candidate = WorkbenchExperiment.Generate(11, WorkbenchExperiment.PreviewMotif);
    var encoded = WorkbenchCandidateJson.Serialize(candidate);
    Equal(WorkbenchCandidateJson.Identity(candidate), WorkbenchCandidateJson.Identity(WorkbenchCandidateJson.Deserialize(encoded)), "strict v2 roundtrip must preserve identity");
    var unknownMember = JsonNode.Parse(encoded)!.AsObject();
    unknownMember["unknown"] = true;
    ExpectArtifact(() => WorkbenchCandidateJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(unknownMember.ToJsonString())), "unknown JSON members must reject");
    var missingSeed = JsonNode.Parse(encoded)!.AsObject();
    missingSeed.Remove("seed");
    ExpectArtifact(() => WorkbenchCandidateJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(missingSeed.ToJsonString())), "omitted seed must reject instead of using a default");
    var missingRouteSwitch = JsonNode.Parse(encoded)!.AsObject();
    missingRouteSwitch["routes"]!.AsArray()[0]!.AsObject().Remove("requiresSwitch");
    ExpectArtifact(() => WorkbenchCandidateJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(missingRouteSwitch.ToJsonString())), "omitted route switch requirement must reject instead of using a default");
    foreach (var property in new[] { "switchEnabled", "recoveryEnabled", "previewOpening" })
    {
        var node = JsonNode.Parse(encoded)!.AsObject();
        node.Remove(property);
        ExpectArtifact(() => WorkbenchCandidateJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())), $"omitted {property} must reject instead of using a default");
    }
    ExpectArtifact(() => WorkbenchCandidateJson.Deserialize("{\"schema\":\"one\",\"schema\":\"two\"}"u8), "duplicate JSON properties must reject");

    var current = WorkbenchExperiment.Generate(11);
    var changedGeometry = current with
    {
        Rooms = current.Rooms.Select(room => StringComparer.Ordinal.Equals(room.Id, "relay")
            ? room with
            {
                Minimum = room.Minimum with { X = room.Minimum.X + 0.25f, Z = room.Minimum.Z + 0.25f },
                Maximum = room.Maximum with { X = room.Maximum.X - 0.25f, Z = room.Maximum.Z - 0.25f },
            }
            : room).ToArray(),
    };
    True(WorkbenchExperiment.Validate(changedGeometry).Length == 0, "modest valid geometry variation must remain admitted");
    NotEqual(WorkbenchCandidateJson.Identity(current), WorkbenchCandidateJson.Identity(changedGeometry), "valid geometry variation must change identity");

    var invalidRoom = current with
    {
        Rooms = current.Rooms.Select(room => StringComparer.Ordinal.Equals(room.Id, "goal") ? room with { Id = "unexpected" } : room).ToArray(),
    };
    True(WorkbenchExperiment.Validate(invalidRoom).Length > 0, "invalid room identity must return validation errors");
    ExpectArtifact(() => WorkbenchCandidateJson.Serialize(invalidRoom), "invalid room identity must reject artifact serialization");
    var invalidRoute = current with
    {
        Routes = current.Routes.Select(route => StringComparer.Ordinal.Equals(route.Id, "goal-start") ? route with { RequiresSwitch = false } : route).ToArray(),
    };
    True(WorkbenchExperiment.Validate(invalidRoute).Length > 0, "invalid locked route must return validation errors");
    ExpectArtifact(() => WorkbenchCandidateJson.Serialize(invalidRoute), "invalid locked route must reject artifact serialization");
}

static void WorkbenchRepairTests()
{
    var repairs = new[]
    {
        ("workbench-failure-11.json", WorkbenchRepair.RestoreSwitch, "switchEnabled"),
        ("recovery-failure-11.json", WorkbenchRepair.RestoreRecovery, "recoveryEnabled"),
        ("preview-failure-11.json", WorkbenchRepair.RestorePreview, "previewOpening"),
    };
    foreach (var (fixture, operation, changedField) in repairs)
    {
        var parent = WorkbenchCandidateJson.Deserialize(File.ReadAllBytes(RepositoryFile("content", "procgen", fixture)));
        Equal(operation, WorkbenchRepair.Operations(parent).Single(), $"{fixture} must expose only its fixture-backed repair");
        var result = WorkbenchRepair.Apply(parent, operation);
        True(WorkbenchExperiment.Analyze(result).Contracts.All(contract => contract.Passed), $"{fixture} repair must restore its bounded behavioral contracts");
        var parentNode = JsonNode.Parse(WorkbenchCandidateJson.Serialize(parent))!.AsObject();
        var resultNode = JsonNode.Parse(WorkbenchCandidateJson.Serialize(result))!.AsObject();
        parentNode.Remove(changedField);
        resultNode.Remove(changedField);
        Equal(parentNode.ToJsonString(), resultNode.ToJsonString(), $"{fixture} repair must preserve all unrelated resolved decisions");

        var receipt = WorkbenchRepairJson.Create(parent, operation);
        Equal(WorkbenchRepairReceipt.CurrentSchema, receipt.Schema, "repair receipt must identify its schema");
        True(receipt.Cost == 1, "repair receipt must retain one semantic field cost");
        Equal(changedField, receipt.ChangedField, "repair receipt must name its semantic field");
        Equal(receipt.ResultIdentity, WorkbenchCandidateJson.Identity(result), "repair receipt must retain canonical result identity");
        var encodedReceipt = WorkbenchRepairJson.Serialize(receipt);
        var decodedReceipt = WorkbenchRepairJson.Deserialize(encodedReceipt);
        Equal(receipt.ResultIdentity, decodedReceipt.ResultIdentity, "repair receipt must strictly roundtrip");

        var tampered = JsonNode.Parse(encodedReceipt)!.AsObject();
        tampered["cost"] = 2;
        ExpectArtifact(() => WorkbenchRepairJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(tampered.ToJsonString())), "tampered repair receipt cost must reject");
        tampered = JsonNode.Parse(encodedReceipt)!.AsObject();
        tampered["parentIdentity"] = "tampered";
        ExpectArtifact(() => WorkbenchRepairJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(tampered.ToJsonString())), "tampered repair receipt parent identity must reject");
        tampered = JsonNode.Parse(encodedReceipt)!.AsObject();
        tampered["resultIdentity"] = "tampered";
        ExpectArtifact(() => WorkbenchRepairJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(tampered.ToJsonString())), "tampered repair receipt result identity must reject");
        tampered = JsonNode.Parse(encodedReceipt)!.AsObject();
        tampered["result"]![changedField] = false;
        ExpectArtifact(() => WorkbenchRepairJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(tampered.ToJsonString())), "tampered repair result must reject");
        tampered = JsonNode.Parse(encodedReceipt)!.AsObject();
        tampered["before"] = null;
        ExpectArtifact(() => WorkbenchRepairJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(tampered.ToJsonString())), "null repair analysis must reject as an artifact validation error");
        ExpectIllegal(() => WorkbenchRepair.Apply(result, operation), "a repeated repair must reject as a no-op");
    }

    var previewWithMissingSwitch = WorkbenchExperiment.Generate(11, WorkbenchExperiment.PreviewMotif) with { SwitchEnabled = false };
    True(WorkbenchRepair.Operations(previewWithMissingSwitch).Contains(WorkbenchRepair.RestoreSwitch, StringComparer.Ordinal), "switch repair must apply to every motif whose activation semantics use the switch");
    var invalid = WorkbenchExperiment.Generate(11) with { Schema = "invalid" };
    ExpectIllegal(() => WorkbenchRepair.Operations(invalid), "invalid repair parents must reject before operation selection");
}

static WorkbenchState ApplyAll(WorkbenchCandidate candidate, IEnumerable<string> actions)
{
    var state = WorkbenchExperiment.Initial(candidate);
    foreach (var action in actions) state = WorkbenchExperiment.Apply(candidate, state, action);
    return state;
}

static HashSet<string> ClosedReachable(WorkbenchCandidate candidate) => Reachable(candidate, false);
static HashSet<string> OpenReachable(WorkbenchCandidate candidate) => Reachable(candidate, true);
static string TopologySignature(WorkbenchCandidate candidate) => string.Join('|', candidate.Routes.OrderBy(route => route.Id, StringComparer.Ordinal).Select(route => $"{route.Id}:{route.From}>{route.To}:{route.RequiresSwitch}"));
static HashSet<string> Reachable(WorkbenchCandidate candidate, bool switchOpen)
{
    var reachable = new HashSet<string>(StringComparer.Ordinal) { candidate.StartRoom };
    var pending = new Queue<string>();
    pending.Enqueue(candidate.StartRoom);
    while (pending.Count > 0)
    {
        var current = pending.Dequeue();
        foreach (var route in candidate.Routes)
        {
            if (route.RequiresSwitch && !switchOpen) continue;
            var next = route.From == current ? route.To : route.To == current ? route.From : null;
            if (next is not null && reachable.Add(next)) pending.Enqueue(next);
        }
    }
    return reachable;
}
static void ExpectIllegal(Action action, string detail)
{
    var threw = false;
    try { action(); } catch (InvalidOperationException) { threw = true; }
    if (!threw) throw new InvalidOperationException(detail);
}
static void ExpectArtifact(Action action, string detail)
{
    var threw = false;
    try { action(); } catch (ArtifactValidationException) { threw = true; }
    if (!threw) throw new InvalidOperationException(detail);
}
static string RepositoryFile(params string[] pathSegments)
{
    foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
    {
        var candidate = Path.Combine(current.FullName, Path.Combine(pathSegments));
        if (File.Exists(candidate)) return candidate;
    }
    throw new InvalidOperationException($"Could not find checked repository artifact '{Path.Combine(pathSegments)}'.");
}
static void Equal(string left, string right, string detail) { if (!StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(detail); }
static void NotEqual(string left, string right, string detail) { if (StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(detail); }
static void True(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
