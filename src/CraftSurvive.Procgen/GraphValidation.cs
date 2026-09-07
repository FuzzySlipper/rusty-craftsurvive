namespace CraftSurvive.Procgen;

public static class GraphValidator
{
    public static ValidationReport Validate(Candidate candidate, GraphBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        budget ??= GraphBudget.Default;
        var diagnostics = new List<Diagnostic>();
        if (budget.MaxNodes < 0 || budget.MaxEdges < 0 || budget.MaxRuleApplications < 0)
            diagnostics.Add(Fatal("invalid_budget", "Budgets must be non-negative."));
        if ((long)candidate.Graph.Nodes.Count > budget.MaxNodes)
            diagnostics.Add(Fatal("node_quota_exceeded", $"Graph contains {candidate.Graph.Nodes.Count} nodes; budget permits {budget.MaxNodes}."));
        if ((long)candidate.Graph.Edges.Count > budget.MaxEdges)
            diagnostics.Add(Fatal("edge_quota_exceeded", $"Graph contains {candidate.Graph.Edges.Count} edges; budget permits {budget.MaxEdges}."));
        var ruleApplications = GraphProvenance.RuleApplicationCount(candidate);
        if ((long)ruleApplications > budget.MaxRuleApplications)
            diagnostics.Add(Fatal("rule_budget_exceeded", $"Candidate has {ruleApplications} rule applications; budget permits {budget.MaxRuleApplications}."));

        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in candidate.Graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(node.Id)) diagnostics.Add(Fatal("node_id_invalid", "Node identity cannot be empty.", nodeId: node.Id));
            if (!nodes.TryAdd(node.Id, node)) diagnostics.Add(Fatal("duplicate_node_id", "Graph node identities must be unique.", nodeId: node.Id, hint: "Use a distinct node id."));
        }
        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in candidate.Graph.Edges.OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(edge.Id)) diagnostics.Add(Fatal("edge_id_invalid", "Edge identity cannot be empty.", edgeId: edge.Id));
            if (!edges.Add(edge.Id)) diagnostics.Add(Fatal("duplicate_edge_id", "Graph edge identities must be unique.", edgeId: edge.Id));
            if (!nodes.ContainsKey(edge.From)) diagnostics.Add(Fatal("edge_from_missing", "Edge source node is missing.", edgeId: edge.Id, hint: "Create the source node or remove the edge."));
            if (!nodes.ContainsKey(edge.To)) diagnostics.Add(Fatal("edge_to_missing", "Edge target node is missing.", edgeId: edge.Id, hint: "Create the target node or remove the edge."));
        }
        var startCount = candidate.Graph.Nodes.Count(node => node.Kind == NodeKind.Start);
        var goalCount = candidate.Graph.Nodes.Count(node => node.Kind == NodeKind.Goal);
        if (startCount != 1) diagnostics.Add(Fatal("start_count_invalid", "Graph must contain exactly one start node."));
        if (goalCount != 1) diagnostics.Add(Fatal("goal_count_invalid", "Graph must contain exactly one goal node."));

        var grantedItems = candidate.Graph.Nodes.Select(node => node.GrantsItem).Where(item => !string.IsNullOrWhiteSpace(item)).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in candidate.Graph.Edges.Where(edge => edge.RequiredItem is not null).OrderBy(edge => edge.Id, StringComparer.Ordinal))
            if (!grantedItems.Contains(edge.RequiredItem!))
                diagnostics.Add(Fatal("required_item_unavailable", $"Edge requires {edge.RequiredItem}, but no node grants it.", edgeId: edge.Id, hint: "Add a reachable provider before the locked edge."));

        var startId = startCount == 1 ? candidate.Graph.Nodes.Single(node => node.Kind == NodeKind.Start).Id : null;
        var goalId = goalCount == 1 ? candidate.Graph.Nodes.Single(node => node.Kind == NodeKind.Goal).Id : null;
        if (startId is not null && goalId is not null)
        {
            var progression = ReachableWithItems(candidate);
            if (!progression.GoalReached)
                diagnostics.Add(Fatal("goal_unreachable", "Goal is not reachable under lock/key constraints.", nodeId: goalId, hint: "Move the provider before the lock or reconnect progression."));
            foreach (var edge in candidate.Graph.Edges.Where(edge => edge.Traversal == TraversalKind.Locked).OrderBy(edge => edge.Id, StringComparer.Ordinal))
                if (!progression.TraversedEdges.Contains(edge.Id))
                    diagnostics.Add(Fatal("locked_edge_never_traversed", "Locked edge could not be traversed after item collection.", edgeId: edge.Id, hint: "Make its provider reachable before this edge."));
        }
        AddObservationalDiagnostics(candidate, diagnostics);
        return new ValidationReport(CanonicalIdentity.Hash(candidate), Order(diagnostics));
    }

    public static ProgressionReport ReachableWithItems(Candidate candidate)
    {
        var starts = candidate.Graph.Nodes.Where(node => node.Kind == NodeKind.Start).ToArray();
        var goals = candidate.Graph.Nodes.Where(node => node.Kind == NodeKind.Goal).ToArray();
        var startId = starts.Length == 1 ? starts[0].Id : null;
        var goalId = goals.Length == 1 ? goals[0].Id : null;
        var nodes = candidate.Graph.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edgesBySource = candidate.Graph.Edges.Where(edge => nodes.ContainsKey(edge.From) && nodes.ContainsKey(edge.To)).GroupBy(edge => edge.From, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var items = new HashSet<string>(StringComparer.Ordinal);
        var traversed = new HashSet<string>(StringComparer.Ordinal);
        if (startId is null || goalId is null || !nodes.ContainsKey(startId) || !nodes.ContainsKey(goalId)) return new ProgressionReport(startId, goalId, reached, items, traversed);
        reached.Add(startId);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var nodeId in reached.OrderBy(id => id, StringComparer.Ordinal).ToArray())
                if (nodes[nodeId].GrantsItem is { Length: > 0 } item && items.Add(item)) changed = true;
            foreach (var nodeId in reached.OrderBy(id => id, StringComparer.Ordinal).ToArray())
                if (edgesBySource.TryGetValue(nodeId, out var outgoing))
                    foreach (var edge in outgoing)
                        if ((edge.RequiredItem is null || items.Contains(edge.RequiredItem)) && reached.Add(edge.To))
                        {
                            traversed.Add(edge.Id);
                            changed = true;
                        }
                        else if (edge.RequiredItem is null || items.Contains(edge.RequiredItem)) traversed.Add(edge.Id);
        }
        return new ProgressionReport(startId, goalId, reached, items, traversed);
    }

    private static void AddObservationalDiagnostics(Candidate candidate, List<Diagnostic> diagnostics)
    {
        var incoming = candidate.Graph.Edges.GroupBy(edge => edge.To, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var outgoing = candidate.Graph.Edges.GroupBy(edge => edge.From, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var node in candidate.Graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (node.Kind != NodeKind.Goal && !outgoing.ContainsKey(node.Id)) diagnostics.Add(new Diagnostic("non_goal_dead_end", DiagnosticSeverity.Warning, "Non-goal node has no outgoing route.", node.Id, RepairHint: "Add a return or rejoin edge."));
            if (node.Kind != NodeKind.Start && !incoming.ContainsKey(node.Id)) diagnostics.Add(new Diagnostic("orphan_node", DiagnosticSeverity.Warning, "Node has no incoming route.", node.Id, RepairHint: "Add an approach edge or remove the orphan."));
            if (node.Tags.Contains("hub", StringComparer.Ordinal) && incoming.GetValueOrDefault(node.Id) + outgoing.GetValueOrDefault(node.Id) < 3) diagnostics.Add(new Diagnostic("hub_incident_edges_low", DiagnosticSeverity.Warning, "Hub has fewer than three incident edges.", node.Id, RepairHint: "Add spokes and a rejoin."));
        }
    }

    private static Diagnostic Fatal(string code, string detail, string? nodeId = null, string? edgeId = null, string? hint = null) => new(code, DiagnosticSeverity.Fatal, detail, nodeId, edgeId, hint);
    internal static IReadOnlyList<Diagnostic> Order(IEnumerable<Diagnostic> diagnostics) => diagnostics.OrderBy(diagnostic => diagnostic.Severity).ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal).ThenBy(diagnostic => diagnostic.NodeId, StringComparer.Ordinal).ThenBy(diagnostic => diagnostic.EdgeId, StringComparer.Ordinal).ToArray();
}
