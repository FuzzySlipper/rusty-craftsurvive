using System.Globalization;
using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// What input reached the player and what the last movement did with it: counts per update, the
/// last event, and the last update that moved. It explains a scripted or live session's motion
/// and owns no gameplay state.
/// </summary>
internal sealed class PlayerInputDiagnostics
{
    private int events;
    private int keys;
    private int pointer;
    private int controllerButtons;
    private int controllerAxes;
    private int clears;
    private ulong totalEvents;
    private ulong totalPointerDeltas;
    private ulong lastEventUpdate;
    private string lastEvent = "none";
    private ulong lastMovementUpdate;
    private PlayerInputFrame lastMovementFrame;
    private uint lastMovementSteps;
    private CharacterStepReceipt? lastMovementStep;
    private Vector3 lastMovementBefore;
    private Vector3 lastMovementAfter;

    internal void Capture(ReadOnlySpan<ProductInputEvent> input, ulong update)
    {
        events = input.Length;
        keys = 0;
        pointer = 0;
        controllerButtons = 0;
        controllerAxes = 0;
        clears = 0;
        foreach (ProductInputEvent inputEvent in input)
        {
            totalEvents = checked(totalEvents + 1UL);
            lastEventUpdate = update;
            switch (inputEvent.Kind)
            {
                case InputEventKind.Key:
                    keys++;
                    lastEvent = $"key:{inputEvent.Keyboard}:{inputEvent.Edge}";
                    break;
                case InputEventKind.PointerDelta:
                    pointer++;
                    totalPointerDeltas = checked(totalPointerDeltas + 1UL);
                    lastEvent = string.Create(CultureInfo.InvariantCulture, $"pointer-delta:{inputEvent.X:F3},{inputEvent.Y:F3}");
                    break;
                case InputEventKind.PointerButton:
                    pointer++;
                    lastEvent = $"pointer-button:{inputEvent.PointerButton}:{inputEvent.Edge}";
                    break;
                case InputEventKind.ControllerButton:
                    controllerButtons++;
                    lastEvent = $"controller-button:{inputEvent.ControllerButton}:{inputEvent.Edge}";
                    break;
                case InputEventKind.ControllerAxis:
                    controllerAxes++;
                    lastEvent = string.Create(CultureInfo.InvariantCulture, $"controller-axis:{inputEvent.ControllerAxis}:{inputEvent.X:F3}");
                    break;
                case InputEventKind.Clear:
                    clears++;
                    lastEvent = $"clear:{inputEvent.ClearReason}";
                    break;
                default:
                    lastEvent = inputEvent.Kind.ToString();
                    break;
            }
        }
    }

    /// <summary>Remembers an update in which the player asked to move, with what the steps did.</summary>
    internal void RecordMovement(ulong update, PlayerInputFrame frame, uint steps, CharacterStepReceipt? step,
        Vector3 before, Vector3 after)
    {
        if (frame.PlanarIntent == Vector2.Zero && !frame.JumpHeld && !frame.CrouchRequested
            && !frame.SprintRequested && !frame.ImpulseHeld)
        {
            return;
        }

        lastMovementUpdate = update;
        lastMovementFrame = frame;
        lastMovementSteps = steps;
        lastMovementStep = step;
        lastMovementBefore = before;
        lastMovementAfter = after;
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"events={events};totalEvents={totalEvents};keys={keys};pointer={pointer};totalPointerDeltas={totalPointerDeltas};controllerButtons={controllerButtons};controllerAxes={controllerAxes};clears={clears};lastEventUpdate={lastEventUpdate};lastEvent={lastEvent}");

    internal string MovementReadout() => lastMovementUpdate == 0UL
        ? "none"
        : string.Create(CultureInfo.InvariantCulture,
            $"update={lastMovementUpdate};intent={Format(lastMovementFrame.PlanarIntent)};controllerSteps={lastMovementSteps};before={Format(lastMovementBefore)};after={Format(lastMovementAfter)};step=[{FormatStep(lastMovementStep)}]");

    internal static string Format(Vector2 value) => string.Create(CultureInfo.InvariantCulture, $"{value.X:F3},{value.Y:F3}");

    internal static string Format(Vector3 value) => string.Create(CultureInfo.InvariantCulture, $"{value.X:F3},{value.Y:F3},{value.Z:F3}");

    internal static string FormatStep(CharacterStepReceipt? step) => step is not CharacterStepReceipt receipt
        ? "none"
        : string.Create(CultureInfo.InvariantCulture,
            $"attempted={receipt.Step.Attempted};accepted={receipt.Step.Accepted};wish={Format(receipt.WishVelocity)};displacement={Format(receipt.Displacement)};blocked={receipt.BlockFlags};casts={receipt.CastCount};movement={receipt.Movement.Mode};immersion={receipt.Movement.Immersion:F3};headSubmerged={receipt.Movement.HeadSubmerged}");
}
