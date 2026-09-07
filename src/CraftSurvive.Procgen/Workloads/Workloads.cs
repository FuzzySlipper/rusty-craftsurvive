using System.Security.Cryptography;
using System.Text;
using static CraftSurvive.Procgen.Workloads.WorkloadGuard;

namespace CraftSurvive.Procgen.Workloads;

public readonly record struct CellCoord(int X, int Y, int Z) : IComparable<CellCoord>
{
    public int CompareTo(CellCoord other) => X != other.X ? X.CompareTo(other.X) : Y != other.Y ? Y.CompareTo(other.Y) : Z.CompareTo(other.Z);
}

public readonly record struct CellBounds(CellCoord Min, CellCoord MaxExclusive)
{
    public bool Contains(CellCoord cell) => cell.X >= Min.X && cell.X < MaxExclusive.X && cell.Y >= Min.Y && cell.Y < MaxExclusive.Y && cell.Z >= Min.Z && cell.Z < MaxExclusive.Z;
    public (int X, int Y, int Z) Dimensions()
    {
        try { return (checked(MaxExclusive.X - Min.X), checked(MaxExclusive.Y - Min.Y), checked(MaxExclusive.Z - Min.Z)); }
        catch (OverflowException) { throw new WorkloadException("bounds_overflow", "The bounds span does not fit Int32."); }
    }
    public ulong Volume()
    {
        var (x, y, z) = Dimensions();
        if (x <= 0 || y <= 0 || z <= 0) throw new WorkloadException("invalid_bounds", "Every bounds axis must have a positive span.");
        try { return checked((ulong)x * (ulong)y * (ulong)z); }
        catch (OverflowException) { throw new WorkloadException("volume_overflow", "The bounds volume does not fit UInt64."); }
    }
}

public enum CellState { Empty, Source, Frontier, Trail }
public enum CellNeighborhood { VonNeumann6, Moore26 }
public enum BoundaryPolicy { FixedEmpty, Wrap }
public enum CellularRule { FrontierTrailV1, ParityChurnV1 }
public enum WorkloadClass { SparsePropagation, DenseChurn, CrossBoundary, LargeResidentSmallHotRegion, HighSurfaceArea }
public enum WorkloadAlgorithm { SparseFrontier, DenseIndexedArray }

