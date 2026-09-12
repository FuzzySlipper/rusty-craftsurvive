using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CraftSurvive.Procgen.Workbench;

namespace CraftSurvive.Procgen.Artifacts;

public sealed record TrialGeometry(WorkbenchRoom[] Rooms, WorkbenchRoute[] Routes);
public sealed record TrialEntry(string Id, string Identity, WorkbenchCandidate Candidate);
public sealed record TrialBank(string Schema, string Identity, TrialEntry[] Entries);
public sealed record TrialChoice(string CandidateId, string Operation, string Reason);
public sealed record TrialSubmission(string Schema, string BankIdentity, string Lane, string Agent, string Model, TrialChoice[] Choices);
public sealed record TrialObservation(string Id, string Identity, string Motif, bool Accepted, string[] FailedRequirements,
    string[] Operations, string GeometryIdentity, string RoutePatternIdentity, WorkbenchAnalysis Analysis);
public sealed record TrialResult(string CandidateId, string ParentIdentity, string ResultIdentity, string Motif,
    string Operation, int RepairCost, bool Accepted, string GeometryIdentity, string RoutePatternIdentity,
    string[] FailedRequirements, WorkbenchCounterexample[] Counterexamples, WorkbenchRepairReceipt? Repair);
public sealed record TrialEvaluation(string Schema, string Protocol, string BankIdentity, string Lane, string Agent, string Model,
    TrialResult[] Results, int Accepted, int DistinctAcceptedCandidates, int AcceptedMotifs, int DistinctGeometries,
    int DistinctRoutePatterns, int RepairCost, double? RepairFieldsPerAcceptedCandidate,
    string PhysicalAcceptance, string HumanCalibration, double? CostPerEnjoyableLevel);

/// <summary>Fixed protocol for a small offline comparison. It delegates generation,
/// repair and acceptance to their existing owners and never accepts new rules in a submission.</summary>
public static class WorkbenchTrial
{
    public const string Protocol = "workbench-selection-repair.v1";
    public const string BankSchema = "craftsurvive.workbench_trial_bank.v1";
    public const string SubmissionSchema = "craftsurvive.workbench_trial_submission.v1";
    public const string EvaluationSchema = "craftsurvive.workbench_trial_evaluation.v1";
    public const int MaximumEntries = 16;
    private const int RequiredChoices = 3;
    private const int MaximumReasonLength = 1200;
    private static readonly string[] Motifs = [WorkbenchExperiment.CurrentMotif, WorkbenchExperiment.RecoveryMotif, WorkbenchExperiment.PreviewMotif];

    public static TrialBank Generate()
    {
        // Two geometries, three mechanics, each with passing/failing counterparts.
        // Neutral IDs are bookkeeping, not a claim of blindness to candidate facts.
        List<WorkbenchCandidate> candidates = [];
        foreach (string motif in Motifs)
            foreach (ulong seed in new ulong[] { 11, 29 })
                foreach (bool failure in new[] { false, true })
                    candidates.Add(WorkbenchExperiment.Generate(seed, motif, failure));
        int[] order = [7, 0, 9, 2, 5, 10, 1, 8, 3, 6, 11, 4];
        TrialEntry[] entries = order.Select((index, ordinal) => new TrialEntry($"C{ordinal + 1:00}",
            WorkbenchCandidateJson.Identity(candidates[index]), candidates[index])).ToArray();
        return new(BankSchema, BankIdentity(entries), entries);
    }

    public static TrialObservation[] Inspect(TrialBank bank)
    {
        Validate(bank);
        return bank.Entries.Select(entry =>
        {
            WorkbenchAnalysis analysis = WorkbenchExperiment.Analyze(entry.Candidate);
            return new TrialObservation(entry.Id, entry.Identity, entry.Candidate.Motif, Accept(analysis),
                Failures(analysis), WorkbenchRepair.Operations(entry.Candidate), GeometryIdentity(entry.Candidate),
                RoutePatternIdentity(entry.Candidate), analysis);
        }).ToArray();
    }

