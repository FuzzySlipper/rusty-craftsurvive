namespace CraftSurvive.Procgen.Workbench;

/// <summary>A geometry treatment deliberately independent of the candidate's intent.
/// The side aperture leaves the center of the intended gate in place.</summary>
public static class WorkbenchRealization
{
    public const string RecipeVersion = "workbench-realization.v1";
    public const string Intact = "intact";
    public const string SideBreach = "side-breach";
    private const float BreachWidth = 1f;
    private const float BreachHeight = 2.25f;
    private const float CutPadding = 0.25f;

    public static string BreachRoute(WorkbenchCandidate candidate) => candidate.Routes
        .Where(r => r.RequiresSwitch)
        .OrderBy(r => r.Id == WorkbenchLayout.GateRouteId ? 0 : r.From == candidate.GoalRoom || r.To == candidate.GoalRoom ? 1 : 2)
        .ThenBy(r => r.Id, StringComparer.Ordinal).First().Id;

    public static WorkbenchLayoutData Resolve(WorkbenchCandidate candidate, string treatment)
    {
        if (treatment is not (Intact or SideBreach)) throw new ArgumentException("Unknown realization treatment.", nameof(treatment));
        WorkbenchLayoutData layout = WorkbenchLayout.Resolve(candidate);
        if (treatment == Intact) return layout;
        string routeId = BreachRoute(candidate);
        WorkbenchVolume gate = layout.Volumes.Single(v => v.Id == routeId + "-gate");
        WorkbenchRoute route = candidate.Routes.Single(r => r.Id == routeId);
        WorkbenchPoint a = WorkbenchLayout.Center(candidate.Rooms.Single(r => r.Id == route.From));
        WorkbenchPoint b = WorkbenchLayout.Center(candidate.Rooms.Single(r => r.Id == route.To));
        bool alongX = a.X != b.X;
        WorkbenchPoint min = alongX
            ? gate.Minimum with { X = gate.Minimum.X - CutPadding, Z = gate.Maximum.Z - BreachWidth }
            : gate.Minimum with { X = gate.Maximum.X - BreachWidth, Z = gate.Minimum.Z - CutPadding };
        WorkbenchPoint max = gate.Maximum with { X = gate.Maximum.X + CutPadding,
            Y = gate.Minimum.Y + BreachHeight, Z = gate.Maximum.Z + CutPadding };
        var aperture = new WorkbenchVolume(routeId + "-breach", "breach", min, max);
        return layout with { Volumes = [.. layout.Volumes, aperture] };
    }
}