public sealed record WorkloadQuotas(ulong MaxVolume, uint MaxSteps, int MaxSeeds, ulong MaxCellStepsPerScenario, int MaxScenarios, ulong MaxCellStepsPerSuite)
{
    public static WorkloadQuotas Default { get; } = new(1_048_576, 4_096, 4_096, 1_048_576, 32, 2_097_152);
    public void Validate() => Require(MaxVolume > 0 && MaxSteps > 0 && MaxSeeds > 0 && MaxCellStepsPerScenario > 0 && MaxScenarios > 0 && MaxCellStepsPerSuite > 0, "invalid_quotas", "Every workload quota must be positive.");
}
public sealed record CellSeed(CellCoord Coord, CellState State);
public sealed record CellularScenario(string Id, WorkloadClass Workload, ulong Seed, CellBounds Bounds, CellNeighborhood Neighborhood, BoundaryPolicy Boundary, CellularRule Rule, uint Steps, IReadOnlyList<CellSeed> InitialCells)
{
    public void Validate(WorkloadQuotas quotas)
    {
        ArgumentNullException.ThrowIfNull(quotas); quotas.Validate();
        Require(!string.IsNullOrWhiteSpace(Id) && Id.Length <= 96 && Id.All(c => char.IsLower(c) || char.IsDigit(c) || c is '.' or '-' or '_'), "invalid_id", "Scenario identity is invalid.");
        var volume = Bounds.Volume();
        Require(volume <= quotas.MaxVolume, "volume_quota_exceeded", "Scenario volume exceeds its quota.");
        Require(Steps > 0 && Steps <= quotas.MaxSteps, "step_quota_exceeded", "Scenario steps are outside its quota.");
        ulong cellSteps; try { cellSteps = checked(volume * Steps); } catch (OverflowException) { throw new WorkloadException("cell_step_overflow", "Scenario cell-step estimate overflowed."); }
        Require(cellSteps <= quotas.MaxCellStepsPerScenario, "cell_step_quota_exceeded", "Scenario cell-step estimate exceeds its quota.");
        ArgumentNullException.ThrowIfNull(InitialCells);
        Require(InitialCells.Count <= quotas.MaxSeeds, "seed_quota_exceeded", "Scenario seed count exceeds its quota.");
        var seen = new HashSet<CellCoord>();
        foreach (var seed in InitialCells) { Require(seed.State != CellState.Empty, "empty_seed", "An initial seed cannot be empty."); Require(Bounds.Contains(seed.Coord), "seed_outside_bounds", "An initial seed lies outside bounds."); Require(seen.Add(seed.Coord), "duplicate_seed", "Initial seed coordinates must be unique."); }
    }
}
public sealed record CellularScenarioSuite(IReadOnlyList<CellularScenario> Scenarios, WorkloadQuotas Quotas)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scenarios); ArgumentNullException.ThrowIfNull(Quotas); Quotas.Validate();
        Require(Scenarios.Count > 0 && Scenarios.Count <= Quotas.MaxScenarios, "scenario_quota_exceeded", "Suite scenario count is outside its quota.");
        var ids = new HashSet<string>(StringComparer.Ordinal); ulong aggregate = 0;
        foreach (var scenario in Scenarios) { scenario.Validate(Quotas); Require(ids.Add(scenario.Id), "duplicate_scenario", "Suite scenario identities must be unique."); try { aggregate = checked(aggregate + checked(scenario.Bounds.Volume() * scenario.Steps)); } catch (OverflowException) { throw new WorkloadException("suite_cell_step_overflow", "Suite cell-step estimate overflowed."); } Require(aggregate <= Quotas.MaxCellStepsPerSuite, "suite_cell_step_quota_exceeded", "Suite cell-step estimate exceeds its quota."); }
    }
}
public sealed record CellDelta(CellCoord Coord, CellState Previous, CellState Current);
public sealed record StateReadout(ulong Empty, ulong Source, ulong Frontier, ulong Trail) { public ulong Active => checked(Source + Frontier + Trail); }
public sealed record StepReadout(uint Step, WorkloadAlgorithm Algorithm, ulong ActiveCells, ulong ResidentCells, ulong ChangedCells, ulong ScannedCells, ulong FrontierSize, CellBounds Bounds, StateReadout States, string DeltaHash, string StateHash, string CumulativeHash, IReadOnlyList<CellDelta> Deltas);
/// <summary>Deterministic CA observation. <see cref="ScenarioIdentity"/> binds every
/// scenario input that defines behavior, rather than only the cells that happen
/// to change during a particular run.</summary>
public sealed record CellularTrace(
    string ScenarioId,
    WorkloadClass Workload,
    string RuleId,
    ulong Seed,
    CellBounds Bounds,
    CellNeighborhood Neighborhood,
    BoundaryPolicy Boundary,
    uint DeclaredSteps,
    WorkloadAlgorithm Algorithm,
    string ScenarioIdentity,
    string InitialStateHash,
    IReadOnlyList<StepReadout> Steps,
    string FinalStateHash,
    string FinalHash);

