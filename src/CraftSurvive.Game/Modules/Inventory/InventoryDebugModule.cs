using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Inventory;

/// <summary>Live-debug adapter over the inventory: read it, grant items, craft and use, as the UI does.</summary>
public sealed class InventoryDebugModule : IDebugCommandModule
{
    private readonly InventoryModule inventory;

    internal InventoryDebugModule(InventoryModule inventory) =>
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));

    [DebugCommand("craft.inventory.readout", Description = "Reads what the player carries, crafting and use counts, and how the inventory save is going.")]
    public string Readout() => inventory.Readout();

    [DebugCommand("craft.inventory.grant", Description = "Gives the player a count of an item (meat, hide, claw, cloth, oil, ration, bandage, torch).")]
    public string Grant(string item, long count) => inventory.Grant(item, (int)Math.Clamp(count, 0, 99));

    [DebugCommand("craft.inventory.craft", Description = "Crafts a recipe (ration, bandage, torch), as the UI's Craft does.")]
    public string Craft(string recipe) => inventory.Craft(recipe);

    [DebugCommand("craft.inventory.use", Description = "Eats or applies an item, as the UI's Use does.")]
    public string Use(string item) => inventory.Use(item);
}
