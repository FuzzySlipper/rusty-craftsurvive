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

    private readonly Func<WorldMapVoxelView?>? mapView;

    private readonly Action<string>? requestStyle;

    private readonly Func<string, string>? showMap;

    internal WorldMapDebugModule(Func<WorldCatalog> catalog, Func<TerrainWorld> terrain, Func<WorldMapVoxelView?> mapView,
        Action<string> requestStyle, Func<string, string> showMap)
        : this(catalog, terrain)
    {
        this.mapView = mapView;
        this.requestStyle = requestStyle;
        this.showMap = showMap;
    }

    [DebugCommand("craft.world.create", Description = "Assisted (#9874): opens the world map and starts generating a new world of a seed and size, as the map's New world does; it is admitted on a later update (craft.world.map reads it).")]
    public string Create(string seed, long size)
    {
        if (!ulong.TryParse(seed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong parsed))
            return "refused: the seed must be a whole number";
        if (catalog().Preparing) return "refused: a world is already being generated";
        // A new world replaces the old one's owners while the map is the published scene, as the map's own
        // New world requires: with first person still published, its meshes would be disposed while drawn.
        showMap?.Invoke("open");
        catalog().BeginPrepare(parsed, checked((int)size));
        return $"preparing seed={parsed} size={size}";
    }

    [DebugCommand("craft.world.view", Description = "Assisted (#9841): opens the world map (open) or closes it (close), as the HUD's World and Explore here buttons do.")]
    public string ShowMap(string view) => showMap?.Invoke(view) ?? "map=none";

    [DebugCommand("craft.world.mapstyle", Description = "Experiment (#9464): rebuilds the map with ground style flat, ground or painted on the next update.")]
    public string SetMapStyle(string style)
    {
        requestStyle?.Invoke(style);
        return "requested=" + style;
    }

    [DebugCommand("craft.world.faceted", Description = "Reads the world map view's load progress and cost, if it has been built (named for the faceted view it once was).")]
    public string ReadFaceted() => mapView?.Invoke()?.Readout ?? "map=none";

    [DebugCommand("craft.world.mapcamera", Description = "Assisted: sets the map camera's distance (coarse cells), yaw and pitch in degrees, as wheel and orbit input would.")]
    public string SetMapCamera(double distance, double yawDegrees, double pitchDegrees)
    {
        WorldMapVoxelView? view = mapView?.Invoke();
        if (view is null) return "map=none";
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