public sealed class CellularAutomaton
{
    private readonly CellularScenario _scenario; private readonly Dictionary<CellCoord, CellState> _cells; private readonly CellState[]? _denseCells; private HashSet<CellCoord> _frontier; private readonly WorkloadAlgorithm _algorithm; private readonly int _width; private readonly int _height; private readonly int _depth; private readonly string _scenarioIdentity; private uint _completed; private string _chain;
    public CellularAutomaton(CellularScenario scenario, WorkloadQuotas quotas)
    {
        ArgumentNullException.ThrowIfNull(scenario); scenario.Validate(quotas); _scenario = scenario; var dimensions = scenario.Bounds.Dimensions(); _width = dimensions.X; _height = dimensions.Y; _depth = dimensions.Z; _algorithm = scenario.Rule == CellularRule.ParityChurnV1 ? WorkloadAlgorithm.DenseIndexedArray : WorkloadAlgorithm.SparseFrontier;
        _cells = _algorithm == WorkloadAlgorithm.SparseFrontier ? scenario.InitialCells.ToDictionary(x => x.Coord, x => x.State) : [];
        if (_algorithm == WorkloadAlgorithm.DenseIndexedArray)
        {
            _denseCells = new CellState[checked(_width * _height * _depth)];
            foreach (var seed in scenario.InitialCells) _denseCells[IndexOf(seed.Coord)] = seed.State;
        }
        _frontier = _algorithm == WorkloadAlgorithm.SparseFrontier ? InitialFrontier() : [];
        _scenarioIdentity = ScenarioIdentity(scenario);
        _chain = StableHash("ca-root", _scenarioIdentity, StateHash());
    }
    public WorkloadAlgorithm Algorithm => _algorithm;
    public uint CompletedSteps => _completed;
    public StepReadout Step()
    {
        if (_completed >= _scenario.Steps) throw new WorkloadException("declared_step_limit_reached", "The declared scenario step limit has already been reached.");
        var next = checked(_completed + 1); var candidates = _algorithm == WorkloadAlgorithm.DenseIndexedArray ? AllCells() : _frontier; var deltas = new List<CellDelta>();
        foreach (var coord in candidates) { var previous = StateAtUnchecked(coord); var current = Evaluate(coord, previous, next); if (current != previous) deltas.Add(new(coord, previous, current)); }
        deltas.Sort((a, b) => a.Coord.CompareTo(b.Coord));
        foreach (var delta in deltas)
        {
            if (_denseCells is { } dense) dense[IndexOf(delta.Coord)] = delta.Current;
            else if (delta.Current == CellState.Empty) _cells.Remove(delta.Coord);
            else _cells[delta.Coord] = delta.Current;
        }
        _frontier = _algorithm == WorkloadAlgorithm.DenseIndexedArray ? new HashSet<CellCoord>() : NextFrontier(deltas); _completed = next;
        var stateHash = StateHash(); var deltaHash = StableHash("ca-deltas", _scenarioIdentity, next.ToString(), string.Join(';', deltas.Select(d => $"{d.Coord.X},{d.Coord.Y},{d.Coord.Z}:{d.Previous}>{d.Current}"))); var states = Counts(); _chain = StableHash("ca-chain", _chain, deltaHash, stateHash, states.Active.ToString());
        return new(next, _algorithm, states.Active, _denseCells is null ? (ulong)_cells.Count : _scenario.Bounds.Volume(), (ulong)deltas.Count, checked((ulong)candidates.Count()), (ulong)_frontier.Count, _scenario.Bounds, states, deltaHash, stateHash, _chain, deltas);
    }
    public CellularTrace Run()
    {
        var initial = StateHash(); var steps = new List<StepReadout>(); while (_completed < _scenario.Steps) steps.Add(Step()); return new(_scenario.Id, _scenario.Workload, _scenario.Rule.ToString(), _scenario.Seed, _scenario.Bounds, _scenario.Neighborhood, _scenario.Boundary, _scenario.Steps, _algorithm, _scenarioIdentity, initial, steps, StateHash(), _chain);
    }
    public CellState StateAt(CellCoord coord) { Require(_scenario.Bounds.Contains(coord), "coordinate_outside_bounds", "Coordinate lies outside scenario bounds."); return StateAtUnchecked(coord); }
    private CellState StateAtUnchecked(CellCoord coord) => _denseCells is { } dense ? dense[IndexOf(coord)] : _cells.TryGetValue(coord, out var value) ? value : CellState.Empty;
    private CellState Evaluate(CellCoord coord, CellState previous, uint next)
    {
        if (_scenario.Rule == CellularRule.ParityChurnV1) { if (previous == CellState.Source) return CellState.Source; var parity = (long)Mod(coord.X, 2) + Mod(coord.Y, 2) + Mod(coord.Z, 2) + (long)(_scenario.Seed & 1) + (next & 1); return (parity & 1) == 0 ? CellState.Frontier : CellState.Empty; }
        if (previous == CellState.Source) return CellState.Source; if (previous == CellState.Frontier) return CellState.Trail; if (previous == CellState.Trail) return CellState.Trail; return Neighbors(coord).Any(x => StateAtUnchecked(x) is CellState.Source or CellState.Frontier) ? CellState.Frontier : CellState.Empty;
    }
    private HashSet<CellCoord> InitialFrontier() { var result = new HashSet<CellCoord>(_cells.Keys); foreach (var cell in _cells.Keys.ToArray()) foreach (var neighbor in Neighbors(cell)) result.Add(neighbor); return result; }
    private HashSet<CellCoord> NextFrontier(IEnumerable<CellDelta> deltas) { var result = new HashSet<CellCoord>(); foreach (var delta in deltas) { result.Add(delta.Coord); foreach (var neighbor in Neighbors(delta.Coord)) result.Add(neighbor); } return result; }
    private IEnumerable<CellCoord> AllCells() { for (var x = _scenario.Bounds.Min.X; x < _scenario.Bounds.MaxExclusive.X; x++) for (var y = _scenario.Bounds.Min.Y; y < _scenario.Bounds.MaxExclusive.Y; y++) for (var z = _scenario.Bounds.Min.Z; z < _scenario.Bounds.MaxExclusive.Z; z++) yield return new(x, y, z); }
    private IEnumerable<CellCoord> Neighbors(CellCoord cell)
    {
        for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            if (_scenario.Neighborhood == CellNeighborhood.VonNeumann6 && Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) != 1) continue;
            var x = (long)cell.X + dx; var y = (long)cell.Y + dy; var z = (long)cell.Z + dz;
            if (x >= _scenario.Bounds.Min.X && x < _scenario.Bounds.MaxExclusive.X && y >= _scenario.Bounds.Min.Y && y < _scenario.Bounds.MaxExclusive.Y && z >= _scenario.Bounds.Min.Z && z < _scenario.Bounds.MaxExclusive.Z) yield return new((int)x, (int)y, (int)z);
            else if (_scenario.Boundary == BoundaryPolicy.Wrap) yield return Wrap(x, y, z);
        }
    }
    private CellCoord Wrap(long x, long y, long z) => new(checked((int)(_scenario.Bounds.Min.X + Mod(x - _scenario.Bounds.Min.X, _width))), checked((int)(_scenario.Bounds.Min.Y + Mod(y - _scenario.Bounds.Min.Y, _height))), checked((int)(_scenario.Bounds.Min.Z + Mod(z - _scenario.Bounds.Min.Z, _depth))));
    private StateReadout Counts()
    {
        ulong source = 0, frontier = 0, trail = 0;
        var values = _denseCells is { } dense ? dense.AsEnumerable() : _cells.Values;
        foreach (var value in values) { if (value == CellState.Source) source++; else if (value == CellState.Frontier) frontier++; else if (value == CellState.Trail) trail++; }
        return new(_scenario.Bounds.Volume() - source - frontier - trail, source, frontier, trail);
    }
    private string StateHash() => _denseCells is { } dense
        ? StableHash("ca-state", string.Join(';', AllCells().Select(cell => $"{cell.X},{cell.Y},{cell.Z}:{dense[IndexOf(cell)]}")))
        : StableHash("ca-state", string.Join(';', _cells.OrderBy(x => x.Key).Select(x => $"{x.Key.X},{x.Key.Y},{x.Key.Z}:{x.Value}")));
    private int IndexOf(CellCoord cell) => checked(((cell.X - _scenario.Bounds.Min.X) * _height + (cell.Y - _scenario.Bounds.Min.Y)) * _depth + (cell.Z - _scenario.Bounds.Min.Z));
    private static int Mod(long value, int divisor) => (int)(((value % divisor) + divisor) % divisor);

    public static string ScenarioIdentity(CellularScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return StableHash(
            "ca-scenario-v1",
            scenario.Id,
            scenario.Workload.ToString(),
            scenario.Seed.ToString(),
            scenario.Bounds.Min.X.ToString(), scenario.Bounds.Min.Y.ToString(), scenario.Bounds.Min.Z.ToString(),
            scenario.Bounds.MaxExclusive.X.ToString(), scenario.Bounds.MaxExclusive.Y.ToString(), scenario.Bounds.MaxExclusive.Z.ToString(),
            scenario.Neighborhood.ToString(),
            scenario.Boundary.ToString(),
            scenario.Rule.ToString(),
            scenario.Steps.ToString(),
            string.Join(';', scenario.InitialCells.OrderBy(seed => seed.Coord).Select(seed => $"{seed.Coord.X},{seed.Coord.Y},{seed.Coord.Z}:{seed.State}")));
    }
}

