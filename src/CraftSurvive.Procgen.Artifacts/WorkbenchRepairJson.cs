using System.Text.Json;
using System.Text.Json.Serialization;
using CraftSurvive.Procgen.Workbench;

namespace CraftSurvive.Procgen.Artifacts;

/// <summary>Bounded behavioral facts retained with a canonical workbench repair.</summary>
public sealed record WorkbenchRepairAnalysis(
    bool Completable,
    int ReachableStates,
    int UnrecoverableStates,
    string[] FailedRequirements);

/// <summary>Durable provenance for exactly one canonical workbench repair.</summary>
public sealed record WorkbenchRepairReceipt(
    string Schema,
    WorkbenchCandidate Parent,
    WorkbenchCandidate Result,
    string ParentIdentity,
    string ResultIdentity,
    string Operation,
    int Cost,
    string ChangedField,
    WorkbenchRepairAnalysis Before,
    WorkbenchRepairAnalysis After)
{
    public const string CurrentSchema = "craftsurvive.workbench_repair_receipt.v1";
}

/// <summary>Strict codec and verifier for canonical workbench repair receipts.</summary>
public static class WorkbenchRepairJson
{
    private const int MaximumPayloadBytes = 128 * 1024;

    public static WorkbenchRepairReceipt Create(WorkbenchCandidate parent, string operation)
    {
        var result = WorkbenchRepair.Apply(parent, operation);
        return new WorkbenchRepairReceipt(
            WorkbenchRepairReceipt.CurrentSchema,
            parent,
            result,
            WorkbenchCandidateJson.Identity(parent),
            WorkbenchCandidateJson.Identity(result),
            operation,
            1,
            WorkbenchRepair.ChangedField(operation),
            Analyze(parent),
            Analyze(result));
    }

    public static byte[] Serialize(WorkbenchRepairReceipt receipt)
    {
        RequireValid(receipt);
        return JsonSerializer.SerializeToUtf8Bytes(receipt, WorkbenchRepairJsonContext.Default.WorkbenchRepairReceipt);
    }

    public static WorkbenchRepairReceipt Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) throw new ArtifactValidationException("workbench_repair_empty", "Workbench repair receipt JSON must contain an object.");
        if (payload.Length > MaximumPayloadBytes) throw new ArtifactValidationException("workbench_repair_too_large", "Workbench repair receipt JSON exceeds the bounded artifact size.");
        try
        {
            var receipt = JsonSerializer.Deserialize(payload, WorkbenchRepairJsonContext.Default.WorkbenchRepairReceipt)
                ?? throw new ArtifactValidationException("workbench_repair_empty", "Workbench repair receipt JSON must contain an object.");
            RequireValid(receipt);
            return receipt;
        }
        catch (ArtifactValidationException) { throw; }
        catch (JsonException exception) { throw new ArtifactValidationException("invalid_workbench_repair_json", exception.Message); }
        catch (InvalidOperationException exception) { throw new ArtifactValidationException("workbench_repair_invalid", exception.Message); }
    }

    private static WorkbenchRepairAnalysis Analyze(WorkbenchCandidate candidate)
    {
        var analysis = WorkbenchExperiment.Analyze(candidate);
        return new WorkbenchRepairAnalysis(
            analysis.Completable,
            analysis.ReachableStates,
            analysis.UnrecoverableStates,
            analysis.Contracts.Where(contract => !contract.Passed).Select(contract => contract.Requirement).ToArray());
    }

    private static void RequireValid(WorkbenchRepairReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!StringComparer.Ordinal.Equals(receipt.Schema, WorkbenchRepairReceipt.CurrentSchema))
            throw new ArtifactValidationException("workbench_repair_schema_invalid", $"Workbench repair receipt schema must be '{WorkbenchRepairReceipt.CurrentSchema}'.");
        if (receipt.Parent is null || receipt.Result is null || receipt.Before is null || receipt.After is null
            || receipt.Before.FailedRequirements is null || receipt.After.FailedRequirements is null)
            throw new ArtifactValidationException("workbench_repair_invalid", "Workbench repair receipt requires parent, result, before/after analyses, and failed requirement collections.");
        WorkbenchRepairReceipt expected;
        try
        {
            expected = Create(receipt.Parent, receipt.Operation);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArtifactValidationException("workbench_repair_invalid", exception.Message);
        }
        if (!Equivalent(expected, receipt))
            throw new ArtifactValidationException("workbench_repair_mismatch", "Workbench repair receipt does not reproduce the canonical repaired result and provenance.");
    }

    private static bool Equivalent(WorkbenchRepairReceipt expected, WorkbenchRepairReceipt actual) =>
        CandidatesEqual(expected.Parent, actual.Parent)
        && CandidatesEqual(expected.Result, actual.Result)
        && StringComparer.Ordinal.Equals(expected.Schema, actual.Schema)
        && StringComparer.Ordinal.Equals(expected.ParentIdentity, actual.ParentIdentity)
        && StringComparer.Ordinal.Equals(expected.ResultIdentity, actual.ResultIdentity)
        && StringComparer.Ordinal.Equals(expected.Operation, actual.Operation)
        && expected.Cost == actual.Cost
        && StringComparer.Ordinal.Equals(expected.ChangedField, actual.ChangedField)
        && AnalysesEqual(expected.Before, actual.Before)
        && AnalysesEqual(expected.After, actual.After);

    private static bool CandidatesEqual(WorkbenchCandidate expected, WorkbenchCandidate actual) =>
        WorkbenchCandidateJson.Serialize(expected).AsSpan().SequenceEqual(WorkbenchCandidateJson.Serialize(actual));

    private static bool AnalysesEqual(WorkbenchRepairAnalysis expected, WorkbenchRepairAnalysis actual) =>
        expected.Completable == actual.Completable
        && expected.ReachableStates == actual.ReachableStates
        && expected.UnrecoverableStates == actual.UnrecoverableStates
        && expected.FailedRequirements.SequenceEqual(actual.FailedRequirements, StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(WorkbenchRepairReceipt))]
[JsonSerializable(typeof(WorkbenchRepairAnalysis))]
[JsonSerializable(typeof(WorkbenchCandidate))]
internal partial class WorkbenchRepairJsonContext : JsonSerializerContext;
