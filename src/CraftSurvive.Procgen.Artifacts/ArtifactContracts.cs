using CraftSurvive.Procgen;
using CraftSurvive.Procgen.Generation;
using CraftSurvive.Procgen.Workloads;

namespace CraftSurvive.Procgen.Artifacts;

/// <summary>
/// Explicit offline input. The preset carries the broader search/trace/CA
/// selection; <see cref="GenerationPolicy"/> is the complete concrete policy
/// used by <see cref="DungeonGenerator"/> for this dungeon run.
/// </summary>
public sealed record ArtifactGenerationRequest(
    string Kind,
    ulong? Seed,
    GenerationPreset SelectedPreset,
    GenerationPolicy GenerationPolicy,
    Candidate Candidate,
    ShapeCatalog Catalog)
{
    public const string CurrentKind = "craftsurvive_procgen.csharp_generation_request.v1";
}

public sealed record ArtifactProvenance(
    string SchemaVersion,
    string Generator,
    string CandidateHash,
    string CatalogHash,
    ulong Seed,
    GenerationPreset SelectedPreset,
    GenerationPolicy GenerationPolicy);

public sealed record ArtifactRejection(string Stage, string Code, string Detail);

/// <summary>Stable stage observations. Wall-clock timings stay out of a durable,
/// deterministic artifact; counters and generation metrics remain inspectable.</summary>
public sealed record ArtifactStageMetric(string Stage, GenerationCounters Counters);

public sealed record ArtifactGenerationResult(
    string Kind,
    ArtifactProvenance Provenance,
    bool Accepted,
    string? ResultIdentity,
    ArtifactRejection? Rejection,
    IReadOnlyList<GenerationAttempt> Attempts,
    IReadOnlyList<ArtifactStageMetric> Stages,
    GenerationCounters Counters,
    GenerationMetrics Metrics,
    DungeonArtifacts? Dungeon,
    BuiltFlowReport? BuiltFlow)
{
    public const string CurrentKind = "craftsurvive_procgen.csharp_dungeon_generation.v1";
}

public sealed record ArtifactReceipt(
    string Kind,
    string Command,
    bool Accepted,
    int ExitCode,
    string RequestHash,
    string ResultHash,
    string ResultPath,
    string ReceiptPath,
    string? ResultIdentity,
    ArtifactRejection? Rejection)
{
    public const string CurrentKind = "craftsurvive_procgen.csharp_receipt.v1";
}

public sealed class ArtifactValidationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class ArtifactIoException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}
