using System.Numerics;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>One drawn place marker: a single place, or several that crowd together at the current zoom.</summary>
internal readonly record struct PlaceCluster(Vector2 Position, int Count, KnownPlace First)
{
    internal bool Single => Count == 1;
}

/// <summary>
/// Places that crowd together at a zoom are drawn as one cluster marker (#9553). Grouping is by a
/// square grid of the given size in world metres, so a group never depends on list order. Home and the
/// sled are never folded into a cluster: they are the expedition's own anchors.
/// </summary>
internal static class PlaceClusters
{
    internal static IReadOnlyList<PlaceCluster> Group(IReadOnlyList<KnownPlace> places, float cellMetres)
    {
        if (!(cellMetres > 0)) return [.. places.Select(place => new PlaceCluster(place.Position, 1, place))];
        List<PlaceCluster> result = [];
        Dictionary<(long X, long Z), List<KnownPlace>> cells = [];
        List<(long X, long Z)> order = [];
        foreach (KnownPlace place in places)
        {
            if (place.Kind is KnownPlaceKind.Home or KnownPlaceKind.Sled)
            {
                result.Add(new(place.Position, 1, place));
                continue;
            }
            (long, long) key = ((long)Math.Floor(place.Position.X / cellMetres), (long)Math.Floor(place.Position.Y / cellMetres));
            if (!cells.TryGetValue(key, out List<KnownPlace>? members)) { cells[key] = members = []; order.Add(key); }
            members.Add(place);
        }
        foreach ((long X, long Z) key in order)
        {
            List<KnownPlace> members = cells[key];
            Vector2 centre = members.Aggregate(Vector2.Zero, (sum, place) => sum + place.Position) / members.Count;
            result.Add(new(members.Count == 1 ? members[0].Position : centre, members.Count, members[0]));
        }
        return result;
    }
}
