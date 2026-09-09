using System.Text.Json.Nodes;
using CraftSurvive.Procgen.Artifacts;
using CraftSurvive.Procgen.Workbench;

foreach (var motif in new[] { WorkbenchExperiment.CurrentMotif, WorkbenchExperiment.RecoveryMotif, WorkbenchExperiment.PreviewMotif })
    PositiveMotif(motif);

CounterexamplesFailContracts();
StrictCandidateJson();

Console.WriteLine("CraftSurvive workbench checks passed.");

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

static WorkbenchState ApplyAll(WorkbenchCandidate candidate, IEnumerable<string> actions)
{
    var state = WorkbenchExperiment.Initial(candidate);
    foreach (var action in actions) state = WorkbenchExperiment.Apply(candidate, state, action);
    return state;
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
static void Equal(string left, string right, string detail) { if (!StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(detail); }
static void NotEqual(string left, string right, string detail) { if (StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(detail); }
static void True(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
