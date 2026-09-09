namespace CraftSurvive.Procgen.Workbench;

/// <summary>A deliberately small offline progression experiment with three
/// named four-room motifs. It is not a general solver.</summary>
public readonly record struct WorkbenchPoint(float X, float Y, float Z);
public sealed record WorkbenchRoom(string Id, WorkbenchPoint Minimum, WorkbenchPoint Maximum);
public sealed record WorkbenchRoute(string Id, string From, string To, float Width, bool RequiresSwitch);
public sealed record WorkbenchCandidate(
    string Schema,
    ulong Seed,
    string Motif,
    WorkbenchRoom[] Rooms,
    WorkbenchRoute[] Routes,
    string SwitchRoom,
    string StartRoom,
    string GoalRoom,
    bool SwitchEnabled,
    bool RecoveryEnabled,
    bool PreviewOpening);

public sealed record WorkbenchState(
    string Room,
    bool SwitchOpen,
    bool Token = false,
    bool Spent = false,
    bool Recovered = false,
    bool Observed = false);
public sealed record WorkbenchContract(string Kind, string Requirement, bool Passed, string Evidence);
public sealed record WorkbenchCounterexample(string Requirement, string[] Actions, string Reason);
public sealed record WorkbenchAnalysis(
    bool Completable,
    int ReachableStates,
    int UnrecoverableStates,
    string[] Witness,
    WorkbenchContract[] Contracts,
    WorkbenchCounterexample[] Counterexamples);

public static class WorkbenchExperiment
{
    public const string CurrentSchema = "craftsurvive.workbench_candidate.v2";
    public const string CurrentMotif = "four-room-return-shortcut";
    public const string RecoveryMotif = "spent-key-recovery";
    public const string PreviewMotif = "visible-before-access";
    private const float Floor = 3f;
    private const float Roof = 7f;
    private const float CorridorWidth = 3f;
    private const float MinimumSpacing = 12f;
    private const float MaximumSpacing = 16f;
    private const float MinimumHalfWidth = 3f;
    private const float MaximumHalfWidth = 4f;
    private const int RoomCount = 4;
    private const int StatesPerRoom = 32;
    private const int MaximumStates = RoomCount * StatesPerRoom;
    private static readonly string[] SupportedMotifs = [CurrentMotif, RecoveryMotif, PreviewMotif];
    private static readonly string[] ExpectedRooms = ["start", "relay", "control", "goal"];
    private static (string Id, string From, string To, bool RequiresSwitch)[] ExpectedRoutesFor(string motif) =>
    [
        ("start-relay", "start", "relay", false),
        ("relay-control", "relay", "control", false),
        ("control-goal", "control", "goal", StringComparer.Ordinal.Equals(motif, PreviewMotif)),
        ("goal-start", "goal", "start", true),
    ];

