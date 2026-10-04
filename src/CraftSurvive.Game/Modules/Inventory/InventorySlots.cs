using Rusty.Engine.Mechanics;

namespace CraftSurvive.Game.Modules.Inventory;

/// <summary>One occupied slot: which slot, what kind of item, and how many.</summary>
internal readonly record struct SlotContents(int Slot, CatalogItem Item, int Count);

/// <summary>One change a plan makes to a slot: a count taken out of it, or put into it.</summary>
internal readonly record struct SlotChange(int Slot, CatalogItem Item, int Count);

/// <summary>What a move does: what leaves which slots first, then what arrives; or why it cannot be done.</summary>
internal sealed record SlotPlan(IReadOnlyList<SlotChange> Taken, IReadOnlyList<SlotChange> Given, string? Refusal = null)
{
    internal static SlotPlan Refused(string why) => new([], [], why);
}

/// <summary>
/// Where carried things sit. The player has <see cref="HotbarSlots"/> hotbar slots and
/// <see cref="PackSlots"/> pack slots in one numbering, hotbar first; each holds one stack of one
/// kind, up to <see cref="ItemCatalog.StackMaximum"/>. A thing lives in exactly one slot: the hotbar
/// is slots of its own, not shortcuts to the pack. These rules are pure: they plan over what the
/// slots hold, and the inventory carries a plan out as one edit.
/// </summary>
internal static class InventorySlots
{
    internal const int HotbarSlots = 9;
    internal const int PackSlots = 27;
    internal const int Count = HotbarSlots + PackSlots;

    private const string StackPrefix = "slot.";

    private static readonly InventoryStackId[] Stacks = [.. Enumerable.Range(0, Count).Select(slot => InventoryStackId.Parse($"{StackPrefix}{slot}"))];

    /// <summary>The Engine stack a slot is.</summary>
    internal static InventoryStackId Stack(int slot) => Stacks[slot];

    /// <summary>The slot an Engine stack is, or -1 when it is none of them.</summary>
    internal static int SlotOf(InventoryStackId stack)
    {
        for (int slot = 0; slot < Count; slot++)
        {
            if (Stacks[slot].Equals(stack))
            {
                return slot;
            }
        }

        return -1;
    }

    internal static bool IsSlot(int slot) => slot is >= 0 and < Count;

    private static int Room => (int)ItemCatalog.StackMaximum;

    /// <summary>
    /// Where items taken in go, all of them or none: onto stacks of the same kind first, then into
    /// empty slots, hotbar before pack, as they come to hand. Null when they do not all fit.
    /// </summary>
    internal static IReadOnlyList<SlotChange>? PlanTake(IReadOnlyList<SlotContents> held, IReadOnlyList<ItemCount> items)
    {
        Dictionary<int, (CatalogItem Item, int Count)> slots = held.ToDictionary(contents => contents.Slot, contents => (contents.Item, contents.Count));
        List<SlotChange> given = [];
        foreach (ItemCount taken in items)
        {
            int left = taken.Count;
            for (int slot = 0; slot < Count && left > 0; slot++)
            {
                if (slots.TryGetValue(slot, out (CatalogItem Item, int Count) there) && there.Item == taken.Item && there.Count < Room)
                {
                    int moved = Math.Min(left, Room - there.Count);
                    slots[slot] = (there.Item, there.Count + moved);
                    given.Add(new SlotChange(slot, taken.Item, moved));
                    left -= moved;
                }
            }

            for (int slot = 0; slot < Count && left > 0; slot++)
            {
                if (!slots.ContainsKey(slot))
                {
                    int moved = Math.Min(left, Room);
                    slots[slot] = (taken.Item, moved);
                    given.Add(new SlotChange(slot, taken.Item, moved));
                    left -= moved;
                }
            }

            if (left > 0)
            {
                return null;
            }
        }

        return given;
    }

    /// <summary>
    /// Which slots give up a number of one kind: the slot asked for first when it holds that kind,
    /// then the pack from its last slot back, then the hotbar, so what the player put on the hotbar
    /// is spent last. Null when not that many are carried.
    /// </summary>
    internal static IReadOnlyList<SlotChange>? PlanSpend(IReadOnlyList<SlotContents> held, CatalogItem item, int count, int preferredSlot = -1)
    {
        IEnumerable<SlotContents> order = held.Where(contents => contents.Item == item)
            .OrderBy(contents => contents.Slot == preferredSlot ? 0 : contents.Slot >= HotbarSlots ? 1 : 2)
            .ThenByDescending(contents => contents.Slot);
        List<SlotChange> taken = [];
        int left = count;
        foreach (SlotContents contents in order)
        {
            if (left == 0)
            {
                break;
            }

            int spent = Math.Min(left, contents.Count);
            taken.Add(new SlotChange(contents.Slot, item, spent));
            left -= spent;
        }

        return left == 0 ? taken : null;
    }

    /// <summary>
    /// Moves a stack, or some of it (<paramref name="count"/>, zero for all), from one slot to another:
    /// into an empty slot; onto a stack of the same kind, as much as it has room for; or, the whole
    /// stack only, swapping places with a stack of another kind.
    /// </summary>
    internal static SlotPlan PlanMove(IReadOnlyList<SlotContents> held, int from, int to, int count)
    {
        if (!IsSlot(from) || !IsSlot(to))
        {
            return SlotPlan.Refused($"there is no slot {(IsSlot(from) ? to : from)}");
        }

        if (from == to)
        {
            return SlotPlan.Refused("it is already there");
        }

        SlotContents? source = held.FirstOrDefault(contents => contents.Slot == from) is { Count: > 0 } found ? found : null;
        if (source is not SlotContents moving)
        {
            return SlotPlan.Refused($"slot {from} is empty");
        }

        int amount = count <= 0 ? moving.Count : Math.Min(count, moving.Count);
        SlotContents? target = held.FirstOrDefault(contents => contents.Slot == to) is { Count: > 0 } there ? there : null;
        if (target is not SlotContents onto)
        {
            return new SlotPlan([new(from, moving.Item, amount)], [new(to, moving.Item, amount)]);
        }

        if (onto.Item == moving.Item)
        {
            int moved = Math.Min(amount, Room - onto.Count);
            return moved <= 0
                ? SlotPlan.Refused($"the {onto.Item.Id} in slot {to} is a full stack")
                : new SlotPlan([new(from, moving.Item, moved)], [new(to, moving.Item, moved)]);
        }

        return amount < moving.Count
            ? SlotPlan.Refused($"part of a stack cannot go onto the {onto.Item.Id} in slot {to}")
            : new SlotPlan(
                [new(from, moving.Item, moving.Count), new(to, onto.Item, onto.Count)],
                [new(to, moving.Item, moving.Count), new(from, onto.Item, onto.Count)]);
    }

    /// <summary>Lays counts of each kind out into slots, as a pickup would, for a save from before there were slots.</summary>
    internal static IReadOnlyList<SlotContents> LayOut(IReadOnlyList<ItemCount> items) =>
        (PlanTake([], items) ?? throw new InvalidOperationException("Carried items do not fit the slots."))
            .GroupBy(change => change.Slot)
            .Select(group => new SlotContents(group.Key, group.First().Item, group.Sum(change => change.Count)))
            .OrderBy(contents => contents.Slot)
            .ToArray();
}
