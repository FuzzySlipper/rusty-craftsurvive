using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Feedback;
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
/// The one owner of what the player carries, held in the Engine's <see cref="InventoryStore"/> as
/// one stack per occupied slot (<see cref="InventorySlots"/>: the hotbar and the pack). It takes in
/// what creatures drop and what a place's cache holds on a first reach, moves stacks between slots
/// as the player drags them, crafts by recipe, and uses items: food through survival, bandages
/// through the player's vitals, torches when a light is placed. Every change is one inventory edit,
/// all of it or none. It publishes the slots and recipes to the UI and saves them.
/// </summary>
internal sealed class InventoryModule : IProductModule
{
    /// <summary>A changed inventory is saved within a second.</summary>
    internal const long SaveDelaySteps = 60;

    private readonly PlayerController player;
    private readonly DiscoveryModule discovery;
    private readonly SurvivalModule survival;
    private readonly ProductUiPublisher ui;
    private readonly Cues cues;
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

    /// <summary>The hotbar slot the player has selected, whose item the use action uses.</summary>
    private int selected;

    internal InventoryModule(IEngineContext engine, ProductStore saves, SaveIdentity identity, PlayerController player,
        DiscoveryModule discovery, SurvivalModule survival, ProductUiPublisher ui, Cues cues)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
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
            foreach (SlotContents contents in saved.Slots)
            {
                store.Grant(owner, contents.Item.Definition, InventorySlots.Stack(contents.Slot), (ulong)contents.Count);
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
                cues.Raise(Cue.Pickup);
            }
        }

        while (discovery.TryTakeFirstVisit(out PoiSite site))
        {
            ItemCount[] cache = SupplyCache.For(seed, site);
            caches++;
            Take(cache, $"found a cache at the {site.Kind}: {Describe(cache)}");
            cues.Raise(Cue.Discovery);
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
        selected = 0;
        last = "none";
        Save();
        Publish();
    }

    public void Dispose() => SaveNow();

    /// <summary>Saves the carried items at once if they changed, for a caller that changed them outside an update (map travel).</summary>
    internal void SaveNow()
    {
        if (store.Revision != savedRevision)
        {
            Save();
        }
    }

    /// <summary>How many requests the inventory has refused, so a caller can tell a refusal from its answer.</summary>
    internal long Refused => refused;

    /// <summary>How many of an item the player carries, across every slot.</summary>
    internal int Count(CatalogItem item) => Held().Where(contents => contents.Item == item).Sum(contents => contents.Count);

    /// <summary>The selected hotbar slot.</summary>
    internal int Selected => selected;

    /// <summary>What the selected hotbar slot holds, or null when it is empty.</summary>
    internal SlotContents? SelectedContents => Held().FirstOrDefault(contents => contents.Slot == selected) is { Count: > 0 } found ? found : null;

    /// <summary>
    /// Selects a hotbar slot: the one picked (when one is), then some steps along the hotbar,
    /// wrapping at either end, as the wheel or the bumpers step it.
    /// </summary>
    internal void Select(int pick, int steps)
    {
        int now = InventorySlots.IsHotbar(pick) ? pick : selected;
        now = ((now + steps) % InventorySlots.HotbarSlots + InventorySlots.HotbarSlots) % InventorySlots.HotbarSlots;
        if (now != selected)
        {
            selected = now;
            Publish();
        }
    }

    /// <summary>What each occupied slot holds, in slot order.</summary>
    internal IReadOnlyList<SlotContents> Held() => [.. store.View(owner).Stacks
        .Select(stack => new SlotContents(InventorySlots.SlotOf(stack.Id), ItemCatalog.All.First(item => item.DefinitionId.Equals(stack.Definition)), (int)stack.Quantity))
        .OrderBy(contents => contents.Slot)];

    /// <summary>
    /// Moves a stack, or <paramref name="count"/> of it (zero for all), from one slot to another, as
    /// the player drags it: into an empty slot, onto the same kind, or swapping with another kind.
    /// </summary>
    internal string Move(int from, int to, int count)
    {
        SlotPlan plan = InventorySlots.PlanMove(Held(), from, to, count);
        if (plan.Refusal is string why)
        {
            return Refuse($"move refused: {why}");
        }

        if (Apply(plan.Taken, plan.Given) is string failed)
        {
            return Refuse($"move refused: {failed}");
        }

        last = $"moved {plan.Given[0].Count} {plan.Given[0].Item.Id} to slot {plan.Given[0].Slot}";
        Publish();
        return last;
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

        List<SlotChange> taken = [];
        foreach (ItemCount input in recipe.Inputs)
        {
            taken.AddRange(InventorySlots.PlanSpend(After(Held(), taken), input.Item, input.Count)!);
        }

        if (InventorySlots.PlanTake(After(Held(), taken), [recipe.Output]) is not IReadOnlyList<SlotChange> given)
        {
            return Refuse($"craft {recipe.Id} refused: no slot for the {recipe.Output.Item.Id}");
        }

        if (Apply(taken, given) is string failed)
        {
            return Refuse($"craft {recipe.Id} refused: {failed}");
        }

        crafted++;
        last = $"crafted {recipe.Describe()}";
        Publish();
        return last;
    }

    /// <summary>
    /// Uses one of an item: eats food or applies a bandage, from <paramref name="fromSlot"/> when it
    /// holds that kind. Materials and torches are not used this way.
    /// </summary>
    internal string Use(string itemId, int fromSlot = -1)
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

        if (!Spend(item, fromSlot))
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

    /// <summary>Spends one of an item, from a slot when it is asked and holds one, or returns false when none is carried.</summary>
    internal bool Spend(CatalogItem item, int fromSlot = -1)
    {
        if (InventorySlots.PlanSpend(Held(), item, 1, fromSlot) is not IReadOnlyList<SlotChange> taken || Apply(taken, []) is not null)
        {
            return false;
        }

        Publish();
        return true;
    }

    /// <summary>
    /// Takes in as many of an item as fit the carry limit and the free slots, up to a count, as one
    /// edit; returns how many came in (taking from the sled, #9473).
    /// </summary>
    internal int Receive(CatalogItem item, int count)
    {
        for (int fits = (int)Math.Min((ulong)Math.Max(0, count), ItemCatalog.CarryLimit - Math.Min(ItemCatalog.CarryLimit, Load())); fits > 0; fits--)
        {
            if (InventorySlots.PlanTake(Held(), [new ItemCount(item, fits)]) is not IReadOnlyList<SlotChange> given || Apply([], given) is not null) continue;
            last = $"took {fits} {item.Id}";
            Publish();
            return fits;
        }
        return 0;
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
        string? failed = InventorySlots.PlanTake(Held(), items) is IReadOnlyList<SlotChange> given ? Apply([], given) : "no free slot";
        if (failed is null)
        {
            last = outcome;
            return;
        }

        leftBehind += items.Sum(carried => carried.Count);
        last = $"{outcome} - left behind: {failed}";
    }

    /// <summary>
    /// Carries out a plan as one edit: everything taken out first, so a swap never holds both stacks
    /// at once, then everything put in. Returns why the Engine refused it, or null when it is done.
    /// </summary>
    private string? Apply(IReadOnlyList<SlotChange> taken, IReadOnlyList<SlotChange> given)
    {
        try
        {
            using InventoryEdit edit = store.Prepare();
            foreach (SlotChange change in taken)
            {
                edit.Consume(owner, InventorySlots.Stack(change.Slot), (ulong)change.Count);
            }

            foreach (SlotChange change in given)
            {
                edit.Grant(owner, change.Item.Definition, InventorySlots.Stack(change.Slot), (ulong)change.Count);
            }

            edit.Publish();
            return null;
        }
        catch (MechanicsException exception)
        {
            return exception.Reason.ToString();
        }
    }

    /// <summary>What the slots would hold after some counts are taken out of them.</summary>
    private static IReadOnlyList<SlotContents> After(IReadOnlyList<SlotContents> held, IReadOnlyList<SlotChange> taken) =>
        [.. held.Select(contents => contents with { Count = contents.Count - taken.Where(change => change.Slot == contents.Slot).Sum(change => change.Count) })
            .Where(contents => contents.Count > 0)];

    private string Refuse(string outcome)
    {
        refused++;
        last = outcome;
        Publish();
        return outcome;
    }

    private void Save()
    {
        slot.Save(new CarriedItems(Held()));
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

    private bool Craftable(Recipe recipe) => recipe.Inputs.All(input => Count(input.Item) >= input.Count);

    /// <summary>What an item is for, as the UI names it.</summary>
    private static string UseName(ItemUse use) => use switch
    {
        ItemUse.Food => "food",
        ItemUse.Healing => "healing",
        ItemUse.Light => "light",
        _ => "material",
    };

    private static string Describe(IEnumerable<ItemCount> items) => string.Join(", ", items.Select(item => $"{item.Count} {item.Item.Id}"));

    /// <summary>Publishes what is carried, what can be made, and what can be used, when any of it changed.</summary>
    private void Publish()
    {
        InventoryUiFacts facts = new(
            Carried(),
            string.Join(";", Held().Select(contents => $"{contents.Slot}|{contents.Item.Id}|{contents.Item.Name}|{contents.Count}|{UseName(contents.Item.Use)}")),
            string.Join(";", Recipes.All.Select(recipe =>
                $"{recipe.Id}|{recipe.Output.Item.Name}|{recipe.Output.Count}|{string.Join("+", recipe.Inputs.Select(input => $"{input.Item.Name}*{input.Count}"))}|{(Craftable(recipe) ? 1 : 0)}")),
            Load(),
            ItemCatalog.CarryLimit,
            Count(ItemCatalog.Torch),
            last)
        {
            HotbarSlots = InventorySlots.HotbarSlots,
            PackSlots = InventorySlots.PackSlots,
            Selected = selected,
        };
        if (published != facts)
        {
            published = facts;
            ui.PublishInventory(facts);
        }
    }
}
