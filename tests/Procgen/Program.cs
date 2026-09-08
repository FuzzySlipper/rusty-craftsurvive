using CraftSurvive.Procgen;
using CraftSurvive.Procgen.Generation;

var core = new GraphCore();
var intent = new SeedIntent("first-slice", "First slice", new[] { "lock", "loop", "hub" });

Repeatability(core, intent);
RuleVocabulary(core, intent);
RejectedRulesAreAtomic(core, intent);
InvalidGraphsAreRejected(core, intent);
ProgressionRejectsKeyAfterGate(core, intent);
RenamedTerminalsRemainValidated(core, intent);
BoundedRepairsAreAtomic(core, intent);
AmbiguousRepairTargetsAreAtomic(core, intent);
RepairAndRuleBudgetsAreIndependent(core, intent);
QuotaOverflowIsRejected(core, intent);
GenerationPolicyAdmissionIsComplete();
CraftSurvive.Procgen.Generation.GenerationProbe.Verify();
CraftSurvive.Procgen.Workloads.WorkloadProbe.Verify();
CaveLevelPlanChecks.Verify();
DungeonLevelPlanChecks.Verify();

Console.WriteLine("CraftSurvive.Procgen checks passed.");

static void Repeatability(GraphCore core, SeedIntent intent)
{
    var left = Apply(core, core.CreateInitial(intent, 17), GraphRule.LockKeyLoop, 701);
    left = Apply(core, left, GraphRule.DetourLoop, 702);
    var right = Apply(core, core.CreateInitial(intent, 17), GraphRule.LockKeyLoop, 701);
    right = Apply(core, right, GraphRule.DetourLoop, 702);
    Equal(CanonicalIdentity.Hash(left), CanonicalIdentity.Hash(right), "identical explicit seeds must repeat exactly");
    NotEqual(CanonicalIdentity.Hash(left), CanonicalIdentity.Hash(core.CreateInitial(intent, 18)), "one seed field must change identity");
}

static void RuleVocabulary(GraphCore core, SeedIntent intent)
{
    var candidate = core.CreateInitial(intent, 80);
    foreach (var (rule, seed) in new[]
    {
        (GraphRule.LockKeyLoop, 1UL), (GraphRule.DetourLoop, 2UL), (GraphRule.HubSpoke, 3UL),
        (GraphRule.HazardResource, 4UL), (GraphRule.BossPreparation, 5UL),
        (GraphRule.GatedBranch, 6UL), (GraphRule.Shortcut, 7UL),
    }) candidate = Apply(core, candidate, rule, seed);
    True(core.Validate(candidate).IsValid, "representative rule vocabulary must compose into a valid graph");
    True(core.Analyze(candidate).Metrics["node_count"] >= 2m, "analysis must expose readable metrics");
    True(core.Score(candidate).Overall is >= 0m and <= 100m, "score must be bounded");
}

static void RejectedRulesAreAtomic(GraphCore core, SeedIntent intent)
{
    var locked = Apply(core, core.CreateInitial(intent, 10), GraphRule.LockKeyLoop, 1);
    var before = CanonicalIdentity.Hash(locked);
    var rejected = core.Apply(locked, GraphRule.LockKeyLoop, 1);
    True(!rejected.Accepted, "duplicate fixed rule must reject");
    Equal(before, CanonicalIdentity.Hash(rejected.Candidate), "rejected rule must not mutate its candidate or identity");
    var detour = Apply(core, locked, GraphRule.DetourLoop, 99);
    var duplicateDetour = core.Apply(detour, GraphRule.DetourLoop, 99);
    True(!duplicateDetour.Accepted, "same deterministic detour seed must explicitly reject");
}

static void InvalidGraphsAreRejected(GraphCore core, SeedIntent intent)
{
    var invalid = new Candidate("invalid", 1, intent.Id,
        new CandidateGraph(
            new[]
            {
                new GraphNode("start", NodeKind.Start, "Start", Array.Empty<string>()),
                new GraphNode("start", NodeKind.Goal, "Goal", Array.Empty<string>()),
                new GraphNode("goal.extra", NodeKind.Goal, "Other Goal", Array.Empty<string>()),
            },
            new[] { new GraphEdge("edge.bad", "start", "missing", EdgeKind.CriticalPath, TraversalKind.Open, Array.Empty<string>()) }),
        Array.Empty<ProvenanceStep>());
    var report = core.Validate(invalid);
    Has(report, "duplicate_node_id");
    Has(report, "edge_to_missing");
    Has(report, "goal_count_invalid");
    True(!report.IsValid, "invalid topology must be rejected");
    True(core.SuggestRepairs(invalid).Count > 0, "invalid topology must receive repair suggestions");
}

