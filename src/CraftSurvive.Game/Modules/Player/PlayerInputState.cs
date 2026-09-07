using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>Owns the product interpretation of physical input facts and one-shot actions.</summary>
internal sealed class PlayerInputState
{
    private bool keyboardForward;
    private bool keyboardBackward;
    private bool keyboardRight;
    private bool keyboardLeft;
    private bool keyboardJump;
    private bool keyboardCrouch;
    private bool keyboardSprint;
    private bool keyboardImpulse;
    private bool controllerJump;
    private bool controllerCrouch;
    private bool controllerSprint;
    private bool controllerImpulse;
    private float controllerMoveX;
    private float controllerMoveY;
    private float controllerLookX;
    private float controllerLookY;
    private int brushRadius = PlayerConstants.DefaultBrushRadius;
    private Vector2 pendingLookDelta;
    private TerrainEditKind? pendingEdit;

    internal PlayerInputFrame Consume(ReadOnlySpan<ProductInputEvent> events, float simulationDeltaSeconds)
    {
        foreach (ProductInputEvent input in events)
        {
            if (input.Kind == InputEventKind.Clear)
            {
                ClearHeld();
                pendingLookDelta = Vector2.Zero;
                pendingEdit = null;
                continue;
            }

            if (input.Kind == InputEventKind.PointerDelta)
            {
                pendingLookDelta += new Vector2(input.X, input.Y);
                continue;
            }

            if (input.Kind == InputEventKind.PointerButton && input.Edge == InputEdge.Pressed)
            {
                pendingEdit = input.PointerButton switch
                {
                    PointerButton.Primary => TerrainEditKind.Clear,
                    PointerButton.Secondary => TerrainEditKind.Set,
                    _ => pendingEdit,
                };
                continue;
            }

            if (input.Kind == InputEventKind.Key)
            {
                ApplyKey(input.Keyboard, input.Edge);
                continue;
            }

            if (input.Kind == InputEventKind.ControllerAxis)
            {
                ApplyControllerAxis(input.ControllerAxis, input.X);
                continue;
            }

            if (input.Kind == InputEventKind.ControllerButton)
            {
                ApplyControllerButton(input.ControllerButton, input.Edge);
            }
        }

        Vector2 controllerMove = ApplyRadialDeadzone(new Vector2(controllerMoveX, -controllerMoveY));
        Vector2 controllerLook = ApplyRadialDeadzone(new Vector2(controllerLookX, controllerLookY));
        PlayerInputFrame frame = new(
            Vector2.Clamp(new Vector2(
                Axis(keyboardRight, keyboardLeft),
                Axis(keyboardForward, keyboardBackward)) + controllerMove,
                -Vector2.One,
                Vector2.One),
            keyboardJump || controllerJump,
            keyboardCrouch || controllerCrouch,
            keyboardSprint || controllerSprint,
            keyboardImpulse || controllerImpulse,
            pendingLookDelta + controllerLook * (PlayerConstants.ControllerLookInputUnitsPerSecond * simulationDeltaSeconds),
            pendingEdit,
            brushRadius);
        pendingLookDelta = Vector2.Zero;
        pendingEdit = null;
        return frame;
    }

    private void ApplyKey(KeyboardControl key, InputEdge edge)
    {
        if (edge == InputEdge.None)
        {
            return;
        }

        bool held = edge is InputEdge.Pressed or InputEdge.Held;
        switch (key)
        {
            case KeyboardControl.KeyW:
                keyboardForward = held;
                break;
            case KeyboardControl.KeyS:
                keyboardBackward = held;
                break;
            case KeyboardControl.KeyD:
                keyboardRight = held;
                break;
            case KeyboardControl.KeyA:
                keyboardLeft = held;
                break;
            case KeyboardControl.Space:
                keyboardJump = held;
                break;
            case KeyboardControl.ControlLeft:
            case KeyboardControl.ControlRight:
                keyboardCrouch = held;
                break;
            case KeyboardControl.ShiftLeft:
            case KeyboardControl.ShiftRight:
                keyboardSprint = held;
                break;
            case KeyboardControl.KeyH:
                keyboardImpulse = held;
                break;
        }

        if (edge != InputEdge.Pressed)
        {
            return;
        }

        switch (key)
        {
            case KeyboardControl.KeyF:
                pendingEdit = TerrainEditKind.Clear;
                break;
            case KeyboardControl.KeyG:
                pendingEdit = TerrainEditKind.Set;
                break;
            case KeyboardControl.Digit1:
                brushRadius = PlayerConstants.MinimumBrushRadius;
                break;
            case KeyboardControl.Digit2:
                brushRadius = PlayerConstants.MediumBrushRadius;
                break;
            case KeyboardControl.Digit3:
                brushRadius = PlayerConstants.MaximumBrushRadius;
                break;
        }
    }

    private void ClearHeld()
    {
        keyboardForward = false;
        keyboardBackward = false;
        keyboardRight = false;
        keyboardLeft = false;
        keyboardJump = false;
        keyboardCrouch = false;
        keyboardSprint = false;
        keyboardImpulse = false;
        controllerJump = false;
        controllerCrouch = false;
        controllerSprint = false;
        controllerImpulse = false;
        controllerMoveX = 0f;
        controllerMoveY = 0f;
        controllerLookX = 0f;
        controllerLookY = 0f;
    }

    private void ApplyControllerAxis(ControllerAxis axis, float value)
    {
        switch (axis)
        {
            case ControllerAxis.Axis0:
                controllerMoveX = value;
                break;
            case ControllerAxis.Axis1:
                controllerMoveY = value;
                break;
            case ControllerAxis.Axis2:
                controllerLookX = value;
                break;
            case ControllerAxis.Axis3:
                controllerLookY = value;
                break;
        }
    }

    private void ApplyControllerButton(ControllerButton button, InputEdge edge)
    {
        if (edge == InputEdge.None)
        {
            return;
        }

        bool held = edge is InputEdge.Pressed or InputEdge.Held;
        switch (button)
        {
            case ControllerButton.Button0:
                controllerJump = held;
                break;
            case ControllerButton.Button1:
                controllerCrouch = held;
                break;
            case ControllerButton.Button2:
                controllerImpulse = held;
                break;
            case ControllerButton.Button10:
                controllerSprint = held;
                break;
        }

        if (edge != InputEdge.Pressed)
        {
            return;
        }

        pendingEdit = button switch
        {
            ControllerButton.Button7 => TerrainEditKind.Clear,
            ControllerButton.Button6 => TerrainEditKind.Set,
            _ => pendingEdit,
        };
    }

    private static Vector2 ApplyRadialDeadzone(Vector2 value)
    {
        float magnitude = value.Length();
        if (magnitude <= PlayerConstants.ControllerStickDeadzone)
        {
            return Vector2.Zero;
        }

        float clampedMagnitude = MathF.Min(magnitude, 1f);
        float remappedMagnitude = (clampedMagnitude - PlayerConstants.ControllerStickDeadzone)
            / (1f - PlayerConstants.ControllerStickDeadzone);
        return value / magnitude * remappedMagnitude;
    }

    private static float Axis(bool positive, bool negative) => positive == negative ? 0f : positive ? 1f : -1f;
}

internal readonly record struct PlayerInputFrame(
    Vector2 PlanarIntent,
    bool JumpHeld,
    bool CrouchRequested,
    bool SprintRequested,
    bool ImpulseHeld,
    Vector2 LookDelta,
    TerrainEditKind? Edit,
    int BrushRadius);