public sealed record GenerationPreset(string Id, string Description, GenerationSearchBudget Search, TraceLimits Trace, WorkloadQuotas CellularAutomata, string PreferredOutcome)
{
    public void Validate() { Require(Id is "tight" or "normal" or "spread", "invalid_preset", "Preset identity must be tight, normal, or spread."); Require(!string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(PreferredOutcome), "invalid_preset", "Preset descriptions and preferences are required."); Search.Validate(); Trace.Validate(); CellularAutomata.Validate(); }
}
public sealed record GenerationSearchBudget(uint MaxAttempts, uint MaxCandidates, uint MaxRoutingStates, uint MaxPlacementArea, uint PreferredMaximum)
{ public void Validate() => Require(MaxAttempts > 0 && MaxCandidates > 0 && MaxRoutingStates > 0 && MaxPlacementArea > 0 && PreferredMaximum > 0, "invalid_search_budget", "Every generation search budget must be positive."); }
public static class GenerationPresets
{
    public static GenerationPreset Tight { get; } = new("tight", "Compact layouts with low search and trace budgets.", new(3, 8, 256, 256, 128), new(128, 32_768, 4_096), new(65_536, 128, 1_024, 65_536, 8, 131_072), "placement_area");
    public static GenerationPreset Normal { get; } = new("normal", "Balanced general-purpose generation controls.", new(6, 16, 1_024, 1_024, 512), new(512, 131_072, 32_768), WorkloadQuotas.Default, "placement_span");
    public static GenerationPreset Spread { get; } = new("spread", "Broader search and observation budgets for spacious outcomes.", new(12, 32, 4_096, 4_096, 2_048), new(1_024, 524_288, 131_072), new(1_048_576, 4_096, 4_096, 1_048_576, 32, 2_097_152), "routed_catalog_cells");
    public static IReadOnlyList<GenerationPreset> All { get; } = new[] { Tight, Normal, Spread };
    public static GenerationPreset Get(string id) => All.Single(x => StringComparer.Ordinal.Equals(x.Id, id));
}

