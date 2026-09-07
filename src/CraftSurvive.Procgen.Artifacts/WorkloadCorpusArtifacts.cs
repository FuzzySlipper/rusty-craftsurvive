using CraftSurvive.Procgen.Workloads;

namespace CraftSurvive.Procgen.Artifacts;

/// <summary>Durable C# corpus envelope for the retained cellular-automata
/// workloads. It deliberately carries the full input suite as well as its
/// observations so a reader can regenerate and strictly admit it.</summary>
public sealed record WorkloadCorpusProvenance(
    string SchemaVersion,
    string Generator,
    string SuiteIdentity);

public sealed record CellularWorkloadCorpus(
    string Kind,
    string CorpusId,
    WorkloadCorpusProvenance Provenance,
    CellularScenarioSuite Suite,
    IReadOnlyList<CellularTrace> Traces,
    string CorpusIdentity)
{
    public const string CurrentKind = "craftsurvive_procgen.csharp_ca_workload_corpus.v1";
    public const string CurrentSchemaVersion = "csharp-ca-workload-corpus-v1";
    public const string CurrentGenerator = "CraftSurvive.Procgen.Artifacts.ArtifactWorkloadCorpusGenerator";
}

internal sealed record CellularWorkloadCorpusIdentityPayload(
    string Kind,
    string CorpusId,
    WorkloadCorpusProvenance Provenance,
    CellularScenarioSuite Suite,
    IReadOnlyList<CellularTrace> Traces);

/// <summary>The only representative corpus construction path. It runs the
/// pure C# CA implementation; no predecessor fixture or compatibility runner
/// participates in its result.</summary>
public sealed class ArtifactWorkloadCorpusGenerator
{
    public const string RepresentativeCorpusId = "representative-csharp-ca-v1";

    public CellularWorkloadCorpus GenerateRepresentative()
    {
        var suite = RepresentativeSuite();
        suite.Validate();
        var traces = suite.Scenarios.Select(scenario => new CellularAutomaton(scenario, suite.Quotas).Run()).ToArray();
        var provenance = new WorkloadCorpusProvenance(
            CellularWorkloadCorpus.CurrentSchemaVersion,
            CellularWorkloadCorpus.CurrentGenerator,
            ArtifactIdentity.HashScenarioSuite(suite));
        var unsigned = new CellularWorkloadCorpus(CellularWorkloadCorpus.CurrentKind, RepresentativeCorpusId, provenance, suite, traces, string.Empty);
        return unsigned with { CorpusIdentity = ArtifactIdentity.HashWorkloadCorpus(unsigned) };
    }

    public static CellularScenarioSuite RepresentativeSuite() => new(
        new CellularScenario[]
        {
            new("corpus.sparse-propagation", WorkloadClass.SparsePropagation, 7101, new(new(0, 0, 0), new(9, 3, 9)), CellNeighborhood.VonNeumann6, BoundaryPolicy.FixedEmpty, CellularRule.FrontierTrailV1, 6, new[] { new CellSeed(new(4, 1, 4), CellState.Source) }),
            new("corpus.dense-churn", WorkloadClass.DenseChurn, 7102, new(new(0, 0, 0), new(6, 3, 6)), CellNeighborhood.Moore26, BoundaryPolicy.FixedEmpty, CellularRule.ParityChurnV1, 4, new[] { new CellSeed(new(3, 1, 3), CellState.Source) }),
            new("corpus.cross-boundary", WorkloadClass.CrossBoundary, 7103, new(new(-4, -2, -4), new(5, 3, 5)), CellNeighborhood.VonNeumann6, BoundaryPolicy.Wrap, CellularRule.FrontierTrailV1, 5, new[] { new CellSeed(new(4, 0, 4), CellState.Source) }),
            new("corpus.large-resident-small-hot-region", WorkloadClass.LargeResidentSmallHotRegion, 7104, new(new(0, 0, 0), new(64, 16, 64)), CellNeighborhood.VonNeumann6, BoundaryPolicy.FixedEmpty, CellularRule.FrontierTrailV1, 4, new[] { new CellSeed(new(32, 8, 32), CellState.Source) }),
            new("corpus.high-surface-area", WorkloadClass.HighSurfaceArea, 7105, new(new(0, 0, 0), new(10, 4, 10)), CellNeighborhood.Moore26, BoundaryPolicy.Wrap, CellularRule.ParityChurnV1, 4, new[] { new CellSeed(new(5, 2, 5), CellState.Source) }),
        },
        WorkloadQuotas.Default);
}

public static class WorkloadCorpusValidator
{
    public static void Validate(CellularWorkloadCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        Require(StringComparer.Ordinal.Equals(corpus.Kind, CellularWorkloadCorpus.CurrentKind), "unsupported_workload_corpus_kind", $"Corpus kind must be '{CellularWorkloadCorpus.CurrentKind}'.");
        Require(!string.IsNullOrWhiteSpace(corpus.CorpusId) && corpus.CorpusId.Length <= 96 && corpus.CorpusId.All(character => char.IsLower(character) || char.IsDigit(character) || character is '.' or '-' or '_'), "invalid_workload_corpus_id", "Corpus identity is invalid.");
        Require(corpus.Provenance is not null, "workload_corpus_provenance_missing", "Corpus provenance is required.");
        var provenance = corpus.Provenance ?? throw new ArtifactValidationException("workload_corpus_provenance_missing", "Corpus provenance is required.");
        Require(StringComparer.Ordinal.Equals(provenance.SchemaVersion, CellularWorkloadCorpus.CurrentSchemaVersion) && StringComparer.Ordinal.Equals(provenance.Generator, CellularWorkloadCorpus.CurrentGenerator), "workload_corpus_provenance_invalid", "Corpus provenance does not identify the current C# schema and generator.");
        ArgumentNullException.ThrowIfNull(corpus.Suite); corpus.Suite.Validate(); ArgumentNullException.ThrowIfNull(corpus.Traces);
        Require(StringComparer.Ordinal.Equals(provenance.SuiteIdentity, ArtifactIdentity.HashScenarioSuite(corpus.Suite)), "workload_corpus_suite_identity_invalid", "Corpus provenance does not bind the declared scenario suite.");
        Require(corpus.Traces.Count == corpus.Suite.Scenarios.Count, "workload_corpus_trace_count_invalid", "Corpus must carry one trace for every declared scenario.");
        for (var index = 0; index < corpus.Suite.Scenarios.Count; index++)
        {
            var regenerated = new CellularAutomaton(corpus.Suite.Scenarios[index], corpus.Suite.Quotas).Run();
            Require(StringComparer.Ordinal.Equals(ArtifactIdentity.HashCellularTrace(regenerated), ArtifactIdentity.HashCellularTrace(corpus.Traces[index])), "workload_corpus_trace_mismatch", "Corpus trace does not regenerate through the authoritative C# CA path.");
        }
        Require(!string.IsNullOrWhiteSpace(corpus.CorpusIdentity) && StringComparer.Ordinal.Equals(corpus.CorpusIdentity, ArtifactIdentity.HashWorkloadCorpus(corpus)), "workload_corpus_identity_invalid", "Corpus identity does not bind current schema, provenance, suite, and traces.");
    }

    private static void Require(bool condition, string code, string detail)
    {
        if (!condition) throw new ArtifactValidationException(code, detail);
    }
}
