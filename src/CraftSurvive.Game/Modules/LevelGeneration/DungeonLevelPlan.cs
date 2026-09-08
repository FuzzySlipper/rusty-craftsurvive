using System.Numerics;
using CraftSurvive.Procgen;

namespace CraftSurvive.Game.Modules.LevelGeneration;

internal sealed record DungeonRoom(int Index, Vector3 Minimum, Vector3 Maximum)
{
    internal string Id => $"room.{Index}";
    internal Vector3 Center => (Minimum + Maximum) * 0.5f;
    internal Vector3 Eye => Center with { Y = DungeonLevelPlan.Floor + 1.55f };
}
internal sealed record DungeonRoute(int From, int To, Vector3 Start, Vector3 End);
internal sealed record DungeonLevelPlan(ulong Seed, Candidate Intent, DungeonRoom[] Rooms, DungeonRoute[] Routes)
{
    internal const float Floor = 3f;
    internal const float PassageWidth = 2.8f;
    internal const float PassageHeight = 3.5f;
    internal const float Pitch = 14f;
    internal static readonly Vector3 Minimum = new(-8, 0, -8);
    internal static readonly Vector3 Maximum = new(50, 11, 36);
    internal static readonly Vector3 AirMinimum = new(-6.5f, Floor, -6.5f);
    internal static readonly Vector3 AirMaximum = new(48.5f, 9.5f, 34.5f);
    internal int IndependentLoops => Routes.Length - Rooms.Length + 1;

    internal static DungeonLevelPlan Create(ulong seed, Func<string, float> unit)
    {
        const int Columns = 4, Rows = 3;
        const float MinimumWidth = 8f, WidthVariation = 3f;
        const float MinimumDepth = 7.5f, DepthVariation = 3f;
        const float MinimumHeight = 4.5f, HeightVariation = 1.5f;
        DungeonRoom[] rooms = Enumerable.Range(0, Columns * Rows).Select(index =>
        {
            float Draw(string key) => Math.Clamp(unit($"room.{index}.{key}"), 0, 1);
            float width = MinimumWidth + Draw("width") * WidthVariation;
            float depth = MinimumDepth + Draw("depth") * DepthVariation;
            float height = MinimumHeight + Draw("height") * HeightVariation;
            Vector3 center = new(index % Columns * Pitch, Floor, index / Columns * Pitch);
            return new DungeonRoom(index, center - new Vector3(width / 2, 0, depth / 2),
                center + new Vector3(width / 2, height, depth / 2));
        }).ToArray();
        // A guaranteed connected serpentine spine, with three cross-passages
        // creating alternatives. Dimensions vary independently of graph intent.
        int[] spine = [0, 1, 2, 3, 7, 6, 5, 4, 8, 9, 10, 11];
        (int From, int To)[] links = spine.Zip(spine.Skip(1), (a, b) => (a, b))
            .Concat(new[] { (0, 4), (1, 5), (6, 10) }).ToArray();
        DungeonRoute[] routes = links.Select(link => new DungeonRoute(link.From, link.To,
            rooms[link.From].Center with { Y = Floor }, rooms[link.To].Center with { Y = Floor })).ToArray();
        Candidate intent = new($"dungeon.{seed}", seed, "enclosed-dungeon",
            new(rooms.Select(room => new GraphNode(room.Id, room.Index == 0 ? NodeKind.Start :
                room.Index == 11 ? NodeKind.Goal : NodeKind.Junction, room.Id, Array.Empty<string>())).ToArray(),
                routes.Select((route, index) => new GraphEdge($"corridor.{index}", rooms[route.From].Id, rooms[route.To].Id,
                    index < spine.Length - 1 ? EdgeKind.CriticalPath : EdgeKind.OptionalBranch,
                    TraversalKind.Open, Array.Empty<string>())).ToArray()),
            [new(1, "dungeon-layout", seed, "Twelve enclosed rooms, fourteen corridors, three loops, one entrance.")]);
        if (!GraphValidator.Validate(intent).IsValid || GraphValidator.ReachableWithItems(intent).ReachedNodes.Count != rooms.Length)
            throw new InvalidOperationException("Dungeon intent must connect every room.");
        return new(seed, intent, rooms, routes);
    }
}