    public static TrialSubmission Baseline(TrialBank bank)
    {
        TrialObservation[] observations = Inspect(bank);
        return new(SubmissionSchema, bank.Identity, "deterministic", "fixed first-passing-by-id", "none",
            Motifs.Select(motif => new TrialChoice(observations.Where(o => o.Motif == motif && o.Accepted)
                .OrderBy(o => o.Id, StringComparer.Ordinal).First().Id, "", "First contract-passing ID for this motif; no edits.")).ToArray());
    }

    public static TrialEvaluation Evaluate(TrialBank bank, TrialSubmission submission)
    {
        Validate(bank);
        Require(submission is not null && submission.Schema == SubmissionSchema, "submission_schema", "Unsupported trial submission.");
        Require(submission!.BankIdentity == bank.Identity, "bank_identity", "Submission does not name this exact bank.");
        Require(submission.Lane is "deterministic" or "selection" or "editing" or "repair-challenge", "lane", "Unknown trial lane.");
        Require(!string.IsNullOrWhiteSpace(submission.Agent) && submission.Agent.Length <= 160
            && !string.IsNullOrWhiteSpace(submission.Model) && submission.Model.Length <= 160, "agent", "Name the submitting agent and configured model.");
        Require(submission.Choices is { Length: RequiredChoices }, "choice_count", "Choose exactly one candidate for each of the three motifs.");
        HashSet<string> motifs = new(StringComparer.Ordinal);
        List<TrialResult> results = [];
        foreach (TrialChoice choice in submission.Choices!)
        {
            Require(choice is not null && choice.Operation is not null && !string.IsNullOrWhiteSpace(choice.Reason)
                && choice.Reason.Length <= MaximumReasonLength, "choice", "A choice needs an operation string and bounded reason.");
            TrialEntry? entry = bank.Entries.SingleOrDefault(e => e.Id == choice!.CandidateId);
            Require(entry is not null, "candidate", "Choice is not in the retained bank.");
            Require(motifs.Add(entry!.Candidate.Motif), "motif_duplicate", "Only one choice per motif is allowed.");
            bool canEdit = submission.Lane is "editing" or "repair-challenge";
            Require(canEdit || choice!.Operation.Length == 0, "edit_forbidden", "Selection and deterministic lanes cannot edit candidates.");
            WorkbenchCandidate result = entry.Candidate;
            WorkbenchRepairReceipt? receipt = null;
            if (choice!.Operation.Length > 0)
            {
                Require(WorkbenchRepair.Operations(result).Contains(choice.Operation, StringComparer.Ordinal), "operation", "Repair is not applicable.");
                receipt = WorkbenchRepairJson.Create(result, choice.Operation);
                result = receipt.Result;
            }
            if (submission.Lane == "repair-challenge")
                Require(!Accept(WorkbenchExperiment.Analyze(entry.Candidate)) && receipt is not null,
                    "challenge", "The separate repair challenge requires a failing parent and one applicable repair.");
            WorkbenchAnalysis analysis = WorkbenchExperiment.Analyze(result);
            results.Add(new(entry.Id, entry.Identity, WorkbenchCandidateJson.Identity(result), result.Motif,
                choice.Operation, receipt?.Cost ?? 0, Accept(analysis), GeometryIdentity(result), RoutePatternIdentity(result),
                Failures(analysis), analysis.Counterexamples, receipt));
        }
        TrialResult[] accepted = results.Where(r => r.Accepted).ToArray();
        int distinct = accepted.Select(r => r.ResultIdentity).Distinct(StringComparer.Ordinal).Count();
        int cost = results.Sum(r => r.RepairCost);
        return new(EvaluationSchema, Protocol, bank.Identity, submission.Lane, submission.Agent, submission.Model, results.ToArray(),
            accepted.Length, distinct, accepted.Select(r => r.Motif).Distinct(StringComparer.Ordinal).Count(),
            accepted.Select(r => r.GeometryIdentity).Distinct(StringComparer.Ordinal).Count(),
            accepted.Select(r => r.RoutePatternIdentity).Distinct(StringComparer.Ordinal).Count(), cost,
            distinct == 0 ? null : (double)cost / distinct,
            "Not evaluated by this offline report; use recorded Engine/world observations.",
            "Pending blind human ratings; technical acceptance does not establish enjoyment or perceived diversity.", null);
    }

