using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace CraftSurvive.Procgen.Generation;

public readonly record struct GridPoint(int X, int Y)
{
    public static GridPoint operator +(GridPoint left, GridPoint right) => checked(new(left.X + right.X, left.Y + right.Y));
    public int ManhattanDistance(GridPoint other) => checked(Math.Abs(X - other.X) + Math.Abs(Y - other.Y));
}

public enum CardinalDirection { North, East, South, West }

public static class CardinalDirections
{
    public static readonly IReadOnlyList<CardinalDirection> Ordered = new[] { CardinalDirection.North, CardinalDirection.East, CardinalDirection.South, CardinalDirection.West };
    public static GridPoint Offset(this CardinalDirection direction) => direction switch
    {
        CardinalDirection.North => new GridPoint(0, -1),
        CardinalDirection.East => new GridPoint(1, 0),
        CardinalDirection.South => new GridPoint(0, 1),
        CardinalDirection.West => new GridPoint(-1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };
    public static CardinalDirection Opposite(this CardinalDirection direction) => (CardinalDirection)(((int)direction + 2) % 4);
    public static CardinalDirection Rotate(this CardinalDirection direction, int quarterTurns) => (CardinalDirection)(((int)direction + quarterTurns % 4 + 4) % 4);
}

public sealed record GenerationPolicy(
    string Id,
    int MaxWidth,
    int MaxHeight,
    int RoomStride,
    int RoomFootprintCells,
    int RoomCandidatesPerRegion,
    int MaxLayoutExpansions,
    int MaxRouteAttempts,
    int MaxRouteExpansionsPerConnection,
    int MaxPlacementDecisions,
    int MaxPlacementBacktracks,
    int MaxCatalogCandidatesPerRequirement,
    int MaxArtifactCells)
{
    public static GenerationPolicy Tight { get; } = new("tight", 96, 96, 10, 5, 12, 384, 2, 10_000, 96, 48, 12, 8_192);
    public static GenerationPolicy Normal { get; } = new("normal", 192, 192, 12, 5, 24, 2_048, 3, 40_000, 256, 128, 32, 32_768);
    public static GenerationPolicy Spread { get; } = new("spread", 384, 384, 16, 5, 48, 8_192, 4, 160_000, 512, 256, 64, 131_072);
}

/// <summary>Current-schema policy admission shared by the generator and product adapters.</summary>
public static class GenerationPolicyValidation
{
    public const int MaxDimension = 16_384;
    public const int MaxRoomFootprintCells = 1_024;
    // Stride cannot exceed a configured axis: larger values cannot add a useful
    // in-bounds layout candidate and risk unchecked downstream multiplication.
    public const int MaxRoomStride = MaxDimension;
    public const int MaxRoomCandidatesPerRegion = 16_384;
    public const int MaxLayoutExpansions = 16_000_000;
    public const int MaxRouteAttempts = 65_536;
    public const int MaxRouteExpansionsPerConnection = 64_000_000;
    public const int MaxPlacementDecisions = 16_000_000;
    public const int MaxPlacementBacktracks = 16_000_000;
    public const int MaxCatalogCandidatesPerRequirement = 16_000_000;
    public const int MaxArtifactCells = 64_000_000;

    public static bool IsValid(GenerationPolicy? policy)
    {
        return policy is not null
            && !string.IsNullOrWhiteSpace(policy.Id)
            && policy.Id.Length <= 96
            && policy.Id.All(static value => char.IsLower(value) || char.IsDigit(value) || value is '.' or '-' or '_')
            && policy.MaxWidth is > 0 and <= MaxDimension && policy.MaxHeight is > 0 and <= MaxDimension
            && policy.RoomFootprintCells is > 0 and <= MaxRoomFootprintCells && policy.RoomStride > policy.RoomFootprintCells && policy.RoomStride <= MaxRoomStride
            && policy.RoomCandidatesPerRegion is > 0 and <= MaxRoomCandidatesPerRegion && policy.MaxLayoutExpansions is > 0 and <= MaxLayoutExpansions
            && policy.MaxRouteAttempts is > 0 and <= MaxRouteAttempts && policy.MaxRouteExpansionsPerConnection is > 0 and <= MaxRouteExpansionsPerConnection
            && policy.MaxPlacementDecisions is > 0 and <= MaxPlacementDecisions && policy.MaxPlacementBacktracks is >= 0 and <= MaxPlacementBacktracks
            && policy.MaxCatalogCandidatesPerRequirement is > 0 and <= MaxCatalogCandidatesPerRequirement && policy.MaxArtifactCells is > 0 and <= MaxArtifactCells;
    }

