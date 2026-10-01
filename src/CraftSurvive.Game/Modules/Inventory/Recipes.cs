using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Inventory;

/// <summary>A count of one kind of item.</summary>
internal readonly record struct ItemCount(CatalogItem Item, int Count);

/// <summary>One recipe: what it takes, and what it makes. Crafting resolves it as one inventory edit.</summary>
internal sealed record Recipe(string Id, ItemCount[] Inputs, ItemCount Output)
{
    internal string Describe() =>
        $"{Output.Count} {Output.Item.Id} from {string.Join(" + ", Inputs.Select(input => $"{input.Count} {input.Item.Id}"))}";
}

/// <summary>The recipe catalogue: food that keeps, bandages, and light. Small on purpose.</summary>
internal static class Recipes
{
    internal static Recipe Ration { get; } = new("ration", [new(ItemCatalog.Meat, 2)], new(ItemCatalog.Ration, 1));

    internal static Recipe Bandage { get; } = new("bandage", [new(ItemCatalog.Hide, 1)], new(ItemCatalog.Bandage, 2));

    internal static Recipe Torch { get; } = new("torch", [new(ItemCatalog.Cloth, 1), new(ItemCatalog.Oil, 1)], new(ItemCatalog.Torch, 2));

    internal static IReadOnlyList<Recipe> All { get; } = [Ration, Bandage, Torch];

    internal static bool TryFind(string id, out Recipe recipe)
    {
        recipe = All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))!;
        return recipe is not null;
    }
}

/// <summary>
/// What a place holds for the player who reaches it first: a supply cache, the exploration reward.
/// The contents are a pure function of the world seed and the place, so a cache is the same however
/// it is reached; a place already known on reload is a return and holds nothing new.
/// </summary>
internal static class SupplyCache
{
    private const int MinimumCloth = 1;
    private const int MaximumCloth = 2;
    private const int MinimumOil = 1;
    private const int MaximumOil = 2;

    /// <summary>A dungeon entrance holds a little more: it is the furthest from home.</summary>
    private const int DungeonBonusRation = 1;

    internal static ItemCount[] For(ulong seed, PoiSite site)
    {
        string scope = $"cache:{site.CellX}:{site.CellZ}";
        List<ItemCount> found =
        [
            new(ItemCatalog.Cloth, KeyedDraws.Draw(seed, scope + ":cloth", MinimumCloth, MaximumCloth)),
            new(ItemCatalog.Oil, KeyedDraws.Draw(seed, scope + ":oil", MinimumOil, MaximumOil)),
            new(ItemCatalog.Ration, 1 + (site.Kind == PoiKind.DungeonEntrance ? DungeonBonusRation : 0)),
        ];
        return [.. found];
    }
}
