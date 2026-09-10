namespace CraftSurvive.Procgen.Workbench;

public sealed record WorkbenchProbe(string Id, string Kind, WorkbenchPoint From, WorkbenchPoint To, bool ExpectedBlocked);

/// <summary>Bounded product requirements to be measured by Engine queries.
/// This chooses test locations from intent, never from treatment or collision results.</summary>
public static class WorkbenchProbePlan
{
    private const float FloorClearance = 0.08f;
    private const float WallClearance = 0.15f;
    private const float GateApproach = 1.5f;

    public static WorkbenchProbe[] Create(WorkbenchCandidate candidate, bool switchOpen, float height, float radius)
    {
        if (height <= radius * 2 || radius <= 0) throw new ArgumentException("Invalid standing body dimensions.");
        List<WorkbenchProbe> probes = [];
        foreach (WorkbenchRoute route in candidate.Routes)
        {
            WorkbenchRoom a = candidate.Rooms.Single(r => r.Id == route.From), b = candidate.Rooms.Single(r => r.Id == route.To);
            WorkbenchPoint from = WorkbenchLayout.Center(a) with { Y = a.Minimum.Y + height / 2 + FloorClearance };
            WorkbenchPoint to = WorkbenchLayout.Center(b) with { Y = b.Minimum.Y + height / 2 + FloorClearance };
            bool alongX = from.X != to.X;
            float edge = route.Width / 2 - radius - WallClearance;
            // Full intended passages: both directions, center and two side lanes.
            for (int lane = -1; lane <= 1; lane++)
            {
                WorkbenchPoint start = Offset(from, alongX, lane * edge), end = Offset(to, alongX, lane * edge);
                Add(route.Id + "/lane" + lane, "route", start, end, route.RequiresSwitch && !switchOpen);
            }
            if (!route.RequiresSwitch) continue;
            // Gate cross-section, independent of the injected aperture: five
            // body centers cover the center and both edges in every gate state.
            var mid = new WorkbenchPoint((from.X + to.X) / 2, from.Y, (from.Z + to.Z) / 2);
            for (int lane = -2; lane <= 2; lane++)
            {
                WorkbenchPoint center = Offset(mid, alongX, lane * edge / 2);
                WorkbenchPoint start = alongX ? center with { X = center.X - GateApproach } : center with { Z = center.Z - GateApproach };
                WorkbenchPoint end = alongX ? center with { X = center.X + GateApproach } : center with { Z = center.Z + GateApproach };
                Add(route.Id + "/gate" + lane, "protected-gate", start, end, !switchOpen);
            }
        }
        for (int a = 0; a < candidate.Rooms.Length; a++)
            for (int b = a + 1; b < candidate.Rooms.Length; b++)
            {
                WorkbenchRoom left = candidate.Rooms[a], right = candidate.Rooms[b];
                if (candidate.Motif == WorkbenchExperiment.LargeMotif && !NeighboringGridRooms(candidate, left, right)) continue;
                if (candidate.Routes.Any(r => r.From == left.Id && r.To == right.Id || r.To == left.Id && r.From == right.Id)) continue;
                WorkbenchPoint start = WorkbenchLayout.Center(left) with { Y = left.Minimum.Y + height / 2 + FloorClearance };
                WorkbenchPoint end = WorkbenchLayout.Center(right) with { Y = right.Minimum.Y + height / 2 + FloorClearance };
                Add(left.Id + "/" + right.Id, "protected-separation", start, end, true);
            }
        return probes.ToArray();

        void Add(string id, string kind, WorkbenchPoint from, WorkbenchPoint to, bool blocked)
        {
            probes.Add(new(id + "/forward", kind, from, to, blocked));
            probes.Add(new(id + "/reverse", kind, to, from, blocked));
        }
    }

    private static WorkbenchPoint Offset(WorkbenchPoint p, bool alongX, float offset) =>
        alongX ? p with { Z = p.Z + offset } : p with { X = p.X + offset };

    public static bool NeighboringGridRooms(WorkbenchCandidate plan, WorkbenchRoom a, WorkbenchRoom b)
    {
        WorkbenchPoint from = WorkbenchLayout.Center(a), to = WorkbenchLayout.Center(b);
        if (from.X != to.X && from.Z != to.Z) return false;
        return !plan.Rooms.Any(r => r.Id != a.Id && r.Id != b.Id && (from.X == to.X
            ? WorkbenchLayout.Center(r).X == from.X && WorkbenchLayout.Center(r).Z > MathF.Min(from.Z, to.Z) && WorkbenchLayout.Center(r).Z < MathF.Max(from.Z, to.Z)
            : WorkbenchLayout.Center(r).Z == from.Z && WorkbenchLayout.Center(r).X > MathF.Min(from.X, to.X) && WorkbenchLayout.Center(r).X < MathF.Max(from.X, to.X)));
    }
}
