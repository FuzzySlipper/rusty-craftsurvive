using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Entities;
using Rusty.Engine.Mechanics;

namespace CraftSurvive.Game.Modules.Inventory;

/// <summary>
/// The one owner of what the player carries, held in the Engine's <see cref="InventoryStore"/>. It
/// takes in what creatures drop and what a place's cache holds on a first reach, crafts by recipe as
/// one inventory edit (all of it or none), and uses items: food through survival, bandages through
/// the player's vitals, torches when a light is placed. It publishes the carried items and recipes
/// to the UI and saves them.
/// </summary>
internal sealed class InventoryModule : IProductModule
{
    /// <summary>A changed inventory is saved within a second.</summary>
    internal const long SaveDelaySteps = 60;

    private readonly PlayerController player;
    private readonly DiscoveryModule discovery;
    private readonly SurvivalModule survival;
    private readonly ProductUiPublisher ui;
    private readonly ulong seed;
    private readonly ProductSaveSlot<CarriedItems> slot;
    private readonly EntityId owner = new(ProductIds.PlayerEntity);
    private InventoryStore store = new();
    private ulong savedRevision;
    private long changedAtStep = long.MinValue;
    private long crafted;
    private long refused;
    private long leftBehind;
    private long caches;
    private string last = "none";
    private InventoryUiFacts? published;