public sealed record TraceLimits(uint MaxEvents, ulong MaxEventBodyBytes, ulong MaxVisualCells)
{ public void Validate() => Require(MaxEvents > 0 && MaxEventBodyBytes > 0 && MaxVisualCells > 0, "invalid_trace_limits", "Every trace limit must be positive."); }
public sealed record TraceRequestIdentity(string RequestId, string InputHash, ulong Seed);
public sealed record TraceResultIdentity(string ResultId, string ResultHash);
public sealed record TraceEvent(uint Id, string Stage, string Kind, string Body, ulong VisualCells, string PreviousHash, string EventHash);
public sealed record TraceSelectionSummary(uint? SelectedAttempt, string Classification, string Reason);
public sealed record TraceAttemptSummary(uint Attempt, string Classification, string Detail, ulong CandidateCount, ulong AcceptedCount);
public sealed record SemanticGenerationTrace(TraceRequestIdentity Request, TraceResultIdentity Result, TraceLimits Limits, string RootHash, IReadOnlyList<TraceEvent> Events, ulong EventBodyBytes, ulong VisualCells, string FinalEventHash, TraceSelectionSummary Selection, IReadOnlyList<TraceAttemptSummary> Attempts)
{
    public IReadOnlyDictionary<string, uint> StageCounters => Events.GroupBy(item => item.Stage, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => checked((uint)group.Count()), StringComparer.Ordinal);
}
public sealed record TraceVerification(bool Valid, string? ErrorCode, string? ErrorDetail);
/// <summary>Connects a replayed trace to the caller's authoritative result and
/// attempt ledger. The replay itself also checks the ledger against its hashed
/// canonical trace events.</summary>
public delegate bool TraceEvidenceVerifier(TraceRequestIdentity request, TraceResultIdentity result, TraceSelectionSummary selection, IReadOnlyList<TraceAttemptSummary> attempts, IReadOnlyList<TraceEvent> events);
public sealed class SemanticTraceBuilder
{
    private readonly TraceRequestIdentity _request; private readonly TraceResultIdentity _result; private readonly TraceLimits _limits; private readonly List<TraceEvent> _events = []; private ulong _bytes; private ulong _visual; private string _previous;
    public SemanticTraceBuilder(TraceRequestIdentity request, TraceResultIdentity result, TraceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(limits); limits.Validate(); RequireValidIdentity(request.RequestId, "invalid_request_identity"); RequireValidIdentity(request.InputHash, "invalid_request_identity"); RequireValidIdentity(result.ResultId, "invalid_result_identity"); RequireValidIdentity(result.ResultHash, "invalid_result_identity"); _request = request; _result = result; _limits = limits; _previous = StableHash("trace-root", request.RequestId, request.InputHash, request.Seed.ToString(), result.ResultId, result.ResultHash, limits.MaxEvents.ToString(), limits.MaxEventBodyBytes.ToString(), limits.MaxVisualCells.ToString());
    }
    public void Add(string stage, string kind, string body, ulong visualCells = 0)
    {
        Require(!TraceEvidence.IsReserved(stage, kind), "reserved_trace_evidence_event", "Attempt and selection evidence is emitted only by Complete.");
        Append(stage, kind, body, visualCells);
    }
    public SemanticGenerationTrace Complete(TraceSelectionSummary selection, IReadOnlyList<TraceAttemptSummary>? attempts = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var completeAttempts = attempts?.ToArray() ?? [];
        TraceEvidence.Validate(selection, completeAttempts);
        foreach (var attempt in completeAttempts) Append("attempt", "finished", TraceEvidence.AttemptBody(attempt));
        Append("selection", "evaluated", TraceEvidence.SelectionBody(selection));
        var root = _events.Count == 0 ? _previous : _events[0].PreviousHash;
        return new(_request, _result, _limits, root, _events.ToArray(), _bytes, _visual, TraceEvidence.CompletionHash(_previous, selection, completeAttempts), selection, completeAttempts);
    }

