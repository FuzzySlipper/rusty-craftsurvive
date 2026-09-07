using System.Numerics;
using CraftSurvive.Game.Modules.LevelGeneration;
using CraftSurvive.Procgen;

internal static class CaveLevelPlanChecks
{
    public static void Verify()
    {
        CaveLevelPlan zero = CaveLevelPlan.Create(7863, static _ => 0f);
        CaveLevelPlan middle = CaveLevelPlan.Create(7863, static _ => .5f);
        CaveLevelPlan one = CaveLevelPlan.Create(7863, static _ => 1f);

        SameInputsProduceEqualGeometry();
        DifferentDrawsAlterGeometry(middle);
        EveryRoomIsReachable(zero);
        EveryRoomIsReachable(middle);
        EveryRoomIsReachable(one);
        PhysicalRoutesHaveTwoIndependentLoops(middle);
        RouteEndpointsMatchTheirRooms(middle);
        RoutesClearAStandingBody(zero);
        RoutesClearAStandingBody(middle);
        RoutesClearAStandingBody(one);
        ChamberBoundsAllowWalkableAir(zero);
        ChamberBoundsAllowWalkableAir(middle);
        ChamberBoundsAllowWalkableAir(one);
        CacheApproachUsesBothDeclaredEntrances(zero, middle);
    }

    private static void SameInputsProduceEqualGeometry()
    {
        CaveLevelPlan left = CaveLevelPlan.Create(7863, static key => key == "hub.x" ? 1f : .5f);
        CaveLevelPlan right = CaveLevelPlan.Create(7863, static key => key == "hub.x" ? 1f : .5f);
        Require(SameGeometry(left, right), "The same seed and keyed draws must reproduce the same room and route geometry.");
    }

    private static void DifferentDrawsAlterGeometry(CaveLevelPlan middle)
    {
        CaveLevelPlan changed = CaveLevelPlan.Create(7863, static key => key == "hub.x" ? 1f : .5f);
        Require(!SameGeometry(middle, changed), "A changed keyed draw must alter planned geometry without a hidden product random source.");
    }

    private static void EveryRoomIsReachable(CaveLevelPlan plan)
    {
        ValidationReport validation = GraphValidator.Validate(plan.Intent);
        Require(validation.IsValid, $"The cave intent must satisfy graph validation: {string.Join(", ", validation.Diagnostics.Select(diagnostic => diagnostic.Code))}.");
        ProgressionReport progression = GraphValidator.ReachableWithItems(plan.Intent);
        Require(progression.GoalReached, "The cave goal must be reachable from its start.");
        Require(progression.ReachedNodes.Count == plan.Rooms.Length, "Every planned cave room must be reachable from the entrance.");
    }

