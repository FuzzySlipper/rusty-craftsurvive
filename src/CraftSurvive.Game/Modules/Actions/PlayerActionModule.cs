using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Actions;

/// <summary>
/// The player-facing UI's way into the game. It reads the UI's action claims from an update's
/// input, aims each at what the player is looking at, hands it to the owner that performs it - the
/// blast or build module - and publishes what it came to. It holds no game state of its own beyond
/// those counts; the UI only claims intents and reads the projection.
/// </summary>
internal sealed class PlayerActionModule
{
    /// <summary>What a UI-placed door, light and container start as: closed, lit, empty.</summary>
    private const long DoorClosed = 0;
    private const long LightLit = 1;
    private const long ContainerEmpty = 0;

    /// <summary>The middle of a cell, from its corner.</summary>
    private const double CellCentre = 0.5d;

    private static readonly byte[] ContractUtf8 = Encoding.UTF8.GetBytes(PlayerAction.Contract);

    private readonly PlayerController player;
    private readonly BlastModule blast;
    private readonly BuildModule build;
    private readonly InventoryModule inventory;
    private readonly SurvivalModule survival;
    private readonly WorldConditionsModule conditions;
    private readonly DungeonModule dungeons;
    private readonly ProductUiPublisher ui;
    private readonly Cues cues;
    private readonly WorldFrame frame;
    private long applied;
    private long refused;
    private string last = "none";
    private bool published;

