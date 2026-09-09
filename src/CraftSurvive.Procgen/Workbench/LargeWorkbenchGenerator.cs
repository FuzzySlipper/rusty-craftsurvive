namespace CraftSurvive.Procgen.Workbench;

/// <summary>Builds and independently validates the larger workbench motif.
/// The resolved room and route arrays remain the artifact authority; the seed
/// is used only by <see cref="Generate"/> to produce a deterministic sample.
/// </summary>
internal static class LargeWorkbenchGenerator
{
    internal const int GridSize = 6;
    internal const int RoomCount = GridSize * GridSize;
    internal const int MinimumRouteCount = RoomCount - 1 + 9;
    internal const int MaximumRouteCount = RoomCount - 1 + 13;
    internal const float Floor = 3f;
    internal const float Roof = 7f;
    internal const float MinimumSpacing = 14f;
    internal const float MaximumSpacing = 16f;
    internal const float MinimumHalfExtent = 3f;
    internal const float MaximumHalfExtent = 5f;
    internal const float MinimumRouteWidth = 3f;
    internal const float MaximumRouteWidth = 4f;
    internal const int LoopCount = 10;
    private const int GoalIndex = RoomCount - 1;
    private const float CoordinateTolerance = 0.01f;

    internal static WorkbenchCandidate Generate(ulong seed, bool counterexample)
    {
        var random = new StableRandom(seed ^ 0x9E3779B97F4A7C15UL);
        var xSpacing = MinimumSpacing + random.NextInt((int)(MaximumSpacing - MinimumSpacing) + 1);
        var zSpacing = MinimumSpacing + random.NextInt((int)(MaximumSpacing - MinimumSpacing) + 1);
        var tree = BuildTree(ref random);
        var distances = TreeDistances(tree, 0);
        var controlIndex = Enumerable.Range(1, GoalIndex - 1)
            .Where(index => distances[index] >= 0)
            .OrderByDescending(index => distances[index])
            .ThenBy(index => index)
            .First();

        var allEdges = GridEdges().ToList();
        var treeSet = tree.ToHashSet();
        var loops = allEdges.Where(edge => !treeSet.Contains(edge)).ToList();
        Shuffle(loops, ref random);
        loops = loops.Take(LoopCount).ToList();
        var edges = tree.Concat(loops).ToArray();
        var optionalGates = loops
            .Where(edge => !IsIncident(edge, GoalIndex))
            .Take(3)
            .ToHashSet();

        string IdFor(int index) => index == 0
            ? "start"
            : index == GoalIndex
                ? "goal"
                : index == controlIndex
                    ? "control"
                    : $"r{index:00}";

        var rooms = Enumerable.Range(0, RoomCount)
            .Select(index =>
            {
                var column = index % GridSize;
                var row = index / GridSize;
                var x = (column - (GridSize - 1) / 2f) * xSpacing;
                var z = (row - (GridSize - 1) / 2f) * zSpacing;
                var halfX = MinimumHalfExtent + 0.5f * random.NextInt((int)((MaximumHalfExtent - MinimumHalfExtent) * 2f) + 1);
                var halfZ = MinimumHalfExtent + 0.5f * random.NextInt((int)((MaximumHalfExtent - MinimumHalfExtent) * 2f) + 1);
                return new WorkbenchRoom(
                    IdFor(index),
                    new WorkbenchPoint(x - halfX, Floor, z - halfZ),
                    new WorkbenchPoint(x + halfX, Roof, z + halfZ));
            })
            .ToArray();

        var routes = edges.Select((edge, index) =>
        {
            var requiresSwitch = IsIncident(edge, GoalIndex) || optionalGates.Contains(edge);
            var width = MinimumRouteWidth + random.NextInt((int)(MaximumRouteWidth - MinimumRouteWidth) + 1);
            return new WorkbenchRoute(
                $"route-{edge.A:00}-{edge.B:00}",
                IdFor(edge.A),
                IdFor(edge.B),
                width,
                requiresSwitch);
        }).ToArray();

        return new WorkbenchCandidate(
            WorkbenchExperiment.CurrentSchema,
            seed,
            WorkbenchExperiment.LargeMotif,
            rooms,
            routes,
            "control",
            "start",
            "goal",
            !counterexample,
            false,
            false);
    }