    private static void PhysicalRoutesHaveTwoIndependentLoops(CaveLevelPlan plan)
    {
        HashSet<(string First, string Second)> passages = plan.Routes
            .Select(route => OrderedEndpoints(route.From, route.To))
            .ToHashSet();
        Require(passages.Count == plan.Routes.Length, "Every cave route must describe a distinct physical passage.");

        Dictionary<string, List<string>> neighbors = plan.Rooms.ToDictionary(room => room.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach ((string first, string second) in passages)
        {
            neighbors[first].Add(second);
            neighbors[second].Add(first);
        }
        HashSet<string> reached = new(StringComparer.Ordinal) { "start" };
        Queue<string> pending = new(["start"]);
        while (pending.Count > 0)
            foreach (string neighbor in neighbors[pending.Dequeue()].OrderBy(id => id, StringComparer.Ordinal))
                if (reached.Add(neighbor)) pending.Enqueue(neighbor);

        Require(reached.Count == plan.Rooms.Length, "The physical passage graph must connect every room.");
        int independentLoops = passages.Count - plan.Rooms.Length + 1;
        Require(independentLoops == 2, "The physical cave must retain two independent undirected loops.");
    }

    private static void RouteEndpointsMatchTheirRooms(CaveLevelPlan plan)
    {
        foreach (CaveRoute route in plan.Routes)
        {
            Require(route.Points.Length >= 2, $"{route.Id} needs endpoints.");
            Require(route.Points[0] == plan.Room(route.From).Center, $"{route.Id} must begin at its source room center.");
            Require(route.Points[^1] == plan.Room(route.To).Center, $"{route.Id} must end at its target room center.");
            Require(route.Radius == CaveLevelPlan.PassageRadius, $"{route.Id} must retain the shared walkable passage radius.");
        }
    }

    private static void ChamberBoundsAllowWalkableAir(CaveLevelPlan plan)
    {
        foreach (CaveRoom room in plan.Rooms)
        {
            Require(ChamberCrossSectionRadius(room, CaveLevelPlan.Floor + .15f) >= .4f,
                $"{room.Id} must keep a body-radius floor cross-section.");
            Require(ChamberCrossSectionRadius(room, CaveLevelPlan.Floor + 1.9f) >= .4f,
                $"{room.Id} must retain player headroom above the floor.");
            Require(room.Center.X - room.Radii.X >= CaveLevelPlan.AirMin.X && room.Center.X + room.Radii.X <= CaveLevelPlan.AirMax.X,
                $"{room.Id} must stay inside the maximum chamber X bounds.");
            Require(room.Center.Y + room.Radii.Y <= CaveLevelPlan.AirMax.Y,
                $"{room.Id} must stay inside the maximum chamber ceiling bound.");
            Require(room.Center.Z - room.Radii.Z >= CaveLevelPlan.AirMin.Z && room.Center.Z + room.Radii.Z <= CaveLevelPlan.AirMax.Z,
                $"{room.Id} must stay inside the maximum chamber Z bounds.");
        }
    }

    private static void RoutesClearAStandingBody(CaveLevelPlan plan)
    {
        const float FootHeight = .15f;
        const float HeadHeight = 1.9f;
        const float BodyRadius = .4f;
        foreach (CaveRoute route in plan.Routes)
            foreach (Vector3 point in route.Points)
            {
                Require(CrossSectionRadius(route, point.Y, CaveLevelPlan.Floor + FootHeight) >= BodyRadius,
                    $"{route.Id} must keep a body-radius floor cross-section at every route control point.");
                Require(CrossSectionRadius(route, point.Y, CaveLevelPlan.Floor + HeadHeight) >= BodyRadius,
                    $"{route.Id} must keep a body-radius head cross-section at every route control point.");
            }
    }

    private static void CacheApproachUsesBothDeclaredEntrances(CaveLevelPlan zero, CaveLevelPlan middle)
    {
        Require(zero.Routes.Single(route => route.To == "cache").From == "start", "A zero cache-approach draw must route the cache from start.");
        Require(middle.Routes.Single(route => route.To == "cache").From == "hub", "A midpoint cache-approach draw must route the cache from hub.");
    }

    private static (string First, string Second) OrderedEndpoints(string first, string second) =>
        StringComparer.Ordinal.Compare(first, second) <= 0 ? (first, second) : (second, first);

    private static float CrossSectionRadius(CaveRoute route, float centerHeight, float sampleHeight)
    {
        float radiusSquared = route.Radius * route.Radius - (sampleHeight - centerHeight) * (sampleHeight - centerHeight);
        return radiusSquared < 0 ? 0 : MathF.Sqrt(radiusSquared);
    }

    private static float ChamberCrossSectionRadius(CaveRoom room, float sampleHeight)
    {
        float normalizedHeight = (sampleHeight - room.Center.Y) / room.Radii.Y;
        float horizontalScale = MathF.Sqrt(MathF.Max(0f, 1f - normalizedHeight * normalizedHeight));
        return MathF.Min(room.Radii.X, room.Radii.Z) * horizontalScale;
    }

    private static bool SameGeometry(CaveLevelPlan left, CaveLevelPlan right) =>
        left.Seed == right.Seed
        && left.Rooms.SequenceEqual(right.Rooms, RoomComparer.Instance)
        && left.Routes.SequenceEqual(right.Routes, RouteComparer.Instance);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RoomComparer : IEqualityComparer<CaveRoom>
    {
        public static RoomComparer Instance { get; } = new();
        public bool Equals(CaveRoom? left, CaveRoom? right) => left is not null && right is not null && left.Id == right.Id && left.Center == right.Center && left.Radii == right.Radii;
        public int GetHashCode(CaveRoom room) => HashCode.Combine(room.Id, room.Center, room.Radii);
    }

    private sealed class RouteComparer : IEqualityComparer<CaveRoute>
    {
        public static RouteComparer Instance { get; } = new();
        public bool Equals(CaveRoute? left, CaveRoute? right) => left is not null && right is not null
            && left.Id == right.Id && left.From == right.From && left.To == right.To && left.Radius == right.Radius && left.Points.SequenceEqual(right.Points);
        public int GetHashCode(CaveRoute route) => HashCode.Combine(route.Id, route.From, route.To, route.Radius, route.Points.Length);
    }
}
