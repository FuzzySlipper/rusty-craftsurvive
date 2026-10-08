using System.Numerics;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>One drawn place marker: a single place, or several that crowd together at the current zoom.</summary>
internal readonly record struct PlaceCluster(Vector2 Position, int Count, KnownPlace First)
{
    internal bool Single => Count == 1;
}

/// <summary>
/// Places that crowd together at a zoom are drawn as one cluster marker (#9553). Two places closer
/// than the reach (world metres, the markers' spacing at the current zoom) join one cluster, and so
/// does any place close to a member, so a group depends only on where the places are, never on list
/// order or a fixed world grid. Home and the sled are never folded into a cluster: they are the
/// expedition's own anchors.
/// </summary>
internal static class PlaceClusters
{
    internal static IReadOnlyList<PlaceCluster> Group(IReadOnlyList<KnownPlace> places, float reachMetres)
    {
        if (!(reachMetres > 0)) return [.. places.Select(place => new PlaceCluster(place.Position, 1, place))];
        List<PlaceCluster> result = [];
        List<KnownPlace> loose = [];
        foreach (KnownPlace place in places)
        {
            if (place.Kind is KnownPlaceKind.Home or KnownPlaceKind.Sled) result.Add(new(place.Position, 1, place));
            else loose.Add(place);
        }

        // Places are binned by the reach, so only neighbouring bins need comparing.
        int[] leader = [.. Enumerable.Range(0, loose.Count)];
        Dictionary<(long X, long Z), List<int>> bins = [];
        for (int index = 0; index < loose.Count; index++)
        {
            (long x, long z) = Bin(loose[index].Position, reachMetres);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dz = -1; dz <= 1; dz++)
                {
                    if (!bins.TryGetValue((x + dx, z + dz), out List<int>? near)) continue;
                    foreach (int other in near)
                    {
                        if (Vector2.Distance(loose[index].Position, loose[other].Position) <= reachMetres) Join(leader, index, other);
                    }
                }
            }

            if (!bins.TryGetValue((x, z), out List<int>? bin)) bins[(x, z)] = bin = [];
            bin.Add(index);
        }

        // Each cluster is listed where its first member was, and named by it.
        Dictionary<int, List<KnownPlace>> clusters = [];
        List<int> order = [];
        for (int index = 0; index < loose.Count; index++)
        {
            int root = Root(leader, index);
            if (!clusters.TryGetValue(root, out List<KnownPlace>? members)) { clusters[root] = members = []; order.Add(root); }
            members.Add(loose[index]);
        }

        foreach (int root in order)
        {
            List<KnownPlace> members = clusters[root];
            Vector2 centre = members.Aggregate(Vector2.Zero, (sum, place) => sum + place.Position) / members.Count;
            result.Add(new(members.Count == 1 ? members[0].Position : centre, members.Count, members[0]));
        }

        return result;
    }

    private static (long X, long Z) Bin(Vector2 position, float reachMetres) =>
        ((long)Math.Floor(position.X / reachMetres), (long)Math.Floor(position.Y / reachMetres));

    private static int Root(int[] leader, int index)
    {
        while (leader[index] != index)
        {
            leader[index] = leader[leader[index]];
            index = leader[index];
        }

        return index;
    }

    private static void Join(int[] leader, int a, int b)
    {
        int ra = Root(leader, a), rb = Root(leader, b);
        if (ra != rb) leader[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }
}
