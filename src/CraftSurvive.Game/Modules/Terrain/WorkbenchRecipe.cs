using System.Numerics;
using CraftSurvive.Procgen.Workbench;
using Rusty.Engine;
using Rusty.Engine.Implicit;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>Realizes resolved experiment decisions using the existing Engine recipe and collision lane.</summary>
internal static class WorkbenchRecipe
{
    private static readonly Transform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
    internal static Vector3 Point(WorkbenchPoint p) => new(p.X, p.Y, p.Z);
    internal static Vector3 Center(WorkbenchRoom room) => (Point(room.Minimum) + Point(room.Maximum)) * 0.5f;

    internal static void Compose(IEngineContext engine, StoneworksMaterials materials,
        WorkbenchCandidate candidate, bool switchOpen, string treatment, Action<RecipeSurface> emit)
    {
        const float Cell = 0.25f, Shell = 1f;
        RecipeWriter writer = new(engine.ImplicitSurfaces, new(Cell, 0f, 0.5f, ImplicitMaterialBoundaryMode.Interpolated), emit);
        using ImplicitRecipe field = writer.Begin();
        Vector3 min = candidate.Rooms.Select(r => Point(r.Minimum)).Aggregate(Vector3.Min) - new Vector3(Shell);
        Vector3 max = candidate.Rooms.Select(r => Point(r.Maximum)).Aggregate(Vector3.Max) + new Vector3(Shell);
        WorkbenchLayoutData layout = WorkbenchRealization.Resolve(candidate, treatment);
        WorkbenchVolume[] rooms = layout.Volumes.Where(v => v.Kind == "room").ToArray();
        WorkbenchRoom first = candidate.Rooms[0];
        ImplicitNode Box(WorkbenchVolume volume) => field.Box(Point(volume.Minimum), Point(volume.Maximum));
        ImplicitNode air = Box(rooms[0]);
        foreach (WorkbenchVolume room in rooms.Skip(1)) air = field.Union(air, Box(room));
        foreach (WorkbenchVolume volume in layout.Volumes.Where(v => v.Kind == "passage"))
        {
            ImplicitNode passage = Box(volume);
            WorkbenchVolume? gate = layout.Volumes.SingleOrDefault(v => v.Kind == "gate" && v.Id == volume.Id + "-gate");
            if (gate is not null && !switchOpen)
            {
                ImplicitNode barrier = Box(gate);
                WorkbenchVolume? window = layout.Volumes.SingleOrDefault(v => v.Kind == "window" && v.Id == volume.Id + "-window");
                if (window is not null) barrier = field.Subtract(barrier, Box(window));
                WorkbenchVolume? breach = layout.Volumes.SingleOrDefault(v => v.Id == volume.Id + "-breach");
                if (breach is not null) barrier = field.Subtract(barrier, Box(breach));
                passage = field.Subtract(passage, barrier);
            }
            air = field.Union(air, passage);
        }
        ImplicitNode solid = field.Subtract(field.Box(min, max), air);
        writer.Surface("workbench resolved blockout", field, solid, min, max, materials.Limestone, Identity,
            [new(field.Box(min, max with { Y = first.Minimum.Y + 0.05f }), materials.Paving)]);
        foreach (WorkbenchMarker marker in layout.Markers)
        {
            // The lookout is a floor patch: it must not occlude its own sightline.
            Vector3 at = Point(marker.Position);
            float height = marker.Id == "goal" ? WorkbenchLayout.GoalMarkerHeight
                : marker.Action == "observe" ? 0.03f : WorkbenchLayout.MarkerHeight;
            float width = WorkbenchLayout.MarkerHalfWidth;
            var material = marker.Id == "goal" ? materials.Brick
                : marker.Action == "recover" ? materials.Moss
                : marker.Action == "spend" ? materials.Paving
                : switchOpen && marker.Action == "activate" ? materials.Moss : materials.Bronze;
            writer.Box("workbench " + marker.Label, at - new Vector3(width, 0, width),
                at + new Vector3(width, height, width), material, Identity);
        }
    }
}