    internal PlayerActionModule(PlayerController player, BlastModule blast, BuildModule build, InventoryModule inventory,
        SurvivalModule survival, WorldConditionsModule conditions, DungeonModule dungeons, ProductUiPublisher ui, Cues cues, WorldFrame frame)
    {
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        this.dungeons = dungeons ?? throw new ArgumentNullException(nameof(dungeons));
        this.survival = survival ?? throw new ArgumentNullException(nameof(survival));
        this.conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.blast = blast ?? throw new ArgumentNullException(nameof(blast));
        this.build = build ?? throw new ArgumentNullException(nameof(build));
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    internal string Readout() => $"actions applied={applied} refused={refused} last={last}";

    /// <summary>Changes one of the game's own options (#9759) and says what it now is; refuses with <see cref="FormatException"/>.</summary>
    internal Func<string, int, string>? ChooseOption { get; set; }

    /// <summary>
    /// Applies the player's hotbar keys - a slot picked or stepped to, and the use of what it holds -
    /// then every action the UI claimed in this update, in the order it claimed them.
    /// </summary>
    internal void Update(ProductUpdate update)
    {
        (int pick, int steps, bool use) = player.HotbarRequests;
        inventory.Select(pick, steps);
        bool claimed = use;
        if (use)
        {
            UseSelected();
        }

        foreach (ProductInputEvent input in update.Input)
        {
            if (input.ValueKind != InputValueKind.ProductPayload || !input.PayloadContract.Span.SequenceEqual(ContractUtf8))
            {
                continue;
            }

            claimed = true;
            try
            {
                Apply(PlayerAction.Parse(input.PayloadData.Span));
            }

            // Browser payloads are untrusted input: a bad one is refused and shown, never thrown
            // out of the update.
            catch (Exception malformed) when (malformed is FormatException or JsonException)
            {
                Refuse($"refused: {malformed.Message}");
            }
        }

        // The first update publishes too, so the UI has the palette before anything is claimed.
        if (claimed || !published)
        {
            ui.PublishActions(new ActionUiFacts(applied, refused, last, BuildPalette.Names));
            published = true;
        }
    }

    /// <summary>A fresh session: nothing has been asked for yet.</summary>
    internal void Restart()
    {
        applied = 0;
        refused = 0;
        last = "none";
    }

    private void Apply(PlayerAction action)
    {
        string name = action.Kind.ToString().ToLowerInvariant();
        if (action.Kind == PlayerActionKind.Option)
        {
            if (ChooseOption is null)
            {
                Refuse($"{name}: the options are not ready");
                return;
            }

            try
            {
                // The panel shows the new value itself: a slider dragged through its range raises no
                // message per step. A refusal is still shown.
                _ = ChooseOption(action.Name, action.OptionValue);
                applied++;
            }
            catch (FormatException refusal)
            {
                Refuse($"{name}: {refusal.Message}");
            }

            return;
        }

        if (action.Kind == PlayerActionKind.Landscape)
        {
            if (player.VisitLandscape(action.Name)) Accept($"Visiting {action.Name}");
            else Refuse("Landscape visit unavailable: leave the dungeon first and choose a known study.");
            return;
        }
        if (action.Kind == PlayerActionKind.Craft)
        {
            long refusedBefore = inventory.Refused;
            Settle(name, inventory.Craft(action.Name), inventory.Refused > refusedBefore, Cue.Craft);
            return;
        }

        if (action.Kind == PlayerActionKind.Use)
        {
            long refusedBefore = inventory.Refused;
            Settle(name, inventory.Use(action.Name, action.Slot), inventory.Refused > refusedBefore);
            return;
        }

        if (action.Kind == PlayerActionKind.Select)
        {
            inventory.Select(action.FromSlot, 0);
            Accept($"{name}: slot {inventory.Selected}");
            return;
        }

        if (action.Kind == PlayerActionKind.Move)
        {
            long refusedBefore = inventory.Refused;
            Settle(name, inventory.Move(action.FromSlot, action.ToSlot, action.Count), inventory.Refused > refusedBefore);
            return;
        }

        if (action.Kind is PlayerActionKind.Enter or PlayerActionKind.Leave)
        {
            long refusedBefore = dungeons.Refused;
            string outcome = action.Kind == PlayerActionKind.Enter ? dungeons.Enter() : dungeons.Leave();
            Settle(name, outcome, dungeons.Refused > refusedBefore, Cue.Portal);
            return;
        }

        if (action.Kind == PlayerActionKind.Rest)
        {
            long refusedBefore = survival.RestsRefused;
            Settle(name, survival.Rest(), survival.RestsRefused > refusedBefore);
            return;
        }

        if (action.Kind == PlayerActionKind.Difficulty)
        {
            long refusedBefore = conditions.DifficultyRefused;
            Settle(name, conditions.SetDifficulty(action.Name), conditions.DifficultyRefused > refusedBefore);
            return;
        }

        if (action.Kind == PlayerActionKind.Light && inventory.Count(ItemCatalog.Torch) <= 0)
        {
            Refuse("light: carrying no torch");
            return;
        }

        if (action.Kind == PlayerActionKind.Undo)
        {
            build.Undo();
            if (build.Feedback.Accepted) Accept(build.Feedback.Message, Cue.Place);
            else Refuse(build.Feedback.Message);
            return;
        }

        TerrainPick aim = player.Aim();
        if (aim.Outcome != TerrainPickOutcome.Picked)
        {
            Refuse($"{name}: nothing within reach to aim at");
            return;
        }

        // A charge breaks what is aimed at; everything built goes on the aimed block's open face,
        // and a floor or wall is laid relative to where the player faces.
        VoxelAddress at = action.Kind == PlayerActionKind.Blast ? aim.Target : aim.Adjacent;
        (int X, int Z) facing = BuildStamp.Cardinal(player.AimForward.X, player.AimForward.Z);
        switch (action.Kind)
        {
            case PlayerActionKind.Blast:
                blast.Fire(at.X, at.Y, at.Z, action.Radius);
                Accept($"{name} r{action.Radius} at {at.X},{at.Y},{at.Z}: {blast.LastOutcome}");
                return;
            case PlayerActionKind.Plate:
                build.PlateAhead(at, action.Size1, action.Size2, facing, (ushort)action.Material);
                break;
            case PlayerActionKind.Wall:
                build.WallAcross(at, action.Size1, action.Size2, facing, (ushort)action.Material);
                break;
            case PlayerActionKind.Door:
                build.Door(at.X, at.Y, at.Z, DoorClosed);
                break;
            case PlayerActionKind.Light:
                PlaceLight(at);
                break;
            case PlayerActionKind.Container:
                build.Container(at.X, at.Y, at.Z, ContainerEmpty);
                break;
        }

        if (build.Feedback.Accepted)
        {
            // Seen where it was built: the middle of the aimed cell, in the session's local frame.
            Accept(build.Feedback.Message);
            cues.RaiseAt(Cue.Place, frame.ToLocal(at.X + CellCentre, at.Y + CellCentre, at.Z + CellCentre));
            return;
        }

        Refuse(build.Feedback.Message);
    }

    /// <summary>A light burns a torch - from the slot asked for, when it holds one - but only once one is actually placed.</summary>
    private void PlaceLight(VoxelAddress at, int fromSlot = -1)
    {
        long placedBefore = build.EntitiesPlaced;
        build.Light(at.X, at.Y, at.Z, LightLit);
        if (build.EntitiesPlaced > placedBefore)
        {
            inventory.Spend(ItemCatalog.Torch, fromSlot);
        }
    }

    /// <summary>
    /// Uses what the selected hotbar slot holds: food is eaten and a bandage applied from that slot,
    /// and a torch is placed as a light where the player aims. Materials are for crafting, not using.
    /// </summary>
    private void UseSelected()
    {
        if (inventory.SelectedContents is not SlotContents held)
        {
            Refuse("use: the selected slot is empty");
            return;
        }

        switch (held.Item.Use)
        {
            case ItemUse.Food or ItemUse.Healing:
                long refusedBefore = inventory.Refused;
                Settle("use", inventory.Use(held.Item.Id, held.Slot), inventory.Refused > refusedBefore);
                return;
            case ItemUse.Light:
                TerrainPick aim = player.Aim();
                if (aim.Outcome != TerrainPickOutcome.Picked)
                {
                    Refuse("use: nothing within reach to place the torch on");
                    return;
                }

                long builtBefore = Built;
                PlaceLight(aim.Adjacent, held.Slot);
                if (Built > builtBefore)
                {
                    Accept($"use: placed a torch at {aim.Adjacent.X},{aim.Adjacent.Y},{aim.Adjacent.Z}");
                    cues.RaiseAt(Cue.Place, frame.ToLocal(aim.Adjacent.X + CellCentre, aim.Adjacent.Y + CellCentre, aim.Adjacent.Z + CellCentre));
                    return;
                }

                Refuse(build.Feedback.Message);
                return;
            default:
                Refuse($"use: {held.Item.Name.ToLowerInvariant()} is for crafting, not using");
                return;
        }
    }

    /// <summary>Everything building has done so far, so an action can tell whether it built anything.</summary>
    private long Built => build.Stamps + build.EntitiesPlaced + build.Undone;

    /// <summary>Counts an owner's answer as applied or refused; an applied one is heard as <paramref name="heard"/>, if anything.</summary>
    private void Settle(string name, string outcome, bool wasRefused, Cue? heard = null)
    {
        if (wasRefused)
        {
            Refuse($"{name}: {outcome}");
        }
        else
        {
            Accept($"{name}: {outcome}", heard);
        }
    }

    private void Accept(string outcome, Cue? heard = null)
    {
        applied++;
        last = outcome;
        if (heard is Cue cue)
        {
            cues.Raise(cue);
        }
    }

    private void Refuse(string outcome)
    {
        refused++;
        last = outcome;
        cues.Raise(Cue.Refused);
    }
}