    private void Append(string stage, string kind, string body, ulong visualCells = 0)
    {
        Require(KnownTraceEvent(stage, kind), "unknown_trace_event", "Trace event stage and kind are not part of the semantic trace vocabulary.");
        ArgumentNullException.ThrowIfNull(body);
        var bytes = checked((ulong)Encoding.UTF8.GetByteCount(body)); var newBytes = checked(_bytes + bytes); var newVisual = checked(_visual + visualCells);
        Require(_events.Count < _limits.MaxEvents, "trace_event_quota_exceeded", "Trace event count exceeds its quota.");
        Require(newBytes <= _limits.MaxEventBodyBytes, "trace_body_quota_exceeded", "Trace body bytes exceed its quota."); Require(newVisual <= _limits.MaxVisualCells, "trace_visual_quota_exceeded", "Trace visual cells exceed their quota.");
        var id = checked((uint)_events.Count); var hash = StableHash("trace-event", id.ToString(), stage, kind, body, visualCells.ToString(), _previous); _events.Add(new(id, stage, kind, body, visualCells, _previous, hash)); _previous = hash; _bytes = newBytes; _visual = newVisual;
    }
}
public static class SemanticTraceReplay
{
    public static TraceVerification Verify(SemanticGenerationTrace trace, TraceEvidenceVerifier evidenceVerifier)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(trace); ArgumentNullException.ThrowIfNull(evidenceVerifier); trace.Limits.Validate();
            var root = StableHash("trace-root", trace.Request.RequestId, trace.Request.InputHash, trace.Request.Seed.ToString(), trace.Result.ResultId, trace.Result.ResultHash, trace.Limits.MaxEvents.ToString(), trace.Limits.MaxEventBodyBytes.ToString(), trace.Limits.MaxVisualCells.ToString());
            Require(StringComparer.Ordinal.Equals(root, trace.RootHash), "trace_root_hash_mismatch", "Trace root hash does not match request/result identities."); Require(trace.Events.Count <= trace.Limits.MaxEvents, "trace_event_quota_exceeded", "Trace event count exceeds its quota.");
            string previous = root; ulong bytes = 0, visual = 0;
            for (var position = 0; position < trace.Events.Count; position++)
            {
                var item = trace.Events[position]; Require(item.Id == position, "trace_event_order_invalid", "Event IDs must be unique, monotonic, and contiguous."); Require(KnownTraceEvent(item.Stage, item.Kind), "unknown_trace_event", "Trace event stage and kind are not part of the semantic trace vocabulary."); Require(StringComparer.Ordinal.Equals(item.PreviousHash, previous), "trace_chain_broken", "Trace previous hash does not match the preceding event.");
                bytes = checked(bytes + (ulong)Encoding.UTF8.GetByteCount(item.Body)); visual = checked(visual + item.VisualCells); var expected = StableHash("trace-event", item.Id.ToString(), item.Stage, item.Kind, item.Body, item.VisualCells.ToString(), item.PreviousHash); Require(StringComparer.Ordinal.Equals(expected, item.EventHash), "trace_event_hash_mismatch", "Trace event hash is invalid."); previous = item.EventHash;
            }
            TraceEvidence.Validate(trace.Selection, trace.Attempts);
            var recordedAttempts = trace.Events.Where(item => item.Stage == "attempt" && item.Kind == "finished").ToArray(); Require(recordedAttempts.Length == trace.Attempts.Count, "trace_attempt_evidence_missing", "Every attempt summary requires exactly one chained canonical event.");
            for (var index = 0; index < trace.Attempts.Count; index++) Require(StringComparer.Ordinal.Equals(recordedAttempts[index].Body, TraceEvidence.AttemptBody(trace.Attempts[index])), "trace_attempt_evidence_mismatch", "Attempt evidence does not match its chained event.");
            var selections = trace.Events.Where(item => item.Stage == "selection" && item.Kind == "evaluated").ToArray(); Require(selections.Length == 1 && StringComparer.Ordinal.Equals(selections[0].Body, TraceEvidence.SelectionBody(trace.Selection)), "trace_selection_evidence_mismatch", "Selection evidence does not match its chained event.");
            Require(bytes == trace.EventBodyBytes && bytes <= trace.Limits.MaxEventBodyBytes, "trace_body_quota_invalid", "Trace body accounting is invalid."); Require(visual == trace.VisualCells && visual <= trace.Limits.MaxVisualCells, "trace_visual_quota_invalid", "Trace visual-cell accounting is invalid.");
            Require(StringComparer.Ordinal.Equals(TraceEvidence.CompletionHash(previous, trace.Selection, trace.Attempts), trace.FinalEventHash), "trace_final_hash_mismatch", "Trace completion hash is invalid."); Require(evidenceVerifier(trace.Request, trace.Result, trace.Selection, trace.Attempts, trace.Events), "stale_result_identity", "The supplied runtime verifier did not bind this trace to its authoritative result and evidence."); return new(true, null, null);
        }
        catch (WorkloadException error) { return new(false, error.Code, error.Message); }
        catch (OverflowException) { return new(false, "trace_accounting_overflow", "Trace accounting overflowed."); }
    }
}