    public static void Validate(TrialBank bank)
    {
        Require(bank is not null && bank.Schema == BankSchema, "bank_schema", "Unsupported trial bank.");
        Require(bank!.Entries is { Length: > 0 and <= MaximumEntries }, "bank_size", "Bank must contain 1–16 resolved candidates.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (TrialEntry entry in bank.Entries!)
        {
            Require(entry is not null && entry.Id is { Length: > 0 and <= 32 } && entry.Id.All(char.IsAsciiLetterOrDigit)
                && ids.Add(entry.Id), "entry_id", "Bank entries need unique bounded alphanumeric IDs.");
            Require(entry!.Candidate is not null && Motifs.Contains(entry.Candidate.Motif, StringComparer.Ordinal), "motif", "Trial uses the three small motifs.");
            Require(entry.Identity == WorkbenchCandidateJson.Identity(entry.Candidate!), "entry_identity", "Resolved candidate identity differs from the bank.");
        }
        Require(bank.Identity == BankIdentity(bank.Entries), "bank_identity", "Retained bank identity is invalid.");
    }

    // Resolved rooms/routes only: provenance, mechanic labels and repairable flags
    // are not counted as room/passage structural variation.
    public static string GeometryIdentity(WorkbenchCandidate candidate) => ArtifactIdentity.HashBytes(
        JsonSerializer.SerializeToUtf8Bytes(new TrialGeometry(candidate.Rooms, candidate.Routes), TrialJson.Default.TrialGeometry));
    public static string RoutePatternIdentity(WorkbenchCandidate candidate) => ArtifactIdentity.HashBytes(Encoding.UTF8.GetBytes(
        string.Join("|", candidate.Routes.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.From}>{r.To}:{r.RequiresSwitch}"))));
    private static string BankIdentity(TrialEntry[] entries) => ArtifactIdentity.HashBytes(Encoding.UTF8.GetBytes(
        Protocol + "\n" + string.Join("\n", entries.Select(e => e.Id + ":" + e.Identity))));
    private static bool Accept(WorkbenchAnalysis analysis) => analysis.Completable && analysis.UnrecoverableStates == 0 && analysis.Contracts.All(c => c.Passed);
    private static string[] Failures(WorkbenchAnalysis analysis) => analysis.Contracts.Where(c => !c.Passed).Select(c => c.Requirement).ToArray();
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string code, string message)
    {
        if (!condition) throw new ArtifactValidationException("trial_" + code, message);
    }
}

public static class WorkbenchTrialJson
{
    private const int MaximumBytes = 512 * 1024;
    public static byte[] SerializeBank(TrialBank bank) { WorkbenchTrial.Validate(bank); return JsonSerializer.SerializeToUtf8Bytes(bank, TrialJson.Default.TrialBank); }
    public static TrialBank ReadBank(ReadOnlySpan<byte> bytes) { var bank = Read(bytes, TrialJson.Default.TrialBank); WorkbenchTrial.Validate(bank); return bank; }
    public static byte[] SerializeSubmission(TrialSubmission submission) => JsonSerializer.SerializeToUtf8Bytes(submission, TrialJson.Default.TrialSubmission);
    public static TrialSubmission ReadSubmission(ReadOnlySpan<byte> bytes) => Read(bytes, TrialJson.Default.TrialSubmission);
    public static byte[] SerializeEvaluation(TrialEvaluation evaluation) => JsonSerializer.SerializeToUtf8Bytes(evaluation, TrialJson.Default.TrialEvaluation);
    public static byte[] SerializeObservations(TrialObservation[] observations) => JsonSerializer.SerializeToUtf8Bytes(observations, TrialJson.Default.TrialObservationArray);
    private static T Read<T>(ReadOnlySpan<byte> bytes, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new ArtifactValidationException("trial_size", "Trial JSON exceeds its bounded size.");
        try { return JsonSerializer.Deserialize(bytes, type) ?? throw new ArtifactValidationException("trial_empty", "Trial JSON is null."); }
        catch (JsonException e) { throw new ArtifactValidationException("trial_json", e.Message); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(TrialBank))]
[JsonSerializable(typeof(TrialGeometry))]
[JsonSerializable(typeof(TrialSubmission))]
[JsonSerializable(typeof(TrialEvaluation))]
[JsonSerializable(typeof(TrialObservation[]))]
internal partial class TrialJson : JsonSerializerContext;