static void ProgressionRejectsKeyAfterGate(GraphCore core, SeedIntent intent)
{
    var candidate = new Candidate("bad-progression", 1, intent.Id,
        new CandidateGraph(
            new[]
            {
                new GraphNode("start", NodeKind.Start, "Start", Array.Empty<string>()),
                new GraphNode("gate", NodeKind.Gate, "Gate", Array.Empty<string>()),
                new GraphNode("key", NodeKind.Key, "Key", Array.Empty<string>(), "gate-key"),
                new GraphNode("goal", NodeKind.Goal, "Goal", Array.Empty<string>()),
            },
            new[]
            {
                new GraphEdge("start-gate", "start", "gate", EdgeKind.CriticalPath, TraversalKind.Locked, Array.Empty<string>(), "gate-key"),
                new GraphEdge("gate-key", "gate", "key", EdgeKind.KeyBranch, TraversalKind.Open, Array.Empty<string>()),
                new GraphEdge("key-goal", "key", "goal", EdgeKind.CriticalPath, TraversalKind.Open, Array.Empty<string>()),
            }),
        Array.Empty<ProvenanceStep>());
    var report = core.Validate(candidate);
    Has(report, "goal_unreachable");
    Has(report, "locked_edge_never_traversed");
}

static void RenamedTerminalsRemainValidated(GraphCore core, SeedIntent intent)
{
    var unreachable = new Candidate("renamed-unreachable", 1, intent.Id,
        new CandidateGraph(
            new[]
            {
                new GraphNode("origin", NodeKind.Start, "Origin", Array.Empty<string>()),
                new GraphNode("exit", NodeKind.Goal, "Exit", Array.Empty<string>()),
            }, Array.Empty<GraphEdge>()), Array.Empty<ProvenanceStep>());
    var rejected = core.Validate(unreachable);
    Has(rejected, "goal_unreachable");
    True(!rejected.IsValid, "sole renamed terminals without a route must reject");

    var valid = unreachable with { Id = "renamed-valid", Graph = new CandidateGraph(unreachable.Graph.Nodes, new[] { new GraphEdge("origin-exit", "origin", "exit", EdgeKind.CriticalPath, TraversalKind.Open, Array.Empty<string>()) }) };
    var progression = GraphValidator.ReachableWithItems(valid);
    True(core.Validate(valid).IsValid, "sole renamed terminals with a route must validate");
    Equal("origin", progression.StartNodeId!, "progression must retain the resolved Start identity");
    Equal("exit", progression.GoalNodeId!, "progression must retain the resolved Goal identity");
    True(progression.GoalReached, "progression must compare against the resolved Goal identity");
}

static void BoundedRepairsAreAtomic(GraphCore core, SeedIntent intent)
{
    var invalid = new Candidate("repairable", 5, intent.Id,
        new CandidateGraph(
            new[]
            {
                new GraphNode("start", NodeKind.Start, "Start", Array.Empty<string>()),
                new GraphNode("goal", NodeKind.Goal, "Goal", Array.Empty<string>()),
            },
            new[]
            {
                new GraphEdge("edge.start.goal", "start", "goal", EdgeKind.CriticalPath, TraversalKind.Open, Array.Empty<string>()),
                new GraphEdge("edge.invalid", "missing", "goal", EdgeKind.OptionalBranch, TraversalKind.Open, Array.Empty<string>()),
            }), Array.Empty<ProvenanceStep>());
    True(!core.Validate(invalid).IsValid, "repair fixture must begin invalid");
    var repaired = core.ApplyRepair(invalid, new RepairRequest(RepairAction.RemoveInvalidEdge, "edge.invalid", 70), new RepairBudget(1));
    True(repaired.Accepted, "bounded invalid-edge repair must be accepted after validation");
    True(core.Validate(repaired.Candidate).IsValid, "accepted repair must leave a valid candidate");
    NotEqual(CanonicalIdentity.Hash(invalid), CanonicalIdentity.Hash(repaired.Candidate), "accepted repair must append deterministic provenance and transform the graph");

    var before = CanonicalIdentity.Hash(invalid);
    var exhausted = core.ApplyRepair(invalid, new RepairRequest(RepairAction.RemoveInvalidEdge, "edge.invalid", 70), new RepairBudget(0));
    True(!exhausted.Accepted, "over-budget repair must reject");
    HasDiagnostic(exhausted.Diagnostics, "repair_budget_exhausted");
    Equal(before, CanonicalIdentity.Hash(exhausted.Candidate), "rejected repair must leave the source candidate unchanged");
}

