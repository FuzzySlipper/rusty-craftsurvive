using System.Globalization;
using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Dungeons;
using Rusty.Engine;

namespace CraftSurvive.Game;

public sealed partial class CraftSurviveProduct
{
    private const string WorldActionContract = "craftsurvive.world.action.v1";
    private static readonly byte[] WorldActionContractUtf8 = Encoding.UTF8.GetBytes(WorldActionContract);
    private const double ArrivalClearance = 4;
    private static readonly int[] NewWorldSizes = [4096, 8192, 16384];
    private const int MetresPerKilometre = 1024;
    private const string FirstWorldMessage = "Generating your first world: raising ranges, running rivers and settling climate...";

    private bool HandleWorldActions(ProductUpdate update)
    {
        bool handled = false;
        foreach (ProductInputEvent input in update.Input)
        {
            if (input.ValueKind != InputValueKind.ProductPayload || !input.PayloadContract.Span.SequenceEqual(WorldActionContractUtf8)) continue;
            handled = true;
            try
            {
                using JsonDocument payload = JsonDocument.Parse(input.PayloadData);
                JsonElement root = payload.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out JsonElement action) || action.ValueKind != JsonValueKind.String)
                    throw new FormatException("Choose a world action.");
                switch (action.GetString())
                {
                    case "open":
                        OpenMap();
                        break;
                    case "close":
                        CloseMap();
                        break;
                    case "create":
                        if (!mapOpen) throw new FormatException("Open the world map to start a new world.");
                        if (!root.TryGetProperty("seed", out JsonElement seedElement) || seedElement.ValueKind != JsonValueKind.String
                            || !ulong.TryParse(seedElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
                            throw new FormatException("Use a whole-number seed between 0 and 18446744073709551615.");
                        if (!root.TryGetProperty("size", out JsonElement sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt32(out int size) || !NewWorldSizes.Contains(size))
                            throw new FormatException("Choose one of the offered world sizes.");
                        if (worlds.Preparing) throw new FormatException("A world is already being generated.");
                        worlds.BeginPrepare(seed, size);
                        worldMessage = FormattableString.Invariant(
                            $"Generating a {size / MetresPerKilometre} km world: raising ranges, running rivers and settling climate...");
                        break;
                    case "visit":
                        if (!mapOpen || !root.TryGetProperty("site", out JsonElement siteElement) || siteElement.ValueKind != JsonValueKind.Number || !siteElement.TryGetInt32(out int site)
                            || site < 0 || site >= worlds.Current.Map.Sites.Count) throw new FormatException("Choose a place on this map.");
                        if (player.InSeparateSpace) throw new FormatException("Return to the overworld before visiting another site.");
                        MapSite destination = worlds.Current.Map.Sites[site];
                        if (player.Teleport(destination.X, terrain.GroundAt(destination.X, destination.Z) + ArrivalClearance, destination.Z) is null)
                            throw new FormatException("No clear arrival here; choose another site.");
                        worldMessage = $"Exploring {destination.Name}";
                        CloseMap();
                        break;
                    default: throw new FormatException("Unknown world action.");
                }
            }
            catch (Exception malformed) when (malformed is FormatException or JsonException)
            {
                worldMessage = malformed.Message;
            }
            PublishWorld();
        }
        return handled;
    }

    private void OpenMap()
    {
        overview ??= new(engine, worlds.Current.Map);
        player.ClearInput();
        sky.Submerged(false);
        sky.Underground(false, WorldConditionsState.Fresh.Time);
        mapOpen = true;
        overview.Activate();
        PublishAppearanceSnapshot();
    }

    private void CloseMap()
    {
        mapOpen = false;
        player.ClearInput();
        sky.Underground(dungeons.State == DungeonState.Inside, conditions.Time);
        sky.Submerged(player.HeadSubmerged);
        player.ActivateCamera();
        PublishAppearanceSnapshot();
    }

    /// <summary>Admit a finished world simulation from the update thread; true when the world changed hands this update.</summary>
    private bool AdmitPreparedWorld()
    {
        WorldMapSave? prepared;
        try { prepared = worlds.TakePrepared(); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            worldMessage = "The world could not be generated: " + failure.Message;
            PublishWorld();
            return false;
        }
        if (prepared is null) return false;
        if (!worldBuilt)
        {
            AdmitFirstWorld(prepared);
            return true;
        }
        try { NewWorld(prepared); }
        catch (FormatException refused) { worldMessage = refused.Message; }
        PublishWorld();
        return true;
    }

    /// <summary>
    /// The first world of a fresh store: commit it, build its owners and start play at the
    /// arrival area, exactly as a restored world starts.
    /// </summary>
    private void AdmitFirstWorld(WorldMapSave prepared)
    {
        if (!worlds.Commit(prepared))
        {
            worldMessage = "The first world could not be saved; restart to try again.";
            PublishWorld();
            return;
        }
        CreateWorld();
        StartWorld();
        mapOpen = false;
        worldMessage = "";
        PublishWorld();
    }

    private void NewWorld(WorldMapSave prepared)
    {
        // The one atomic map record selects the new gameplay namespace. Old slots retain
        // their captured keys and can flush safely even after this selection commits.
        if (!worlds.Commit(prepared)) throw new FormatException("The world changed elsewhere; restart before creating another.");
        ui.ResetWorld();
        mapOpen = false;
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        overview?.Dispose();
        overview = null;
        foreach (IProductModule module in gameplay.Reverse()) module.Dispose();
        sky.Dispose(); player.Dispose(); terrain.Dispose();
        frame = new(); cues = new Cues(); entities = new BlockEntityIndex();
        CreateWorld();
        entityDebug.ReplaceStore("craft", player.EntityStore);
        entityDebug.ReplaceStore("creatures", creatures.EntityStore);
        terrain.Start(); player.Start();
        foreach (IProductModule module in gameplay) module.Start();
        worldMessage = "New world ready. Choose a place to explore, or return to the starting area.";
        OpenMap();
    }

    private void PublishWorld()
    {
        if (!worlds.HasWorld)
        {
            TerrainConfiguration first = TerrainConfiguration.Default;
            ui.PublishMap(new(true, first.Seed.ToString(CultureInfo.InvariantCulture), first.Size, "", worldMessage, 0));
            return;
        }
        WorldMap map = worlds.Current.Map;
        string sites = string.Join(';', map.Sites.Select((site, index) => FormattableString.Invariant(
            $"{index}|{site.Name}|{WorldMap.Region(site.Geography)}|{site.X:F0}|{site.Z:F0}|{site.Geography.Elevation:F0}")));
        ui.PublishMap(new(mapOpen, map.Configuration.Seed.ToString(CultureInfo.InvariantCulture), map.Configuration.Size,
            sites, worldMessage, worlds.Current.Generation));
    }
}
