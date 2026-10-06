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
        sledWithParty ? null : sled.Sled.Position);

    /// <summary>How the list describes a place: how it is known and the country it stands in.</summary>
    private static string PlaceDetail(KnownPlace place, WorldMap map)
    {
        string known = place.Kind switch
        {
            KnownPlaceKind.Home => "home",
            KnownPlaceKind.Entrance => "dungeon entrance",
            KnownPlaceKind.Visited => "visited",
            KnownPlaceKind.Sled => "where the sled was left",
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
