using System.Globalization;
using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Towns;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game;

/// <summary>
/// Towns in the expedition (#9895; owner decisions 2026-10-14). Each settlement's town stands beside
/// its water and is seen from outside; on foot the traveller is held at its bubble's edge and offered
/// entry, and a map journey to it ends at its arrival view. Entering opens the composed visit from
/// where the traveller stands; leaving returns them there. Browsing a town costs no time; a service's
/// real action (a meal, a rest, supplies) goes through the survival and inventory owners. Being in a
/// town is not saved: a restored game resumes at the arrival view, outside the bubble.
/// </summary>
public sealed partial class CraftSurviveProduct
{
    /// <summary>A town counts as wet when this much falls, or when the traveller arrives this soaked.</summary>
    private const double WetPrecipitation = .2, SoakedWetness = .5;

    /// <summary>How far about a town's seat (m) no creature is placed: a town is a sanctuary.</summary>
    private const double SanctuaryMetres = 160;


    private const string NoTownHere = "No town is near enough to enter.";
    private const string TravellingOn = "The expedition is on the move; stop at a town to go in.";
    /// <summary>The live-debug word for "the town offered here", as Enter takes it.</summary>
    private const string TownDoorstepKey = "doorstep";
    private const string LeaveTownFirst = "Leave the town view before changing the expedition.";

    private static readonly byte[] TownContractUtf8 = Encoding.UTF8.GetBytes(TownContract);
    private const string TownContract = "craftsurvive.town.action.v1";

    private readonly TownContent townContent;
    private readonly TownModule town;
    private TownPresence? townPresence;
    private TownSite[] townSites = [];

    /// <summary>
    /// This world's town sites, read from its settlements before the player stands anywhere: a new
    /// expedition starts at the sea port's arrival view.
    /// </summary>
    private void LocateTowns() => townSites = [.. Settlements.For(worlds.Current.Map)
        .Select(settlement => TownSite.For(settlement, townContent, TownGroundAt))
        .OfType<TownSite>()];

