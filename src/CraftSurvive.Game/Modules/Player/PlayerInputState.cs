using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>Owns the product interpretation of physical input facts and one-shot actions.</summary>
internal sealed class PlayerInputState
{
    private bool forward;
    private bool backward;
    private bool right;
    private bool left;
    private bool jump;
    private bool crouch;
    private bool sprint;
    private bool impulse;
    private int brushRadius = PlayerConstants.DefaultBrushRadius;
    private Vector2 pendingLookDelta;
    private TerrainEditKind? pendingEdit;

    internal PlayerInputFrame Consume(ReadOnlySpan<ProductInputEvent> events)
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
            }
        }

        PlayerInputFrame frame = new(
            new Vector2(Axis(right, left), Axis(forward, backward)),
            jump,
            crouch,
            sprint,
            impulse,
            pendingLookDelta,
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
                forward = held;
                break;
            case KeyboardControl.KeyS:
                backward = held;
                break;
            case KeyboardControl.KeyD:
                right = held;
                break;
            case KeyboardControl.KeyA:
                left = held;
                break;
            case KeyboardControl.Space:
                jump = held;
                break;
            case KeyboardControl.ControlLeft:
            case KeyboardControl.ControlRight:
                crouch = held;
                break;
            case KeyboardControl.ShiftLeft:
            case KeyboardControl.ShiftRight:
                sprint = held;
                break;
            case KeyboardControl.KeyH:
                impulse = held;
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
        forward = false;
        backward = false;
        right = false;
        left = false;
        jump = false;
        crouch = false;
        sprint = false;
        impulse = false;
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
