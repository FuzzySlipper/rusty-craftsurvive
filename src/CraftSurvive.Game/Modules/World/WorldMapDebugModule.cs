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
    public string ReadMap()
    {
        WorldCatalog c = catalog();
        if (!c.HasWorld) return FormattableString.Invariant($"world=generating;preparing={c.Preparing};restore={c.RestoreOutcome}");
        var map = c.Current.Map;
        return FormattableString.Invariant($"seed={map.Configuration.Seed};size={map.Configuration.Size};generation={c.Current.Generation};nodes={map.Grid.Count};spacing={map.Spacing:F1};rivers={map.Rivers.Reaches.Count};fingerprint={map.Fingerprint:x16};generatedMs={c.GenerationMilliseconds:F2};storedBytes={c.StoredBytes};restore={c.RestoreOutcome};sites=[{string.Join(';', map.Sites)}];terrain=[{terrain().GenerationReadout()}]");
    }
}
