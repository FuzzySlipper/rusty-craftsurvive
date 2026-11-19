using System.Globalization;
using System.Numerics;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>A cave lake or pool (#10876): its surface's height and the world columns it covers.</summary>
internal sealed record CavePool(float Surface, IReadOnlyList<Vector2> Columns);

/// <summary>
/// What a cave offers its animals (#10876), in world metres: the twilight of its mouth's tendril (on the overworld's
/// side), the floors of its damp dead ends, its lakes and pools, the deep floors along its main route, and where each
/// community it grew stands. The dungeon module reads them from the cave's layout; placement is decided here.
/// </summary>
/// <param name="Key">The cave's own key: every home in it is named from it, so the ledger keeps its losses.</param>
internal sealed record CaveSites(
    string Key,
    IReadOnlyList<Vector3> Mouth,
    IReadOnlyList<Vector3> DeadEnds,
    IReadOnlyList<CavePool> Pools,
    IReadOnlyList<Vector3> DeepFloors,
    IReadOnlyDictionary<CaveHabitat, IReadOnlyList<Vector3>> Communities)
{
    /// <summary>The places a cave holds for animals that live in one of its zones or communities.</summary>
    internal IReadOnlyList<Vector3> Of(CaveHabitat habitat) => habitat switch
    {
        CaveHabitat.Mouth => Mouth,
        CaveHabitat.DampDeadEnds => DeadEnds,
        CaveHabitat.Pools => [.. Pools.Select(PoolHome)],
        CaveHabitat.DeepFloors => DeepFloors,
        _ => Communities.TryGetValue(habitat, out IReadOnlyList<Vector3>? sites) ? sites : [],
    };

    /// <summary>A pool's home: on its surface over its middle column, the column nearest the mean of them all.</summary>
    internal static Vector3 PoolHome(CavePool pool)
    {
        Vector2 mean = pool.Columns.Aggregate(Vector2.Zero, (sum, column) => sum + column) / Math.Max(1, pool.Columns.Count);
        Vector2 middle = pool.Columns.MinBy(column => Vector2.DistanceSquared(column, mean));
        return new Vector3(middle.X, pool.Surface, middle.Y);
    }
}

/// <summary>A small cave creature's home (#10876): a group of one ambient kind, and the pool it swims in, if it swims.</summary>
internal sealed record CaveAmbientHome(string Key, string Cave, CaveAmbientKind Kind, Vector3 Home, int Count, CavePool? Pool);

/// <summary>
/// Where a cave's animals live (#10876; den-wiki wandering-years/cave-wildlife): each cave kind takes homes at the sites
/// of its own zone or community, at most its kind's number to a cave, spread through them and kept apart; a cave without
/// that zone or community holds none of it. Pure: the same cave gives the same homes in every session.
/// </summary>
internal static class CaveFauna
{
    /// <summary>Two homes of one kind in a cave stand at least this far apart, so they are not one encounter.</summary>
    internal const float HomesApartMetres = 12f;

    /// <summary>The hostile animals' homes in a cave: herd records whose home carries its floor's height.</summary>
    internal static IReadOnlyList<HerdRecord> Homes(CaveSites sites, WildlifeCatalog catalog, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(catalog);
        List<HerdRecord> homes = [];
        int order = 0;
        foreach (WildlifeKind kind in catalog.Kinds.Where(kind => kind.Cave is not null && kind.Hunt is not null))
        {
            WildlifeCave cave = kind.Cave!;
            // Two kinds of one community take different sites: each starts its spread a share further along.
            int index = 0;
            foreach (Vector3 site in Spread(sites.Of(cave.Lives), cave.HomesPerCave, order++))
            {
                string key = string.Create(CultureInfo.InvariantCulture, $"{kind.Id}@{sites.Key}#{index++}");
                homes.Add(new HerdRecord(key, kind.Id, new Vector2(site.X, site.Z), Group(key, seed, kind.GroupMinimum, kind.GroupMaximum)) { Floor = site.Y });
            }
        }

        return homes;
    }

    /// <summary>The small creatures' homes in a cave.</summary>
    internal static IReadOnlyList<CaveAmbientHome> Ambient(CaveSites sites, WildlifeCatalog catalog, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(catalog);
        List<CaveAmbientHome> homes = [];
        int order = 0;
        foreach (CaveAmbientKind kind in catalog.CaveAmbient)
        {
            int index = 0;
            if (kind.Cave.Lives == CaveHabitat.Pools)
            {
                // A swimmer keeps to one pool, so its homes are pools, not points.
                foreach (CavePool pool in SpreadPools(sites.Pools, kind.Cave.HomesPerCave))
                {
                    string key = string.Create(CultureInfo.InvariantCulture, $"{kind.Id}@{sites.Key}#{index++}");
                    homes.Add(new CaveAmbientHome(key, sites.Key, kind, CaveSites.PoolHome(pool), Group(key, seed, kind.GroupMinimum, kind.GroupMaximum), pool));
                }

                order++;
                continue;
            }

            foreach (Vector3 site in Spread(sites.Of(kind.Cave.Lives), kind.Cave.HomesPerCave, order++))
            {
                string key = string.Create(CultureInfo.InvariantCulture, $"{kind.Id}@{sites.Key}#{index++}");
                homes.Add(new CaveAmbientHome(key, sites.Key, kind, site, Group(key, seed, kind.GroupMinimum, kind.GroupMaximum), null));
            }
        }

        return homes;
    }

    /// <summary>
    /// Up to some sites through a list, evenly along it from a start a share of the way in, each kept
    /// <see cref="HomesApartMetres"/> from those taken before it.
    /// </summary>
    internal static IEnumerable<Vector3> Spread(IReadOnlyList<Vector3> sites, int most, int offset)
    {
        if (sites.Count == 0 || most <= 0) yield break;
        int count = sites.Count;
        int start = (int)((long)offset * count / (most + 1) % count);
        // Evenly through the list first, so the homes spread over it; then the rest in order, for any refused as too near.
        IEnumerable<int> order = Enumerable.Range(0, most).Select(k => (start + (int)((long)k * count / most)) % count)
            .Concat(Enumerable.Range(0, count).Select(i => (start + i) % count)).Distinct();
        List<Vector3> taken = [];
        foreach (int at in order)
        {
            if (taken.Count >= most) yield break;
            Vector3 site = sites[at];
            if (taken.Any(other => Vector3.Distance(other, site) < HomesApartMetres)) continue;
            taken.Add(site);
            yield return site;
        }
    }

    private static IEnumerable<CavePool> SpreadPools(IReadOnlyList<CavePool> pools, int most) =>
        pools.Where(pool => pool.Columns.Count > 0).OrderByDescending(pool => pool.Columns.Count).Take(most);

    /// <summary>How many a home holds: a keyed draw between its kind's fewest and most.</summary>
    private static int Group(string key, ulong seed, int minimum, int maximum)
    {
        ulong hash = seed ^ 0xCAFE_F00DUL;
        foreach (char c in key) hash = (hash ^ c) * 0x100_0000_01B3UL;
        return minimum + (int)(hash % (ulong)(maximum - minimum + 1));
    }
}