    public static void Validate(GenerationPolicy policy)
    {
        if (!IsValid(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Generation policy contains an invalid bound or quota.");
        }
    }
}

public sealed record CatalogExit(string Id, GridPoint Cell, CardinalDirection Direction, IReadOnlyList<string>? Tags = null);
public sealed record CatalogSocket(string Id, GridPoint Cell, string Kind, IReadOnlyList<string>? Tags = null);
public sealed record CatalogShape(string Id, IReadOnlyList<GridPoint> WalkableCells, IReadOnlyList<CatalogExit> Exits, IReadOnlyList<CatalogSocket>? Sockets = null, IReadOnlyList<string>? Tags = null);
public sealed record ShapeCatalog(string Id, IReadOnlyList<CatalogShape> Shapes)
{
    public static ShapeCatalog Default { get; } = new("builtin.rooms.v1", new[]
    {
        new CatalogShape("room.cross.5", Square(5), new[]
        {
            new CatalogExit("north", new GridPoint(2, 0), CardinalDirection.North), new CatalogExit("east", new GridPoint(4, 2), CardinalDirection.East),
            new CatalogExit("south", new GridPoint(2, 4), CardinalDirection.South), new CatalogExit("west", new GridPoint(0, 2), CardinalDirection.West),
        }, new[] { new CatalogSocket("center", new GridPoint(2, 2), "content") }, new[] { "room", "default" }),
    });

