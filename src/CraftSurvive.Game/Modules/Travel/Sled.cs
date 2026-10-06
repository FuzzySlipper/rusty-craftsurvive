using System.Numerics;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>
/// The expedition's sled (#9473, Den design/overland-travel-mode): finite cargo beyond the pack,
/// and a place in the world. On the map it travels with the party when the party set out from
/// beside it; on foot it stays where the party left it. Engine-free; the product owns its
/// presentation and saving.
/// </summary>
internal sealed class Sled
{
    /// <summary>What the sled can carry, in the same load units as the pack (every item costs one).</summary>
    internal const int Capacity = 400;
    /// <summary>The party takes the sled along, and the player can load it, from this close.</summary>
    internal const float ReachMetres = 12;

    private readonly Dictionary<CatalogItem, int> cargo = [];

    internal Sled(Vector2 position) => Position = position;

    internal Vector2 Position { get; private set; }
    internal int Load => cargo.Values.Sum();
    internal double LoadFraction => (double)Load / Capacity;
    internal int Free => Capacity - Load;
    internal IReadOnlyDictionary<CatalogItem, int> Cargo => cargo;

    internal int Count(CatalogItem item) => cargo.GetValueOrDefault(item);

    internal bool Within(Vector2 at) => Vector2.Distance(at, Position) <= ReachMetres;

    internal void MoveTo(Vector2 position) => Position = position;

    /// <summary>Stow up to <paramref name="count"/>; returns how many fitted.</summary>
    internal int Stow(CatalogItem item, int count)
    {
        int fitted = Math.Clamp(count, 0, Free);
        if (fitted > 0) cargo[item] = Count(item) + fitted;
        return fitted;
    }

    /// <summary>Take out up to <paramref name="count"/>; returns how many came out.</summary>
    internal int Take(CatalogItem item, int count)
    {
        int taken = Math.Clamp(count, 0, Count(item));
        if (taken == 0) return 0;
        if (Count(item) == taken) cargo.Remove(item);
        else cargo[item] -= taken;
        return taken;
    }

    /// <summary>Replace the cargo wholesale, as a restore does; refuses more than the capacity.</summary>
    internal void Restore(Vector2 position, IEnumerable<(CatalogItem Item, int Count)> items)
    {
        (CatalogItem Item, int Count)[] restored = [.. items];
        if (restored.Any(entry => entry.Count <= 0) || restored.Sum(entry => entry.Count) > Capacity)
            throw new InvalidOperationException("A sled holds positive counts within its capacity.");
        cargo.Clear();
        foreach ((CatalogItem item, int count) in restored) cargo[item] = Count(item) + count;
        Position = position;
    }

    internal string Describe() => cargo.Count == 0 ? "empty"
        : string.Join(", ", ItemCatalog.All.Where(item => Count(item) > 0).Select(item => $"{Count(item)} {item.Name.ToLowerInvariant()}"));
}

/// <summary>
/// How a hitched sled changes travel. Runners glide on snow and ice and drag on rock, sand and through
/// forest; a load slows the whole march. Settled for slice 7; tuning to revisit with play.
/// </summary>
internal static class SledTravel
{
    /// <summary>A full sled marches this much slower than an empty one.</summary>
    internal const double FullLoadSlowdown = 0.5;

    internal static double Terrain(MapBiome biome) => biome switch
    {
        MapBiome.IceField => 0.7,
        MapBiome.Tundra => 0.8,
        MapBiome.Alpine => 1.4,
        MapBiome.Desert => 1.25,
        MapBiome.TemperateForest or MapBiome.BorealForest or MapBiome.Rainforest => 1.2,
        _ => 1,
    };

    internal static double LoadMultiplier(double loadFraction) => 1 + Math.Clamp(loadFraction, 0, 1) * FullLoadSlowdown;
}