internal static class TraceEvidence
{
    public static bool IsReserved(string stage, string kind) => (stage, kind) is ("attempt", "finished") or ("selection", "evaluated");
    public static string AttemptBody(TraceAttemptSummary attempt) => $"attempt={attempt.Attempt};classification={attempt.Classification};detail={attempt.Detail};candidates={attempt.CandidateCount};accepted={attempt.AcceptedCount}";
    public static string SelectionBody(TraceSelectionSummary selection) => $"selected={selection.SelectedAttempt?.ToString() ?? "none"};classification={selection.Classification};reason={selection.Reason}";
    public static string CompletionHash(string previousEventHash, TraceSelectionSummary selection, IReadOnlyList<TraceAttemptSummary> attempts) => StableHash("trace-completion-v1", previousEventHash, SelectionBody(selection), string.Join('\u001e', attempts.Select(AttemptBody)));
    public static void Validate(TraceSelectionSummary selection, IReadOnlyList<TraceAttemptSummary> attempts)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(attempts); Require(!string.IsNullOrWhiteSpace(selection.Classification) && !string.IsNullOrWhiteSpace(selection.Reason), "invalid_trace_selection", "Trace selection requires a classification and reason.");
        for (var index = 0; index < attempts.Count; index++) { var attempt = attempts[index]; Require(attempt.Attempt == index, "trace_attempt_order_invalid", "Trace attempts must be complete, zero-based, and contiguous."); Require(!string.IsNullOrWhiteSpace(attempt.Classification) && !string.IsNullOrWhiteSpace(attempt.Detail), "invalid_trace_attempt", "Trace attempt summaries require a classification and detail."); Require(attempt.AcceptedCount <= attempt.CandidateCount, "invalid_trace_attempt", "Accepted candidates cannot exceed candidates considered."); }
        if (selection.SelectedAttempt is { } selected) { Require(selected < attempts.Count, "trace_selection_attempt_invalid", "Selected attempt is outside the complete attempt ledger."); Require(StringComparer.Ordinal.Equals(attempts[(int)selected].Classification, selection.Classification), "trace_selection_mismatch", "Selected attempt classification does not match the selection."); Require(attempts[(int)selected].AcceptedCount > 0, "trace_selection_mismatch", "A selected attempt must have accepted candidates."); }
        else { Require(attempts.All(attempt => !StringComparer.Ordinal.Equals(attempt.Classification, "accepted") && attempt.AcceptedCount == 0), "trace_selection_mismatch", "An exhausted selection cannot omit an accepted attempt."); }
    }
}

