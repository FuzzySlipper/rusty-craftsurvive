using CraftSurvive.Game.Modules.Towns;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The active world read out as data, page by page (#11125): what the authoring tools' golden-seed export
/// (authoring/tools/golden-seed) asks of a running host. Each command answers one JSON object; see
/// <see cref="WorldExport"/> for the layers.
/// </summary>
public sealed class WorldExportDebugModule : IDebugCommandModule
{
    private readonly Func<WorldCatalog> catalog;
    private readonly Func<IReadOnlyList<TownSite>> towns;
    private readonly IRandomService random;
    private WorldExport? export;

    internal WorldExportDebugModule(Func<WorldCatalog> catalog, Func<IReadOnlyList<TownSite>> towns, IRandomService random)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.towns = towns ?? throw new ArgumentNullException(nameof(towns));
        this.random = random ?? throw new ArgumentNullException(nameof(random));
    }

    [DebugCommand("craft.export.header", Description = "Export (#11125): the active world's identity (seed, size, generator version, fingerprint, design) and the names the other export pages use.")]
    public string Header() => Read(e => e.Header());

    [DebugCommand("craft.export.raster", Description = "Export (#11125): rows of the continent raster at pixels a side from the north-west corner - elevation, temperature, moisture and rock (float32) and biome and water codes (bytes), little-endian base64.")]
    public string Raster(long pixels, long firstRow, long rows) => Read(e => e.Raster(checked((int)pixels), checked((int)firstRow), checked((int)rows)));

    [DebugCommand("craft.export.rivers", Description = "Export (#11125): a page of the map's own 1 km rivers, as flat x,z,halfWidth point lists.")]
    public string Rivers(long page) => Read(e => e.MapRivers(checked((int)page)));

    [DebugCommand("craft.export.drainage", Description = "Export (#11125): a page of the walked drainage's reaches (250 m), with flow, navigability, channel form and path.")]
    public string Drainage(long page) => Read(e => e.Drainage(checked((int)page)));

    [DebugCommand("craft.export.places", Description = "Export (#11125): the world's towns as the game builds them, their settlements, the lakes and the representative sites.")]
    public string Places() => Read(e => e.Places());

    [DebugCommand("craft.export.window", Description = "Export (#11125): a square window at walking detail about (x, z) of half-side half metres: ground height and biome every step metres, the places, crossings and cave ways whose anchor cells lie in it, and its walked rivers.")]
    public string Window(long x, long z, long half, long step) => Read(e => e.Window(x, z, half, step));

    /// <summary>A town as the export writes it; the authoring tools' exporter seats towns the same way.</summary>
    internal static ExportedTown Exported(TownSite town)
    {
        (double arrivalX, double arrivalZ) = town.Arrival;
        Settlement settlement = town.Settlement;
        return new(town.Key, town.Name, settlement.Kind, settlement.Reason, town.Frame.X, town.Frame.Z, town.Frame.Y,
            town.Frame.FacingX, town.Frame.FacingZ, arrivalX, arrivalZ, town.Recipe, town.Layout.Key, town.Palette, town.Landmark,
            settlement.Reach, settlement.Score, settlement.Shelter, settlement.Flat, town.ServiceNames,
            town.Keepers.ToDictionary(pair => pair.Key, pair => pair.Value.Name));
    }

    private string Read(Func<WorldExport, string> page)
    {
        WorldCatalog worlds = catalog();
        if (!worlds.HasWorld) return "refused: no world yet";
        if (export is null || !ReferenceEquals(export.Map, worlds.Current.Map))
        {
            export = new WorldExport(worlds.Current.Map, () => [.. towns().Select(Exported)], new EngineTerrainDraws(random));
        }

        try
        {
            return page(export);
        }
        catch (ArgumentOutOfRangeException error)
        {
            return "refused: " + error.Message;
        }
    }
}
