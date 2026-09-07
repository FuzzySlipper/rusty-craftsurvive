using System.Collections.ObjectModel;

namespace CraftSurvive.Procgen.Generation;

/// <summary>Pure, bounded, deterministic Candidate-to-dungeon generation facade.</summary>
public sealed class DungeonGenerator
{
    public DungeonGenerationResult Generate(Candidate candidate, GenerationPolicy policy, ulong seed, ShapeCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        catalog ??= ShapeCatalog.Default;
        var attempts = new List<GenerationAttempt>();
        var stages = new List<StageObservation>();
        var counters = new MutableCounters();
        try
        {
            ValidatePolicy(policy);
            var validation = Observe("graph", stages, counters, () => GraphValidator.Validate(candidate));
            if (!validation.IsValid) throw new GenerationFailure("graph", "candidate_invalid", validation.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Fatal).Detail);
            ValidateCatalog(catalog);
            var intermediate = Observe("intermediate", stages, counters, () => BuildIntermediate(candidate, counters));
            var layout = Observe("layout", stages, counters, () => BuildLayout(intermediate, policy, seed, counters));
            var requirements = Observe("requirements", stages, counters, () => BuildRequirements(intermediate));
            var matches = Observe("matching", stages, counters, () => Match(layout, requirements, catalog, policy, counters));
            var pieces = Observe("placement", stages, counters, () => Place(layout, matches, catalog, policy, counters));
            EnsureMaterializedBounds(pieces.SelectMany(piece => piece.WalkableCells), policy, "placement");
            var routed = Observe("routing", stages, counters, () => Route(intermediate, pieces, policy, counters));
            var sockets = pieces.SelectMany(piece => piece.Sockets.Values.OrderBy(socket => socket.Id, StringComparer.Ordinal).Select(socket => new SocketFact($"socket.{piece.RegionId}.{socket.Id}", piece.RegionId, socket.Kind, socket.Cell))).OrderBy(socket => socket.Id, StringComparer.Ordinal).ToArray();
            var artifacts = new DungeonArtifacts(intermediate, layout, requirements, matches, pieces, routed.Routes, routed.Portals, sockets, routed.Walkable);
            var materialized = EnsureMaterializedBounds(artifacts.WalkableCells, policy, "routing");
            var flow = Observe("built_flow", stages, counters, () => ValidateBuiltFlow(candidate, artifacts));
            if (!flow.Valid) throw new GenerationFailure("built_flow", "physical_logical_mismatch", flow.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Fatal).Detail);
            var snapshot = counters.Snapshot();
            attempts.Add(new GenerationAttempt(1, "complete", true, "accepted", "All generation stages accepted.", snapshot));
            var metrics = DungeonGenerationResult.ComputeMetrics(snapshot, materialized.Width, materialized.Height, materialized.Area, artifacts.WalkableCells.Count);
            return new DungeonGenerationResult(true, GenerationIdentity.Hash(candidate, policy, seed, artifacts), null, null, attempts, stages, snapshot, metrics, artifacts, flow);
        }
        catch (GenerationFailure failure)
        {
            var snapshot = counters.Snapshot();
            attempts.Add(new GenerationAttempt(1, failure.Stage, false, failure.Code, failure.Message, snapshot));
            return DungeonGenerationResult.Rejected(failure.Stage, failure.Code, failure.Message, attempts, stages, snapshot);
        }
        catch (OverflowException exception)
        {
            var snapshot = counters.Snapshot();
            attempts.Add(new GenerationAttempt(1, "bounds", false, "coordinate_overflow", exception.Message, snapshot));
            return DungeonGenerationResult.Rejected("bounds", "coordinate_overflow", exception.Message, attempts, stages, snapshot);
        }
    }

    private static T Observe<T>(string stage, List<StageObservation> stages, MutableCounters counters, Func<T> operation)
    {
        try { return operation(); }
        finally { stages.Add(new StageObservation(stage, counters.Snapshot())); }
    }

    private static void ValidatePolicy(GenerationPolicy policy)
    {
        if (!GenerationPolicyValidation.IsValid(policy))
            throw new GenerationFailure("policy", "invalid_policy", "Generation policy contains an invalid bound or quota.");
    }

    private static void ValidateCatalog(ShapeCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.Id) || catalog.Shapes.Count == 0) throw new GenerationFailure("catalog", "catalog_empty", "Catalog must contain at least one shape.");
        var shapeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shape in catalog.Shapes.OrderBy(shape => shape.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(shape.Id) || !shapeIds.Add(shape.Id)) throw new GenerationFailure("catalog", "duplicate_shape_id", $"Catalog shape id '{shape.Id}' is duplicate or empty.");
            var cells = new HashSet<GridPoint>();
            if (shape.WalkableCells.Count == 0 || shape.WalkableCells.Any(cell => !cells.Add(cell))) throw new GenerationFailure("catalog", "shape_cells_invalid", $"Shape '{shape.Id}' has no cells or duplicate cells.");
            var exits = new HashSet<string>(StringComparer.Ordinal);
            foreach (var exit in shape.Exits) if (string.IsNullOrWhiteSpace(exit.Id) || !exits.Add(exit.Id) || !cells.Contains(exit.Cell)) throw new GenerationFailure("catalog", "shape_exit_invalid", $"Shape '{shape.Id}' has an invalid exit '{exit.Id}'.");
            var sockets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var socket in shape.Sockets ?? Array.Empty<CatalogSocket>()) if (string.IsNullOrWhiteSpace(socket.Id) || !sockets.Add(socket.Id) || !cells.Contains(socket.Cell)) throw new GenerationFailure("catalog", "shape_socket_invalid", $"Shape '{shape.Id}' has an invalid socket '{socket.Id}'.");
        }
    }

    private static IntermediateDungeon BuildIntermediate(Candidate candidate, MutableCounters counters)
    {
        var nodes = candidate.Graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var regions = nodes.Values.Select(node => new IntermediateRegion($"region.{node.Id}", node.Id, node.Kind, node.Kind.ToString().ToLowerInvariant(), node.GrantsItem, node.Tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray())).ToArray();
        var byNode = regions.ToDictionary(region => region.SourceNodeId, StringComparer.Ordinal);
        var connections = candidate.Graph.Edges.OrderBy(edge => edge.Id, StringComparer.Ordinal).Select(edge =>
        {
            if (!byNode.TryGetValue(edge.From, out var from) || !byNode.TryGetValue(edge.To, out var to)) throw new GenerationFailure("intermediate", "edge_node_missing", $"Graph edge '{edge.Id}' references a missing region.");
            return new IntermediateConnection($"connection.{edge.Id}", edge.Id, from.Id, to.Id, edge.Kind, edge.Traversal, edge.RequiredItem, edge.Tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray());
        }).ToArray();
        counters.Regions = regions.Length; counters.Connections = connections.Length;
        return new IntermediateDungeon(candidate.Id, CanonicalIdentity.Hash(candidate), regions, connections);
    }

    private static LayoutPlan BuildLayout(IntermediateDungeon dungeon, GenerationPolicy policy, ulong seed, MutableCounters counters)
    {
        var start = dungeon.Regions.SingleOrDefault(region => region.Kind == NodeKind.Start) ?? throw new GenerationFailure("layout", "start_region_missing", "Intermediate dungeon has no start region.");
        var adjacent = dungeon.Connections.SelectMany(connection => new[] { (connection.FromRegionId, connection.ToRegionId), (connection.ToRegionId, connection.FromRegionId) }).GroupBy(pair => pair.Item1, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(pair => pair.Item2).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var origins = new Dictionary<string, GridPoint>(StringComparer.Ordinal) { [start.Id] = new GridPoint(policy.MaxWidth / 2, policy.MaxHeight / 2) };
        var frontier = new Queue<string>(); frontier.Enqueue(start.Id);
        var offset = (int)(seed % 4);
        while (frontier.TryDequeue(out var source))
        {
            if (!adjacent.TryGetValue(source, out var neighbors)) continue;
            foreach (var target in neighbors)
            {
                if (origins.ContainsKey(target)) continue;
                var placed = false;
                for (var radius = 1; radius <= policy.RoomCandidatesPerRegion && !placed; radius++)
                foreach (var direction in CardinalDirections.Ordered.Select((direction, index) => CardinalDirections.Ordered[(index + offset) % 4]))
                {
                    if (++counters.LayoutExpansions > policy.MaxLayoutExpansions) throw new GenerationFailure("layout", "layout_quota_exhausted", "Layout expansion quota was exhausted.");
                    var origin = origins[source] + new GridPoint(direction.Offset().X * checked(policy.RoomStride * radius), direction.Offset().Y * checked(policy.RoomStride * radius));
                    if (!Fits(origin, policy.RoomFootprintCells, policy.RoomFootprintCells, policy) || origins.Values.Any(existing => Math.Abs(existing.X - origin.X) < policy.RoomStride && Math.Abs(existing.Y - origin.Y) < policy.RoomStride)) continue;
                    origins.Add(target, origin); frontier.Enqueue(target); placed = true; break;
                }
                if (!placed) throw new GenerationFailure("layout", "layout_no_slot", $"No bounded layout slot was found for '{target}'.");
            }
        }
        if (origins.Count != dungeon.Regions.Count) throw new GenerationFailure("layout", "layout_disconnected", "Graph regions are disconnected from the start region.");
        var rooms = origins.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new LayoutRoom(pair.Key, pair.Value, policy.RoomFootprintCells, policy.RoomFootprintCells)).ToArray();
        var minX = rooms.Min(room => room.Origin.X); var minY = rooms.Min(room => room.Origin.Y); var maxX = rooms.Max(room => checked(room.Origin.X + room.Width)); var maxY = rooms.Max(room => checked(room.Origin.Y + room.Height));
        var width = checked(maxX - minX); var height = checked(maxY - minY); var area = checked(width * height);
        if (width > policy.MaxWidth || height > policy.MaxHeight || area > policy.MaxArtifactCells) throw new GenerationFailure("layout", "layout_bounds_exceeded", "Layout exceeded configured dimensions or artifact-cell quota.");
        return new LayoutPlan(rooms, width, height, area);
    }

    private static bool Fits(GridPoint origin, int width, int height, GenerationPolicy policy) => origin.X >= 0 && origin.Y >= 0 && checked(origin.X + width) <= policy.MaxWidth && checked(origin.Y + height) <= policy.MaxHeight;

    private static MaterializedBounds EnsureMaterializedBounds(IEnumerable<GridPoint> sourceCells, GenerationPolicy policy, string stage)
    {
        var cells = sourceCells.ToArray();
        if (cells.Length == 0) throw new GenerationFailure(stage, "materialized_cells_empty", "Materialized geometry must contain at least one walkable cell.");
        if (cells.Any(cell => cell.X < 0 || cell.Y < 0 || cell.X >= policy.MaxWidth || cell.Y >= policy.MaxHeight))
            throw new GenerationFailure(stage, "materialized_coordinate_bounds_exceeded", $"Materialized geometry has a cell outside the [0,{policy.MaxWidth}) x [0,{policy.MaxHeight}) policy bounds.");
        var minX = cells.Min(cell => cell.X); var maxX = cells.Max(cell => cell.X); var minY = cells.Min(cell => cell.Y); var maxY = cells.Max(cell => cell.Y);
        var width = checked(maxX - minX + 1); var height = checked(maxY - minY + 1); var area = checked(width * height);
        if (width > policy.MaxWidth) throw new GenerationFailure(stage, "materialized_width_exceeded", $"Materialized geometry width {width} exceeds policy maximum {policy.MaxWidth}.");
        if (height > policy.MaxHeight) throw new GenerationFailure(stage, "materialized_height_exceeded", $"Materialized geometry height {height} exceeds policy maximum {policy.MaxHeight}.");
        if (area > policy.MaxArtifactCells || cells.Distinct().Count() > policy.MaxArtifactCells) throw new GenerationFailure(stage, "materialized_area_exceeded", $"Materialized geometry area {area} or cell count exceeds policy maximum {policy.MaxArtifactCells}.");
        return new MaterializedBounds(width, height, area);
    }

    private static IReadOnlyList<PieceRequirement> BuildRequirements(IntermediateDungeon dungeon) => dungeon.Regions.OrderBy(region => region.Id, StringComparer.Ordinal).Select(region => new PieceRequirement(region.Id, dungeon.Connections.Where(connection => connection.FromRegionId == region.Id || connection.ToRegionId == region.Id).OrderBy(connection => connection.SourceEdgeId, StringComparer.Ordinal).ToArray(), string.IsNullOrWhiteSpace(region.GrantsItem) ? Array.Empty<string>() : new[] { "content" })).ToArray();

    private static IReadOnlyList<MatchedShape> Match(LayoutPlan layout, IReadOnlyList<PieceRequirement> requirements, ShapeCatalog catalog, GenerationPolicy policy, MutableCounters counters)
    {
        var cache = new Dictionary<(string ShapeId, int Turns), TransformedShape>();
        var rooms = layout.Rooms.ToDictionary(room => room.RegionId, StringComparer.Ordinal);
        var result = new List<MatchedShape>(requirements.Count);
        foreach (var requirement in requirements.OrderBy(requirement => requirement.RegionId, StringComparer.Ordinal))
        {
            var selected = default(MatchedShape);
            foreach (var shape in catalog.Shapes.OrderBy(shape => shape.Id, StringComparer.Ordinal))
            for (var turns = 0; turns < 4; turns++)
            {
                if (++counters.CatalogCandidates > policy.MaxCatalogCandidatesPerRequirement * requirements.Count) throw new GenerationFailure("matching", "catalog_candidate_quota_exhausted", "Catalog matching candidate quota was exhausted.");
                if (!cache.TryGetValue((shape.Id, turns), out var transformed)) cache[(shape.Id, turns)] = transformed = Transform(shape, turns);
                if (transformed.Exits.Count < requirement.Connections.Count || requirement.RequiredSockets.Any(required => !transformed.Sockets.Any(socket => string.Equals(socket.Kind, required, StringComparison.Ordinal)))) continue;
                var availableExits = transformed.Exits.OrderBy(exit => exit.Id, StringComparer.Ordinal).ToList();
                var chosenExits = new List<CatalogExit>();
                foreach (var connection in requirement.Connections.OrderBy(connection => connection.SourceEdgeId, StringComparer.Ordinal))
                {
                    var otherRegion = connection.FromRegionId == requirement.RegionId ? connection.ToRegionId : connection.FromRegionId;
                    var delta = new GridPoint(rooms[otherRegion].Origin.X - rooms[requirement.RegionId].Origin.X, rooms[otherRegion].Origin.Y - rooms[requirement.RegionId].Origin.Y);
                    var exit = availableExits.OrderByDescending(candidate => candidate.Direction.Offset().X * delta.X + candidate.Direction.Offset().Y * delta.Y).ThenBy(candidate => candidate.Id, StringComparer.Ordinal).First();
                    availableExits.Remove(exit); chosenExits.Add(exit);
                }
                if (chosenExits.Count != requirement.Connections.Count) continue;
                var exitMap = requirement.Connections.OrderBy(connection => connection.SourceEdgeId, StringComparer.Ordinal).Select((connection, index) => (connection.SourceEdgeId, chosenExits[index])).ToDictionary(pair => pair.SourceEdgeId, pair => pair.Item2, StringComparer.Ordinal);
                selected = new MatchedShape(requirement.RegionId, shape.Id, turns, new ReadOnlyDictionary<string, CatalogExit>(exitMap), new ReadOnlyDictionary<string, CatalogSocket>(transformed.Sockets.ToDictionary(socket => socket.Id, StringComparer.Ordinal)));
                break;
            }
            if (selected is null) throw new GenerationFailure("matching", "no_matching_exits", $"No catalog shape has enough exits for '{requirement.RegionId}'.");
            result.Add(selected);
        }
        return result;
    }

    private static TransformedShape Transform(CatalogShape shape, int turns)
    {
        GridPoint Point(GridPoint point) { var value = point; for (var i = 0; i < turns; i++) value = new GridPoint(checked(-value.Y), value.X); return value; }
        var cells = shape.WalkableCells.Select(Point).ToArray(); var minX = cells.Min(cell => cell.X); var minY = cells.Min(cell => cell.Y);
        GridPoint Normalize(GridPoint point) { var rotated = Point(point); return new GridPoint(checked(rotated.X - minX), checked(rotated.Y - minY)); }
        return new TransformedShape(cells.Select(cell => new GridPoint(checked(cell.X - minX), checked(cell.Y - minY))).ToArray(), shape.Exits.Select(exit => exit with { Cell = Normalize(exit.Cell), Direction = exit.Direction.Rotate(turns) }).ToArray(), (shape.Sockets ?? Array.Empty<CatalogSocket>()).Select(socket => socket with { Cell = Normalize(socket.Cell) }).ToArray());
    }

    private static IReadOnlyList<PlacedPiece> Place(LayoutPlan layout, IReadOnlyList<MatchedShape> matches, ShapeCatalog catalog, GenerationPolicy policy, MutableCounters counters)
    {
        var rooms = layout.Rooms.ToDictionary(room => room.RegionId, StringComparer.Ordinal); var state = new PlacementState(); var results = new List<PlacedPiece>();
        foreach (var match in matches.OrderBy(match => match.RegionId, StringComparer.Ordinal))
        {
            var checkpoint = state.Checkpoint; var room = rooms[match.RegionId]; var shape = catalog.Shapes.Single(shape => shape.Id == match.ShapeId); var transformed = Transform(shape, match.QuarterTurns);
            var origin = room.Origin;
            var placedCells = transformed.Cells.Select(cell => cell + origin).ToArray();
            if (++counters.PlacementDecisions > policy.MaxPlacementDecisions || !state.TryPlace(match.RegionId, placedCells, policy.MaxArtifactCells))
            {
                state.Rollback(checkpoint); counters.PlacementBacktracks++;
                if (counters.PlacementBacktracks > policy.MaxPlacementBacktracks) throw new GenerationFailure("placement", "placement_backtrack_quota_exhausted", "Placement rollback quota was exhausted.");
                throw new GenerationFailure("placement", "placement_collision", $"Shape '{shape.Id}' overlaps an existing placed shape.");
            }
            var exits = match.ExitMap.ToDictionary(pair => pair.Key, pair => pair.Value with { Cell = pair.Value.Cell + origin }, StringComparer.Ordinal);
            var sockets = match.SocketMap.ToDictionary(pair => pair.Key, pair => pair.Value with { Cell = pair.Value.Cell + origin }, StringComparer.Ordinal);
            results.Add(new PlacedPiece(match.RegionId, shape.Id, match.QuarterTurns, origin, placedCells, new ReadOnlyDictionary<string, CatalogExit>(exits), new ReadOnlyDictionary<string, CatalogSocket>(sockets)));
        }
        return results;
    }

    private static RouteOutcome Route(IntermediateDungeon dungeon, IReadOnlyList<PlacedPiece> pieces, GenerationPolicy policy, MutableCounters counters)
    {
        var byRegion = pieces.ToDictionary(piece => piece.RegionId, StringComparer.Ordinal); var occupied = new HashSet<GridPoint>(pieces.SelectMany(piece => piece.WalkableCells)); var walkable = new HashSet<GridPoint>(occupied); var routes = new List<CorridorRoute>(); var portals = new List<PortalFact>();
        foreach (var connection in dungeon.Connections.OrderBy(connection => connection.SourceEdgeId, StringComparer.Ordinal))
        {
            if (++counters.RouteAttempts > policy.MaxRouteAttempts * dungeon.Connections.Count) throw new GenerationFailure("routing", "route_attempt_quota_exhausted", "Route-attempt quota was exhausted.");
            var fromExit = byRegion[connection.FromRegionId].Exits[connection.SourceEdgeId]; var toExit = byRegion[connection.ToRegionId].Exits[connection.SourceEdgeId];
            var from = fromExit.Cell + fromExit.Direction.Offset(); var to = toExit.Cell + toExit.Direction.Offset();
            var routedCells = FindPath(from, to, occupied, policy, counters);
            if (routedCells is null) throw new GenerationFailure("routing", "route_collision_or_exhaustion", $"No bounded orthogonal route exists for '{connection.SourceEdgeId}' from {from} to {to}.");
            var path = new[] { fromExit.Cell }.Concat(routedCells).Append(toExit.Cell).ToArray();
            foreach (var cell in path) { occupied.Add(cell); walkable.Add(cell); }
            counters.RoutedCells += path.Length; counters.RouteBends += CountBends(path);
            routes.Add(new CorridorRoute($"route.{connection.SourceEdgeId}", connection.SourceEdgeId, connection.FromRegionId, connection.ToRegionId, path, connection.Traversal, connection.RequiredItem));
            if (connection.Traversal is TraversalKind.Locked or TraversalKind.OneWayReturn or TraversalKind.Hidden) portals.Add(new PortalFact($"portal.{connection.SourceEdgeId}", connection.SourceEdgeId, path[path.Length / 2], connection.Traversal, connection.RequiredItem));
        }
        return new RouteOutcome(routes, portals, walkable);
    }

    private static IReadOnlyList<GridPoint>? FindPath(GridPoint start, GridPoint goal, HashSet<GridPoint> occupied, GenerationPolicy policy, MutableCounters counters)
    {
        var queue = new PriorityQueue<GridPoint, (int Cost, int Tie)>(); var cameFrom = new Dictionary<GridPoint, GridPoint>(); var cost = new Dictionary<GridPoint, int> { [start] = 0 }; var tie = 0; var localExpansions = 0; queue.Enqueue(start, (start.ManhattanDistance(goal), tie++));
        while (queue.TryDequeue(out var current, out _))
        {
            counters.RouteExpansions++;
            if (++localExpansions > policy.MaxRouteExpansionsPerConnection) throw new GenerationFailure("routing", "route_expansion_quota_exhausted", "Route expansion quota was exhausted.");
            if (current == goal)
            {
                var path = new List<GridPoint> { current }; while (cameFrom.TryGetValue(path[^1], out var previous)) path.Add(previous); path.Reverse(); return path;
            }
            foreach (var direction in CardinalDirections.Ordered)
            {
                var next = current + direction.Offset();
                if (next.X < 0 || next.Y < 0 || next.X >= policy.MaxWidth || next.Y >= policy.MaxHeight || (occupied.Contains(next) && next != goal && next != start)) continue;
                var nextCost = checked(cost[current] + 1);
                if (cost.TryGetValue(next, out var known) && known <= nextCost) continue;
                cost[next] = nextCost; cameFrom[next] = current; queue.Enqueue(next, (checked(nextCost + next.ManhattanDistance(goal)), tie++));
            }
        }
        return null;
    }

    public BuiltFlowReport ValidateBuiltFlow(Candidate candidate, DungeonArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(artifacts);
        var diagnostics = new List<Diagnostic>(); var logical = GraphValidator.ReachableWithItems(candidate).ReachedNodes;
        ValidateCandidateArtifactBinding(candidate, artifacts, diagnostics);
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Fatal))
            return new BuiltFlowReport(false, diagnostics.OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal).ToArray(), logical, new HashSet<string>(StringComparer.Ordinal));
        var regions = artifacts.Intermediate.Regions.ToDictionary(region => region.Id, StringComparer.Ordinal); var start = regions.Values.Single(region => region.Kind == NodeKind.Start); var reached = new HashSet<string>(StringComparer.Ordinal) { start.Id }; var items = new HashSet<string>(StringComparer.Ordinal);
        var routes = artifacts.Routes.ToDictionary(route => route.SourceEdgeId, StringComparer.Ordinal); var pieces = artifacts.Pieces.ToDictionary(piece => piece.RegionId, StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var regionId in reached.OrderBy(id => id, StringComparer.Ordinal).ToArray()) if (regions[regionId].GrantsItem is { Length: > 0 } item && items.Add(item)) changed = true;
            foreach (var connection in artifacts.Intermediate.Connections.OrderBy(connection => connection.SourceEdgeId, StringComparer.Ordinal))
            {
                if (!routes.TryGetValue(connection.SourceEdgeId, out var route) || !pieces.TryGetValue(connection.FromRegionId, out var fromPiece) || !pieces.TryGetValue(connection.ToRegionId, out var toPiece) || route.FromRegionId != connection.FromRegionId || route.ToRegionId != connection.ToRegionId || route.Traversal != connection.Traversal || route.RequiredItem != connection.RequiredItem || !fromPiece.Exits.TryGetValue(connection.SourceEdgeId, out var fromExit) || !toPiece.Exits.TryGetValue(connection.SourceEdgeId, out var toExit) || !RouteBindsExits(route, fromExit, toExit) || !IsWalkableRoute(route, artifacts.WalkableCells) || !fromPiece.WalkableCells.Contains(route.Cells[0]) || !toPiece.WalkableCells.Contains(route.Cells[^1])) { diagnostics.Add(new Diagnostic("built_route_missing_or_discontinuous", DiagnosticSeverity.Fatal, $"Connection '{connection.SourceEdgeId}' has no continuous provenance-preserving physical route.", EdgeId: connection.SourceEdgeId)); continue; }
                if (reached.Contains(connection.FromRegionId) && (connection.RequiredItem is null || items.Contains(connection.RequiredItem)) && reached.Add(connection.ToRegionId)) changed = true;
            }
        }
        var physicalNodes = reached.Select(region => regions[region].SourceNodeId).ToHashSet(StringComparer.Ordinal);
        if (!logical.SetEquals(physicalNodes)) diagnostics.Add(new Diagnostic("built_flow_reachability_mismatch", DiagnosticSeverity.Fatal, "Item-aware physical reachability does not match logical graph reachability."));
        var expectedWalkable = artifacts.Pieces.SelectMany(piece => piece.WalkableCells).Concat(artifacts.Routes.SelectMany(route => route.Cells)).ToHashSet();
        if (!expectedWalkable.SetEquals(artifacts.WalkableCells)) diagnostics.Add(new Diagnostic("built_flow_walkable_tampered", DiagnosticSeverity.Fatal, "Walkable projection does not match placed-piece and corridor facts."));
        var expectedPortalEdges = artifacts.Routes.Where(route => route.Traversal is TraversalKind.Locked or TraversalKind.OneWayReturn or TraversalKind.Hidden).Select(route => route.SourceEdgeId).ToHashSet(StringComparer.Ordinal);
        var portalsByEdge = artifacts.Portals.GroupBy(portal => portal.SourceEdgeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var edgeId in expectedPortalEdges)
        {
            if (!portalsByEdge.TryGetValue(edgeId, out var portals) || portals.Length != 1 || !routes[edgeId].Cells.Contains(portals[0].Cell) || portals[0].Traversal != routes[edgeId].Traversal || portals[0].RequiredItem != routes[edgeId].RequiredItem)
                diagnostics.Add(new Diagnostic("built_portal_provenance_mismatch", DiagnosticSeverity.Fatal, $"Portal facts do not preserve '{edgeId}' traversal provenance.", EdgeId: edgeId));
        }
        if (portalsByEdge.Keys.Any(edgeId => !expectedPortalEdges.Contains(edgeId))) diagnostics.Add(new Diagnostic("built_portal_extra", DiagnosticSeverity.Fatal, "Built portal facts include an edge that does not require a portal."));
        return new BuiltFlowReport(diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Fatal), diagnostics.OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal).ToArray(), logical, reached);
    }

    private static void ValidateCandidateArtifactBinding(Candidate candidate, DungeonArtifacts artifacts, List<Diagnostic> diagnostics)
    {
        var candidateValidation = GraphValidator.Validate(candidate);
        if (!candidateValidation.IsValid)
        {
            diagnostics.Add(new Diagnostic("built_flow_candidate_invalid", DiagnosticSeverity.Fatal, "Built-flow validation requires a valid candidate graph."));
            return;
        }
        var intermediate = artifacts.Intermediate;
        if (!StringComparer.Ordinal.Equals(candidate.Id, intermediate.CandidateId) || !StringComparer.Ordinal.Equals(CanonicalIdentity.Hash(candidate), intermediate.CandidateHash))
            diagnostics.Add(new Diagnostic("built_flow_candidate_identity_mismatch", DiagnosticSeverity.Fatal, "Candidate identity or canonical graph hash does not match the intermediate artifact."));

        var regionsBySource = intermediate.Regions.GroupBy(region => region.SourceNodeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (intermediate.Regions.Count != candidate.Graph.Nodes.Count || regionsBySource.Count != candidate.Graph.Nodes.Count)
            diagnostics.Add(new Diagnostic("built_flow_region_set_mismatch", DiagnosticSeverity.Fatal, "Intermediate regions do not form a one-to-one complete mapping of candidate nodes."));
        foreach (var node in candidate.Graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (!regionsBySource.TryGetValue(node.Id, out var regions) || regions.Length != 1)
            {
                diagnostics.Add(new Diagnostic("built_flow_region_missing", DiagnosticSeverity.Fatal, $"Candidate node '{node.Id}' does not have exactly one intermediate region.", NodeId: node.Id));
                continue;
            }
            var region = regions[0];
            if (!StringComparer.Ordinal.Equals(region.Id, $"region.{node.Id}") || region.Kind != node.Kind || !StringComparer.Ordinal.Equals(region.Role, node.Kind.ToString().ToLowerInvariant()) || !StringComparer.Ordinal.Equals(region.GrantsItem, node.GrantsItem) || !SequenceEqualOrdinal(region.Tags, node.Tags))
                diagnostics.Add(new Diagnostic("built_flow_region_semantics_mismatch", DiagnosticSeverity.Fatal, $"Intermediate region '{region.Id}' does not exactly preserve candidate node '{node.Id}' semantics.", NodeId: node.Id));
        }

        var connectionsByEdge = intermediate.Connections.GroupBy(connection => connection.SourceEdgeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (intermediate.Connections.Count != candidate.Graph.Edges.Count || connectionsByEdge.Count != candidate.Graph.Edges.Count)
            diagnostics.Add(new Diagnostic("built_flow_connection_set_mismatch", DiagnosticSeverity.Fatal, "Intermediate connections do not form a one-to-one complete mapping of candidate edges."));
        foreach (var edge in candidate.Graph.Edges.OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            if (!connectionsByEdge.TryGetValue(edge.Id, out var connections) || connections.Length != 1)
            {
                diagnostics.Add(new Diagnostic("built_flow_connection_missing", DiagnosticSeverity.Fatal, $"Candidate edge '{edge.Id}' does not have exactly one intermediate connection.", EdgeId: edge.Id));
                continue;
            }
            var connection = connections[0];
            if (!StringComparer.Ordinal.Equals(connection.Id, $"connection.{edge.Id}") || !StringComparer.Ordinal.Equals(connection.FromRegionId, $"region.{edge.From}") || !StringComparer.Ordinal.Equals(connection.ToRegionId, $"region.{edge.To}") || connection.Kind != edge.Kind || connection.Traversal != edge.Traversal || !StringComparer.Ordinal.Equals(connection.RequiredItem, edge.RequiredItem) || !SequenceEqualOrdinal(connection.Tags, edge.Tags))
                diagnostics.Add(new Diagnostic("built_flow_connection_semantics_mismatch", DiagnosticSeverity.Fatal, $"Intermediate connection '{connection.Id}' does not exactly preserve candidate edge '{edge.Id}' semantics.", EdgeId: edge.Id));
        }

        var routesByEdge = artifacts.Routes.GroupBy(route => route.SourceEdgeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (artifacts.Routes.Count != intermediate.Connections.Count || routesByEdge.Count != intermediate.Connections.Count)
            diagnostics.Add(new Diagnostic("built_flow_route_set_mismatch", DiagnosticSeverity.Fatal, "Physical routes do not form a one-to-one complete mapping of intermediate connections."));
        foreach (var connection in intermediate.Connections.OrderBy(connection => connection.SourceEdgeId, StringComparer.Ordinal))
        {
            if (!routesByEdge.TryGetValue(connection.SourceEdgeId, out var routes) || routes.Length != 1)
                diagnostics.Add(new Diagnostic("built_flow_route_missing", DiagnosticSeverity.Fatal, $"Intermediate connection '{connection.SourceEdgeId}' does not have exactly one physical route.", EdgeId: connection.SourceEdgeId));
        }
        foreach (var edgeId in routesByEdge.Keys.Where(edgeId => !connectionsByEdge.ContainsKey(edgeId)).OrderBy(edgeId => edgeId, StringComparer.Ordinal))
            diagnostics.Add(new Diagnostic("built_flow_route_extra", DiagnosticSeverity.Fatal, $"Physical route '{edgeId}' has no authoritative intermediate connection.", EdgeId: edgeId));

        var requirementsByRegion = artifacts.Requirements.GroupBy(requirement => requirement.RegionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var region in intermediate.Regions)
        {
            if (!requirementsByRegion.TryGetValue(region.Id, out var requirements) || requirements.Length != 1 || !RequirementsMatch(region, intermediate.Connections, requirements[0]))
                diagnostics.Add(new Diagnostic("built_flow_requirement_chain_mismatch", DiagnosticSeverity.Fatal, $"Piece requirement chain is stale or incomplete for '{region.Id}'.", NodeId: region.SourceNodeId));
        }
        if (requirementsByRegion.Count != intermediate.Regions.Count) diagnostics.Add(new Diagnostic("built_flow_requirement_set_mismatch", DiagnosticSeverity.Fatal, "Piece requirement chain includes extra or duplicate regions."));

        var matchesByRegion = artifacts.Matches.GroupBy(match => match.RegionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var piecesByRegion = artifacts.Pieces.GroupBy(piece => piece.RegionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var requirement in artifacts.Requirements)
        {
            if (!matchesByRegion.TryGetValue(requirement.RegionId, out var matches) || matches.Length != 1 || !piecesByRegion.TryGetValue(requirement.RegionId, out var pieces) || pieces.Length != 1 || !MatchAndPieceAgree(requirement, matches[0], pieces[0]))
                diagnostics.Add(new Diagnostic("built_flow_match_chain_mismatch", DiagnosticSeverity.Fatal, $"Matched-shape or placed-piece chain is stale or incomplete for '{requirement.RegionId}'.", NodeId: requirement.RegionId));
        }
        if (matchesByRegion.Count != artifacts.Requirements.Count || piecesByRegion.Count != artifacts.Requirements.Count) diagnostics.Add(new Diagnostic("built_flow_match_set_mismatch", DiagnosticSeverity.Fatal, "Matched-shape or placed-piece chain includes extra or duplicate regions."));

        if (!SocketFactsMatch(artifacts))
            diagnostics.Add(new Diagnostic("built_socket_provenance_mismatch", DiagnosticSeverity.Fatal, "Published socket facts do not exactly preserve the matched and placed socket set."));
    }

    private static bool RequirementsMatch(IntermediateRegion region, IReadOnlyList<IntermediateConnection> connections, PieceRequirement requirement) =>
        requirement.RegionId == region.Id &&
        SequenceEqualOrdinal(requirement.RequiredSockets, string.IsNullOrWhiteSpace(region.GrantsItem) ? Array.Empty<string>() : new[] { "content" }) &&
        ConnectionSetsEqual(requirement.Connections, connections.Where(connection => connection.FromRegionId == region.Id || connection.ToRegionId == region.Id));

    private static bool MatchAndPieceAgree(PieceRequirement requirement, MatchedShape match, PlacedPiece piece) =>
        StringComparer.Ordinal.Equals(match.RegionId, piece.RegionId) &&
        StringComparer.Ordinal.Equals(match.ShapeId, piece.ShapeId) &&
        match.QuarterTurns == piece.QuarterTurns &&
        ExactKeys(match.ExitMap, requirement.Connections.Select(connection => connection.SourceEdgeId)) &&
        HasRequiredSocketKinds(requirement.RequiredSockets, match.SocketMap.Values) &&
        KeyedValuesMatch(match.ExitMap, static (exit, origin) => exit with { Cell = exit.Cell + origin }, piece.Exits, piece.Origin, CatalogExitEqual) &&
        KeyedValuesMatch(match.SocketMap, static (socket, origin) => socket with { Cell = socket.Cell + origin }, piece.Sockets, piece.Origin, CatalogSocketEqual, requireKeyedIdentity: true);

    private static bool ConnectionSetsEqual(IEnumerable<IntermediateConnection> left, IEnumerable<IntermediateConnection> right)
    {
        var leftByEdge = left.GroupBy(connection => connection.SourceEdgeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var rightByEdge = right.GroupBy(connection => connection.SourceEdgeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return leftByEdge.Count == rightByEdge.Count && leftByEdge.All(pair => pair.Value.Length == 1 && rightByEdge.TryGetValue(pair.Key, out var expected) && expected.Length == 1 && IntermediateConnectionEqual(pair.Value[0], expected[0]));
    }

    private static bool IntermediateConnectionEqual(IntermediateConnection left, IntermediateConnection right) =>
        StringComparer.Ordinal.Equals(left.Id, right.Id) &&
        StringComparer.Ordinal.Equals(left.SourceEdgeId, right.SourceEdgeId) &&
        StringComparer.Ordinal.Equals(left.FromRegionId, right.FromRegionId) &&
        StringComparer.Ordinal.Equals(left.ToRegionId, right.ToRegionId) &&
        left.Kind == right.Kind &&
        left.Traversal == right.Traversal &&
        StringComparer.Ordinal.Equals(left.RequiredItem, right.RequiredItem) &&
        SequenceEqualOrdinal(left.Tags, right.Tags);

    private static bool ExactKeys<TValue>(IReadOnlyDictionary<string, TValue> values, IEnumerable<string> expected) =>
        values.Count == expected.Count() && values.Keys.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool HasRequiredSocketKinds(IEnumerable<string> requiredKinds, IEnumerable<CatalogSocket> sockets)
    {
        var available = sockets.GroupBy(socket => socket.Kind, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var required in requiredKinds.OrderBy(kind => kind, StringComparer.Ordinal))
        {
            if (!available.TryGetValue(required, out var count) || count == 0) return false;
            available[required] = count - 1;
        }
        return true;
    }

    private static bool KeyedValuesMatch<TValue>(IReadOnlyDictionary<string, TValue> source, Func<TValue, GridPoint, TValue> translate, IReadOnlyDictionary<string, TValue> placed, GridPoint origin, Func<TValue, TValue, bool> semanticEqual, bool requireKeyedIdentity = false) =>
        source.Count == placed.Count &&
        source.All(pair => (!requireKeyedIdentity || StringComparer.Ordinal.Equals(pair.Key, GetIdentity(pair.Value))) && placed.TryGetValue(pair.Key, out var placedValue) && (!requireKeyedIdentity || StringComparer.Ordinal.Equals(pair.Key, GetIdentity(placedValue))) && semanticEqual(translate(pair.Value, origin), placedValue));

    private static string GetIdentity<TValue>(TValue value) => value switch
    {
        CatalogExit exit => exit.Id,
        CatalogSocket socket => socket.Id,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static bool CatalogExitEqual(CatalogExit left, CatalogExit right) =>
        StringComparer.Ordinal.Equals(left.Id, right.Id) && left.Cell == right.Cell && left.Direction == right.Direction && SequenceEqualOrdinal(left.Tags ?? Array.Empty<string>(), right.Tags ?? Array.Empty<string>());

    private static bool CatalogSocketEqual(CatalogSocket left, CatalogSocket right) =>
        StringComparer.Ordinal.Equals(left.Id, right.Id) && left.Cell == right.Cell && StringComparer.Ordinal.Equals(left.Kind, right.Kind) && SequenceEqualOrdinal(left.Tags ?? Array.Empty<string>(), right.Tags ?? Array.Empty<string>());

    private static bool SocketFactsMatch(DungeonArtifacts artifacts)
    {
        var expected = artifacts.Pieces.SelectMany(piece => piece.Sockets.Values.Select(socket => new SocketFact($"socket.{piece.RegionId}.{socket.Id}", piece.RegionId, socket.Kind, socket.Cell))).GroupBy(socket => socket.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var actual = artifacts.Sockets.GroupBy(socket => socket.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return expected.Count == actual.Count && expected.All(pair => pair.Value.Length == 1 && actual.TryGetValue(pair.Key, out var facts) && facts.Length == 1 && SocketFactEqual(pair.Value[0], facts[0]));
    }

    private static bool SocketFactEqual(SocketFact left, SocketFact right) =>
        StringComparer.Ordinal.Equals(left.Id, right.Id) && StringComparer.Ordinal.Equals(left.RegionId, right.RegionId) && StringComparer.Ordinal.Equals(left.Kind, right.Kind) && left.Cell == right.Cell;

    private static bool SequenceEqualOrdinal(IEnumerable<string> left, IEnumerable<string> right) =>
        left.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(right.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static int CountBends(IReadOnlyList<GridPoint> path)
    {
        var bends = 0; GridPoint? previous = null;
        for (var i = 1; i < path.Count; i++) { var step = new GridPoint(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y); if (previous is not null && previous.Value != step) bends++; previous = step; }
        return bends;
    }

    private static bool IsWalkableRoute(CorridorRoute route, IReadOnlySet<GridPoint> walkable) => route.Cells.Count > 0 && route.Cells.All(walkable.Contains) && route.Cells.Zip(route.Cells.Skip(1)).All(pair => pair.First.ManhattanDistance(pair.Second) == 1);

    private static bool RouteBindsExits(CorridorRoute route, CatalogExit fromExit, CatalogExit toExit) =>
        route.Cells.Count >= 3 && route.Cells[0] == fromExit.Cell && route.Cells[^1] == toExit.Cell && route.Cells[1] == fromExit.Cell + fromExit.Direction.Offset() && route.Cells[^2] == toExit.Cell + toExit.Direction.Offset();

    private sealed record TransformedShape(IReadOnlyList<GridPoint> Cells, IReadOnlyList<CatalogExit> Exits, IReadOnlyList<CatalogSocket> Sockets);
    private sealed record RouteOutcome(IReadOnlyList<CorridorRoute> Routes, IReadOnlyList<PortalFact> Portals, IReadOnlySet<GridPoint> Walkable);
    private readonly record struct MaterializedBounds(int Width, int Height, int Area);
    private sealed class MutableCounters
    {
        public int Regions, Connections, LayoutExpansions, CatalogCandidates, PlacementDecisions, PlacementBacktracks, RouteAttempts, RouteExpansions, RoutedCells, RouteBends;
        public GenerationCounters Snapshot() => new(Regions, Connections, LayoutExpansions, CatalogCandidates, PlacementDecisions, PlacementBacktracks, RouteAttempts, RouteExpansions, RoutedCells, RouteBends);
    }
    private sealed class PlacementState
    {
        private readonly HashSet<GridPoint> _occupied = new(); private readonly List<GridPoint> _undo = new();
        public int Checkpoint => _undo.Count;
        public bool TryPlace(string _, IReadOnlyList<GridPoint> cells, int maxCells)
        {
            if (_occupied.Count + cells.Count > maxCells || cells.Any(_occupied.Contains)) return false;
            foreach (var cell in cells) if (_occupied.Add(cell)) _undo.Add(cell); return true;
        }
        public void Rollback(int checkpoint) { while (_undo.Count > checkpoint) { var cell = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _occupied.Remove(cell); } }
    }
}