public static class WorkloadProbe
{
    public static void Verify()
    {
        var quotas = WorkloadQuotas.Default; var sparse = Scenario("probe.sparse", 7, CellularRule.FrontierTrailV1, BoundaryPolicy.FixedEmpty, new[] { new CellSeed(new(1, 1, 1), CellState.Source) }); var repeatA = new CellularAutomaton(sparse, quotas).Run(); var repeatB = new CellularAutomaton(sparse, quotas).Run(); Probe(StringComparer.Ordinal.Equals(repeatA.FinalHash, repeatB.FinalHash), "same seed must repeat");
        var empty = Scenario("probe.identity", 7, CellularRule.FrontierTrailV1, BoundaryPolicy.FixedEmpty, []); var emptyTrace = new CellularAutomaton(empty, quotas).Run();
        foreach (var changed in new[]
        {
            empty with { Workload = WorkloadClass.CrossBoundary },
            empty with { Neighborhood = CellNeighborhood.Moore26 },
            empty with { Boundary = BoundaryPolicy.Wrap },
            empty with { Bounds = new CellBounds(new(0, 0, 0), new(5, 4, 4)) },
        })
        {
            var changedTrace = new CellularAutomaton(changed, quotas).Run();
            Probe(!StringComparer.Ordinal.Equals(emptyTrace.ScenarioIdentity, changedTrace.ScenarioIdentity) && !StringComparer.Ordinal.Equals(emptyTrace.FinalHash, changedTrace.FinalHash), "every behavior-defining CA field must change trace identity");
        }
        var denseScenario = Scenario("probe.dense", 7, CellularRule.ParityChurnV1, BoundaryPolicy.FixedEmpty, []); var dense = new CellularAutomaton(denseScenario, quotas).Step(); Probe(dense.Algorithm == WorkloadAlgorithm.DenseIndexedArray && dense.ScannedCells == 64, "dense churn must use indexed full-domain scanning"); Probe(!StringComparer.Ordinal.Equals(new CellularAutomaton(denseScenario, quotas).Run().FinalHash, new CellularAutomaton(denseScenario with { Seed = 8 }, quotas).Run().FinalHash), "seed difference must change trace"); Probe(repeatA.Algorithm == WorkloadAlgorithm.SparseFrontier, "frontier rule must use sparse scanning");
        var wrap = new CellularAutomaton(Scenario("probe.wrap", 1, CellularRule.FrontierTrailV1, BoundaryPolicy.Wrap, new[] { new CellSeed(new(0, 0, 0), CellState.Source) }), quotas); wrap.Step(); Probe(wrap.StateAt(new(3, 0, 0)) == CellState.Frontier, "wrap boundary must cross the far edge");
        var suite = new CellularScenarioSuite(new[] { sparse }, quotas); suite.Validate(); Expect("step_quota_exceeded", () => (sparse with { Steps = 0 }).Validate(quotas)); Expect("seed_outside_bounds", () => (sparse with { InitialCells = new[] { new CellSeed(new(8, 1, 1), CellState.Source) } }).Validate(quotas)); Expect("duplicate_seed", () => (sparse with { InitialCells = new[] { new CellSeed(new(1, 1, 1), CellState.Source), new CellSeed(new(1, 1, 1), CellState.Trail) } }).Validate(quotas)); Expect("suite_cell_step_quota_exceeded", () => new CellularScenarioSuite(new[] { sparse, sparse with { Id = "probe.sparse2" } }, quotas with { MaxCellStepsPerSuite = 64 }).Validate());
        var limits = new TraceLimits(3, 256, 2); var builder = new SemanticTraceBuilder(new("request", "input", 7), new("result", "hash"), limits); builder.Add("input", "bound", "a", 1); var trace = builder.Complete(new(0, "accepted", "best"), new[] { new TraceAttemptSummary(0, "accepted", "best", 2, 1) }); Probe(SemanticTraceReplay.Verify(trace, static (_, _, selection, attempts, _) => selection.SelectedAttempt == 0 && attempts.Count == 1).Valid && trace.StageCounters["attempt"] == 1, "trace must verify complete chained evidence against the authoritative result"); Expect("trace_event_quota_exceeded", () => builder.Add("run", "finished", "c")); Expect("trace_body_quota_exceeded", () => new SemanticTraceBuilder(new("r", "i", 1), new("o", "h"), new(1, 1, 1)).Add("input", "bound", "xx")); Expect("unknown_trace_event", () => new SemanticTraceBuilder(new("r", "i", 1), new("o", "h"), new(1, 2, 1)).Add("unknown", "event", "x")); Expect("reserved_trace_evidence_event", () => new SemanticTraceBuilder(new("r", "i", 1), new("o", "h"), new(3, 64, 1)).Add("attempt", "finished", "forged")); Probe(!SemanticTraceReplay.Verify(trace with { Events = trace.Events.Reverse().ToArray() }, static (_, _, _, _, _) => true).Valid, "reordered trace must reject"); Probe(!SemanticTraceReplay.Verify(trace with { Selection = trace.Selection with { SelectedAttempt = null, Classification = "exhausted" } }, static (_, _, _, _, _) => true).Valid, "tampered selection must reject"); Probe(!SemanticTraceReplay.Verify(trace with { Attempts = new[] { trace.Attempts[0] with { Detail = "tampered" } } }, static (_, _, _, _, _) => true).Valid, "tampered complete attempts must reject"); Probe(!SemanticTraceReplay.Verify(trace, static (_, _, _, _, _) => false).Valid, "unbound rechain must reject");
        foreach (var preset in GenerationPresets.All) preset.Validate(); Probe(GenerationPresets.All.Select(x => x.Id).SequenceEqual(new[] { "tight", "normal", "spread" }), "presets must have stable complete identities"); Probe(GenerationPresets.Tight.Search != GenerationPresets.Spread.Search && GenerationPresets.Tight.Trace != GenerationPresets.Spread.Trace, "preset selection must replace complete values");
    }
    private static CellularScenario Scenario(string id, ulong seed, CellularRule rule, BoundaryPolicy boundary, IReadOnlyList<CellSeed> seeds) => new(id, rule == CellularRule.ParityChurnV1 ? WorkloadClass.DenseChurn : WorkloadClass.SparsePropagation, seed, new(new(0, 0, 0), new(4, 4, 4)), CellNeighborhood.VonNeumann6, boundary, rule, 2, seeds);
    private static void Expect(string code, Action action) { try { action(); throw new InvalidOperationException($"Expected {code}."); } catch (WorkloadException error) when (error.Code == code) { } }
    private static void Probe(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
}

public sealed class WorkloadException(string code, string message) : InvalidOperationException(message) { public string Code { get; } = code; }
internal static class WorkloadGuard
{
    public static void Require(bool condition, string code, string message) { if (!condition) throw new WorkloadException(code, message); }
    public static string StableHash(params string[] parts) { using var hash = SHA256.Create(); return Convert.ToHexString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)))).ToLowerInvariant(); }
    public static void RequireValidIdentity(string value, string code) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 256, code, "Identity must be nonempty and bounded.");
    public static bool KnownTraceEvent(string stage, string kind) => (stage, kind) switch
    {
        ("input", "bound") or ("attempt", "started") or ("selection", "evaluated") or ("attempt", "finished") or ("validation", "completed") or ("run", "finished") => true,
        _ => false,
    };
}
