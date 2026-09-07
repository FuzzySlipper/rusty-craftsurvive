using System.Collections.Immutable;

namespace CraftSurvive.Procgen;

public sealed record SeedIntent(
    string Id,
    string Title,
    IReadOnlyList<string> DesiredPatterns,
    IReadOnlyList<string>? Notes = null)
{
    public ImmutableArray<string> OrderedPatterns => DesiredPatterns.OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
}

public enum NodeKind { Start, Goal, Gate, Key, Treasure, Shortcut, Hazard, Resource, Junction }
public enum EdgeKind { CriticalPath, KeyBranch, OptionalBranch, Shortcut }
public enum TraversalKind { Open, Locked, OneWayReturn, Hidden }
public enum GraphRule { LockKeyLoop, DetourLoop, HubSpoke, HazardResource, BossPreparation, GatedBranch, Shortcut }
public enum DiagnosticSeverity { Info, Warning, Fatal }

public sealed record GraphNode(
    string Id,
    NodeKind Kind,
    string Label,
    IReadOnlyList<string> Tags,
    string? GrantsItem = null);

public sealed record GraphEdge(
    string Id,
    string From,
    string To,
    EdgeKind Kind,
    TraversalKind Traversal,
    IReadOnlyList<string> Tags,
    string? RequiredItem = null);

public sealed record ProvenanceStep(int Step, string Operation, ulong Seed, string Summary);

public sealed record CandidateGraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)
{
    public static CandidateGraph Initial() => new(
        new[]
        {
            new GraphNode("start", NodeKind.Start, "Start", new[] { "critical" }),
            new GraphNode("goal", NodeKind.Goal, "Goal", new[] { "critical" }),
        },
        new[]
        {
            new GraphEdge("edge.start.goal", "start", "goal", EdgeKind.CriticalPath, TraversalKind.Open, new[] { "initial" }),
        });
}

public sealed record Candidate(
    string Id,
    ulong Seed,
    string IntentId,
    CandidateGraph Graph,
    IReadOnlyList<ProvenanceStep> Provenance)
{
    public static Candidate Create(SeedIntent intent, ulong seed) => new(
        $"candidate.{intent.Id}.{seed}", seed, intent.Id, CandidateGraph.Initial(),
        new[] { new ProvenanceStep(1, "initialize", seed, $"Initialized from {intent.Id}") });
}

public sealed record GraphBudget(int MaxNodes, int MaxEdges, int MaxRuleApplications)
{
    public static GraphBudget Default { get; } = new(64, 128, 24);
}

public sealed record RepairBudget(int MaxActions)
{
    public static RepairBudget Default { get; } = new(8);
}

public sealed record GraphTuning(
    decimal CriticalPathWeight,
    decimal LoopWeight,
    decimal OptionalWeight,
    decimal LockedEdgeWeight,
    decimal DeadEndPenalty)
{
    public static GraphTuning Default { get; } = new(2.5m, 1.8m, 1.2m, 2.5m, 4.0m);
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Detail,
    string? NodeId = null,
    string? EdgeId = null,
    string? RepairHint = null);

public sealed record ValidationReport(string CanonicalHash, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Fatal);
    public int FatalCount => Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Fatal);
}

public sealed record ProgressionReport(
    string? StartNodeId,
    string? GoalNodeId,
    IReadOnlySet<string> ReachedNodes,
    IReadOnlySet<string> CollectedItems,
    IReadOnlySet<string> TraversedEdges)
{
    public bool GoalReached => GoalNodeId is not null && ReachedNodes.Contains(GoalNodeId);
}

public sealed record RuleApplication(Candidate Candidate, bool Accepted, IReadOnlyList<Diagnostic> Diagnostics)
{
    public string CanonicalHash => CanonicalIdentity.Hash(Candidate);
}

public sealed record GraphAnalysis(
    string CanonicalHash,
    IReadOnlyList<string> CriticalPath,
    IReadOnlyList<string> Dominators,
    IReadOnlyList<string> DeadEnds,
    IReadOnlyList<string> LockedItems,
    IReadOnlyDictionary<string, decimal> Metrics);

public sealed record ScoreReport(string CanonicalHash, decimal Overall, IReadOnlyDictionary<string, decimal> Metrics);

public sealed record RepairSuggestion(string Code, DiagnosticSeverity Severity, string Detail, IReadOnlyList<string> Actions);

public enum RepairAction { ReconnectDeadEndToGoal, RemoveOrphanNode, RemoveInvalidEdge }

public sealed record RepairRequest(RepairAction Action, string TargetId, ulong Seed);

public sealed record RepairApplication(Candidate Candidate, bool Accepted, IReadOnlyList<Diagnostic> Diagnostics)
{
    public string CanonicalHash => CanonicalIdentity.Hash(Candidate);
}
