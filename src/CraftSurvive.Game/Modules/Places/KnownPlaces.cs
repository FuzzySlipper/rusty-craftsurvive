using System.Numerics;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Places;

internal enum KnownPlaceKind { Home, Visited, Seen, Entrance, Sled }

/// <summary>A place the expedition knows and can travel to; <see cref="Key"/> is stable across publishes.</summary>
internal readonly record struct KnownPlace(string Key, string Name, KnownPlaceKind Kind, Vector2 Position);

/// <summary>
/// The places on the map (#9471). There is no fog of war: geography is all visible, and what is
/// known is places. Home comes first, then what the discovery journal holds (reached, seen,
/// revealed from a vantage point or by a travel event), most recently learned first.
/// The map holds expedition-scale sites only (#9553): the way into a dungeon, ruins, vantage points,
/// home and the sled. Walking landmarks (standing stones, cave mouths) on the 256 m site lattice stay
/// in the journal and on the minimap, where a session on foot reads them.
/// </summary>
internal static class KnownPlaces
{
    internal const string HomeKey = "home";
    internal const string SledKey = "sled";
    private const string PlacePrefix = "poi:";

    /// <summary>The kinds of found place the map shows; the rest are walking landmarks.</summary>
    internal static bool IsMapSite(PoiKind kind) => kind is PoiKind.DungeonEntrance or PoiKind.Ruin or PoiKind.VantagePoint;

    /// <param name="sled">Where the sled was left, when it is not with the party (#9473).</param>
    internal static IReadOnlyList<KnownPlace> List(HomeMarker home, IEnumerable<DiscoveryEntry> journal, Vector2? sled = null) =>
    [
        new(HomeKey, "Home", KnownPlaceKind.Home, new((float)home.X, (float)home.Z)),
        .. sled is Vector2 left ? [new KnownPlace(SledKey, "The sled", KnownPlaceKind.Sled, left)] : Array.Empty<KnownPlace>(),
        .. journal.Where(entry => IsMapSite(entry.Kind)).OrderByDescending(entry => entry.LastTick).Select(entry => new KnownPlace(
            PlacePrefix + entry.SiteId, DiscoveryRules.PlaceName(entry.Kind), Kind(entry), new(entry.X, entry.Z))),
    ];

    /// <summary>A dungeon entrance is marked as one however it became known; other places by how well they are known.</summary>
    private static KnownPlaceKind Kind(DiscoveryEntry entry) =>
        entry.Kind == PoiKind.DungeonEntrance ? KnownPlaceKind.Entrance
        : entry.Stage == DiscoveryStage.Visited ? KnownPlaceKind.Visited
        : KnownPlaceKind.Seen;
}
