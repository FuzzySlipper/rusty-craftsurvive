namespace CraftSurvive.Procgen.Generation;

/// <summary>Small no-framework verification surface for later check-project wiring.</summary>
public static class GenerationProbe
{
    public static void Verify()
    {
        var graph = new GraphCore();
        var candidate = graph.CreateInitial(new SeedIntent("probe", "Probe", Array.Empty<string>()), 41);
        candidate = graph.Apply(candidate, GraphRule.LockKeyLoop, 7).Candidate;
        var generator = new DungeonGenerator();
        var first = generator.Generate(candidate, GenerationPolicy.Normal, 99);
        var second = generator.Generate(candidate, GenerationPolicy.Normal, 99);
        if (!first.Accepted || !second.Accepted || first.Identity != second.Identity || GenerationResultIdentity.Hash(first) != GenerationResultIdentity.Hash(second)) throw new InvalidOperationException("Complete Core generation results must be deterministic for the probe candidate.");
        if (generator.Generate(candidate, GenerationPolicy.Tight with { MaxRouteExpansionsPerConnection = 1 }, 99).Accepted) throw new InvalidOperationException("A route quota exhaustion must fail closed.");
        var invalidCatalog = new ShapeCatalog("bad", new[] { ShapeCatalog.Default.Shapes[0], ShapeCatalog.Default.Shapes[0] });
        if (generator.Generate(candidate, GenerationPolicy.Normal, 99, invalidCatalog).Accepted) throw new InvalidOperationException("Duplicate catalog identities must fail closed.");
        var oneExitCatalog = new ShapeCatalog("one-exit", new[] { new CatalogShape("one", new[] { new GridPoint(0, 0) }, new[] { new CatalogExit("only", new GridPoint(0, 0), CardinalDirection.North) }) });
        if (generator.Generate(candidate, GenerationPolicy.Normal, 99, oneExitCatalog).Accepted) throw new InvalidOperationException("An unmatched exit requirement must fail closed.");
        var tampered = first.Artifacts! with { WalkableCells = new HashSet<GridPoint>() };
        if (generator.ValidateBuiltFlow(candidate, tampered).Valid) throw new InvalidOperationException("Tampered placement walkability must fail built-flow validation.");
        var staleCandidate = candidate with { Graph = new CandidateGraph(candidate.Graph.Nodes, candidate.Graph.Edges.Select(edge => edge.Id == "edge.start.gate_1" ? edge with { Traversal = TraversalKind.Hidden } : edge).ToArray()) };
        if (generator.ValidateBuiltFlow(staleCandidate, first.Artifacts).Valid) throw new InvalidOperationException("A candidate whose edge traversal changed must fail stale built-flow provenance validation.");
        var staleMatchChain = first.Artifacts with { Matches = Array.Empty<MatchedShape>() };
        if (generator.ValidateBuiltFlow(candidate, staleMatchChain).Valid) throw new InvalidOperationException("A stale match chain must fail built-flow provenance validation.");
        var requirement = first.Artifacts.Requirements.First(value => value.Connections.Count > 0);
        var mutatedConnection = requirement.Connections[0] with { Traversal = requirement.Connections[0].Traversal == TraversalKind.Hidden ? TraversalKind.Open : TraversalKind.Hidden };
        var semanticallyStaleRequirements = first.Artifacts with
        {
            Requirements = first.Artifacts.Requirements.Select(value => value.RegionId == requirement.RegionId ? value with { Connections = value.Connections.Select(connection => connection.SourceEdgeId == mutatedConnection.SourceEdgeId ? mutatedConnection : connection).ToArray() } : value).ToArray(),
        };
        if (generator.ValidateBuiltFlow(candidate, semanticallyStaleRequirements).Valid) throw new InvalidOperationException("A requirement connection whose traversal changes while retaining its edge id must fail built-flow provenance validation.");
        var requiredSocket = first.Artifacts.Sockets.First(socket => first.Artifacts.Requirements.Single(requirement => requirement.RegionId == socket.RegionId).RequiredSockets.Contains(socket.Kind, StringComparer.Ordinal));
        var missingRequiredSocket = first.Artifacts with { Sockets = first.Artifacts.Sockets.Where(socket => socket.Id != requiredSocket.Id).ToArray() };
        if (generator.ValidateBuiltFlow(candidate, missingRequiredSocket).Valid) throw new InvalidOperationException("Removing a published required socket must fail built-flow provenance validation.");
        var sourceRoute = first.Artifacts.Routes[0];
        var extraRoute = sourceRoute with { Id = "route.synthetic.extra", SourceEdgeId = "edge.synthetic.extra" };
        var artifactsWithExtraRoute = first.Artifacts with { Routes = first.Artifacts.Routes.Concat(new[] { extraRoute }).ToArray() };
        if (generator.ValidateBuiltFlow(candidate, artifactsWithExtraRoute).Valid) throw new InvalidOperationException("An extra physical route without an authoritative source connection must fail built-flow provenance validation.");
        var initial = graph.CreateInitial(new SeedIntent("probe.policy", "Policy Probe", Array.Empty<string>()), 42);
        var normal = generator.Generate(initial, GenerationPolicy.Normal, 100);
        var spread = generator.Generate(initial, GenerationPolicy.Spread, 100);
        if (!normal.Accepted || !spread.Accepted || normal.Identity == spread.Identity) throw new InvalidOperationException("Policy changes must be observable in the deterministic generation identity.");
        MaterializedBounds(generator, initial);
    }

    private static void MaterializedBounds(DungeonGenerator generator, Candidate initial)
    {
        var policy = GenerationPolicy.Normal with { Id = "probe.materialized", MaxWidth = 32, MaxHeight = 32, RoomStride = 8, RoomFootprintCells = 5, MaxArtifactCells = 512 };
        var exactCatalog = SpanCatalog("exact", policy.MaxWidth - policy.MaxWidth / 2 - 1);
        var exact = generator.Generate(initial, policy, 0, exactCatalog);
        if (!exact.Accepted || exact.Artifacts is null || exact.Artifacts.WalkableCells.Any(cell => cell.X >= policy.MaxWidth || cell.Y >= policy.MaxHeight) || exact.Metrics.Width <= policy.RoomFootprintCells)
            throw new InvalidOperationException("A catalog shape ending exactly on the materialized policy boundary must be accepted and reported from materialized cells.");
        var oneOver = generator.Generate(initial, policy, 0, SpanCatalog("one-over", policy.MaxWidth - policy.MaxWidth / 2));
        if (oneOver.Accepted || oneOver.RejectionCode != "materialized_coordinate_bounds_exceeded")
            throw new InvalidOperationException("A catalog shape one cell beyond the materialized policy boundary must fail closed with an observable bounds diagnostic.");
    }

    private static ShapeCatalog SpanCatalog(string id, int farX) => new(id, new[]
    {
        new CatalogShape("span", new[] { new GridPoint(0, 0), new GridPoint(farX, 0) }, new[] { new CatalogExit("north", new GridPoint(0, 0), CardinalDirection.North) }),
    });
}
