using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine.Entities;
using Rusty.Engine.Mechanics;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The item catalogue, recipes and caches over the Engine's real inventory store: crafting is one
/// edit that consumes and grants together or not at all, and a cache is a pure function of the place.
/// </summary>
internal static class InventoryChecks
{
    private static readonly EntityId Owner = new(1UL);

    internal static void Run()
    {
        Check.That(ItemCatalog.All.Select(item => item.Code).Distinct().Count() == ItemCatalog.All.Count
            && ItemCatalog.All.Select(item => item.Id).Distinct().Count() == ItemCatalog.All.Count, "every item has its own code and id");
        Check.That(CreatureKinds.Hostile.Loot.Entries.All(entry => ItemCatalog.TryFind(entry.ItemId, out _)),
            "every creature drop is an item the inventory can carry");
        Check.That(Recipes.All.All(recipe => recipe.Inputs.All(input => input.Count > 0) && recipe.Output.Count > 0),
            "every recipe takes and makes something");

        InventoryStore store = NewStore();
        Grant(store, ItemCatalog.Meat, 3);
        Craft(store, Recipes.Ration);
        Check.That(Held(store, ItemCatalog.Meat) == 1 && Held(store, ItemCatalog.Ration) == 1, "a ration costs two meat and makes one ration");

        ulong revision = store.Revision;
        bool refused = Throws(() => Craft(store, Recipes.Ration));
        Check.That(refused && Held(store, ItemCatalog.Meat) == 1 && Held(store, ItemCatalog.Ration) == 1 && store.Revision == revision,
            "a recipe short of its inputs must change nothing");

        Grant(store, ItemCatalog.Cloth, 1);
        refused = Throws(() => Craft(store, Recipes.Torch));
        Check.That(refused && Held(store, ItemCatalog.Cloth) == 1 && Held(store, ItemCatalog.Torch) == 0,
            "a recipe with one input present and one missing must not consume the one present");
        Grant(store, ItemCatalog.Oil, 1);
        Craft(store, Recipes.Torch);
        Check.That(Held(store, ItemCatalog.Cloth) == 0 && Held(store, ItemCatalog.Oil) == 0 && Held(store, ItemCatalog.Torch) == 2,
            "cloth and oil make two torches");

        InventoryStore full = NewStore();
        Grant(full, ItemCatalog.Hide, (int)ItemCatalog.StackMaximum);
        Grant(full, ItemCatalog.Bandage, (int)(ItemCatalog.CarryLimit - ItemCatalog.StackMaximum));
        refused = Throws(() => Craft(full, Recipes.Bandage));
        Check.That(refused && Held(full, ItemCatalog.Hide) == (int)ItemCatalog.StackMaximum,
            "a recipe whose output would not fit must not consume its inputs");

        PoiSite dungeon = new(3, -4, PoiKind.DungeonEntrance, 400, -500, 9, 6, 0, 0);
        PoiSite stones = dungeon with { Kind = PoiKind.StandingStones };
        ItemCount[] cache = SupplyCache.For(42UL, dungeon);
        Check.That(cache.SequenceEqual(SupplyCache.For(42UL, dungeon)), "a cache must be the same however often it is asked for");
        Check.That(cache.All(found => found.Count > 0) && cache.Any(found => found.Item == ItemCatalog.Cloth) && cache.Any(found => found.Item == ItemCatalog.Oil),
            "a cache holds the cloth and oil torches are made from");
        Check.That(cache.Single(found => found.Item == ItemCatalog.Ration).Count > SupplyCache.For(42UL, stones).Single(found => found.Item == ItemCatalog.Ration).Count,
            "a dungeon entrance's cache holds more food than an ordinary place's");
    }

    private static InventoryStore NewStore()
    {
        InventoryStore store = new();
        store.RegisterInventory(new InventoryState(Owner, [new InventoryCapacityLimit(ItemCatalog.Carry, ItemCatalog.CarryLimit)]));
        return store;
    }

    private static void Grant(InventoryStore store, CatalogItem item, int count) =>
        store.Grant(Owner, item.Definition, item.Stack, (ulong)count);

    /// <summary>The same edit the inventory module makes: consume every input, grant the output, publish.</summary>
    private static void Craft(InventoryStore store, Recipe recipe)
    {
        using InventoryEdit edit = store.Prepare();
        foreach (ItemCount input in recipe.Inputs)
        {
            edit.Consume(Owner, input.Item.Stack, (ulong)input.Count);
        }

        edit.Grant(Owner, recipe.Output.Item.Definition, recipe.Output.Item.Stack, (ulong)recipe.Output.Count);
        edit.Publish();
    }

    private static int Held(InventoryStore store, CatalogItem item)
    {
        foreach (InventoryStack stack in store.View(Owner).Stacks)
        {
            if (stack.Id.Equals(item.Stack))
            {
                return (int)stack.Quantity;
            }
        }

        return 0;
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (MechanicsException)
        {
            return true;
        }
    }
}
