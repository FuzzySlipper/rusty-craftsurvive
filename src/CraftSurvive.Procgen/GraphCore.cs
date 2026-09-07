namespace CraftSurvive.Procgen;

/// <summary>Pure, bounded graph construction. Every rejected proposal returns the input candidate unchanged.</summary>
public sealed class GraphCore
{
    public Candidate CreateInitial(SeedIntent intent, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.Id)) throw new ArgumentException("Intent id is required.", nameof(intent));
        return Candidate.Create(intent, seed);
    }

    public RuleApplication Apply(Candidate candidate, GraphRule rule, ulong seed, GraphBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        budget ??= GraphBudget.Default;
        var diagnostic = CheckApplicationBudget(candidate, budget);
        if (diagnostic is not null) return Reject(candidate, diagnostic);
        var proposal = Propose(candidate, rule, seed);
        if (proposal.Error is not null) return Reject(candidate, proposal.Error);
        var proposed = new Candidate(
            candidate.Id,
            candidate.Seed,
            candidate.IntentId,
            proposal.Graph!,
            candidate.Provenance.Concat(new[] { new ProvenanceStep(candidate.Provenance.Count + 1, rule.ToString(), seed, $"Applied {rule}.") }).ToArray());
        var validation = GraphValidator.Validate(proposed, budget);
        return validation.IsValid
            ? new RuleApplication(proposed, true, validation.Diagnostics)
            : new RuleApplication(candidate, false, validation.Diagnostics);
    }

    public ValidationReport Validate(Candidate candidate, GraphBudget? budget = null) => GraphValidator.Validate(candidate, budget);
    public GraphAnalysis Analyze(Candidate candidate) => GraphAnalytics.Analyze(candidate);
    public ScoreReport Score(Candidate candidate, GraphTuning? tuning = null) => GraphAnalytics.Score(candidate, tuning ?? GraphTuning.Default);
    public IReadOnlyList<RepairSuggestion> SuggestRepairs(Candidate candidate) => GraphRepairs.Suggest(GraphValidator.Validate(candidate));

    /// <summary>Applies one narrow product-owned repair, validating the complete proposed graph before acceptance.</summary>
    public RepairApplication ApplyRepair(Candidate candidate, RepairRequest request, RepairBudget? budget = null, GraphBudget? graphBudget = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);
        budget ??= RepairBudget.Default;
        graphBudget ??= GraphBudget.Default;
        if (budget.MaxActions < 0) return RejectRepair(candidate, Fatal("invalid_repair_budget", "Repair budget must be non-negative."));
        var priorRepairs = candidate.Provenance.Count(step => step.Operation.StartsWith("repair:", StringComparison.Ordinal));
        if ((long)priorRepairs >= budget.MaxActions) return RejectRepair(candidate, Fatal("repair_budget_exhausted", "Repair action budget is exhausted."));

        var proposal = ProposeRepair(candidate, request);
        if (proposal.Error is not null) return RejectRepair(candidate, proposal.Error);
        var repaired = new Candidate(candidate.Id, candidate.Seed, candidate.IntentId, proposal.Graph!, candidate.Provenance.Concat(new[]
        {
            new ProvenanceStep(candidate.Provenance.Count + 1, $"repair:{request.Action}", request.Seed, $"Applied {request.Action} to {request.TargetId}.")
        }).ToArray());
        var validation = GraphValidator.Validate(repaired, graphBudget);
        return validation.IsValid ? new RepairApplication(repaired, true, validation.Diagnostics) : new RepairApplication(candidate, false, validation.Diagnostics);
    }

    private static Diagnostic? CheckApplicationBudget(Candidate candidate, GraphBudget budget)
    {
        if (budget.MaxNodes < 0 || budget.MaxEdges < 0 || budget.MaxRuleApplications < 0)
            return Fatal("invalid_budget", "Budgets must be non-negative.");
        if ((long)GraphProvenance.RuleApplicationCount(candidate) >= budget.MaxRuleApplications)
            return Fatal("rule_budget_exhausted", "Rule application budget is exhausted.");
        return null;
    }

    private static RuleApplication Reject(Candidate candidate, Diagnostic diagnostic) => new(candidate, false, new[] { diagnostic });
    private static RepairApplication RejectRepair(Candidate candidate, Diagnostic diagnostic) => new(candidate, false, new[] { diagnostic });

    private static (CandidateGraph? Graph, Diagnostic? Error) ProposeRepair(Candidate candidate, RepairRequest request)
    {
        var nodes = candidate.Graph.Nodes.ToList();
        var edges = candidate.Graph.Edges.ToList();
        var starts = nodes.Where(node => node.Kind == NodeKind.Start).Select(node => node.Id).ToArray();
        var goals = nodes.Where(node => node.Kind == NodeKind.Goal).Select(node => node.Id).ToArray();
        var start = starts.Length == 1 ? starts[0] : null;
        var goal = goals.Length == 1 ? goals[0] : null;
        if (string.IsNullOrWhiteSpace(request.TargetId)) return (null, Fatal("repair_target_required", "Repair requires a target id."));
        switch (request.Action)
        {
            case RepairAction.ReconnectDeadEndToGoal:
            {
                if (goal is null) return (null, Fatal("repair_goal_ambiguous", "Reconnect repair requires exactly one goal node."));
                var targetResult = ResolveNodeTarget(nodes, request.TargetId);
                if (targetResult.Error is not null) return (null, targetResult.Error);
                var target = targetResult.Target!;
                if (target.Id == goal || target.Id == start) return (null, Fatal("repair_target_invalid", "Start and goal cannot be reconnected as dead ends."));
                if (edges.Any(edge => edge.From == target.Id)) return (null, Fatal("repair_target_not_dead_end", "Reconnect repair applies only to a node with no outgoing edges."));
                var edgeId = $"edge.repair.{Slug(target.Id)}.{Slug(goal)}.{Suffix(request.Seed)}";
                if (edges.Any(edge => edge.Id == edgeId)) return (null, Fatal("repair_edge_duplicate", "Repair edge identity already exists."));
                edges.Add(new GraphEdge(edgeId, target.Id, goal, EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "repair", "rejoin" }));
                break;
            }
            case RepairAction.RemoveOrphanNode:
            {
                var targetResult = ResolveNodeTarget(nodes, request.TargetId);
                if (targetResult.Error is not null) return (null, targetResult.Error);
                var target = targetResult.Target!;
                if (target.Id == start || target.Id == goal) return (null, Fatal("repair_target_invalid", "Start and goal cannot be removed."));
                if (edges.Any(edge => edge.To == target.Id)) return (null, Fatal("repair_target_not_orphan", "Remove-orphan repair requires no incoming edge."));
                nodes.Remove(target);
                edges.RemoveAll(edge => edge.From == target.Id || edge.To == target.Id);
                break;
            }
            case RepairAction.RemoveInvalidEdge:
            {
                var targetResult = ResolveEdgeTarget(edges, request.TargetId);
                if (targetResult.Error is not null) return (null, targetResult.Error);
                var target = targetResult.Target!;
                var identities = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
                if (identities.Contains(target.From) && identities.Contains(target.To)) return (null, Fatal("repair_target_not_invalid", "Remove-invalid-edge applies only to an edge with a missing endpoint."));
                edges.Remove(target);
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(request), request.Action, "Unknown repair action.");
        }
        return (new CandidateGraph(nodes.ToArray(), edges.ToArray()), null);
    }

    private static (GraphNode? Target, Diagnostic? Error) ResolveNodeTarget(IReadOnlyList<GraphNode> nodes, string targetId)
    {
        var matches = nodes.Where(node => node.Id == targetId).ToArray();
        return matches.Length switch
        {
            0 => (null, Fatal("repair_target_missing", "Repair target node does not exist.")),
            1 => (matches[0], null),
            _ => (null, Fatal("repair_target_ambiguous", $"Repair target node '{targetId}' has {matches.Length} matching identities.")),
        };
    }

    private static (GraphEdge? Target, Diagnostic? Error) ResolveEdgeTarget(IReadOnlyList<GraphEdge> edges, string targetId)
    {
        var matches = edges.Where(edge => edge.Id == targetId).ToArray();
        return matches.Length switch
        {
            0 => (null, Fatal("repair_target_missing", "Repair target edge does not exist.")),
            1 => (matches[0], null),
            _ => (null, Fatal("repair_target_ambiguous", $"Repair target edge '{targetId}' has {matches.Length} matching identities.")),
        };
    }

    private static (CandidateGraph? Graph, Diagnostic? Error) Propose(Candidate candidate, GraphRule rule, ulong seed)
    {
        var nodes = candidate.Graph.Nodes.ToList();
        var edges = candidate.Graph.Edges.ToList();
        var ids = new HashSet<string>(nodes.Select(node => node.Id), StringComparer.Ordinal);
        var edgeIds = new HashSet<string>(edges.Select(edge => edge.Id), StringComparer.Ordinal);
        bool HasNode(string id) => ids.Contains(id);
        bool HasEdge(string id) => edgeIds.Contains(id);
        void Node(string id, NodeKind kind, string label, string[] tags, string? item = null) { nodes.Add(new GraphNode(id, kind, label, tags, item)); ids.Add(id); }
        void Edge(string id, string from, string to, EdgeKind kind, TraversalKind traversal, string[] tags, string? item = null) { edges.Add(new GraphEdge(id, from, to, kind, traversal, tags, item)); edgeIds.Add(id); }
        Diagnostic Duplicate(string marker) => Fatal("rule_already_applied", $"{rule} is already present ({marker}).", hint: "Choose a different rule or use a fresh candidate.");

        switch (rule)
        {
            case GraphRule.LockKeyLoop:
                if (HasNode("gate.locked_1")) return (null, Duplicate("gate.locked_1"));
                edges.RemoveAll(edge => edge.Id == "edge.start.goal");
                Node("gate.locked_1", NodeKind.Gate, "Locked Gate", new[] { "critical", "lock" });
                Node("key.gate_1", NodeKind.Key, "Gate Key", new[] { "branch", "key" }, "item.gate_key_1");
                Edge("edge.start.gate_1", "start", "gate.locked_1", EdgeKind.CriticalPath, TraversalKind.Open, new[] { "approach" });
                Edge("edge.gate_1.goal", "gate.locked_1", "goal", EdgeKind.CriticalPath, TraversalKind.Locked, new[] { "locked" }, "item.gate_key_1");
                Edge("edge.start.key_1", "start", "key.gate_1", EdgeKind.KeyBranch, TraversalKind.Open, new[] { "branch" });
                Edge("edge.key_1.gate_1", "key.gate_1", "gate.locked_1", EdgeKind.KeyBranch, TraversalKind.Open, new[] { "return" });
                break;
            case GraphRule.DetourLoop:
            {
                var suffix = Suffix(seed);
                var treasure = $"treasure.detour_{suffix}";
                if (HasNode(treasure)) return (null, Duplicate(treasure));
                Node(treasure, NodeKind.Treasure, "Optional Treasure", new[] { "optional", "reward" });
                Edge($"edge.start.{treasure}", "start", treasure, EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "detour" });
                Edge($"edge.{treasure}.goal", treasure, "goal", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "rejoin" });
                break;
            }
            case GraphRule.HubSpoke:
                if (HasNode("hub.central_1")) return (null, Duplicate("hub.central_1"));
                Node("hub.central_1", NodeKind.Junction, "Wayfinding Hub", new[] { "hub", "merge", "wayfinding_anchor" });
                Node("resource.clue_1", NodeKind.Resource, "Route Clue", new[] { "optional", "preparation" });
                Node("hazard.watch_1", NodeKind.Hazard, "Watched Passage", new[] { "hazard", "optional" });
                Node("treasure.cache_1", NodeKind.Treasure, "Hub Cache", new[] { "optional", "reward" });
                Edge("edge.start.hub_1", "start", "hub.central_1", EdgeKind.CriticalPath, TraversalKind.Open, new[] { "approach" });
                Edge("edge.hub_1.goal", "hub.central_1", "goal", EdgeKind.CriticalPath, TraversalKind.Open, new[] { "rejoin" });
                Edge("edge.hub_1.clue_1", "hub.central_1", "resource.clue_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "branch" });
                Edge("edge.clue_1.hub_1", "resource.clue_1", "hub.central_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "return" });
                Edge("edge.hub_1.watch_1", "hub.central_1", "hazard.watch_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "branch", "pressure" });
                Edge("edge.watch_1.goal", "hazard.watch_1", "goal", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "rejoin" });
                Edge("edge.hub_1.cache_1", "hub.central_1", "treasure.cache_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "branch" });
                Edge("edge.cache_1.hub_1", "treasure.cache_1", "hub.central_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "return" });
                break;
            case GraphRule.HazardResource:
                if (HasNode("hazard.sluice_1")) return (null, Duplicate("hazard.sluice_1"));
                Node("hazard.sluice_1", NodeKind.Hazard, "Flooded Sluice", new[] { "hazard", "optional" });
                Node("resource.safety_1", NodeKind.Resource, "Safety Cache", new[] { "optional", "preparation" }, "item.safety_cache_1");
                Edge("edge.start.safety_1", "start", "resource.safety_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "branch" });
                Edge("edge.safety_1.sluice_1", "resource.safety_1", "hazard.sluice_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "preparation" });
                Edge("edge.start.sluice_1", "start", "hazard.sluice_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "branch", "pressure" });
                Edge("edge.sluice_1.goal", "hazard.sluice_1", "goal", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "rejoin" });
                break;
            case GraphRule.BossPreparation:
                if (HasNode("gate.boss_1")) return (null, Duplicate("gate.boss_1"));
                var approach = HasNode("gate.locked_1") ? "gate.locked_1" : "start";
                edges.RemoveAll(edge => edge.From == approach && edge.To == "goal" && edge.Kind == EdgeKind.CriticalPath);
                Node("gate.boss_1", NodeKind.Gate, "Boss Threshold", new[] { "boss", "critical" });
                Node("resource.boss_prep_1", NodeKind.Resource, "Boss Preparation", new[] { "optional", "preparation" }, "item.boss_preparation_1");
                Edge("edge.approach.boss_1", approach, "gate.boss_1", EdgeKind.CriticalPath, TraversalKind.Open, new[] { "approach" });
                Edge("edge.boss_1.goal", "gate.boss_1", "goal", EdgeKind.CriticalPath, TraversalKind.Locked, new[] { "boss", "locked" }, "item.boss_preparation_1");
                Edge("edge.approach.boss_prep_1", approach, "resource.boss_prep_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "branch", "preparation" });
                Edge("edge.boss_prep_1.boss_1", "resource.boss_prep_1", "gate.boss_1", EdgeKind.OptionalBranch, TraversalKind.Open, new[] { "return" });
                break;
            case GraphRule.GatedBranch:
                if (HasNode("treasure.gated_1")) return (null, Duplicate("treasure.gated_1"));
                Node("key.treasure_1", NodeKind.Key, "Treasure Key", new[] { "key", "optional" }, "item.treasure_key_1");
                Node("treasure.gated_1", NodeKind.Treasure, "Gated Treasure", new[] { "optional", "reward" });
                Edge("edge.start.treasure_key_1", "start", "key.treasure_1", EdgeKind.KeyBranch, TraversalKind.Open, new[] { "branch" });
                Edge("edge.treasure_key_1.treasure_1", "key.treasure_1", "treasure.gated_1", EdgeKind.OptionalBranch, TraversalKind.Locked, new[] { "branch", "locked" }, "item.treasure_key_1");
                Edge("edge.treasure_1.goal", "treasure.gated_1", "goal", EdgeKind.OptionalBranch, TraversalKind.Locked, new[] { "locked", "rejoin" }, "item.treasure_key_1");
                break;
            case GraphRule.Shortcut:
                if (HasEdge("edge.shortcut.return.start")) return (null, Duplicate("edge.shortcut.return.start"));
                Node("shortcut.return_1", NodeKind.Shortcut, "Return Shortcut", new[] { "shortcut" });
                Edge("edge.goal.shortcut_1", "goal", "shortcut.return_1", EdgeKind.Shortcut, TraversalKind.Open, new[] { "shortcut" });
                Edge("edge.shortcut.return.start", "shortcut.return_1", "start", EdgeKind.Shortcut, TraversalKind.OneWayReturn, new[] { "return", "shortcut" });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown graph rule.");
        }
        return (new CandidateGraph(nodes.ToArray(), edges.ToArray()), null);
    }

    private static Diagnostic Fatal(string code, string detail, string? hint = null) => new(code, DiagnosticSeverity.Fatal, detail, RepairHint: hint);
    private static string Suffix(ulong seed) => seed.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    private static string Slug(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')).Trim('-');
}

internal static class GraphProvenance
{
    public static int RuleApplicationCount(Candidate candidate) => candidate.Provenance.Count(step => Enum.TryParse<GraphRule>(step.Operation, ignoreCase: false, out _));
}
