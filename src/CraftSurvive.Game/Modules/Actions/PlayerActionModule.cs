using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Player;
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

    private static readonly byte[] ContractUtf8 = Encoding.UTF8.GetBytes(PlayerAction.Contract);

    private readonly PlayerController player;
    private readonly BlastModule blast;
    private readonly BuildModule build;
    private readonly ProductUiPublisher ui;
    private long applied;
    private long refused;
    private string last = "none";
    private bool published;

    internal PlayerActionModule(PlayerController player, BlastModule blast, BuildModule build, ProductUiPublisher ui)
    {
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.blast = blast ?? throw new ArgumentNullException(nameof(blast));
        this.build = build ?? throw new ArgumentNullException(nameof(build));
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    internal string Readout() => $"actions applied={applied} refused={refused} last={last}";

    /// <summary>Applies every action the UI claimed in this update, in the order it claimed them.</summary>
    internal void Update(ProductUpdate update)
    {
        bool claimed = false;
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
        if (action.Kind == PlayerActionKind.Undo)
        {
            build.Undo();
            Accept($"{name}: {build.LastOutcome}");
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
                build.Light(at.X, at.Y, at.Z, LightLit);
                break;
            case PlayerActionKind.Container:
                build.Container(at.X, at.Y, at.Z, ContainerEmpty);
                break;
        }

        Accept($"{name} at {at.X},{at.Y},{at.Z}: {build.LastOutcome}");
    }

    private void Accept(string outcome)
    {
        applied++;
        last = outcome;
    }

    private void Refuse(string outcome)
    {
        refused++;
        last = outcome;
    }
}
