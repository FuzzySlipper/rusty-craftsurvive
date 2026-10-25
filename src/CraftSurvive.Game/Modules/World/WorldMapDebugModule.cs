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

    [DebugCommand("craft.world.mapsweep", Description = "Study (#10345), with the map open: puts the party token at (fromX, fromZ), forgets the map layers' stream timings, then moves it toward (toX, toZ) so many metres an update, streaming the detail patch the same way every run (craft.world.mapstream reads the cost).")]
    public string Sweep(double fromX, double fromZ, double toX, double toZ, double metresPerUpdate)
    {
        WorldMapVoxelView? view = mapView?.Invoke();
        if (view is null) return "map=none";
        if (metresPerUpdate <= 0) return "refused: the step must be positive";
        view.Sweep(new((float)fromX, (float)fromZ), new((float)toX, (float)toZ), (float)metresPerUpdate);
        return view.StreamReadout;
    }

    [DebugCommand("craft.world.mapstream", Description = "Study (#10345): reads each map layer's stream cost since the last sweep began - update percentiles, hand-offs, apply and refresh cost, and deferred hand-offs' lag in updates.")]
    public string ReadStream() => mapView?.Invoke()?.StreamReadout ?? "map=none";

    [DebugCommand("craft.world.mapdigest", Description = "Study (#10346): digests of each map layer's sampled ground, so a window a sweep streamed to can be compared with the same window built by a jump.")]
    public string ReadDigest() => mapView?.Invoke()?.DigestReadout ?? "map=none";

    [DebugCommand("craft.world.mapshift", Description = "Study (#10346): exact (the default) has a detail window shift resample only the detail chunks whose edge fade changed and re-sink the coarse ground from kept ranges; rings re-streams both windows' edge rings and samples every sink afresh, as before, for the A/B.")]
    public string SetMapShift(string mode)
    {
        WorldMapVoxelView.ExactShift = mode != "rings";
        return WorldMapVoxelView.ExactShift ? "a window shift resamples what it changed" : "a window shift re-streams both windows' edge rings";
    }

    [DebugCommand("craft.world.mapregion", Description = "Study (#10347, #10352): sliced (the default) has a continent's region window moving resample only the coarse and patch columns and ribbon tiles where its fade changed, dropping the patch's and the sink's kept ground only there, the columns over later updates; exact resamples those columns in the move's update; band resamples both windows' whole fade bands and drops it all, as before #10347, for the A/B.")]
    public string SetMapRegion(string mode)
    {
        WorldMapVoxelView.ExactRegion = mode != "band";
        WorldMapVoxelView.SliceRegion = mode == "sliced";
        return !WorldMapVoxelView.ExactRegion ? "a region window move resamples both windows' fade bands"
            : WorldMapVoxelView.SliceRegion ? "a region window move resamples where its fade changed, over later updates"
            : "a region window move resamples where its fade changed, at once";
    }

    [DebugCommand("craft.world.mapslice", Description = "Study (#10352): how long, in milliseconds, each of the coarse and the patch layer spends an update resampling what a sliced region move changed.")]
    public string SetMapSlice(double milliseconds)
    {
        if (milliseconds <= 0) return "refused: the slice must be positive";
        WorldMapVoxelView.RegionSliceMilliseconds = milliseconds;
        return FormattableString.Invariant($"a sliced region move resamples up to {milliseconds} ms a layer an update");
    }

    [DebugCommand("craft.world.mapkeep", Description = "Study (#10354): on (the default) has the map's windows moving read again only what they change - the coarse tier's columns by the patch keep their ground and resample for their sink alone, the clutter keeps the ground at its points and scatters for the edge fade alone, the patch's columns are reused wherever the fade reaches, the region ribbons read the kept coarse tops, and a sliced region move admits its outermost ring over later updates; off reads them afresh, as before, for the A/B.")]
    public string SetMapKeep(string mode)
    {
        WorldMapVoxelView.KeepGround = mode != "off";
        return WorldMapVoxelView.KeepGround ? "the map's windows moving keep the ground they do not change" : "the map's windows moving read their ground afresh";
    }

    [DebugCommand("craft.world.mapdefer", Description = "Study (#10345, rusty-engine #10327): on hands the map layers' chunks to the Engine deferred once each is drawn - written now, meshed on its worker, installed at a later update's start, the projection refreshed every update until then (the default); off hands them over synchronously, for the A/B.")]
    public string SetMapDefer(string mode)
    {
        MapVoxelLayer.Deferred = mode == "on";
        return MapVoxelLayer.Deferred ? "map chunks are handed over deferred once drawn" : "map chunks are handed over synchronously";
    }

    private string Read()
    {
        WorldCatalog c = catalog();
        if (!c.HasWorld) return FormattableString.Invariant($"world=generating;preparing={c.Preparing};restore={c.RestoreOutcome}");
        var map = c.Current.Map;
        return FormattableString.Invariant($"seed={map.Configuration.Seed};size={map.Configuration.Size};generation={c.Current.Generation};nodes={map.Grid.Count};spacing={map.Spacing:F1};rivers={map.Rivers.Reaches.Count};fingerprint={map.Fingerprint:x16};generatedMs={c.GenerationMilliseconds:F2};storedBytes={c.StoredBytes};restore={c.RestoreOutcome};sites=[{string.Join(';', map.Sites)}];terrain=[{terrain().GenerationReadout()}]");
    }
}
