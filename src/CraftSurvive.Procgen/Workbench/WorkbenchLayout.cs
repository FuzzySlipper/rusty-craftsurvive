namespace CraftSurvive.Procgen.Workbench;

public sealed record WorkbenchVolume(string Id, string Kind, WorkbenchPoint Minimum, WorkbenchPoint Maximum);
public sealed record WorkbenchMarker(string Id, string Room, string Action, WorkbenchPoint Position, string Label);
public sealed record WorkbenchLayoutData(WorkbenchVolume[] Volumes, WorkbenchMarker[] Markers, string GateRoute);

/// <summary>Resolved construction inputs shared by the mesh recipe and inspection diagram.
/// These are product geometry decisions, not another spatial/collision service.</summary>
public static class WorkbenchLayout
{
    public const float GateHalfDepth = 0.5f;
    public const float WindowHalfWidth = 0.65f;
    public const float WindowBottom = 1.25f;
    public const float WindowTop = 1.75f;
    public const float StandingEyeHeight = 1.55f;
    public const float MarkerInset = 0.8f;
    public const float MarkerHalfWidth = 0.3f;
    public const float MarkerHeight = 1.1f;
    public const float GoalMarkerHeight = 2f;
    public const string GateRouteId = "goal-start";

    public static WorkbenchLayoutData Resolve(WorkbenchCandidate candidate)
    {
        var errors = WorkbenchExperiment.Validate(candidate);
        if (errors.Length != 0) throw new InvalidOperationException(string.Join(", ", errors));
        var volumes = candidate.Rooms.Select(r => new WorkbenchVolume(r.Id, "room", r.Minimum, r.Maximum)).ToList();
        foreach (WorkbenchRoute route in candidate.Routes)
        {
            WorkbenchRoom a = candidate.Rooms.Single(r => r.Id == route.From);
            WorkbenchRoom b = candidate.Rooms.Single(r => r.Id == route.To);
            WorkbenchPoint from = Center(a), to = Center(b);
            float half = route.Width / 2f;
            volumes.Add(new(route.Id, "passage",
                new(MathF.Min(from.X, to.X) - half, a.Minimum.Y, MathF.Min(from.Z, to.Z) - half),
                new(MathF.Max(from.X, to.X) + half, a.Maximum.Y, MathF.Max(from.Z, to.Z) + half)));
            if (!route.RequiresSwitch) continue;
            var mid = new WorkbenchPoint((from.X + to.X) / 2f, from.Y, (from.Z + to.Z) / 2f);
            bool alongX = from.X != to.X;
            float halfX = alongX ? GateHalfDepth : half;
            float halfZ = alongX ? half : GateHalfDepth;
            volumes.Add(new(route.Id + "-gate", "gate",
                new(mid.X - halfX, a.Minimum.Y, mid.Z - halfZ),
                new(mid.X + halfX, a.Maximum.Y, mid.Z + halfZ)));
            if (route.Id == GateRouteId && candidate.Motif == WorkbenchExperiment.PreviewMotif && candidate.PreviewOpening)
                volumes.Add(new(route.Id + "-window", "window",
                    new(mid.X - (alongX ? GateHalfDepth : WindowHalfWidth), a.Minimum.Y + WindowBottom,
                        mid.Z - (alongX ? WindowHalfWidth : GateHalfDepth)),
                    new(mid.X + (alongX ? GateHalfDepth : WindowHalfWidth), a.Minimum.Y + WindowTop,
                        mid.Z + (alongX ? WindowHalfWidth : GateHalfDepth))));
        }
        WorkbenchRoom control = candidate.Rooms.Single(r => r.Id == candidate.SwitchRoom);
        WorkbenchRoom goal = candidate.Rooms.Single(r => r.Id == candidate.GoalRoom);
        WorkbenchPoint goalPoint = Center(goal) with { Y = goal.Minimum.Y, Z = goal.Maximum.Z - MarkerInset };
        var markers = new List<WorkbenchMarker>
        {
            new("switch", control.Id, "activate", EastMarker(control), "Open gate"),
            new("goal", goal.Id, "", goalPoint, "Goal"),
        };
        if (candidate.Motif == WorkbenchExperiment.RecoveryMotif)
        {
            WorkbenchRoom relay = candidate.Rooms.Single(r => r.Id == "relay");
            markers.Add(new("spend", relay.Id, "spend", EastMarker(relay), "Spend key (irreversible)"));
            if (candidate.RecoveryEnabled)
                markers.Add(new("recover", goal.Id, "recover", Center(goal) with { X = goal.Minimum.X + MarkerInset, Y = goal.Minimum.Y }, "Recover one key"));
        }
        if (candidate.Motif == WorkbenchExperiment.PreviewMotif)
        {
            WorkbenchRoom start = candidate.Rooms.Single(r => r.Id == candidate.StartRoom);
            markers.Add(new("lookout", start.Id, "observe", Center(start) with { Y = start.Minimum.Y }, "Acknowledge visible goal"));
        }
        return new(volumes.ToArray(), markers.ToArray(), GateRouteId);
    }

    public static WorkbenchPoint Center(WorkbenchRoom room) => new(
        (room.Minimum.X + room.Maximum.X) / 2f,
        (room.Minimum.Y + room.Maximum.Y) / 2f,
        (room.Minimum.Z + room.Maximum.Z) / 2f);
    private static WorkbenchPoint EastMarker(WorkbenchRoom room) =>
        Center(room) with { X = room.Maximum.X - MarkerInset, Y = room.Minimum.Y };
}
