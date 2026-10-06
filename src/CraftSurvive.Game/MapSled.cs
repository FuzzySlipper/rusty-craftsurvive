using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Places;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game;

/// <summary>
/// The expedition's sled (#9473). On the map it is part of the party when the party set out from
/// beside it: routes then favour snow and ice, a load slows the march, and its rations feed the
/// party once the pack's run out. "Explore here" leaves it at the token, where it stands in first
/// person and the pack screen can stow into it and take from it while the player is within reach.
/// How it is pulled about on foot is a later exploration (#9518).
/// </summary>
public sealed partial class CraftSurviveProduct
{
    /// <summary>The sled is drawn in first person within this distance.</summary>
    private const float SledDrawMetres = 256;

    private SledStore sled = null!;
    private TravelCostModel? sledTravelCost;
    /// <summary>Whether the sled travels with the party on this map visit.</summary>
    private bool sledWithParty;

    private TravelCostModel SledTravelCost => sledTravelCost ??= new TravelCostModel(worlds.Current.Map, SledTravel.Terrain);

    /// <summary>The cost model for how the party travels now.</summary>
    private TravelCostModel PartyCost => sledWithParty ? SledTravelCost : TravelCost;

    private Vector2 PlayerGround => new(player.WorldFeetPosition.X, player.WorldFeetPosition.Z);

    /// <summary>
    /// On opening the map, the sled joins the party only if the player stands beside it, whatever the
    /// journey's state: a paused route reopened away from the sled leaves it behind. A route already
    /// planned is planned again for how the party now travels, and the map's places follow at once.
    /// </summary>
    private void HitchSledIfNear()
    {
        if (party is null || party.State == TravelState.Travelling) return;
        bool hitched = sled.Sled.Within(PlayerGround);
        bool changed = hitched != sledWithParty;
        sledWithParty = hitched;
        ApplySledLoad();
        if (changed && party is { State: TravelState.Planned or TravelState.Paused, Route: TravelRoute route })
        {
            party.Plan(PartyCost, route.Points[^1], route.Destination);
            worldMessage = party.Route is TravelRoute replanned ? RoutePreview(replanned) : party.Last;
        }
        RefreshPlaces();
    }

    /// <summary>Both map views show the places as they stand now, the sled among them only when left behind.</summary>
    private void RefreshPlaces()
    {
        IReadOnlyList<KnownPlace> known = KnownPlacesNow();
        overview?.ShowPlaces(known);
        facetedMap?.ShowPlaces(known);
    }

    private void ApplySledLoad()
    {
        if (party is not null) party.LoadMultiplier = sledWithParty ? SledTravel.LoadMultiplier(sled.Sled.LoadFraction) : 1;
    }

    /// <summary>A hitched sled stops where the party stops, and is saved there; a party stopping beside its sled hitches it.</summary>
    private void SettleSled()
    {
        if (party is null) return;
        // A party that stops beside the sled it left takes it up again.
        if (!sledWithParty && sled.Sled.Within(party.Position))
        {
            sledWithParty = true;
            ApplySledLoad();
            RefreshPlaces();
        }
        if (!sledWithParty) return;
        sled.Sled.MoveTo(party.Position);
        sled.Save();
    }

    /// <summary>A ration from the hitched sled, once the pack has none.</summary>
    private bool TakeSledRation() => sledWithParty && sled.Sled.Take(ItemCatalog.Ration, 1) == 1;

    private string SledSupplies() => sledWithParty
        ? string.Create(CultureInfo.InvariantCulture, $"sled {sled.Sled.Load}/{Sled.Capacity} ({sled.Sled.Count(ItemCatalog.Ration)} rations)")
        : string.Create(CultureInfo.InvariantCulture, $"sled left behind {Vector2.Distance(party?.Position ?? PlayerGround, sled.Sled.Position) / 1000:F1} km away");

    /// <summary>Stow a kind from the pack into the sled, or take it out, while the player stands beside it.</summary>
    private void SledTransfer(string op, string itemId)
    {
        if (player.InSeparateSpace || !sled.Sled.Within(PlayerGround)) throw new FormatException("Stand beside the sled to load it.");
        if (!ItemCatalog.TryFind(itemId, out CatalogItem item)) throw new FormatException("Choose something to move.");
        switch (op)
        {
            case "stow":
                int stowed = sled.Sled.Stow(item, inventory.Count(item));
                int spent = 0;
                while (spent < stowed && inventory.Spend(item)) spent++;
                if (spent < stowed) sled.Sled.Take(item, stowed - spent);
                worldMessage = spent > 0 ? $"Stowed {spent} {item.Name.ToLowerInvariant()} on the sled." : "The sled is full.";
                break;
            case "take":
                int wanted = sled.Sled.Count(item);
                int granted = inventory.Receive(item, wanted);
                sled.Sled.Take(item, granted);
                worldMessage = granted > 0 ? $"Took {granted} {item.Name.ToLowerInvariant()} from the sled." : "The pack is full.";
                break;
            default:
                throw new FormatException("Stow or take.");
        }
        sled.Save();
        inventory.SaveNow();
        PublishSled();
    }

    private void PublishSled()
    {
        float distance = Vector2.Distance(PlayerGround, sled.Sled.Position);
        ui.PublishSled(new SledUiFacts(!player.InSeparateSpace && sled.Sled.Within(PlayerGround), sled.Sled.Load, Sled.Capacity,
            string.Join(';', ItemCatalog.All.Where(item => sled.Sled.Count(item) > 0)
                .Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.Id}|{item.Name}|{sled.Sled.Count(item)}"))),
            Math.Round(distance)));
    }

    /// <summary>The sled in the first-person snapshot, when it stands near enough to be seen.</summary>
    private AppearanceFact[] SledFacts()
    {
        Vector2 at = sled.Sled.Position;
        if (player.InSeparateSpace || Vector2.Distance(PlayerGround, at) > SledDrawMetres) return [];
        return [sled.Fact(frame, terrain.GroundAt(at.X, at.Y))];
    }
}
