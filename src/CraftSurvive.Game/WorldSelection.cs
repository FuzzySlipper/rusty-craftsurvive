using System.Numerics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Places;
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
    private static readonly int[] NewWorldSizes = [4096, 8192, 16384, MapScale.DefaultContinentalSize];
    private const int MetresPerKilometre = 1024;
    private const string BuildingMapMessage = "Drawing the map...";
    private const string OpenMapView = "open";
    /// <summary>The two views the map once had (#9841) still open it, for older lane scripts.</summary>
    private static readonly string[] FormerMapViews = ["smooth", "faceted"];
    private const string CloseMapView = "close";
    private const string FirstWorldMessage ="Generating your first world: raising ranges, running rivers and settling climate...";

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
                    // The pause menu's hold, apart from the journey's own pause (#9916 review).
                    case MenuPauseAction:
                        SetPaused(true);
                        break;
                    case MenuResumeAction:
                        SetPaused(false);
                        break;
                    case "quit":
                        Quit();
                        break;
                    case "open":
                        OpenMap();
                        break;
                    case "close":
                        ExploreAtParty();
                        CloseMap();
                        break;
                    case "travel":
                        if (!mapOpen || !root.TryGetProperty("place", out JsonElement placeKey) || placeKey.ValueKind != JsonValueKind.String
                            || KnownPlacesNow().FirstOrDefault(known => known.Key == placeKey.GetString()) is not { Key: not null } place)
                            throw new FormatException("Choose a known place.");
                        PlanTravel(place.Position, place.Name);
                        break;
                    case "sled":
                        if (!root.TryGetProperty("op", out JsonElement sledOp) || sledOp.ValueKind != JsonValueKind.String
                            || !root.TryGetProperty("item", out JsonElement sledItem) || sledItem.ValueKind != JsonValueKind.String)
                            throw new FormatException("Choose what to stow or take.");
                        SledTransfer(sledOp.GetString()!, sledItem.GetString()!);
                        break;
                    case "event":
                        if (!mapOpen || !root.TryGetProperty("choice", out JsonElement choice) || choice.ValueKind != JsonValueKind.String)
                            throw new FormatException("Choose an answer to the event.");
                        ResolveTravelEvent(choice.GetString()!);
                        break;
                    case "home":
                        if (!mapOpen) throw new FormatException("Open the world map to set home.");
                        SetHomeAtParty();
                        break;
                    case "waypoint":
                        if (!mapOpen) throw new FormatException("Open the world map to travel.");
                        ShowMapView();
                        PlanTravel(mapView!.Waypoint, "the waypoint");
                        break;
                    case "speed":
                        if (!root.TryGetProperty("speed", out JsonElement speedElement) || speedElement.ValueKind != JsonValueKind.Number || !speedElement.TryGetInt32(out int speed))
                            throw new FormatException("Choose one of the offered travel speeds.");
                        SetTravelSpeed(speed);
                        break;
                    case "raft" or "canoe":
                        if (!mapOpen) throw new FormatException("Open the world map to build a boat.");
                        BuildBoat(action.GetString()!);
                        break;
                    case "go" or "pause" or "halt" or "camp":
                        if (!mapOpen) throw new FormatException("Open the world map to travel.");
                        TravelAction(action.GetString()!);
                        break;
                    case "create":
                        if (!mapOpen && !paused) throw new FormatException("Open the world map or the menu to start a new world.");
                        if (!root.TryGetProperty("seed", out JsonElement seedElement) || seedElement.ValueKind != JsonValueKind.String
                            || !ulong.TryParse(seedElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
                            throw new FormatException("Use a whole-number seed between 0 and 18446744073709551615.");
                        if (!root.TryGetProperty("size", out JsonElement sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt32(out int size) || !NewWorldSizes.Contains(size))
                            throw new FormatException("Choose one of the offered world sizes.");
                        if (worlds.Preparing) throw new FormatException("A world is already being generated.");
                        worlds.BeginPrepare(seed, size);
                        // From the menu the game runs again on the map, where the new world arrives.
                        SetPaused(false);
                        if (!mapOpen) OpenMap();
                        worldMessage = MapScale.IsContinental(size)
                            ? FormattableString.Invariant($"Generating a {size / 1000} km continent: raising ranges, running rivers and settling climate (this takes a little while)...")
                            : FormattableString.Invariant($"Generating a {size / MetresPerKilometre} km world: raising ranges, running rivers and settling climate...");
                        break;
                    case "visit":
                        if (!mapOpen || !root.TryGetProperty("site", out JsonElement siteElement) || siteElement.ValueKind != JsonValueKind.Number || !siteElement.TryGetInt32(out int site)
                            || site < 0 || site >= worlds.Current.Map.Sites.Count) throw new FormatException("Choose a place on this map.");
                        if (player.InSeparateSpace) throw new FormatException("Return to the overworld before visiting another site.");
                        MapSite destination = worlds.Current.Map.Sites[site];
                        if (BoundRefusal(destination.X, destination.Z) is string refusal) throw new FormatException(refusal);
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
        player.ClearInput();
        sky.Submerged(false);
        sky.Underground(false, WorldConditionsState.Fresh.Time);
        sky.Map(true);
        mapOpen = true;
        // The party and the sled's hitch are settled first, so the map draws the places as they now stand.
        SyncPartyToPlayer();
        ShowMapView();
    }

    private void CloseMap()
    {
        mapOpen = false;
        player.ClearInput();
        sky.Map(false);
        sky.Underground(dungeons.State == DungeonState.Inside, conditions.Time);
        sky.Submerged(player.HeadSubmerged);
        player.ActivateCamera();
        PublishAppearanceSnapshot();
    }

    /// <summary>Open the map or close it, as the World button and Explore here do (craft.world.view, #9841).</summary>
    private string MapViewReadout(string view)
    {
        switch (view)
        {
            case OpenMapView:
            case var former when FormerMapViews.Contains(former):
                if (!mapOpen) OpenMap();
                break;
            case CloseMapView:
                if (mapOpen)
                {
                    ExploreAtParty();
                    CloseMap();
                }
                break;
            default: return $"refused: {OpenMapView} or {CloseMapView}";
        }
        PublishWorld();
        return $"open={mapOpen};message={worldMessage}";
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
        mapView?.Dispose();
        mapView = null;
        travelCost = null;
        party = null;
        travelEvents = null;
        pendingEvent = null;
        sledTravelCost = null;
        sledWithParty = false;
        boatWithParty = false;
        foreach (IProductModule module in gameplay.Reverse()) module.Dispose();
        sled.Save();
        sled.Dispose();
        boat.Save();
        boat.Dispose();
        sky.Dispose(); player.Dispose(); terrain.Dispose();
        frame = new(); cues = new Cues(); entities = new BlockEntityIndex();
        CreateWorld();
        entityDebug.RegisterStore("craft", player.EntityStore);
        entityDebug.RegisterStore("creatures", creatures.EntityStore);
        terrain.Start(); player.Start();
        foreach (IProductModule module in gameplay) module.Start();
        worldMessage = "New world ready. Choose a place to explore, or return to the starting area.";
        OpenMap();
    }

    /// <summary>Show the map: built once per world and style, its detail patch then following the party.</summary>
    private void ShowMapView()
    {
        Vector3 partyFeet = player.WorldFeetPosition;
        mapView ??= new(engine, context.Content, worlds.Current.Map, partyFeet, mapStyle);
        mapView.MoveParty(party?.Position ?? new(partyFeet.X, partyFeet.Z));
        mapView.ShowRoute(party?.Route?.Points);
        mapView.ShowPlaces(KnownPlacesNow());
        mapView.Activate();
        if (!mapView.Loaded) worldMessage = BuildingMapMessage;
        PublishAppearanceSnapshot();
    }

    /// <summary>Rebuild the map in a newly requested ground style, from an update.</summary>
    private void ApplyRequestedMapStyle()
    {
        if (requestedMapStyle is not MapSurfaceStyle style) return;
        requestedMapStyle = null;
        mapStyle = style;
        DisposeMapView();
        if (mapOpen) ShowMapView();
    }

    /// <summary>Retire the map view: its markers and clutter leave the published snapshot before their appearances go.</summary>
    private void DisposeMapView()
    {
        if (mapView is null) return;
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        mapView.Dispose();
        mapView = null;
    }

    /// <summary>Feed the map a bounded batch per update while it is open.</summary>
    private void AdvanceMapView()
    {
        if (mapView is null) return;
        bool wasLoaded = mapView.Loaded;
        mapView.Advance();
        if (wasLoaded || !mapView.Loaded) return;
        if (worldMessage == BuildingMapMessage) worldMessage = "";
        PublishWorld();
    }

    private void PublishWorld()
    {
        if (!worlds.HasWorld)
        {
            TerrainConfiguration first = TerrainConfiguration.Default;
            ui.PublishMap(new(true, first.Seed.ToString(CultureInfo.InvariantCulture), first.Size, "", worldMessage, 0, "", "idle", "", "", travelSpeed));
            return;
        }
        WorldMap map = worlds.Current.Map;
        IReadOnlyList<KnownPlace> known = KnownPlacesNow();
        mapView?.ShowPlaces(known);
        Vector2 from = party?.Position ?? new(player.WorldFeetPosition.X, player.WorldFeetPosition.Z);
        string sites = string.Join(';', known.Select(place => FormattableString.Invariant(
            $"{place.Key}|{place.Name}|{PlaceDetail(place, map)}|{place.Position.X:F0}|{place.Position.Y:F0}|{Vector2.Distance(from, place.Position) / 1000:F1}|{place.Kind.ToString().ToLowerInvariant()}")));
        ui.PublishMap(new(mapOpen, map.Configuration.Seed.ToString(CultureInfo.InvariantCulture), map.Configuration.Size,
            sites, worldMessage, worlds.Current.Generation, TravelStatus(), TravelPhase, TravelSupplies(), TravelEventFacts(), travelSpeed));
    }
}
