using System.Numerics;
using CraftSurvive.Procgen;

namespace CraftSurvive.Game.Modules.LevelGeneration;

internal sealed record CaveRoom(string Id, Vector3 Center, Vector3 Radii);
internal sealed record CaveRoute(string Id, string From, string To, Vector3[] Points, float Radius);
internal sealed record CaveLevelPlan(ulong Seed, Candidate Intent, CaveRoom[] Rooms, CaveRoute[] Routes)
{
    internal const float Floor = 3f;
    internal const float EyeHeight = 1.55f;
    internal const float PassageRadius = 3.1f;
    internal static readonly Vector3 Min = new(-19, 0, -8);
    internal static readonly Vector3 Max = new(19, 17, 39);
    internal static readonly Vector3 AirMin = new(-17.5f, Floor, -6.5f);
    internal static readonly Vector3 AirMax = new(17.5f, 15.5f, 37.5f);
    internal CaveRoom Room(string id) => Rooms.Single(room => room.Id == id);
    internal Vector3 Eye(string id) => Room(id).Center with { Y = Floor + EyeHeight };
    internal int IndependentLoops => Routes.Length - Rooms.Length + 1;

    // The caller supplies Engine keyed draws. Layout has no random stream,
    // service handles or field evaluator and can be checked independently.
    internal static CaveLevelPlan Create(ulong seed, Func<string, float> unit)
    {
        const float CenterHeight = 5.5f;
        const float Jitter = 1.2f;
        float Draw(string key) => Math.Clamp(unit(key), 0f, 1f);
        CaveRoom Make(string id, Vector3 center, Vector3 radii, bool fixedPosition = false) => new(id,
            fixedPosition ? center : center + new Vector3((Draw(id + ".x") * 2 - 1) * Jitter, 0, (Draw(id + ".z") * 2 - 1) * Jitter),
            radii * new Vector3(0.9f + Draw(id + ".width") * 0.2f, 0.9f + Draw(id + ".height") * 0.2f, 0.9f + Draw(id + ".depth") * 0.2f));
        CaveRoom[] rooms = [
            Make("start", new(0, CenterHeight, 0), new(3.8f, 3.8f, 4), true),
            Make("hub", new(0, CenterHeight, 10), new(4.6f, 5, 4.5f)),
            Make("left", new(-10, CenterHeight, 20), new(4.8f, 6.8f, 4.7f)),
            Make("right", new(10, CenterHeight, 20), new(4, 4.1f, 4.3f)),
            Make("goal", new(0, CenterHeight, 31), new(5.5f, 5.3f, 4.5f)),
            Make("cache", new(-11, CenterHeight, 6), new(3.2f, 3.6f, 3.5f))];
        string cacheApproach = Draw("cache.approach") < 0.5f ? "start" : "hub";
        (string From, string To)[] connections = [("start", "hub"), ("hub", "left"), ("hub", "right"),
            ("left", "goal"), ("right", "goal"), (cacheApproach, "cache"), ("cache", "left")];
        GraphNode[] nodes = rooms.Select(room => new GraphNode(room.Id,
            room.Id == "start" ? NodeKind.Start : room.Id == "goal" ? NodeKind.Goal : room.Id == "cache" ? NodeKind.Treasure : NodeKind.Junction,
            room.Id, Array.Empty<string>())).ToArray();
        GraphEdge[] edges = connections.Select((pair, index) => new GraphEdge($"passage.{index}", pair.From, pair.To,
            index >= 5 ? EdgeKind.OptionalBranch : EdgeKind.CriticalPath, TraversalKind.Open, Array.Empty<string>())).ToArray();
        Candidate intent = new($"cave.{seed}", seed, "branching-cave", new(nodes, edges),
            [new(1, "cave-layout", seed, "Open passages; two alternative loops and one entrance.")]);
        ValidationReport validation = GraphValidator.Validate(intent);
        if (!validation.IsValid || GraphValidator.ReachableWithItems(intent).ReachedNodes.Count != rooms.Length)
            throw new InvalidOperationException("Generated cave intent must reach every room from its entrance.");
        CaveRoute[] routes = edges.Select(edge =>
        {
            Vector3 from = rooms.Single(room => room.Id == edge.From).Center;
            Vector3 to = rooms.Single(room => room.Id == edge.To).Center;
            Vector3 lateral = Vector3.Normalize(Vector3.Cross(to - from, Vector3.UnitY));
            Vector3 middle = (from + to) * 0.5f + lateral * ((Draw(edge.Id + ".bend") * 2 - 1) * Jitter);
            return new CaveRoute(edge.Id, edge.From, edge.To, [from, middle, to], PassageRadius);
        }).ToArray();
        return new(seed, intent, rooms, routes);
    }
}
