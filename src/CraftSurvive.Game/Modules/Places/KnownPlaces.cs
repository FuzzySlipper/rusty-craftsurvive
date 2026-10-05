using System.Numerics;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Places;

internal enum KnownPlaceKind { Home, Visited, Seen, Entrance }

/// <summary>A place the expedition knows and can travel to; <see cref="Key"/> is stable across publishes.</summary>
internal readonly record struct KnownPlace(string Key, string Name, KnownPlaceKind Kind, Vector2 Position);

/// <summary>
/// The places on the map (#9471). There is no fog of war: geography is all visible, and what is
/// known is places. Home comes first, then what the discovery journal holds (reached, seen,
/// revealed from a vantage point or by a travel event), most recently learned first.
/// </summary>
internal static class KnownPlaces
{
    internal const string HomeKey = "home";
    private const string PlacePrefix = "poi:";

    internal static IReadOnlyList<KnownPlace> List(HomeMarker home, IEnumerable<DiscoveryEntry> journal) =>
    [
        new(HomeKey, "Home", KnownPlaceKind.Home, new((float)home.X, (float)home.Z)),
        .. journal.OrderByDescending(entry => entry.LastTick).Select(entry => new KnownPlace(
            PlacePrefix + entry.SiteId, DiscoveryRules.PlaceName(entry.Kind), Kind(entry), new(entry.X, entry.Z))),
    ];

    /// <summary>A dungeon entrance is marked as one however it became known; other places by how well they are known.</summary>
    private static KnownPlaceKind Kind(DiscoveryEntry entry) =>
        entry.Kind == PoiKind.DungeonEntrance ? KnownPlaceKind.Entrance
        : entry.Stage == DiscoveryStage.Visited ? KnownPlaceKind.Visited
        : KnownPlaceKind.Seen;
}