    /// <summary>
    /// Towns stand on the recipe's continuous ground, which the drawn surface follows wherever it has
    /// streamed in yet, so a town placed from afar does not float or sink once its ground arrives.
    /// </summary>
    private double TownGroundAt(double x, double z) =>
        terrain.Recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z)) + GroundTopFace;

    /// <summary>Where a new expedition starts: the sea port's arrival view, or the spawn column without one.</summary>
    private System.Numerics.Vector2 StartColumn() => townSites.FirstOrDefault(site => site.Port) is TownSite port
        ? new((float)port.Arrival.X, (float)port.Arrival.Z)
        : PlayerConstants.SpawnColumn;

    /// <summary>Stands this world's towns: the presence that draws the nearest, and their sanctuary.</summary>
    private void CreateTowns()
    {
        townPresence = new TownPresence(engine, context.Content, townContent, frame, TownGroundAt);
        town.Blocked = TownBlocked;
        creatures.Sanctuary = (x, z) => townSites.Any(site =>
            ((site.Frame.X - x) * (site.Frame.X - x)) + ((site.Frame.Z - z) * (site.Frame.Z - z)) < SanctuaryMetres * SanctuaryMetres);
    }

    /// <summary>Withdraws this world's towns; the caller publishes the empty snapshot and then disposes.</summary>
    private void ReleaseTowns()
    {
        town.Leave();
        town.Offer("");
        townPresence?.Release();
    }

    private void DisposeTowns()
    {
        townPresence?.Dispose();
        townPresence = null;
        townSites = [];
    }

    /// <summary>Whether a point is inside a town's bubble, for the composed walking bound.</summary>
    private bool InTownBubble(double x, double z) => townSites.Any(site => site.Holds(x, z));

    /// <summary>The standing town's draw facts; none from a separate space.</summary>
    private AppearanceFact[] TownFacts() => player.InSeparateSpace || townPresence is null ? [] : townPresence.Facts;

    /// <summary>
    /// The town the traveller may enter now: walking, where they stand; with the map open, where the
    /// party stands still (never while it travels).
    /// </summary>
    private TownSite? OfferedTown()
    {
        System.Numerics.Vector3 feet = player.WorldFeetPosition;
        return player.InSeparateSpace ? null : TownEntry.Offered(townSites, mapOpen, (feet.X, feet.Z),
            party is null ? null : (party.Position.X, party.Position.Y), party is { State: TravelState.Travelling });
    }

    /// <summary>Keeps the map's offer with the party: shown while it stands at a town, withdrawn as it travels on.</summary>
    private void OfferAtParty() => town.Offer(town.Open ? "" : OfferedTown()?.Name ?? "");

    /// <summary>
    /// The walking update's towns: the nearest stands in the world, its signs swing, and at its doorstep
    /// (where a turn-back at its edge also leaves the traveller) entry is offered.
    /// </summary>
    private void FollowTowns(double hostSeconds)
    {
        if (townPresence is null) return;
        System.Numerics.Vector3 feet = player.WorldFeetPosition;
        townPresence.Follow(townSites, feet.X, feet.Z);
        townPresence.Advance(hostSeconds);
        town.Offer(OfferedTown()?.Name ?? "");
    }

    /// <summary>The conditions a visit answers to: the weather falling or the traveller soaked, the hour and the kind of place.</summary>
    private TownConditions TownConditionsNow(TownSite site) => new(
        sky.Look.Precipitation >= WetPrecipitation || survival.State.Wetness >= SoakedWetness, conditions.IsNight, site.Port);

    /// <summary>
    /// Opens a visit to the town at the doorstep, or the one a map journey arrived at. The traveller is
    /// brought to its arrival view first, so the composed view stands where they do.
    /// </summary>
    private string EnterTown(string? key = null)
    {
        if (!worldBuilt || worlds.Preparing || townPresence is null) return "Towns are open once the world is ready.";
        if (town.Open) return town.Readout();
        // An offer is checked again when it is taken: a journey under way never opens a town behind it.
        if (mapOpen && party is { State: TravelState.Travelling }) return TravellingOn;
        TownSite? site = key is not null ? townSites.FirstOrDefault(s => s.Key == key || s.Name == key) : OfferedTown();
        if (site is null || player.InSeparateSpace) return NoTownHere;
        if (mapOpen) CloseMap();
        (double x, double z) = site.Arrival;
        if (player.Teleport(x, terrain.GroundAt(x, z) + ArrivalClearance, z) is null) return $"No clear ground at {site.Name}'s arrival view.";
        // The party stands where the traveller does, so the map finds the expedition at the town.
        party?.Relocate(new((float)x, (float)z));
        player.ClearInput();
        mapView?.ClearInput();
        townPresence.Follow(townSites, x, z);
        if (!town.Enter(site, townPresence, TownConditionsNow(site), frame, player.FarDistance)) return town.Readout();
        player.SaveContinuationNow();
        PublishAppearanceSnapshot();
        return town.Readout();
    }

    /// <summary>Ends the visit: the traveller stands at the arrival view with their own camera again.</summary>
    private string LeaveTown()
    {
        if (!town.Open) return town.Readout();
        town.Leave();
        player.ClearInput();
        player.ActivateCamera();
        PublishAppearanceSnapshot();
        return town.Readout();
    }

    /// <summary>
    /// A map journey settles outside every town's bubble: arriving at a town (or a waypoint inside one)
    /// stands the party at its arrival view and offers entry.
    /// </summary>
    private void SettleOutsideTowns()
    {
        if (party is null) return;
        if (townSites.FirstOrDefault(site => site.Holds(party.Position.X, party.Position.Y)
            || site.Welcomes(party.Position.X, party.Position.Y)) is TownSite site)
        {
            (double x, double z) = site.Arrival;
            party.Relocate(new((float)x, (float)z));
        }

        OfferAtParty();
    }

    /// <summary>Plans a journey to a known place, as the map's place list does; a town's journey ends at its arrival view (#9895).</summary>
    private void PlanToPlace(string key)
    {
        if (KnownPlacesNow().FirstOrDefault(known => known.Key == key) is not { Key: not null } place)
            throw new FormatException("Choose a known place.");
        PlanTravel(TownArrival(place.Key) is (double ax, double az) ? new((float)ax, (float)az) : place.Position, place.Name);
    }

    /// <summary>The place-list journey, for the live check; reads where the plan ends.</summary>
    private string PlanToPlaceReadout(string key)
    {
        if (!mapOpen) return "refused: open the map first";
        try { PlanToPlace(key); }
        catch (FormatException refused) { return $"refused: {refused.Message}"; }
        return $"{worldMessage} | {TravelReadout()}";
    }

    /// <summary>The arrival view of a town by its known-place key, for planning a journey there.</summary>
    private (double X, double Z)? TownArrival(string key) => townSites.FirstOrDefault(site => site.Key == key)?.Arrival;

    /// <summary>The live reasons a service choice cannot be taken now.</summary>
    private string? TownBlocked(ServiceChoice choice) => choice.Action switch
    {
        "resupply" when ItemCatalog.TryFind(choice.Item, out CatalogItem item) && inventory.Count(item) >= choice.Allotment => choice.Full,
        _ => null,
    };

    /// <summary>Applies a service's real action through the expedition's owners and says what changed.</summary>
    private TownServiceOutcome ApplyTownService(TownServiceRequest request)
    {
        ServiceChoice choice = request.Choice;
        IReadOnlyDictionary<string, string> said = townContent.Outcomes;
        switch (choice.Action)
        {
            case "meal":
            {
                double before = survival.State.Satiety;
                survival.Eat(choice.Nourishment);
                survival.SaveNow();
                return new(true, said["meal"].Replace("{food}", Change(before, survival.State.Satiety), StringComparison.Ordinal));
            }
            case "rest":
            {
                (int health, double food, double wet, double chill) before = (player.Vitals.State.Health, survival.State.Satiety,
                    survival.State.Wetness * 100, survival.State.Chill * 100);
                WorldTime start = conditions.Time;
                long refused = survival.RestsRefused;
                string result = conditions.IsNight ? survival.Rest(Meal) : survival.RestFor(choice.Hours / PlayHoursPerSecond, Meal);
                if (survival.RestsRefused != refused) return new(false, said["rest-refused"].Replace("{reason}", result, StringComparison.Ordinal));
                SaveJourney();
                double hours = (conditions.Time.Day - start.Day + conditions.Time.DayFraction - start.DayFraction) * 24;
                return new(true, said["rest"]
                    .Replace("{hours}", hours.ToString("F1", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{health}", $"{before.health} → {player.Vitals.State.Health}", StringComparison.Ordinal)
                    .Replace("{food}", Change(before.food, survival.State.Satiety), StringComparison.Ordinal)
                    .Replace("{wet}", Change(before.wet, survival.State.Wetness * 100), StringComparison.Ordinal)
                    .Replace("{chill}", Change(before.chill, survival.State.Chill * 100), StringComparison.Ordinal));
            }
            case "resupply" when ItemCatalog.TryFind(choice.Item, out CatalogItem item):
            {
                int before = inventory.Count(item);
                int wanted = Math.Max(0, choice.Allotment - before);
                int taken = wanted > 0 ? inventory.Receive(item, wanted) : 0;
                inventory.SaveNow();
                if (taken == 0) return new(false, said["pack-full"]);
                return new(true, said["resupply"].Replace("{item}", item.Name, StringComparison.Ordinal)
                    .Replace("{before}", before.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{after}", inventory.Count(item).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
            default:
                return new(false, "");
        }

        static string Change(double before, double after) => string.Create(CultureInfo.InvariantCulture, $"{before:F0}% → {after:F0}%");
    }

    private string TownMotion(string state)
    {
        if (state is "on" or "off") town.SetMotion(state == "on");
        return town.Readout();
    }

    /// <summary>This world's towns: key, kind, seat, pad, facing and recipe, one per line.</summary>
    private string TownSites() => townSites.Length == 0 ? "no towns in this world"
        : string.Join('\n', townSites.Select(site => site.Describe));

    /// <summary>Stands the traveller at a town's arrival view (debug assistance), without entering.</summary>
    private string ApproachTown(string key)
    {
        if (!worldBuilt || town.Open) return town.Readout();
        if (townSites.FirstOrDefault(s => s.Key == key || s.Name == key) is not TownSite site) return NoTownHere;
        if (mapOpen) CloseMap();
        (double x, double z) = site.Arrival;
        return player.Teleport(x, terrain.GroundAt(x, z) + ArrivalClearance, z) is null ? $"No clear ground at {site.Name}'s arrival view." : site.Describe;
    }

    /// <summary>Opens a service, or returns to the overview with "back" (debug assistance; the same path as a door).</summary>
    private string SelectTownService(string service)
    {
        if (!town.Open) return town.Readout();
        if (service == "back") town.Back();
        else town.Select(service);
        return town.Readout();
    }

    /// <summary>Takes a choice in the open service as the menu does, and reads what was said and done.</summary>
    private string ChooseTownService(string service, string choice)
    {
        if (!town.Open || town.Site is not TownSite site) return town.Readout();
        if (town.Choose(service, choice, town.Visit) is TownServiceRequest request)
            town.Record(request, ApplyTownService(request), TownConditionsNow(site));
        return town.Readout() + ";" + town.ServiceReadout;
    }

    private bool HandleTownActions(ProductUpdate update)
    {
        bool handled = false;
        foreach (ProductInputEvent input in update.Input)
        {
            if (input.ValueKind != InputValueKind.ProductPayload || !input.PayloadContract.Span.SequenceEqual(TownContractUtf8)) continue;
            handled = true;
            try
            {
                using JsonDocument payload = JsonDocument.Parse(input.PayloadData);
                JsonElement root = payload.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out JsonElement action) || action.ValueKind != JsonValueKind.String) continue;
                string destination = root.TryGetProperty("destination", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                switch (action.GetString())
                {
                    case "enter":
                        string entered = EnterTown();
                        if (!town.Open) { worldMessage = entered; PublishWorld(); }
                        break;
                    case "leave": LeaveTown(); break;
                    case "back" when town.Open: town.Back(); break;
                    case "focus" when town.Open: town.FocusDestination(destination); break;
                    case "select" when town.Open: town.Select(destination); break;
                    case "motion" when town.Open: town.SetMotion(!town.Motion); break;
                    case "choose" when town.Open:
                        if (root.TryGetProperty("service", out JsonElement service) && service.ValueKind == JsonValueKind.String
                            && root.TryGetProperty("visit", out JsonElement visit) && visit.ValueKind == JsonValueKind.String
                            && ulong.TryParse(visit.GetString(), out ulong revision)
                            && town.Choose(service.GetString()!, destination, revision) is TownServiceRequest request
                            && town.Site is TownSite site)
                        {
                            TownServiceOutcome outcome = ApplyTownService(request);
                            town.Record(request, outcome, TownConditionsNow(site));
                        }

                        break;
                }
            }
            catch (JsonException)
            {
                // A malformed UI payload makes no state change.
            }
        }

        return handled;
    }
}
