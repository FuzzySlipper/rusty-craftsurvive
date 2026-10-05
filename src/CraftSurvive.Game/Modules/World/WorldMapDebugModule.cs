using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.World;

public sealed class WorldMapDebugModule : IDebugCommandModule
{
    private readonly Func<WorldCatalog> catalog;
    private readonly Func<TerrainWorld> terrain;
    internal WorldMapDebugModule(Func<WorldCatalog> catalog, Func<TerrainWorld> terrain)
    { this.catalog = catalog; this.terrain = terrain; }

    [DebugCommand("craft.world.map", Description = "Reads active generated map identity, size, generation cost, persistence and representative sites.")]
    public string ReadMap() => Read();

    private readonly Func<WorldMapVoxelView?>? faceted;

    private readonly Action<string>? requestStyle;

    internal WorldMapDebugModule(Func<WorldCatalog> catalog, Func<TerrainWorld> terrain, Func<WorldMapVoxelView?> faceted,
        Action<string> requestStyle)
        : this(catalog, terrain)
    {
        this.faceted = faceted;
        this.requestStyle = requestStyle;
    }

    [DebugCommand("craft.world.mapstyle", Description = "Experiment (#9464): rebuilds the faceted map with ground style flat, ground or painted on the next update.")]
    public string SetMapStyle(string style)
    {
        requestStyle?.Invoke(style);
        return "requested=" + style;
    }

    [DebugCommand("craft.world.faceted", Description = "Reads the prototype faceted map view's load progress and cost, if it has been built.")]
    public string ReadFaceted() => faceted?.Invoke()?.Readout ?? "faceted=none";

    [DebugCommand("craft.world.mapcamera", Description = "Assisted: sets the faceted map camera's distance (coarse cells), yaw and pitch in degrees, as wheel and orbit input would.")]
    public string SetMapCamera(double distance, double yawDegrees, double pitchDegrees)
    {
        WorldMapVoxelView? view = faceted?.Invoke();
        if (view is null) return "faceted=none";
        view.SetCamera((float)distance, (float)yawDegrees, (float)pitchDegrees);
        return view.Readout;
    }

    private string Read()
    {
        WorldCatalog c = catalog();
        if (!c.HasWorld) return FormattableString.Invariant($"world=generating;preparing={c.Preparing};restore={c.RestoreOutcome}");
        var map = c.Current.Map;
        return FormattableString.Invariant($"seed={map.Configuration.Seed};size={map.Configuration.Size};generation={c.Current.Generation};nodes={map.Grid.Count};spacing={map.Spacing:F1};rivers={map.Rivers.Reaches.Count};fingerprint={map.Fingerprint:x16};generatedMs={c.GenerationMilliseconds:F2};storedBytes={c.StoredBytes};restore={c.RestoreOutcome};sites=[{string.Join(';', map.Sites)}];terrain=[{terrain().GenerationReadout()}]");
    }
}
