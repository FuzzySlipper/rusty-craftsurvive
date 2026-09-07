namespace CraftSurvive.Procgen;

public static class GraphAnalytics
{
    public static GraphAnalysis Analyze(Candidate candidate)
    {
        var starts = candidate.Graph.Nodes.Where(node => node.Kind == NodeKind.Start).Select(node => node.Id).ToArray();
        var goals = candidate.Graph.Nodes.Where(node => node.Kind == NodeKind.Goal).Select(node => node.Id).ToArray();
        var start = starts.Length == 1 ? starts[0] : null;
        var goal = goals.Length == 1 ? goals[0] : null;
        var path = start is not null && goal is not null ? ShortestPath(candidate, start, goal, null) : Array.Empty<string>();
        var deadEnds = candidate.Graph.Nodes.Where(node => node.Kind != NodeKind.Goal && !candidate.Graph.Edges.Any(edge => edge.From == node.Id)).Select(node => node.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var dominators = start is not null && goal is not null
            ? candidate.Graph.Nodes.Where(node => node.Id != start && node.Id != goal).Where(node => path.Count > 0 && ShortestPath(candidate, start, goal, node.Id).Count == 0).Select(node => node.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var lockedItems = candidate.Graph.Edges.Select(edge => edge.RequiredItem).Where(item => item is not null).Select(item => item!).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var metrics = Metrics(candidate, path.Count == 0 ? 0 : path.Count - 1, deadEnds.Length);
        return new GraphAnalysis(CanonicalIdentity.Hash(candidate), path, dominators, deadEnds, lockedItems, metrics);
    }

    public static ScoreReport Score(Candidate candidate, GraphTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        var analysis = Analyze(candidate);
        decimal Get(string name) => analysis.Metrics[name];
        var score = 10m
            + Math.Min(8m, Get("critical_path_length")) * tuning.CriticalPathWeight
            + Math.Min(8m, Get("loop_count")) * tuning.LoopWeight
            + Math.Min(10m, Get("optional_branch_count")) * tuning.OptionalWeight
            + Math.Min(4m, Get("locked_edge_count")) * tuning.LockedEdgeWeight
            - Get("dead_end_count") * tuning.DeadEndPenalty;
        return new ScoreReport(analysis.CanonicalHash, decimal.Round(decimal.Clamp(score, 0m, 100m), 2), analysis.Metrics);
    }

    private static IReadOnlyDictionary<string, decimal> Metrics(Candidate candidate, int criticalPath, int deadEnds)
    {
        var nodeCount = candidate.Graph.Nodes.Count;
        var edgeCount = candidate.Graph.Edges.Count;
        var connectedComponents = nodeCount == 0 ? 0 : 1;
        var loops = Math.Max(0L, (long)edgeCount - nodeCount + connectedComponents);
        return new SortedDictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["critical_path_length"] = criticalPath,
            ["dead_end_count"] = deadEnds,
            ["edge_count"] = edgeCount,
            ["locked_edge_count"] = candidate.Graph.Edges.Count(edge => edge.Traversal == TraversalKind.Locked),
            ["loop_count"] = loops,
            ["node_count"] = nodeCount,
            ["optional_branch_count"] = candidate.Graph.Edges.Count(edge => edge.Kind == EdgeKind.OptionalBranch),
            ["shortcut_count"] = candidate.Graph.Edges.Count(edge => edge.Kind == EdgeKind.Shortcut),
        };
    }

    private static IReadOnlyList<string> ShortestPath(Candidate candidate, string start, string goal, string? avoid)
    {
        if (start == avoid || goal == avoid) return Array.Empty<string>();
        var adjacency = candidate.Graph.Edges.Where(edge => edge.From != avoid && edge.To != avoid).GroupBy(edge => edge.From, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var queue = new Queue<string>();
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == goal)
            {
                var path = new List<string>();
                for (var cursor = goal; ; cursor = previous[cursor]) { path.Add(cursor); if (cursor == start) break; }
                path.Reverse();
                return path;
            }
            if (!adjacency.TryGetValue(current, out var outgoing)) continue;
            foreach (var edge in outgoing)
                if (seen.Add(edge.To)) { previous[edge.To] = current; queue.Enqueue(edge.To); }
        }
        return Array.Empty<string>();
    }
}

public static class GraphRepairs
{
    public static IReadOnlyList<RepairSuggestion> Suggest(ValidationReport report) => report.Diagnostics
        .Select(diagnostic => new RepairSuggestion(diagnostic.Code, diagnostic.Severity, diagnostic.Detail, Actions(diagnostic)))
        .OrderBy(suggestion => suggestion.Severity).ThenBy(suggestion => suggestion.Code, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> Actions(Diagnostic diagnostic) => diagnostic.Code switch
    {
        "goal_unreachable" => new[] { "Reconnect start to goal with an item-feasible route.", "Move the key or resource provider before its lock." },
        "locked_edge_never_traversed" => new[] { "Move the provider branch ahead of the locked edge.", "Add an open route to the provider." },
        "required_item_unavailable" => new[] { "Add a key or resource node that grants the required item." },
        "edge_from_missing" or "edge_to_missing" => new[] { "Create the missing endpoint or remove the edge." },
        "duplicate_node_id" or "duplicate_edge_id" => new[] { "Use a unique deterministic identity." },
        "node_quota_exceeded" or "edge_quota_exceeded" => new[] { "Raise the named budget or choose a smaller graph rule sequence." },
        _ when diagnostic.RepairHint is not null => new[] { diagnostic.RepairHint },
        _ => Array.Empty<string>(),
    };
}
