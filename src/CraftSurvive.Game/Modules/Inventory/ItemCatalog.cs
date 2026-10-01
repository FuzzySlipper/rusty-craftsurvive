using Rusty.Engine.Mechanics;

namespace CraftSurvive.Game.Modules.Inventory;

/// <summary>What an item is for, when the player uses it.</summary>
internal enum ItemUse
{
    /// <summary>Material: crafted from, never used directly.</summary>
    None,

    /// <summary>Eaten: its value is nourishment.</summary>
    Food,

    /// <summary>Applied: its value is health given back.</summary>
    Healing,

    /// <summary>Spent when a light is placed.</summary>
    Light,
}

/// <summary>One kind of item the player can carry. The code is its stored identity: append only.</summary>
internal sealed record CatalogItem(int Code, string Id, string Name, ItemUse Use, double Value)
{
    internal ItemDefinitionId DefinitionId { get; } = ItemDefinitionId.Parse($"craftsurvive.{Id}");

    /// <summary>The player carries each kind in one stack, named by the kind.</summary>
    internal InventoryStackId Stack { get; } = InventoryStackId.Parse(Id);

    internal ItemDefinition Definition => new(
        DefinitionId,
        ItemKind.Fungible,
        ItemCatalog.StackMaximum,
        [],
        [new ItemCapacityCost(ItemCatalog.Carry, 1UL)],
        null,
        []);
}

/// <summary>
/// The items of an expedition. Supplies come from creatures (meat, hide, claws) and from the caches
/// at places reached for the first time (cloth, oil, rations); crafting turns them into food that
/// keeps, bandages and torches. There is no ore, no tool tier and nothing mined.
/// </summary>
internal static class ItemCatalog
{
    /// <summary>How many of one kind a stack may hold.</summary>
    internal const ulong StackMaximum = 99UL;

    /// <summary>The carry metric every item costs one of, and how much the player can carry.</summary>
    internal static CapacityMetricId Carry { get; } = CapacityMetricId.Parse("craftsurvive.carry");

    internal const ulong CarryLimit = 120UL;

    internal static CatalogItem Meat { get; } = new(1, "meat", "Meat", ItemUse.Food, 12d);

    internal static CatalogItem Hide { get; } = new(2, "hide", "Hide", ItemUse.None, 0d);

    internal static CatalogItem Claw { get; } = new(3, "claw", "Claw", ItemUse.None, 0d);

    internal static CatalogItem Cloth { get; } = new(4, "cloth", "Cloth", ItemUse.None, 0d);

    internal static CatalogItem Oil { get; } = new(5, "oil", "Oil", ItemUse.None, 0d);

    internal static CatalogItem Ration { get; } = new(6, "ration", "Ration", ItemUse.Food, 40d);

    internal static CatalogItem Bandage { get; } = new(7, "bandage", "Bandage", ItemUse.Healing, 8d);

    internal static CatalogItem Torch { get; } = new(8, "torch", "Torch", ItemUse.Light, 0d);

    /// <summary>Every item, in code order.</summary>
    internal static IReadOnlyList<CatalogItem> All { get; } = [Meat, Hide, Claw, Cloth, Oil, Ration, Bandage, Torch];

    internal static bool TryFind(string id, out CatalogItem item)
    {
        item = All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))!;
        return item is not null;
    }

    internal static bool TryFind(int code, out CatalogItem item)
    {
        item = All.FirstOrDefault(candidate => candidate.Code == code)!;
        return item is not null;
    }
}
