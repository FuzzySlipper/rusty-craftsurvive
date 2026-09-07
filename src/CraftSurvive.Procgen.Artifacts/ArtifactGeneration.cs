using System.Security.Cryptography;
using System.Text;
using CraftSurvive.Procgen;
using CraftSurvive.Procgen.Generation;
using CraftSurvive.Procgen.Workloads;

namespace CraftSurvive.Procgen.Artifacts;

/// <summary>Filesystem-free adapter from a strict artifact request to the one
/// authoritative C# generation pipeline.</summary>
public sealed class ArtifactGenerator
{
    private readonly DungeonGenerator _generator;

    public ArtifactGenerator(DungeonGenerator? generator = null) => _generator = generator ?? new DungeonGenerator();

    public ArtifactGenerationResult Generate(ArtifactGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selectedPreset = ArtifactRequestValidator.Validate(request);
        var seed = request.Seed!.Value;
        var result = _generator.Generate(request.Candidate, request.GenerationPolicy, seed, request.Catalog);
        var rejection = result.Accepted
            ? null
            : new ArtifactRejection(result.RejectionStage!, result.RejectionCode!, result.Attempts.Last().Detail);
        var provenance = new ArtifactProvenance(
            "csharp-v1",
            "CraftSurvive.Procgen.Generation.DungeonGenerator",
            CanonicalIdentity.Hash(request.Candidate),
            ArtifactIdentity.HashCatalog(request.Catalog),
            seed,
            selectedPreset,
            request.GenerationPolicy);
        return new ArtifactGenerationResult(
            ArtifactGenerationResult.CurrentKind,
            provenance,
            result.Accepted,
            result.Accepted ? result.Identity : null,
            rejection,
            result.Attempts.ToArray(),
            result.Stages.Select(stage => new ArtifactStageMetric(stage.Stage, stage.Counters)).ToArray(),
            result.Counters,
            result.Metrics,
            result.Artifacts,
            result.BuiltFlow);
    }
}

public static class ArtifactRequestValidator
{
    public static GenerationPreset Validate(ArtifactGenerationRequest request)
    {
        Require(StringComparer.Ordinal.Equals(request.Kind, ArtifactGenerationRequest.CurrentKind), "unsupported_request_kind", $"Request kind must be '{ArtifactGenerationRequest.CurrentKind}'.");
        Require(request.Seed.HasValue, "seed_required", "An explicit unsigned seed is required.");
        Require(request.SelectedPreset is not null && !string.IsNullOrWhiteSpace(request.SelectedPreset.Id), "preset_required", "A complete workload preset selection is required.");
        Require(request.GenerationPolicy is not null, "generation_policy_required", "A concrete generation policy is required.");
        Require(request.Candidate is not null && request.Candidate.Graph is not null, "candidate_required", "A candidate graph is required.");
        var candidate = request.Candidate ?? throw new ArtifactValidationException("candidate_required", "A candidate graph is required.");
        var graph = candidate.Graph ?? throw new ArtifactValidationException("candidate_required", "A candidate graph is required.");
        Require(graph.Nodes is not null && graph.Edges is not null && candidate.Provenance is not null, "candidate_malformed", "Candidate collections must be present.");
        Require(request.Catalog is not null && request.Catalog.Shapes is not null, "catalog_required", "A catalog selection is required.");
        ValidateCandidateShape(candidate);
        ValidateCatalogShape(request.Catalog!);

        var requestedPreset = request.SelectedPreset ?? throw new ArtifactValidationException("preset_required", "A complete workload preset selection is required.");
        var selected = GenerationPresets.All.SingleOrDefault(preset => StringComparer.Ordinal.Equals(preset.Id, requestedPreset.Id));
        Require(selected is not null, "unknown_preset", $"Preset '{requestedPreset.Id}' is not one of the supported complete preset selections.");
        var policy = request.GenerationPolicy ?? throw new ArtifactValidationException("generation_policy_required", "A concrete generation policy is required.");
        var preset = selected ?? throw new ArtifactValidationException("unknown_preset", $"Preset '{requestedPreset.Id}' is not one of the supported complete preset selections.");
        Require(Equals(requestedPreset, preset), "preset_definition_mismatch", "Selected preset must contain the complete current preset definition.");
        Require(StringComparer.Ordinal.Equals(policy.Id, preset.Id), "preset_policy_mismatch", "Generation policy ID must match the selected workload preset ID.");
        ValidatePolicyBounds(policy);
        return preset;
    }

