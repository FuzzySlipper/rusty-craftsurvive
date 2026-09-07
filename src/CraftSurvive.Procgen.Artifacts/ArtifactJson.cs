using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CraftSurvive.Procgen.Workloads;

namespace CraftSurvive.Procgen.Artifacts;

public static class ArtifactJson
{
    public static ArtifactGenerationRequest DeserializeRequest(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, ProcgenArtifactJsonContext.Default.ArtifactGenerationRequest)
                ?? throw new ArtifactValidationException("request_empty", "Request JSON must contain an object.");
        }
        catch (ArtifactValidationException) { throw; }
        catch (JsonException exception) { throw new ArtifactValidationException("invalid_request_json", exception.Message); }
    }

    public static CellularWorkloadCorpus DeserializeWorkloadCorpus(ReadOnlySpan<byte> payload)
    {
        try
        {
            var corpus = JsonSerializer.Deserialize(payload, ProcgenArtifactJsonContext.Default.CellularWorkloadCorpus)
                ?? throw new ArtifactValidationException("workload_corpus_empty", "Workload corpus JSON must contain an object.");
            WorkloadCorpusValidator.Validate(corpus);
            return corpus;
        }
        catch (ArtifactValidationException) { throw; }
        catch (JsonException exception) { throw new ArtifactValidationException("invalid_workload_corpus_json", exception.Message); }
    }

    public static byte[] SerializeRequest(ArtifactGenerationRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, ProcgenArtifactJsonContext.Default.ArtifactGenerationRequest);
    public static byte[] SerializeResult(ArtifactGenerationResult result) => JsonSerializer.SerializeToUtf8Bytes(result, ProcgenArtifactJsonContext.Default.ArtifactGenerationResult);
    public static byte[] SerializeReceipt(ArtifactReceipt receipt) => JsonSerializer.SerializeToUtf8Bytes(receipt, ProcgenArtifactJsonContext.Default.ArtifactReceipt);
    public static byte[] SerializeWorkloadCorpus(CellularWorkloadCorpus corpus) => JsonSerializer.SerializeToUtf8Bytes(corpus, ProcgenArtifactJsonContext.Default.CellularWorkloadCorpus);
    internal static byte[] SerializeWorkloadCorpusIdentityPayload(CellularWorkloadCorpusIdentityPayload payload) => JsonSerializer.SerializeToUtf8Bytes(payload, ProcgenArtifactJsonContext.Default.CellularWorkloadCorpusIdentityPayload);
    internal static byte[] SerializeScenarioSuite(CellularScenarioSuite suite) => JsonSerializer.SerializeToUtf8Bytes(suite, ProcgenArtifactJsonContext.Default.CellularScenarioSuite);
    internal static byte[] SerializeCellularTrace(CellularTrace trace) => JsonSerializer.SerializeToUtf8Bytes(trace, ProcgenArtifactJsonContext.Default.CellularTrace);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ArtifactGenerationRequest))]
[JsonSerializable(typeof(ArtifactGenerationResult))]
[JsonSerializable(typeof(ArtifactReceipt))]
[JsonSerializable(typeof(CellularWorkloadCorpus))]
[JsonSerializable(typeof(CellularWorkloadCorpusIdentityPayload))]
[JsonSerializable(typeof(CellularScenarioSuite))]
[JsonSerializable(typeof(CellularTrace))]
internal partial class ProcgenArtifactJsonContext : JsonSerializerContext;