static void AmbiguousRepairTargetsAreAtomic(GraphCore core, SeedIntent intent)
{
    var duplicateNodes = new Candidate("duplicate-nodes", 1, intent.Id,
        new CandidateGraph(
            new[]
            {
                new GraphNode("start", NodeKind.Start, "Start", Array.Empty<string>()),
                new GraphNode("goal", NodeKind.Goal, "Goal", Array.Empty<string>()),
                new GraphNode("orphan", NodeKind.Treasure, "First Orphan", Array.Empty<string>()),
                new GraphNode("orphan", NodeKind.Treasure, "Second Orphan", Array.Empty<string>()),
            },
            new[] { new GraphEdge("edge.start.goal", "start", "goal", EdgeKind.CriticalPath, TraversalKind.Open, Array.Empty<string>()) }), Array.Empty<ProvenanceStep>());
    var beforeNodes = CanonicalIdentity.Hash(duplicateNodes);
    var nodeRepair = core.ApplyRepair(duplicateNodes, new RepairRequest(RepairAction.RemoveOrphanNode, "orphan", 2));
    True(!nodeRepair.Accepted, "duplicate node repair target must reject instead of throw");
    HasDiagnostic(nodeRepair.Diagnostics, "repair_target_ambiguous");
    Equal(beforeNodes, CanonicalIdentity.Hash(nodeRepair.Candidate), "ambiguous node repair must be fail-atomic");

    var duplicateEdges = new Candidate("duplicate-edges", 1, intent.Id,
        new CandidateGraph(
            new[]
            {
                new GraphNode("start", NodeKind.Start, "Start", Array.Empty<string>()),
                new GraphNode("goal", NodeKind.Goal, "Goal", Array.Empty<string>()),
            },
            new[]
            {
                new GraphEdge("edge.start.goal", "start", "goal", EdgeKind.CriticalPath, TraversalKind.Open, Array.Empty<string>()),
                new GraphEdge("edge.invalid", "missing-a", "goal", EdgeKind.OptionalBranch, TraversalKind.Open, Array.Empty<string>()),
                new GraphEdge("edge.invalid", "missing-b", "goal", EdgeKind.OptionalBranch, TraversalKind.Open, Array.Empty<string>()),
            }), Array.Empty<ProvenanceStep>());
    var beforeEdges = CanonicalIdentity.Hash(duplicateEdges);
    var edgeRepair = core.ApplyRepair(duplicateEdges, new RepairRequest(RepairAction.RemoveInvalidEdge, "edge.invalid", 2));
    True(!edgeRepair.Accepted, "duplicate edge repair target must reject instead of throw");
    HasDiagnostic(edgeRepair.Diagnostics, "repair_target_ambiguous");
    Equal(beforeEdges, CanonicalIdentity.Hash(edgeRepair.Candidate), "ambiguous edge repair must be fail-atomic");
}

static void RepairAndRuleBudgetsAreIndependent(GraphCore core, SeedIntent intent)
{
    var initial = core.CreateInitial(intent, 44);
    var repairable = initial with
    {
        Graph = new CandidateGraph(initial.Graph.Nodes, initial.Graph.Edges.Concat(new[]
        {
            new GraphEdge("edge.invalid", "missing", "goal", EdgeKind.OptionalBranch, TraversalKind.Open, Array.Empty<string>()),
        }).ToArray())
    };
    var noRules = new GraphBudget(16, 16, 0);
    var repaired = core.ApplyRepair(repairable, new RepairRequest(RepairAction.RemoveInvalidEdge, "edge.invalid", 9), new RepairBudget(1), noRules);
    True(repaired.Accepted, "repair must not consume a zero rule-application budget");
    True(core.Validate(repaired.Candidate, noRules).IsValid, "repaired candidate must validate with zero rule applications");

    var oneRule = new GraphBudget(16, 16, 1);
    var applied = core.Apply(repaired.Candidate, GraphRule.LockKeyLoop, 10, oneRule);
    True(applied.Accepted, "repair provenance must not reduce the later exact rule budget");
    var exhausted = core.Apply(applied.Candidate, GraphRule.DetourLoop, 11, oneRule);
    True(!exhausted.Accepted, "second rule must exhaust its separate exact rule budget");
    HasDiagnostic(exhausted.Diagnostics, "rule_budget_exhausted");
}

static void QuotaOverflowIsRejected(GraphCore core, SeedIntent intent)
{
    var initial = core.CreateInitial(intent, ulong.MaxValue);
    var result = core.Apply(initial, GraphRule.HubSpoke, 1, new GraphBudget(3, 4, 2));
    True(!result.Accepted, "proposal beyond named quota must reject");
    HasDiagnostic(result.Diagnostics, "node_quota_exceeded");
    Equal(CanonicalIdentity.Hash(initial), CanonicalIdentity.Hash(result.Candidate), "quota rejection must be fail-atomic");
}

