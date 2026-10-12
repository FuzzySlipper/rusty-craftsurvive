using System.Numerics;
using CraftSurvive.Game.Modules.Places;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game;

/// <summary>
/// Map knowledge (#9471): the places the expedition knows replace the representative site list as
/// the map's destinations and markers. Home is a marker the player sets at the party's position.
/// </summary>
public sealed partial class CraftSurviveProduct
{
    private IReadOnlyList<KnownPlace> KnownPlacesNow() => KnownPlaces.List(home.Home, discovery.Entries,
        sledWithParty ? null : sled.Sled.Position, SettlementsNow(),
        Bounds is FrontierBound bound ? at => bound.Open(at.X, at.Y) : null);

    /// <summary>The kind of reserved site a design marks its mountain hold (#9875).</summary>
    private const string HoldKind = nameof(SettlementKind.Hold);

    /// <summary>
    /// The continent's settlements (#9875): the sea port and the river towns chosen on its water network,
    /// then the hold its design reserves at the neck.
    /// </summary>
    private IEnumerable<(string Name, SettlementKind Role, double X, double Z)> SettlementsNow() =>
    [
        .. Settlements.For(worlds.Current.Map).Select(s => (s.Name, s.Kind, s.X, s.Z)),
        .. (Bounds?.Sites ?? []).Where(site => site.Kind == HoldKind).Select(site => (site.Name, SettlementKind.Hold, site.X, site.Z)),
    ];

    /// <summary>How the list describes a place: how it is known and the country it stands in.</summary>
    private static string PlaceDetail(KnownPlace place, WorldMap map)
    {
        string known = place.Kind switch
        {
            KnownPlaceKind.Home => "home",
            KnownPlaceKind.Entrance => "dungeon entrance",
            KnownPlaceKind.Visited => "visited",
            KnownPlaceKind.Sled => "where the sled was left",
            KnownPlaceKind.Town => place.Role switch
            {
                SettlementKind.Port => "sea port",
                SettlementKind.Hold => "mountain hold",
                _ => "river town",
            },
            _ => "seen from afar",
        };
        return $"{known} · {WorldMap.Region(map.Sample(place.Position.X, place.Position.Y))}";
    }

    /// <summary>Home moves to where the party stands (or the player, before any journey).</summary>
    private void SetHomeAtParty()
    {
        Vector2 here = party?.Position ?? new(player.WorldFeetPosition.X, player.WorldFeetPosition.Z);
        home.Set(here);
        worldMessage = FormattableString.Invariant($"Home is now here ({here.X:F0}, {here.Y:F0}).");
    }
}