    private static IReadOnlyList<GridPoint> Square(int size) => Enumerable.Range(0, size).SelectMany(y => Enumerable.Range(0, size).Select(x => new GridPoint(x, y))).ToArray();
}

public sealed record IntermediateRegion(string Id, string SourceNodeId, NodeKind Kind, string Role, string? GrantsItem, IReadOnlyList<string> Tags);
public sealed record IntermediateConnection(string Id, string SourceEdgeId, string FromRegionId, string ToRegionId, EdgeKind Kind, TraversalKind Traversal, string? RequiredItem, IReadOnlyList<string> Tags);
public sealed record IntermediateDungeon(string CandidateId, string CandidateHash, IReadOnlyList<IntermediateRegion> Regions, IReadOnlyList<IntermediateConnection> Connections);

public sealed record LayoutRoom(string RegionId, GridPoint Origin, int Width, int Height);
public sealed record LayoutPlan(IReadOnlyList<LayoutRoom> Rooms, int Width, int Height, int Area);
public sealed record PieceRequirement(string RegionId, IReadOnlyList<IntermediateConnection> Connections, IReadOnlyList<string> RequiredSockets);
public sealed record MatchedShape(string RegionId, string ShapeId, int QuarterTurns, IReadOnlyDictionary<string, CatalogExit> ExitMap, IReadOnlyDictionary<string, CatalogSocket> SocketMap);
public sealed record PlacedPiece(string RegionId, string ShapeId, int QuarterTurns, GridPoint Origin, IReadOnlyList<GridPoint> WalkableCells, IReadOnlyDictionary<string, CatalogExit> Exits, IReadOnlyDictionary<string, CatalogSocket> Sockets);
public sealed record CorridorRoute(string Id, string SourceEdgeId, string FromRegionId, string ToRegionId, IReadOnlyList<GridPoint> Cells, TraversalKind Traversal, string? RequiredItem);
public sealed record PortalFact(string Id, string SourceEdgeId, GridPoint Cell, TraversalKind Traversal, string? RequiredItem);
public sealed record SocketFact(string Id, string RegionId, string Kind, GridPoint Cell);
public sealed record DungeonArtifacts(IntermediateDungeon Intermediate, LayoutPlan Layout, IReadOnlyList<PieceRequirement> Requirements, IReadOnlyList<MatchedShape> Matches, IReadOnlyList<PlacedPiece> Pieces, IReadOnlyList<CorridorRoute> Routes, IReadOnlyList<PortalFact> Portals, IReadOnlyList<SocketFact> Sockets, IReadOnlySet<GridPoint> WalkableCells);

public sealed record GenerationCounters(int Regions, int Connections, int LayoutExpansions, int CatalogCandidates, int PlacementDecisions, int PlacementBacktracks, int RouteAttempts, int RouteExpansions, int RoutedCells, int RouteBends);
public sealed record GenerationMetrics(int Width, int Height, int Area, decimal FillRatio, int RoutedCells, int Bends, int SearchExpansions, int CatalogCandidates, int RejectionCount);
/// <summary>Deterministic stage/counter evidence. Wall-clock timing belongs in a non-authoritative tool adapter.</summary>
public sealed record StageObservation(string Stage, GenerationCounters Counters);
public sealed record GenerationAttempt(int Attempt, string Stage, bool Accepted, string Code, string Detail, GenerationCounters Counters);
public sealed record BuiltFlowReport(bool Valid, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlySet<string> LogicalReachableRegions, IReadOnlySet<string> PhysicalReachableRegions);

public sealed record DungeonGenerationResult(
    bool Accepted,
    string Identity,
    string? RejectionStage,
    string? RejectionCode,
    IReadOnlyList<GenerationAttempt> Attempts,
    IReadOnlyList<StageObservation> Stages,
    GenerationCounters Counters,
    GenerationMetrics Metrics,
    DungeonArtifacts? Artifacts,
    BuiltFlowReport? BuiltFlow)
{
    public static DungeonGenerationResult Rejected(string stage, string code, string detail, IReadOnlyList<GenerationAttempt> attempts, IReadOnlyList<StageObservation> stages, GenerationCounters counters) => new(false, string.Empty, stage, code, attempts, stages, counters, ComputeMetrics(counters, 0, 0, 0, 0) with { RejectionCount = 1 }, null, null);
    public static GenerationMetrics ComputeMetrics(GenerationCounters counters, int width, int height, int area, int occupied) => new(width, height, area, area == 0 ? 0 : decimal.Round((decimal)occupied / area, 6), counters.RoutedCells, counters.RouteBends, checked(counters.LayoutExpansions + counters.RouteExpansions), counters.CatalogCandidates, 0);
}

internal sealed class GenerationFailure(string stage, string code, string detail) : Exception(detail)
{
    public string Stage { get; } = stage;
    public string Code { get; } = code;
}

internal static class GenerationIdentity
{
    public static string Hash(Candidate candidate, GenerationPolicy policy, ulong seed, DungeonArtifacts artifacts)
    {
        var text = new StringBuilder();
        void Add(params object?[] fields) { foreach (var field in fields) text.Append(field?.ToString() ?? string.Empty).Append('|'); text.Append('\n'); }
        Add(CanonicalIdentity.Hash(candidate), policy.Id, policy.MaxWidth, policy.MaxHeight, policy.RoomStride, policy.RoomFootprintCells, policy.RoomCandidatesPerRegion, policy.MaxLayoutExpansions, policy.MaxRouteAttempts, policy.MaxRouteExpansionsPerConnection, policy.MaxPlacementDecisions, policy.MaxPlacementBacktracks, policy.MaxCatalogCandidatesPerRequirement, policy.MaxArtifactCells, seed, artifacts.Layout.Width, artifacts.Layout.Height);
        foreach (var piece in artifacts.Pieces.OrderBy(piece => piece.RegionId, StringComparer.Ordinal)) Add("piece", piece.RegionId, piece.ShapeId, piece.QuarterTurns, piece.Origin.X, piece.Origin.Y);
        foreach (var route in artifacts.Routes.OrderBy(route => route.SourceEdgeId, StringComparer.Ordinal)) { Add("route", route.SourceEdgeId, route.Traversal, route.RequiredItem); foreach (var cell in route.Cells) Add("cell", cell.X, cell.Y); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Stable complete-result identity for deterministic Core verification; timing is intentionally excluded.</summary>
public static class GenerationResultIdentity
{
    public static string Hash(DungeonGenerationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder();
        void Add(params object?[] fields) { foreach (var field in fields) text.Append(field?.ToString() ?? string.Empty).Append('|'); text.Append('\n'); }
        void AddCounters(GenerationCounters counters) => Add(counters.Regions, counters.Connections, counters.LayoutExpansions, counters.CatalogCandidates, counters.PlacementDecisions, counters.PlacementBacktracks, counters.RouteAttempts, counters.RouteExpansions, counters.RoutedCells, counters.RouteBends);
        Add(result.Accepted, result.Identity, result.RejectionStage, result.RejectionCode);
        AddCounters(result.Counters);
        Add(result.Metrics.Width, result.Metrics.Height, result.Metrics.Area, result.Metrics.FillRatio, result.Metrics.RoutedCells, result.Metrics.Bends, result.Metrics.SearchExpansions, result.Metrics.CatalogCandidates, result.Metrics.RejectionCount);
        foreach (var attempt in result.Attempts) { Add(attempt.Attempt, attempt.Stage, attempt.Accepted, attempt.Code, attempt.Detail); AddCounters(attempt.Counters); }
        foreach (var stage in result.Stages) { Add(stage.Stage); AddCounters(stage.Counters); }
        if (result.Artifacts is not null)
        {
            var artifacts = result.Artifacts;
            Add(artifacts.Intermediate.CandidateId, artifacts.Intermediate.CandidateHash, artifacts.Layout.Width, artifacts.Layout.Height, artifacts.Layout.Area);
            foreach (var region in artifacts.Intermediate.Regions.OrderBy(value => value.Id, StringComparer.Ordinal)) { Add("region", region.Id, region.SourceNodeId, region.Kind, region.Role, region.GrantsItem); foreach (var tag in region.Tags.OrderBy(value => value, StringComparer.Ordinal)) Add("region-tag", tag); }
            foreach (var connection in artifacts.Intermediate.Connections.OrderBy(value => value.Id, StringComparer.Ordinal)) { Add("connection", connection.Id, connection.SourceEdgeId, connection.FromRegionId, connection.ToRegionId, connection.Kind, connection.Traversal, connection.RequiredItem); foreach (var tag in connection.Tags.OrderBy(value => value, StringComparer.Ordinal)) Add("connection-tag", tag); }
            foreach (var room in artifacts.Layout.Rooms.OrderBy(value => value.RegionId, StringComparer.Ordinal)) Add("room", room.RegionId, room.Origin.X, room.Origin.Y, room.Width, room.Height);
            foreach (var requirement in artifacts.Requirements.OrderBy(value => value.RegionId, StringComparer.Ordinal)) { Add("requirement", requirement.RegionId); foreach (var connection in requirement.Connections.OrderBy(value => value.SourceEdgeId, StringComparer.Ordinal)) Add("requirement-connection", connection.SourceEdgeId); foreach (var socket in requirement.RequiredSockets.OrderBy(value => value, StringComparer.Ordinal)) Add("requirement-socket", socket); }
            foreach (var match in artifacts.Matches.OrderBy(value => value.RegionId, StringComparer.Ordinal)) { Add("match", match.RegionId, match.ShapeId, match.QuarterTurns); foreach (var entry in match.ExitMap.OrderBy(value => value.Key, StringComparer.Ordinal)) Add("match-exit", entry.Key, entry.Value.Id, entry.Value.Cell.X, entry.Value.Cell.Y, entry.Value.Direction); foreach (var entry in match.SocketMap.OrderBy(value => value.Key, StringComparer.Ordinal)) Add("match-socket", entry.Key, entry.Value.Id, entry.Value.Cell.X, entry.Value.Cell.Y, entry.Value.Kind); }
            foreach (var piece in artifacts.Pieces.OrderBy(value => value.RegionId, StringComparer.Ordinal)) { Add("piece", piece.RegionId, piece.ShapeId, piece.QuarterTurns, piece.Origin.X, piece.Origin.Y); foreach (var cell in piece.WalkableCells.OrderBy(value => value.X).ThenBy(value => value.Y)) Add("piece-cell", cell.X, cell.Y); }
            foreach (var route in artifacts.Routes.OrderBy(value => value.SourceEdgeId, StringComparer.Ordinal)) { Add("route", route.Id, route.SourceEdgeId, route.FromRegionId, route.ToRegionId, route.Traversal, route.RequiredItem); foreach (var cell in route.Cells) Add("route-cell", cell.X, cell.Y); }
            foreach (var portal in artifacts.Portals.OrderBy(value => value.Id, StringComparer.Ordinal)) Add("portal", portal.Id, portal.SourceEdgeId, portal.Cell.X, portal.Cell.Y, portal.Traversal, portal.RequiredItem);
            foreach (var socket in artifacts.Sockets.OrderBy(value => value.Id, StringComparer.Ordinal)) Add("socket", socket.Id, socket.RegionId, socket.Kind, socket.Cell.X, socket.Cell.Y);
            foreach (var cell in artifacts.WalkableCells.OrderBy(value => value.X).ThenBy(value => value.Y)) Add("walkable", cell.X, cell.Y);
        }
        if (result.BuiltFlow is not null)
        {
            Add(result.BuiltFlow.Valid);
            foreach (var diagnostic in result.BuiltFlow.Diagnostics) Add(diagnostic.Code, diagnostic.Severity, diagnostic.Detail, diagnostic.NodeId, diagnostic.EdgeId, diagnostic.RepairHint);
            foreach (var region in result.BuiltFlow.LogicalReachableRegions.OrderBy(value => value, StringComparer.Ordinal)) Add("logical", region);
            foreach (var region in result.BuiltFlow.PhysicalReachableRegions.OrderBy(value => value, StringComparer.Ordinal)) Add("physical", region);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