    private static void ValidateCandidateShape(Candidate candidate)
    {
        Require(!string.IsNullOrWhiteSpace(candidate.Id) && !string.IsNullOrWhiteSpace(candidate.IntentId), "candidate_malformed", "Candidate identity and intent are required.");
        foreach (var node in candidate.Graph.Nodes)
        {
            Require(node is not null && !string.IsNullOrWhiteSpace(node.Id) && !string.IsNullOrWhiteSpace(node.Label) && node.Tags is not null, "candidate_malformed", "Every graph node requires an ID, label, and tag collection.");
            var tags = node?.Tags ?? throw new ArtifactValidationException("candidate_malformed", "Every graph node requires a tag collection.");
            Require(tags.All(tag => !string.IsNullOrWhiteSpace(tag)), "candidate_malformed", "Graph node tags must be nonempty.");
        }
        foreach (var edge in candidate.Graph.Edges)
        {
            Require(edge is not null && !string.IsNullOrWhiteSpace(edge.Id) && !string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To) && edge.Tags is not null, "candidate_malformed", "Every graph edge requires IDs and a tag collection.");
            var tags = edge?.Tags ?? throw new ArtifactValidationException("candidate_malformed", "Every graph edge requires a tag collection.");
            Require(tags.All(tag => !string.IsNullOrWhiteSpace(tag)), "candidate_malformed", "Graph edge tags must be nonempty.");
        }
        foreach (var step in candidate.Provenance)
            Require(step is not null && !string.IsNullOrWhiteSpace(step.Operation) && !string.IsNullOrWhiteSpace(step.Summary), "candidate_malformed", "Every provenance step requires an operation and summary.");
    }

    private static void ValidateCatalogShape(ShapeCatalog catalog)
    {
        Require(!string.IsNullOrWhiteSpace(catalog.Id), "catalog_malformed", "Catalog identity is required.");
        foreach (var shape in catalog.Shapes)
        {
            Require(shape is not null && !string.IsNullOrWhiteSpace(shape.Id) && shape.WalkableCells is not null && shape.Exits is not null, "catalog_malformed", "Every catalog shape requires an ID, cells, and exits.");
            var exits = shape?.Exits ?? throw new ArtifactValidationException("catalog_malformed", "Every catalog shape requires exits.");
            var sockets = shape?.Sockets ?? [];
            Require(exits.All(exit => exit is not null && !string.IsNullOrWhiteSpace(exit.Id)), "catalog_malformed", "Catalog exits require IDs.");
            Require(sockets.All(socket => socket is not null && !string.IsNullOrWhiteSpace(socket.Id) && !string.IsNullOrWhiteSpace(socket.Kind)), "catalog_malformed", "Catalog sockets require IDs and kinds.");
        }
    }

    private static void ValidatePolicyBounds(GenerationPolicy policy)
    {
        Require(!string.IsNullOrWhiteSpace(policy.Id), "invalid_generation_policy", "Generation policy requires an ID.");
        Require(GenerationPolicyValidation.IsValid(policy), "invalid_generation_policy", "Generation policy must satisfy the current complete Core bounds, including a stride no greater than 16384.");
    }

    private static void Require(bool condition, string code, string detail)
    {
        if (!condition) throw new ArtifactValidationException(code, detail);
    }
}

public static class ArtifactIdentity
{
    public static string HashRequest(ArtifactGenerationRequest request) => HashBytes(ArtifactJson.SerializeRequest(request));
    public static string HashResult(ArtifactGenerationResult result) => HashBytes(ArtifactJson.SerializeResult(result));
    public static string HashScenarioSuite(CellularScenarioSuite suite) => HashBytes(ArtifactJson.SerializeScenarioSuite(suite));
    public static string HashCellularTrace(CellularTrace trace) => HashBytes(ArtifactJson.SerializeCellularTrace(trace));
    public static string HashWorkloadCorpus(CellularWorkloadCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        return HashBytes(ArtifactJson.SerializeWorkloadCorpusIdentityPayload(new(corpus.Kind, corpus.CorpusId, corpus.Provenance, corpus.Suite, corpus.Traces)));
    }
    public static string HashCatalog(ShapeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var builder = new StringBuilder();
        void Add(params object?[] values) => builder.AppendJoin('|', values.Select(value => value?.ToString() ?? string.Empty)).Append('\n');
        Add(catalog.Id);
        foreach (var shape in catalog.Shapes.OrderBy(shape => shape.Id, StringComparer.Ordinal))
        {
            Add("shape", shape.Id);
            foreach (var cell in shape.WalkableCells.OrderBy(cell => cell.X).ThenBy(cell => cell.Y)) Add("cell", cell.X, cell.Y);
            foreach (var exit in shape.Exits.OrderBy(exit => exit.Id, StringComparer.Ordinal)) Add("exit", exit.Id, exit.Cell.X, exit.Cell.Y, exit.Direction, string.Join(',', (exit.Tags ?? []).OrderBy(tag => tag, StringComparer.Ordinal)));
            foreach (var socket in (shape.Sockets ?? []).OrderBy(socket => socket.Id, StringComparer.Ordinal)) Add("socket", socket.Id, socket.Cell.X, socket.Cell.Y, socket.Kind, string.Join(',', (socket.Tags ?? []).OrderBy(tag => tag, StringComparer.Ordinal)));
            Add("tags", string.Join(',', (shape.Tags ?? []).OrderBy(tag => tag, StringComparer.Ordinal)));
        }
        return HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