    public static WorkbenchCandidate Generate(ulong seed, string motif = CurrentMotif, bool counterexample = false)
    {
        if (!SupportedMotifs.Contains(motif, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(motif), motif, "The workbench only supports its three named motifs.");
        var xSpacing = MinimumSpacing + (seed % 5UL);
        var zSpacing = MinimumSpacing + ((seed / 5UL) % 5UL);
        var halfWidth = (seed % 3UL) switch { 0UL => 3f, 1UL => 3.5f, _ => 4f };
        var rooms = new[]
        {
            Room("start", 0f, 0f, halfWidth),
            Room("relay", xSpacing, 0f, halfWidth),
            Room("control", xSpacing, zSpacing, halfWidth),
            Room("goal", 0f, zSpacing, halfWidth),
        };
        var routes = new[]
        {
            new WorkbenchRoute("start-relay", "start", "relay", CorridorWidth, false),
            new WorkbenchRoute("relay-control", "relay", "control", CorridorWidth, false),
            new WorkbenchRoute("control-goal", "control", "goal", CorridorWidth, StringComparer.Ordinal.Equals(motif, PreviewMotif)),
            new WorkbenchRoute("goal-start", "goal", "start", CorridorWidth, true),
        };
        var switchEnabled = !counterexample || !StringComparer.Ordinal.Equals(motif, CurrentMotif);
        var recoveryEnabled = StringComparer.Ordinal.Equals(motif, RecoveryMotif) && !counterexample;
        var previewOpening = StringComparer.Ordinal.Equals(motif, PreviewMotif) && !counterexample;
        return new WorkbenchCandidate(CurrentSchema, seed, motif, rooms, routes, "control", "start", "goal", switchEnabled, recoveryEnabled, previewOpening);
    }

    public static string[] Validate(WorkbenchCandidate candidate)
    {
        if (candidate is null) return ["candidate_missing"];
        var errors = new List<string>();
        Require(StringComparer.Ordinal.Equals(candidate.Schema, CurrentSchema), "schema_invalid", errors);
        Require(SupportedMotifs.Contains(candidate.Motif, StringComparer.Ordinal), "motif_invalid", errors);
        Require(candidate.Rooms is { Length: RoomCount }, "room_count_invalid", errors);
        Require(candidate.Routes is { Length: 4 }, "route_count_invalid", errors);
        Require(StringComparer.Ordinal.Equals(candidate.StartRoom, "start"), "start_room_invalid", errors);
        Require(StringComparer.Ordinal.Equals(candidate.SwitchRoom, "control"), "switch_room_invalid", errors);
        Require(StringComparer.Ordinal.Equals(candidate.GoalRoom, "goal"), "goal_room_invalid", errors);
        if (candidate.Rooms is not { Length: RoomCount } rooms || candidate.Routes is not { Length: 4 } routes)
            return errors.ToArray();
        var byId = new Dictionary<string, WorkbenchRoom>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            Require(room is not null && !string.IsNullOrWhiteSpace(room.Id), "room_malformed", errors);
            if (room is null || string.IsNullOrWhiteSpace(room.Id)) continue;
            Require(ExpectedRooms.Contains(room.Id, StringComparer.Ordinal), "room_identity_invalid", errors);
            Require(byId.TryAdd(room.Id, room), "room_identity_duplicate", errors);
            ValidateRoom(room, errors);
        }
        foreach (var id in ExpectedRooms)
            Require(byId.ContainsKey(id), "room_identity_missing", errors);
        if (ExpectedRooms.All(byId.ContainsKey))
        {
            ValidateRectangularMotif(byId, errors);
            foreach (var left in rooms)
            foreach (var right in rooms)
                if (StringComparer.Ordinal.Compare(left.Id, right.Id) < 0)
                    Require(!Overlaps(left, right), "room_overlap", errors);
        }
        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var routePairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            Require(route is not null && !string.IsNullOrWhiteSpace(route.Id), "route_malformed", errors);
            if (route is null || string.IsNullOrWhiteSpace(route.Id)) continue;
            Require(routeIds.Add(route.Id), "route_identity_duplicate", errors);
            var expected = ExpectedRoutesFor(candidate.Motif).SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Id, route.Id));
            Require(expected != default, "route_identity_invalid", errors);
            var endpoints = !string.IsNullOrWhiteSpace(route.From) && !string.IsNullOrWhiteSpace(route.To);
            Require(endpoints && byId.ContainsKey(route.From) && byId.ContainsKey(route.To) && !StringComparer.Ordinal.Equals(route.From, route.To), "route_reference_invalid", errors);
            if (endpoints)
                Require(routePairs.Add(Pair(route.From, route.To)), "route_duplicate", errors);
            Require(float.IsFinite(route.Width) && route.Width == CorridorWidth, "route_width_invalid", errors);
            if (expected != default)
                Require(StringComparer.Ordinal.Equals(route.From, expected.From) && StringComparer.Ordinal.Equals(route.To, expected.To) && route.RequiresSwitch == expected.RequiresSwitch, "route_shape_invalid", errors);
            if (endpoints && byId.TryGetValue(route.From, out var from) && byId.TryGetValue(route.To, out var to))
                Require(IsAxisAligned(Center(from), Center(to)), "route_not_axis_aligned", errors);
        }
        foreach (var expected in ExpectedRoutesFor(candidate.Motif))
            Require(routeIds.Contains(expected.Id), "route_identity_missing", errors);
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static WorkbenchState Initial(WorkbenchCandidate candidate)
    {
        EnsureValid(candidate);
        return new WorkbenchState(candidate.StartRoom, false, StringComparer.Ordinal.Equals(candidate.Motif, RecoveryMotif));
    }

    public static string[] LegalActions(WorkbenchCandidate candidate, WorkbenchState state)
    {
        EnsureValid(candidate);
        EnsureState(candidate, state);
        var actions = new List<string>();
        foreach (var route in candidate.Routes)
        {
            if (route.RequiresSwitch && !state.SwitchOpen) continue;
            if (StringComparer.Ordinal.Equals(route.From, state.Room)) actions.Add($"move:{route.To}");
            else if (StringComparer.Ordinal.Equals(route.To, state.Room)) actions.Add($"move:{route.From}");
        }
        if (StringComparer.Ordinal.Equals(candidate.Motif, RecoveryMotif))
        {
            if (StringComparer.Ordinal.Equals(state.Room, "relay") && !state.Spent && state.Token)
                actions.Add("spend");
            if (StringComparer.Ordinal.Equals(state.Room, candidate.SwitchRoom) && state.Spent && state.Token && !state.SwitchOpen)
                actions.Add("activate");
            if (StringComparer.Ordinal.Equals(state.Room, candidate.GoalRoom) && state.Spent && !state.Token && !state.Recovered && candidate.RecoveryEnabled)
                actions.Add("recover");
        }
        else
        {
            var previewReady = !StringComparer.Ordinal.Equals(candidate.Motif, PreviewMotif) || state.Observed;
            if (StringComparer.Ordinal.Equals(state.Room, candidate.SwitchRoom) && candidate.SwitchEnabled && !state.SwitchOpen && previewReady)
                actions.Add("activate");
            if (StringComparer.Ordinal.Equals(candidate.Motif, PreviewMotif) && StringComparer.Ordinal.Equals(state.Room, candidate.StartRoom) && candidate.PreviewOpening && !state.Observed)
                actions.Add("observe");
        }
        return actions.ToArray();
    }

    public static WorkbenchState Apply(WorkbenchCandidate candidate, WorkbenchState state, string action)
    {
        if (string.IsNullOrWhiteSpace(action) || !LegalActions(candidate, state).Contains(action, StringComparer.Ordinal))
            throw new InvalidOperationException("The requested workbench action is not legal in this state.");
        if (StringComparer.Ordinal.Equals(action, "activate"))
            return state with { SwitchOpen = true, Token = StringComparer.Ordinal.Equals(candidate.Motif, RecoveryMotif) ? false : state.Token };
        if (StringComparer.Ordinal.Equals(action, "spend"))
            return state with { Token = false, Spent = true };
        if (StringComparer.Ordinal.Equals(action, "recover"))
            return state with { Token = true, Recovered = true };
        if (StringComparer.Ordinal.Equals(action, "observe"))
            return state with { Observed = true };
        return state with { Room = action["move:".Length..] };
    }

    public static bool Complete(WorkbenchCandidate candidate, WorkbenchState state)
    {
        EnsureValid(candidate);
        if (state is null || !candidate.Rooms.Any(room => StringComparer.Ordinal.Equals(room.Id, state.Room)))
            return false;
        return StringComparer.Ordinal.Equals(state.Room, candidate.GoalRoom) && state.SwitchOpen && (!StringComparer.Ordinal.Equals(candidate.Motif, RecoveryMotif) || state.Spent);
    }

    public static string[] Witness(WorkbenchCandidate candidate)
    {
        var search = Explore(candidate);
        var completion = search.Order.FirstOrDefault(index => Complete(candidate, Decode(index)));
        return completion == 0 && !Complete(candidate, Decode(0)) ? [] : Trace(search, completion);
    }

    public static WorkbenchAnalysis Analyze(WorkbenchCandidate candidate)
    {
        var search = Explore(candidate);
        var witness = Witness(candidate);
        var canReachCompletion = new bool[MaximumStates];
        var reverse = Enumerable.Range(0, MaximumStates).Select(_ => new List<int>()).ToArray();
        foreach (var index in search.Order)
        foreach (var destination in search.Next[index])
            reverse[destination].Add(index);
        var pending = new Queue<int>();
        foreach (var index in search.Order.Where(index => Complete(candidate, Decode(index))))
        {
            canReachCompletion[index] = true;
            pending.Enqueue(index);
        }
        while (pending.Count > 0)
        {
            foreach (var prior in reverse[pending.Dequeue()])
            {
                if (canReachCompletion[prior]) continue;
                canReachCompletion[prior] = true;
                pending.Enqueue(prior);
            }
        }
        var unrecoverable = search.Order.Where(index => !canReachCompletion[index]).ToArray();
        var contracts = Contracts(candidate, witness).ToArray();
        var counterexamples = contracts
            .Where(contract => !contract.Passed)
            .Select(contract => new WorkbenchCounterexample(contract.Requirement, Trace(search, FailureIndex(candidate, unrecoverable)), contract.Evidence))
            .ToList();
        if (unrecoverable.Length > 0)
        {
            var failure = FailureIndex(candidate, unrecoverable);
            counterexamples.Add(new WorkbenchCounterexample("reachable-state-cannot-complete", Trace(search, failure), "A reachable fixed experiment state has no path to completion."));
        }
        return new WorkbenchAnalysis(witness.Length > 0, search.Order.Count, unrecoverable.Length, witness, contracts, counterexamples.ToArray());
    }

    private static IEnumerable<WorkbenchContract> Contracts(WorkbenchCandidate candidate, string[] witness)
    {
        var initial = Initial(candidate);
        var shortcutRejected = !LegalActions(candidate, initial).Contains("move:goal", StringComparer.Ordinal) && Throws(() => Apply(candidate, initial, "move:goal"));
        yield return new WorkbenchContract("completion", "a valid action witness reaches completion", witness.Length > 0 && Complete(candidate, ApplyAll(candidate, initial, witness)), witness.Length > 0 ? "The bounded state enumeration found a complete witness." : "No bounded state path reaches completion.");
        yield return new WorkbenchContract("locked-shortcut", "the goal-start shortcut rejects before activation", shortcutRejected, shortcutRejected ? "move:goal is absent and rejected from the initial state." : "The shortcut was available before opening the switch.");
        if (StringComparer.Ordinal.Equals(candidate.Motif, RecoveryMotif))
            yield return RecoveryContract(candidate);
        if (StringComparer.Ordinal.Equals(candidate.Motif, PreviewMotif))
            yield return PreviewContract(candidate);
    }

    private static WorkbenchContract RecoveryContract(WorkbenchCandidate candidate)
    {
        var state = Apply(candidate, Initial(candidate), "move:relay");
        var canSpend = LegalActions(candidate, state).Contains("spend", StringComparer.Ordinal);
        if (canSpend) state = Apply(candidate, state, "spend");
        var cannotSpendTwice = !LegalActions(candidate, state).Contains("spend", StringComparer.Ordinal);
        state = Apply(candidate, state, "move:control");
        state = Apply(candidate, state, "move:goal");
        var canRecover = LegalActions(candidate, state).Contains("recover", StringComparer.Ordinal);
        if (canRecover) state = Apply(candidate, state, "recover");
        var cannotRecoverTwice = !LegalActions(candidate, state).Contains("recover", StringComparer.Ordinal);
        var passed = canSpend && canRecover && cannotSpendTwice && cannotRecoverTwice;
        return new WorkbenchContract("recovery", "the spent token recovers exactly once before activation", passed, passed ? "spend and recover each become unavailable after one legal use." : "The recovery sequence cannot restore one spent token exactly once.");
    }

    private static WorkbenchContract PreviewContract(WorkbenchCandidate candidate)
    {
        var state = Apply(candidate, Initial(candidate), "move:relay");
        state = Apply(candidate, state, "move:control");
        var blockedBeforeObservation = !LegalActions(candidate, state).Contains("activate", StringComparer.Ordinal);
        state = Apply(candidate, state, "move:relay");
        state = Apply(candidate, state, "move:start");
        var canObserve = LegalActions(candidate, state).Contains("observe", StringComparer.Ordinal);
        if (canObserve) state = Apply(candidate, state, "observe");
        state = Apply(candidate, state, "move:relay");
        state = Apply(candidate, state, "move:control");
        var canActivateAfterObservation = LegalActions(candidate, state).Contains("activate", StringComparer.Ordinal);
        var passed = blockedBeforeObservation && canObserve && canActivateAfterObservation;
        return new WorkbenchContract("preview", "the opening must be observed before activation", passed, passed ? "activate is unavailable until the label-model observation action occurs." : "The preview observation cannot establish the required activation condition.");
    }

    private static int FailureIndex(WorkbenchCandidate candidate, int[] unrecoverable)
    {
        // Failure traces contain only admitted model actions, so the UI can
        // replay them to the state that cannot complete. Rejected action probes
        // remain contract evidence, not steps disguised as a legal trace.
        if (StringComparer.Ordinal.Equals(candidate.Motif, RecoveryMotif))
        {
            foreach (int index in unrecoverable)
                if (Decode(index) is { Spent: true, Token: false }) return index;
        }
        return unrecoverable.Length > 0 ? unrecoverable[0] : Encode(Initial(candidate));
    }

    // Explicitly 4 rooms x 5 boolean facts: a bounded experiment, not an API for general search.
    private static Search Explore(WorkbenchCandidate candidate)
    {
        EnsureValid(candidate);
        var prior = new int?[MaximumStates];
        var via = new string?[MaximumStates];
        var next = Enumerable.Range(0, MaximumStates).Select(_ => new List<int>()).ToArray();
        var order = new List<int>();
        var reached = new bool[MaximumStates];
        var pending = new Queue<int>();
        var start = Encode(Initial(candidate));
        reached[start] = true;
        prior[start] = start;
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            var index = pending.Dequeue();
            order.Add(index);
            var state = Decode(index);
            foreach (var action in LegalActions(candidate, state))
            {
                var destination = Encode(Apply(candidate, state, action));
                next[index].Add(destination);
                if (reached[destination]) continue;
                reached[destination] = true;
                prior[destination] = index;
                via[destination] = action;
                pending.Enqueue(destination);
            }
        }
        return new Search(start, prior, via, next, order);
    }

    private static int Encode(WorkbenchState state)
    {
        var room = Array.IndexOf(ExpectedRooms, state.Room);
        if (room < 0) throw new InvalidOperationException("Workbench state references an unknown room.");
        var bits = (state.SwitchOpen ? 1 : 0) | (state.Token ? 2 : 0) | (state.Spent ? 4 : 0) | (state.Recovered ? 8 : 0) | (state.Observed ? 16 : 0);
        return room * StatesPerRoom + bits;
    }
    private static WorkbenchState Decode(int index)
    {
        var bits = index % StatesPerRoom;
        return new WorkbenchState(ExpectedRooms[index / StatesPerRoom], (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0);
    }
    private static string[] Trace(Search search, int destination)
    {
        if (!search.Prior[destination].HasValue) return [];
        var actions = new List<string>();
        for (var current = destination; current != search.Start; current = search.Prior[current]!.Value) actions.Add(search.Via[current]!);
        actions.Reverse();
        return actions.ToArray();
    }
    private static WorkbenchState ApplyAll(WorkbenchCandidate candidate, WorkbenchState state, IEnumerable<string> actions)
    {
        foreach (var action in actions) state = Apply(candidate, state, action);
        return state;
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
    private static WorkbenchRoom Room(string id, float x, float z, float halfWidth) => new(id, new WorkbenchPoint(x - halfWidth, Floor, z - halfWidth), new WorkbenchPoint(x + halfWidth, Roof, z + halfWidth));
    private static void ValidateRoom(WorkbenchRoom room, List<string> errors)
    {
        var min = room.Minimum;
        var max = room.Maximum;
        Require(Finite(min) && Finite(max), "room_coordinate_nonfinite", errors);
        Require(Bounded(min) && Bounded(max), "room_coordinate_out_of_bounds", errors);
        Require(min.X < max.X && min.Y < max.Y && min.Z < max.Z, "room_extent_invalid", errors);
        Require(min.Y == Floor && max.Y == Roof, "room_vertical_bounds_invalid", errors);
        Require(HalfWidth(room) >= MinimumHalfWidth && HalfWidth(room) <= MaximumHalfWidth && HalfWidth(room) == HalfDepth(room), "room_width_invalid", errors);
    }
    private static void ValidateRectangularMotif(IReadOnlyDictionary<string, WorkbenchRoom> rooms, List<string> errors)
    {
        var start = Center(rooms["start"]);
        var relay = Center(rooms["relay"]);
        var control = Center(rooms["control"]);
        var goal = Center(rooms["goal"]);
        Require(start == new WorkbenchPoint(0f, 5f, 0f), "start_center_invalid", errors);
        Require(relay.Y == 5f && relay.Z == 0f && relay.X >= MinimumSpacing && relay.X <= MaximumSpacing, "relay_center_invalid", errors);
        Require(control.Y == 5f && control.X == relay.X && control.Z >= MinimumSpacing && control.Z <= MaximumSpacing, "control_center_invalid", errors);
        Require(goal.Y == 5f && goal.X == 0f && goal.Z == control.Z, "goal_center_invalid", errors);
    }
    private static bool Overlaps(WorkbenchRoom left, WorkbenchRoom right) => left.Minimum.X < right.Maximum.X && left.Maximum.X > right.Minimum.X && left.Minimum.Y < right.Maximum.Y && left.Maximum.Y > right.Minimum.Y && left.Minimum.Z < right.Maximum.Z && left.Maximum.Z > right.Minimum.Z;
    private static bool IsAxisAligned(WorkbenchPoint from, WorkbenchPoint to) => ((from.X == to.X ? 1 : 0) + (from.Y == to.Y ? 1 : 0) + (from.Z == to.Z ? 1 : 0)) == 2;
    private static WorkbenchPoint Center(WorkbenchRoom room) => new((room.Minimum.X + room.Maximum.X) / 2f, (room.Minimum.Y + room.Maximum.Y) / 2f, (room.Minimum.Z + room.Maximum.Z) / 2f);
    private static float HalfWidth(WorkbenchRoom room) => (room.Maximum.X - room.Minimum.X) / 2f;
    private static float HalfDepth(WorkbenchRoom room) => (room.Maximum.Z - room.Minimum.Z) / 2f;
    private static bool Finite(WorkbenchPoint point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    private static bool Bounded(WorkbenchPoint point) => MathF.Abs(point.X) <= 64f && MathF.Abs(point.Y) <= 16f && MathF.Abs(point.Z) <= 64f;
    private static string Pair(string from, string to) => StringComparer.Ordinal.Compare(from, to) < 0 ? $"{from}\u001f{to}" : $"{to}\u001f{from}";
    private static void EnsureValid(WorkbenchCandidate candidate)
    {
        var errors = Validate(candidate);
        if (errors.Length > 0)
            throw new InvalidOperationException($"Workbench candidate is invalid: {string.Join(", ", errors)}.");
    }

    private static void EnsureState(WorkbenchCandidate candidate, WorkbenchState state)
    {
        if (state is null || !candidate.Rooms.Any(room => StringComparer.Ordinal.Equals(room.Id, state.Room)))
            throw new InvalidOperationException("Workbench state references an unknown room.");
    }

    private static void Require(bool condition, string error, List<string> errors)
    {
        if (!condition) errors.Add(error);
    }
    private sealed record Search(int Start, int?[] Prior, string?[] Via, List<int>[] Next, List<int> Order);
}