    internal InventoryModule(IEngineContext engine, ProductStore saves, SaveIdentity identity, PlayerController player,
        DiscoveryModule discovery, SurvivalModule survival, ProductUiPublisher ui)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.survival = survival ?? throw new ArgumentNullException(nameof(survival));
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        seed = identity.Seed;
        slot = new ProductSaveSlot<CarriedItems>(engine, saves, SaveManifest.PlayerInventory, new InventoryCodec(identity));
    }

    public void Start()
    {
        Register();
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: CarriedItems saved })
        {
            foreach (ItemCount carried in saved.Items)
            {
                store.Grant(owner, carried.Item.Definition, carried.Item.Stack, (ulong)carried.Count);
            }
        }

        savedRevision = store.Revision;
        Publish();
    }

    public void Update(ProductStep step)
    {
        while (player.Progress.TryTakeDrop(out LootDrop drop))
        {
            if (ItemCatalog.TryFind(drop.ItemId, out CatalogItem item))
            {
                Take([new ItemCount(item, drop.Quantity)], $"picked up {drop.Quantity} {item.Id}");
            }
        }

        while (discovery.TryTakeFirstVisit(out PoiSite site))
        {
            ItemCount[] cache = SupplyCache.For(seed, site);
            caches++;
            Take(cache, $"found a cache at the {site.Kind}: {Describe(cache)}");
        }

        if (store.Revision != savedRevision)
        {
            if (changedAtStep == long.MinValue)
            {
                changedAtStep = step.Step;
            }
            else if (step.Step - changedAtStep >= SaveDelaySteps)
            {
                Save();
            }
        }

        Publish();
    }

    /// <summary>A fresh session carries nothing.</summary>
    public void Restart()
    {
        store = new InventoryStore();
        Register();
        crafted = refused = leftBehind = caches = 0;
        last = "none";
        Save();
        Publish();
    }

    public void Dispose()
    {
        if (store.Revision != savedRevision)
        {
            Save();
        }
    }

    /// <summary>How many requests the inventory has refused, so a caller can tell a refusal from its answer.</summary>
    internal long Refused => refused;

    /// <summary>How many of an item the player carries.</summary>
    internal int Count(CatalogItem item)
    {
        foreach (InventoryStack stack in store.View(owner).Stacks)
        {
            if (stack.Id.Equals(item.Stack))
            {
                return (int)stack.Quantity;
            }
        }

        return 0;
    }

    /// <summary>
    /// Crafts one recipe as one inventory edit: every input is consumed and the output granted, or
    /// nothing changes and the refusal says why.
    /// </summary>
    internal string Craft(string recipeId)
    {
        if (!Recipes.TryFind(recipeId, out Recipe recipe))
        {
            return Refuse($"craft refused: \"{recipeId}\" is not a recipe");
        }

        foreach (ItemCount input in recipe.Inputs)
        {
            if (Count(input.Item) < input.Count)
            {
                return Refuse($"craft {recipe.Id} refused: needs {input.Count} {input.Item.Id}, carrying {Count(input.Item)}");
            }
        }

        try
        {
            using InventoryEdit edit = store.Prepare();
            foreach (ItemCount input in recipe.Inputs)
            {
                edit.Consume(owner, input.Item.Stack, (ulong)input.Count);
            }

            edit.Grant(owner, recipe.Output.Item.Definition, recipe.Output.Item.Stack, (ulong)recipe.Output.Count);
            edit.Publish();
        }
        catch (MechanicsException exception)
        {
            return Refuse($"craft {recipe.Id} refused: {exception.Reason}");
        }

        crafted++;
        last = $"crafted {recipe.Describe()}";
        Publish();
        return last;
    }

    /// <summary>Uses one of an item: eats food or applies a bandage. Materials and torches are not used this way.</summary>
    internal string Use(string itemId)
    {
        if (!ItemCatalog.TryFind(itemId, out CatalogItem item))
        {
            return Refuse($"use refused: \"{itemId}\" is not an item");
        }

        if (item.Use is not (ItemUse.Food or ItemUse.Healing))
        {
            return Refuse($"use refused: {item.Id} is not eaten or applied");
        }

        if (item.Use == ItemUse.Healing && player.Vitals.State.Health >= player.Vitals.MaximumHealth)
        {
            return Refuse("use refused: already at full health");
        }

        if (!Spend(item))
        {
            return Refuse($"use refused: carrying no {item.Id}");
        }

        if (item.Use == ItemUse.Food)
        {
            survival.Eat(item.Value);
            last = $"ate a {item.Id}";
        }
        else
        {
            player.Vitals.Heal((int)item.Value);
            last = $"applied a {item.Id}";
        }

        Publish();
        return last;
    }

    /// <summary>Spends one of an item, or returns false when none is carried.</summary>
    internal bool Spend(CatalogItem item)
    {
        if (Count(item) <= 0)
        {
            return false;
        }

        store.Consume(owner, item.Stack, 1UL);
        Publish();
        return true;
    }

    /// <summary>Gives the player items, for a live check of crafting and use.</summary>
    internal string Grant(string itemId, int count)
    {
        if (!ItemCatalog.TryFind(itemId, out CatalogItem item) || count <= 0)
        {
            return Refuse($"grant refused: {count} \"{itemId}\"");
        }

        Take([new ItemCount(item, count)], $"granted {count} {item.Id}");
        return Readout();
    }

    internal string Readout() =>
        $"inventory carrying=[{Carried()}] carry={Load()}/{ItemCatalog.CarryLimit} crafted={crafted} refused={refused} leftBehind={leftBehind} caches={caches} last={last} restore={slot.RestoreOutcome} saves={slot.Saves} failure={slot.LastFailure ?? "none"}";

    private void Register() =>
        store.RegisterInventory(new InventoryState(owner, [new InventoryCapacityLimit(ItemCatalog.Carry, ItemCatalog.CarryLimit)]));

    /// <summary>Takes items in as one edit; when they do not all fit, they are left behind together and counted.</summary>
    private void Take(IReadOnlyList<ItemCount> items, string outcome)
    {
        try
        {
            using InventoryEdit edit = store.Prepare();
            foreach (ItemCount carried in items)
            {
                edit.Grant(owner, carried.Item.Definition, carried.Item.Stack, (ulong)carried.Count);
            }

            edit.Publish();
            last = outcome;
        }
        catch (MechanicsException exception)
        {
            leftBehind += items.Sum(carried => carried.Count);
            last = $"{outcome} - left behind: {exception.Reason}";
        }
    }

    private string Refuse(string outcome)
    {
        refused++;
        last = outcome;
        Publish();
        return outcome;
    }

    private void Save()
    {
        List<ItemCount> carried = [.. ItemCatalog.All
            .Select(item => new ItemCount(item, Count(item)))
            .Where(item => item.Count > 0)];
        slot.Save(new CarriedItems(carried));
        savedRevision = store.Revision;
        changedAtStep = long.MinValue;
    }

    private ulong Load()
    {
        foreach (CapacityUsage usage in store.View(owner).Capacity)
        {
            if (usage.Metric.Equals(ItemCatalog.Carry))
            {
                return usage.Used;
            }
        }

        return 0UL;
    }

    private string Carried() => string.Join(", ", ItemCatalog.All.Where(item => Count(item) > 0).Select(item => $"{item.Id} {Count(item)}"));

    private static string Describe(IEnumerable<ItemCount> items) => string.Join(", ", items.Select(item => $"{item.Count} {item.Item.Id}"));

    /// <summary>Publishes what is carried, what can be made, and what can be used, when any of it changed.</summary>
    private void Publish()
    {
        InventoryUiFacts facts = new(
            Carried(),
            string.Join(",", Recipes.All.Select(recipe => $"{recipe.Id}:{recipe.Describe()}:{(recipe.Inputs.All(input => Count(input.Item) >= input.Count) ? 1 : 0)}")),
            string.Join(",", ItemCatalog.All.Where(item => item.Use is ItemUse.Food or ItemUse.Healing && Count(item) > 0).Select(item => item.Id)),
            Count(ItemCatalog.Torch),
            last);
        if (published != facts)
        {
            published = facts;
            ui.PublishInventory(facts);
        }
    }
}
