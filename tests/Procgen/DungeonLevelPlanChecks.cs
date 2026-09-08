using System.Numerics;
using CraftSurvive.Game.Modules.LevelGeneration;
using CraftSurvive.Procgen;

internal static class DungeonLevelPlanChecks
{
    public static void Verify()
    {
        SameInputsProduceEqualPlan();
        foreach (float draw in new[] { 0f, .5f, 1f })
        {
            DungeonLevelPlan plan = DungeonLevelPlan.Create(8129, _ => draw);
            EveryRoomIsReachable(plan);
            CorridorsConnectAxisAlignedRoomEndpoints(plan);
            RoomsRemainInsideTheEnclosingShell(plan);
        }
    }

    private static void SameInputsProduceEqualPlan()
    {
        DungeonLevelPlan left = DungeonLevelPlan.Create(8129, static key => key == "room.7.width" ? 1f : .5f);
        DungeonLevelPlan right = DungeonLevelPlan.Create(8129, static key => key == "room.7.width" ? 1f : .5f);

        Require(left.Seed == right.Seed, "The same dungeon seed must be retained.");
        Require(left.Rooms.SequenceEqual(right.Rooms), "The same keyed draws must reproduce dungeon rooms.");
        Require(left.Routes.SequenceEqual(right.Routes), "The same keyed draws must reproduce dungeon corridors.");
        Require(CanonicalIdentity.Hash(left.Intent) == CanonicalIdentity.Hash(right.Intent),
            "The same inputs must reproduce the dungeon graph intent.");
    }

    private static void EveryRoomIsReachable(DungeonLevelPlan plan)
    {
        Require(plan.Rooms.Length == 12, "The dungeon must plan all twelve rooms.");
        Require(plan.Routes.Length == 14, "The dungeon must plan fourteen routes.");
        Require(plan.IndependentLoops == 3, "The dungeon must retain three independent loops.");

        ValidationReport validation = GraphValidator.Validate(plan.Intent);
        Require(validation.IsValid, "The dungeon plan intent must remain a valid graph.");
        ProgressionReport progression = GraphValidator.ReachableWithItems(plan.Intent);
        Require(progression.GoalReached, "The dungeon goal must be reachable from its entrance.");
        Require(progression.ReachedNodes.Count == plan.Rooms.Length,
            "Every planned dungeon room must be reachable from the entrance.");
    }

    private static void CorridorsConnectAxisAlignedRoomEndpoints(DungeonLevelPlan plan)
    {
        HashSet<(int First, int Second)> routes = [];
        foreach (DungeonRoute route in plan.Routes)
        {
            Require(route.From >= 0 && route.From < plan.Rooms.Length && route.To >= 0 && route.To < plan.Rooms.Length,
                "Each dungeon corridor must refer to planned rooms.");
            Require(route.From != route.To, "A dungeon corridor must join two rooms.");
            Require(route.Start == AtFloor(plan.Rooms[route.From].Center),
                "A dungeon corridor must begin at its source-room center on the floor.");
            Require(route.End == AtFloor(plan.Rooms[route.To].Center),
                "A dungeon corridor must end at its destination-room center on the floor.");
            Require(route.Start.Y == route.End.Y && (route.Start.X == route.End.X || route.Start.Z == route.End.Z),
                "Dungeon corridors must be horizontal and axis-aligned.");
            Require(route.Start.X != route.End.X || route.Start.Z != route.End.Z,
                "Dungeon corridors must have non-zero horizontal length.");

            (int First, int Second) endpoints = route.From < route.To ? (route.From, route.To) : (route.To, route.From);
            Require(routes.Add(endpoints), "Dungeon corridors must not duplicate a room connection.");
        }
    }

    private static void RoomsRemainInsideTheEnclosingShell(DungeonLevelPlan plan)
    {
        Require(DungeonLevelPlan.Minimum.Y < DungeonLevelPlan.AirMinimum.Y,
            "The enclosing dungeon must reserve material beneath its walkable floor.");
        Require(DungeonLevelPlan.AirMaximum.Y < DungeonLevelPlan.Maximum.Y,
            "The enclosing dungeon must reserve material above its room roofs.");
        foreach (DungeonRoom room in plan.Rooms)
        {
            Require(room.Minimum.X >= DungeonLevelPlan.AirMinimum.X && room.Maximum.X <= DungeonLevelPlan.AirMaximum.X
                && room.Minimum.Z >= DungeonLevelPlan.AirMinimum.Z && room.Maximum.Z <= DungeonLevelPlan.AirMaximum.Z,
                $"{room.Id} must stay inside the enclosed dungeon's horizontal air bounds.");
            Require(room.Minimum.Y >= DungeonLevelPlan.AirMinimum.Y && room.Maximum.Y < DungeonLevelPlan.AirMaximum.Y,
                $"{room.Id} must leave floor and roof shell space at every supported draw extreme.");
        }
    }

    private static Vector3 AtFloor(Vector3 point) => point with { Y = DungeonLevelPlan.Floor };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
