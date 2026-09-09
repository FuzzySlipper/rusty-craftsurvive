using System.Text.Json;
using System.Text.Json.Serialization;
using CraftSurvive.Procgen.Workbench;

namespace CraftSurvive.Procgen.Artifacts;

/// <summary>Strict durable codec for the bounded workbench experiment.</summary>
public static class WorkbenchCandidateJson
{
    private const int MaximumPayloadBytes = 64 * 1024;

    public static byte[] Serialize(WorkbenchCandidate candidate)
    {
        RequireValid(candidate);
        return JsonSerializer.SerializeToUtf8Bytes(candidate, WorkbenchCandidateJsonContext.Default.WorkbenchCandidate);
    }

    public static WorkbenchCandidate Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) throw new ArtifactValidationException("workbench_candidate_empty", "Workbench candidate JSON must contain an object.");
        if (payload.Length > MaximumPayloadBytes) throw new ArtifactValidationException("workbench_candidate_too_large", "Workbench candidate JSON exceeds the bounded artifact size.");
        try
        {
            var candidate = JsonSerializer.Deserialize(payload, WorkbenchCandidateJsonContext.Default.WorkbenchCandidate)
                ?? throw new ArtifactValidationException("workbench_candidate_empty", "Workbench candidate JSON must contain an object.");
            RequireValid(candidate);
            return candidate;
        }
        catch (ArtifactValidationException) { throw; }
        catch (JsonException exception) { throw new ArtifactValidationException("invalid_workbench_candidate_json", exception.Message); }
    }

    public static string Identity(WorkbenchCandidate candidate) => ArtifactIdentity.HashBytes(Serialize(candidate));

    private static void RequireValid(WorkbenchCandidate candidate)
    {
        var errors = WorkbenchExperiment.Validate(candidate);
        if (errors.Length > 0) throw new ArtifactValidationException("workbench_candidate_invalid", $"Workbench candidate is invalid: {string.Join(", ", errors)}.");
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(WorkbenchCandidate))]
internal partial class WorkbenchCandidateJsonContext : JsonSerializerContext;