    internal static string[] Validate(WorkbenchCandidate candidate)
    {
        if (candidate is null) return ["candidate_missing"];
        var errors = new List<string>();
        Require(StringComparer.Ordinal.Equals(candidate.Schema, WorkbenchExperiment.CurrentSchema), "schema_invalid", errors);
        Require(StringComparer.Ordinal.Equals(candidate.Motif, WorkbenchExperiment.LargeMotif), "motif_invalid", errors);
        Require(candidate.Rooms is { Length: RoomCount }, "room_count_invalid", errors);
        Require(candidate.Routes is { Length: >= MinimumRouteCount and <= MaximumRouteCount }, "route_count_invalid", errors);
        Require(StringComparer.Ordinal.Equals(candidate.StartRoom, "start"), "start_room_invalid", errors);
        Require(StringComparer.Ordinal.Equals(candidate.SwitchRoom, "control"), "switch_room_invalid", errors);
        Require(StringComparer.Ordinal.Equals(candidate.GoalRoom, "goal"), "goal_room_invalid", errors);
        Require(!candidate.RecoveryEnabled, "recovery_flag_invalid", errors);
        Require(!candidate.PreviewOpening, "preview_flag_invalid", errors);
        if (candidate.Rooms is not { Length: RoomCount } rooms ||
            candidate.Routes is not { Length: >= MinimumRouteCount and <= MaximumRouteCount } routes)
            return errors.Distinct(StringComparer.Ordinal).ToArray();

        var byId = new Dictionary<string, WorkbenchRoom>(StringComparer.Ordinal);
        var numericIds = new HashSet<int>();
        foreach (var room in rooms)
        {
            Require(room is not null && !string.IsNullOrWhiteSpace(room.Id), "room_malformed", errors);
            if (room is null || string.IsNullOrWhiteSpace(room.Id)) continue;
            Require(byId.TryAdd(room.Id, room), "room_identity_duplicate", errors);
            if (room.Id is not ("start" or "control" or "goal"))
            {
                var numeric = room.Id.Length == 3 && room.Id[0] == 'r' && int.TryParse(room.Id.AsSpan(1), out var parsed) ? parsed : -1;
                Require(numeric is >= 1 and < GoalIndex, "room_identity_invalid", errors);
                if (numeric >= 1 && numeric < GoalIndex)
                    Require(numericIds.Add(numeric), "room_grid_identity_duplicate", errors);
            }
            ValidateRoom(room, errors);
        }
        Require(byId.ContainsKey("start"), "start_identity_missing", errors);
        Require(byId.ContainsKey("control"), "control_identity_missing", errors);
        Require(byId.ContainsKey("goal"), "goal_identity_missing", errors);
        Require(numericIds.Count == RoomCount - 3, "room_identity_count_invalid", errors);
        if (byId.Count != rooms.Length || !byId.ContainsKey("start") || !byId.ContainsKey("control") || !byId.ContainsKey("goal"))
            return errors.Distinct(StringComparer.Ordinal).ToArray();
        for (var leftIndex = 0; leftIndex < rooms.Length; leftIndex++)
        for (var rightIndex = leftIndex + 1; rightIndex < rooms.Length; rightIndex++)
            Require(!Overlaps(rooms[leftIndex], rooms[rightIndex]), "room_overlap", errors);

        var centers = rooms.ToDictionary(room => room.Id, Center, StringComparer.Ordinal);
        var xCoordinates = DistinctCoordinates(centers.Values.Select(point => point.X));
        var zCoordinates = DistinctCoordinates(centers.Values.Select(point => point.Z));
        Require(xCoordinates.Count == GridSize, "grid_x_count_invalid", errors);
        Require(zCoordinates.Count == GridSize, "grid_z_count_invalid", errors);
        ValidateSpacing(xCoordinates, "x", errors);
        ValidateSpacing(zCoordinates, "z", errors);

        var cells = new Dictionary<string, (int Column, int Row)>(StringComparer.Ordinal);
        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var room in rooms)
        {
            var center = centers[room.Id];
            var column = CoordinateIndex(xCoordinates, center.X);
            var row = CoordinateIndex(zCoordinates, center.Z);
            Require(column >= 0 && row >= 0, "room_grid_coordinate_invalid", errors);
            if (column < 0 || row < 0) continue;
            cells[room.Id] = (column, row);
            Require(occupied.Add((column, row)), "room_grid_overlap", errors);
        }
        Require(occupied.Count == RoomCount, "room_grid_coverage_invalid", errors);
        if (cells.TryGetValue("start", out var startCell))
            Require(startCell == (0, 0), "start_grid_cell_invalid", errors);
        if (cells.TryGetValue("goal", out var goalCell))
            Require(goalCell == (GridSize - 1, GridSize - 1), "goal_grid_cell_invalid", errors);

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var routePairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            Require(route is not null && !string.IsNullOrWhiteSpace(route.Id), "route_malformed", errors);
            if (route is null || string.IsNullOrWhiteSpace(route.Id)) continue;
            Require(routeIds.Add(route.Id), "route_identity_duplicate", errors);
            var endpoints = !string.IsNullOrWhiteSpace(route.From) && !string.IsNullOrWhiteSpace(route.To)
                && byId.ContainsKey(route.From) && byId.ContainsKey(route.To)
                && !StringComparer.Ordinal.Equals(route.From, route.To);
            Require(endpoints, "route_reference_invalid", errors);
            if (!endpoints) continue;
            Require(routePairs.Add(Pair(route.From, route.To)), "route_duplicate", errors);
            Require(float.IsFinite(route.Width) && route.Width >= MinimumRouteWidth && route.Width <= MaximumRouteWidth, "route_width_invalid", errors);
            if (!cells.TryGetValue(route.From, out var from) || !cells.TryGetValue(route.To, out var to))
            {
                Require(false, "route_grid_reference_invalid", errors);
                continue;
            }
            Require(Math.Abs(from.Column - to.Column) + Math.Abs(from.Row - to.Row) == 1, "route_not_grid_adjacent", errors);
            var fromCenter = centers[route.From];
            var toCenter = centers[route.To];
            Require(Math.Abs(fromCenter.X - toCenter.X) <= CoordinateTolerance || Math.Abs(fromCenter.Z - toCenter.Z) <= CoordinateTolerance, "route_not_axis_aligned", errors);
        }

        if (byId.ContainsKey("goal") && cells.ContainsKey("goal"))
        {
            var goalRoutes = routes.Where(route => route is not null && (StringComparer.Ordinal.Equals(route.From, "goal") || StringComparer.Ordinal.Equals(route.To, "goal"))).ToArray();
            Require(goalRoutes.Length > 0, "goal_route_missing", errors);
            Require(goalRoutes.All(route => route.RequiresSwitch), "goal_route_unlocked", errors);
            Require(routes.Any(route => route is not null && route.RequiresSwitch && !StringComparer.Ordinal.Equals(route.From, "goal") && !StringComparer.Ordinal.Equals(route.To, "goal")), "optional_gate_missing", errors);
        }

        var closed = ReachableRooms(rooms, routes, "start", switchOpen: false);
        var open = ReachableRooms(rooms, routes, "start", switchOpen: true);
        foreach (var room in rooms)
        {
            if (StringComparer.Ordinal.Equals(room.Id, "goal"))
            {
                Require(!closed.Contains(room.Id), "goal_reachable_before_switch", errors);
            }
            else
            {
                Require(closed.Contains(room.Id), "non_goal_unreachable_before_switch", errors);
            }
        }
        Require(open.Count == RoomCount, "open_graph_disconnected", errors);
        Require(closed.Contains("control"), "switch_room_unreachable_before_switch", errors);
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static List<GridEdge> BuildTree(ref StableRandom random)
    {
        var visited = new bool[RoomCount];
        var tree = new List<GridEdge>(RoomCount - 1);
        var stack = new List<int> { 0 };
        visited[0] = true;
        while (stack.Count > 0)
        {
            var current = stack[^1];
            var choices = Neighbors(current)
                .Where(index => index != GoalIndex && !visited[index])
                .ToArray();
            if (choices.Length == 0)
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            var next = choices[random.NextInt(choices.Length)];
            visited[next] = true;
            tree.Add(new GridEdge(current, next));
            stack.Add(next);
        }

        var goalChoices = Neighbors(GoalIndex).Where(index => visited[index]).ToArray();
        tree.Add(new GridEdge(GoalIndex, goalChoices[random.NextInt(goalChoices.Length)]));
        return tree;
    }

    private static int[] TreeDistances(IReadOnlyList<GridEdge> tree, int start)
    {
        var distances = Enumerable.Repeat(-1, RoomCount).ToArray();
        var adjacent = Enumerable.Range(0, RoomCount).Select(_ => new List<int>()).ToArray();
        foreach (var edge in tree)
        {
            adjacent[edge.A].Add(edge.B);
            adjacent[edge.B].Add(edge.A);
        }
        var pending = new Queue<int>();
        distances[start] = 0;
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var next in adjacent[current])
            {
                if (distances[next] >= 0) continue;
                distances[next] = distances[current] + 1;
                pending.Enqueue(next);
            }
        }
        return distances;
    }

    private static IEnumerable<GridEdge> GridEdges()
    {
        for (var row = 0; row < GridSize; row++)
        for (var column = 0; column < GridSize; column++)
        {
            var current = row * GridSize + column;
            if (column + 1 < GridSize) yield return new GridEdge(current, current + 1);
            if (row + 1 < GridSize) yield return new GridEdge(current, current + GridSize);
        }
    }

    private static IEnumerable<int> Neighbors(int index)
    {
        var row = index / GridSize;
        var column = index % GridSize;
        if (column > 0) yield return index - 1;
        if (column + 1 < GridSize) yield return index + 1;
        if (row > 0) yield return index - GridSize;
        if (row + 1 < GridSize) yield return index + GridSize;
    }

    private static void Shuffle<T>(IList<T> values, ref StableRandom random)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var other = random.NextInt(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }
    }

    private static bool IsIncident(GridEdge edge, int node) => edge.A == node || edge.B == node;

    private static void ValidateRoom(WorkbenchRoom room, List<string> errors)
    {
        var min = room.Minimum;
        var max = room.Maximum;
        Require(Finite(min) && Finite(max), "room_coordinate_nonfinite", errors);
        Require(Bounded(min) && Bounded(max), "room_coordinate_out_of_bounds", errors);
        Require(min.X < max.X && min.Y < max.Y && min.Z < max.Z, "room_extent_invalid", errors);
        Require(min.Y == Floor && max.Y == Roof, "room_vertical_bounds_invalid", errors);
        var halfX = (max.X - min.X) / 2f;
        var halfZ = (max.Z - min.Z) / 2f;
        Require(halfX >= MinimumHalfExtent && halfX <= MaximumHalfExtent, "room_width_invalid", errors);
        Require(halfZ >= MinimumHalfExtent && halfZ <= MaximumHalfExtent, "room_depth_invalid", errors);
    }

    private static void ValidateSpacing(IReadOnlyList<float> coordinates, string axis, List<string> errors)
    {
        for (var index = 1; index < coordinates.Count; index++)
        {
            var spacing = coordinates[index] - coordinates[index - 1];
            Require(spacing >= MinimumSpacing - CoordinateTolerance && spacing <= MaximumSpacing + CoordinateTolerance, $"grid_{axis}_spacing_invalid", errors);
        }
    }

    private static List<float> DistinctCoordinates(IEnumerable<float> coordinates) => coordinates
        .OrderBy(value => value)
        .Aggregate(new List<float>(), (values, value) =>
        {
            if (values.Count == 0 || Math.Abs(values[^1] - value) > CoordinateTolerance) values.Add(value);
            return values;
        });

    private static int CoordinateIndex(IReadOnlyList<float> coordinates, float value)
    {
        for (var index = 0; index < coordinates.Count; index++)
            if (Math.Abs(coordinates[index] - value) <= CoordinateTolerance) return index;
        return -1;
    }

    private static HashSet<string> ReachableRooms(
        IReadOnlyList<WorkbenchRoom> rooms,
        IReadOnlyList<WorkbenchRoute> routes,
        string start,
        bool switchOpen)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var route in routes)
            {
                if (route is null) continue;
                if (route.RequiresSwitch && !switchOpen) continue;
                var next = StringComparer.Ordinal.Equals(route.From, current) ? route.To
                    : StringComparer.Ordinal.Equals(route.To, current) ? route.From
                    : null;
                if (next is null || !rooms.Any(room => room is not null && StringComparer.Ordinal.Equals(room.Id, next)) || !reachable.Add(next)) continue;
                pending.Enqueue(next);
            }
        }
        return reachable;
    }

    private static WorkbenchPoint Center(WorkbenchRoom room) => new(
        (room.Minimum.X + room.Maximum.X) / 2f,
        (room.Minimum.Y + room.Maximum.Y) / 2f,
        (room.Minimum.Z + room.Maximum.Z) / 2f);

    private static bool Finite(WorkbenchPoint point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    private static bool Bounded(WorkbenchPoint point) => MathF.Abs(point.X) <= 64f && MathF.Abs(point.Y) <= 16f && MathF.Abs(point.Z) <= 64f;
    private static bool Overlaps(WorkbenchRoom left, WorkbenchRoom right) => left.Minimum.X < right.Maximum.X && left.Maximum.X > right.Minimum.X && left.Minimum.Y < right.Maximum.Y && left.Maximum.Y > right.Minimum.Y && left.Minimum.Z < right.Maximum.Z && left.Maximum.Z > right.Minimum.Z;
    private static string Pair(string from, string to) => StringComparer.Ordinal.Compare(from, to) < 0 ? $"{from}\u001f{to}" : $"{to}\u001f{from}";

    private static void Require(bool condition, string error, List<string> errors)
    {
        if (!condition) errors.Add(error);
    }

    private readonly record struct GridEdge
    {
        internal GridEdge(int first, int second)
        {
            A = Math.Min(first, second);
            B = Math.Max(first, second);
        }

        internal int A { get; }
        internal int B { get; }
    }

    private struct StableRandom
    {
        private ulong state;

        internal StableRandom(ulong seed) => state = seed;

        internal int NextInt(int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            return (int)(NextUInt64() % (ulong)exclusiveMaximum);
        }

        private ulong NextUInt64()
        {
            state += 0x9E3779B97F4A7C15UL;
            var value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