static void GenerationPolicyAdmissionIsComplete()
{
    True(GenerationPolicyValidation.IsValid(GenerationPolicy.Normal), "The current complete normal policy must be admitted.");
    True(!GenerationPolicyValidation.IsValid(GenerationPolicy.Normal with { MaxWidth = 0 }), "Each required positive policy field must reject its zero boundary.");
    True(!GenerationPolicyValidation.IsValid(GenerationPolicy.Normal with { RoomStride = GenerationPolicy.Normal.RoomFootprintCells }), "The stride one-under minimum must reject.");
    True(!GenerationPolicyValidation.IsValid(GenerationPolicy.Normal with { MaxPlacementBacktracks = -1 }), "The non-negative backtrack boundary must reject.");
    GenerationPolicy atLimit = GenerationPolicy.Normal with
    {
        Id = "limits",
        MaxWidth = GenerationPolicyValidation.MaxDimension,
        MaxHeight = GenerationPolicyValidation.MaxDimension,
        RoomStride = GenerationPolicyValidation.MaxRoomStride,
        RoomFootprintCells = GenerationPolicyValidation.MaxRoomFootprintCells,
        RoomCandidatesPerRegion = GenerationPolicyValidation.MaxRoomCandidatesPerRegion,
        MaxLayoutExpansions = GenerationPolicyValidation.MaxLayoutExpansions,
        MaxRouteAttempts = GenerationPolicyValidation.MaxRouteAttempts,
        MaxRouteExpansionsPerConnection = GenerationPolicyValidation.MaxRouteExpansionsPerConnection,
        MaxPlacementDecisions = GenerationPolicyValidation.MaxPlacementDecisions,
        MaxPlacementBacktracks = GenerationPolicyValidation.MaxPlacementBacktracks,
        MaxCatalogCandidatesPerRequirement = GenerationPolicyValidation.MaxCatalogCandidatesPerRequirement,
        MaxArtifactCells = GenerationPolicyValidation.MaxArtifactCells,
    };
    True(GenerationPolicyValidation.IsValid(atLimit), "Every declared current-schema upper boundary must be admitted together.");
    foreach (GenerationPolicy oneOver in new[]
    {
        atLimit with { MaxWidth = GenerationPolicyValidation.MaxDimension + 1 },
        atLimit with { MaxHeight = GenerationPolicyValidation.MaxDimension + 1 },
        atLimit with { RoomStride = GenerationPolicyValidation.MaxRoomStride + 1 },
        atLimit with { RoomFootprintCells = GenerationPolicyValidation.MaxRoomFootprintCells + 1 },
        atLimit with { RoomCandidatesPerRegion = GenerationPolicyValidation.MaxRoomCandidatesPerRegion + 1 },
        atLimit with { MaxLayoutExpansions = GenerationPolicyValidation.MaxLayoutExpansions + 1 },
        atLimit with { MaxRouteAttempts = GenerationPolicyValidation.MaxRouteAttempts + 1 },
        atLimit with { MaxRouteExpansionsPerConnection = GenerationPolicyValidation.MaxRouteExpansionsPerConnection + 1 },
        atLimit with { MaxPlacementDecisions = GenerationPolicyValidation.MaxPlacementDecisions + 1 },
        atLimit with { MaxPlacementBacktracks = GenerationPolicyValidation.MaxPlacementBacktracks + 1 },
        atLimit with { MaxCatalogCandidatesPerRequirement = GenerationPolicyValidation.MaxCatalogCandidatesPerRequirement + 1 },
        atLimit with { MaxArtifactCells = GenerationPolicyValidation.MaxArtifactCells + 1 },
    }) True(!GenerationPolicyValidation.IsValid(oneOver), "Every declared current-schema upper boundary plus one must reject.");
}

static Candidate Apply(GraphCore core, Candidate candidate, GraphRule rule, ulong seed)
{
    var result = core.Apply(candidate, rule, seed);
    if (!result.Accepted) throw new InvalidOperationException($"{rule} was rejected: {string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code))}");
    return result.Candidate;
}

static void Has(ValidationReport report, string code) => HasDiagnostic(report.Diagnostics, code);
static void HasDiagnostic(IReadOnlyList<Diagnostic> diagnostics, string code) => True(diagnostics.Any(diagnostic => diagnostic.Code == code), $"expected diagnostic {code}");
static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal(string left, string right, string message) { if (!StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(message); }
static void NotEqual(string left, string right, string message) { if (StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(message); }
