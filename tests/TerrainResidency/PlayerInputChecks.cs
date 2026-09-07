using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Tests;

internal static class PlayerInputChecks
{
    internal static void Run()
    {
        var input = new PlayerInputState();
        PlayerInputFrame frame = input.Consume(
        [
            Axis(ControllerAxis.Axis0, 0.30f),
            Axis(ControllerAxis.Axis1, -0.80f),
            Axis(ControllerAxis.Axis2, 0.50f),
            Axis(ControllerAxis.Axis3, -0.25f),
        ], 1f / 60f);
        Require(frame.PlanarIntent.X > 0f && frame.PlanarIntent.Y > 0f,
            "left stick did not retain standard move directions");
        Require(frame.PlanarIntent.Length() is > 0f and < 1f,
            "left stick lost its remapped analog magnitude");
        Require(frame.LookDelta.X > 0f && frame.LookDelta.Y < 0f,
            "right stick did not apply simulation-time look deltas");

        var deadzoneInput = new PlayerInputState();
        frame = deadzoneInput.Consume(
        [
            Axis(ControllerAxis.Axis0, PlayerConstants.ControllerStickDeadzone),
            Axis(ControllerAxis.Axis2, PlayerConstants.ControllerStickDeadzone),
        ], 1f / 60f);
        Require(frame.PlanarIntent == Vector2.Zero && frame.LookDelta == Vector2.Zero,
            "controller stick deadzone did not apply to movement and look");

        Vector2 sixtyHzLook = SumHeldStickLook(60, 1f / 60f);
        Vector2 thirtyHzLook = SumHeldStickLook(30, 1f / 30f);
        Require(Vector2.Distance(sixtyHzLook, thirtyHzLook) < 0.001f,
            "held controller look depended on update frequency");
        var zeroDeltaInput = new PlayerInputState();
        frame = zeroDeltaInput.Consume([Axis(ControllerAxis.Axis2, 0.5f)], 0f);
        Require(frame.LookDelta == Vector2.Zero,
            "controller look advanced without admitted simulation time");

        frame = input.Consume([Key(KeyboardControl.KeyW, InputEdge.Pressed)], 1f / 60f);
        Require(frame.PlanarIntent.Y > 0f, "keyboard forward did not combine with the controller state");
        frame = input.Consume([Key(KeyboardControl.KeyW, InputEdge.Released)], 1f / 60f);
        Require(frame.PlanarIntent.Y > 0f,
            "keyboard release cancelled the separately held controller movement");

        frame = input.Consume(
        [
            Button(ControllerButton.Button0, InputEdge.Pressed),
            Button(ControllerButton.Button1, InputEdge.Pressed),
            Button(ControllerButton.Button2, InputEdge.Pressed),
            Button(ControllerButton.Button10, InputEdge.Pressed),
        ], 1f / 60f);
        Require(frame.JumpHeld && frame.CrouchRequested && frame.ImpulseHeld && frame.SprintRequested,
            "standard controller action buttons were not retained");

        frame = input.Consume([Button(ControllerButton.Button7, InputEdge.Pressed)], 1f / 60f);
        Require(frame.Edit == TerrainEditKind.Clear, "right trigger did not queue a clear edit");
        frame = input.Consume([Button(ControllerButton.Button6, InputEdge.Pressed)], 1f / 60f);
        Require(frame.Edit == TerrainEditKind.Set, "left trigger did not queue a set edit");

        frame = input.Consume([Clear()], 1f / 60f);
        Require(frame.PlanarIntent == Vector2.Zero && frame.LookDelta == Vector2.Zero
            && !frame.JumpHeld && !frame.CrouchRequested && !frame.ImpulseHeld && !frame.SprintRequested
            && frame.Edit is null,
            "clear did not reset keyboard and controller input state");
    }

    private static ProductInputEvent Axis(ControllerAxis axis, float value) => new(
        InputEventKind.ControllerAxis, InputEdge.None, InputDevice.Controller, InputChannel.Axis,
        InputAxis.None, KeyboardControl.None, PointerButton.None, ControllerButton.None, axis,
        InputClearReason.None, InputValueKind.Axis, InputPhase.Axis, InputProvenance.Physical,
        default, default, default, value, 0f, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static Vector2 SumHeldStickLook(int updates, float simulationDeltaSeconds)
    {
        var input = new PlayerInputState();
        input.Consume(
        [
            Axis(ControllerAxis.Axis2, 0.50f),
            Axis(ControllerAxis.Axis3, -0.25f),
        ], 0f);
        Vector2 accumulated = Vector2.Zero;
        for (int update = 0; update < updates; update++)
        {
            accumulated += input.Consume(ReadOnlySpan<ProductInputEvent>.Empty, simulationDeltaSeconds).LookDelta;
        }

        return accumulated;
    }

    private static ProductInputEvent Button(ControllerButton button, InputEdge edge) => new(
        InputEventKind.ControllerButton, edge, InputDevice.Controller, InputChannel.Button,
        InputAxis.None, KeyboardControl.None, PointerButton.None, button, ControllerAxis.None,
        InputClearReason.None, InputValueKind.Digital, InputPhase.Pressed, InputProvenance.Physical,
        default, default, default, 0f, 0f, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static ProductInputEvent Key(KeyboardControl key, InputEdge edge) => new(
        InputEventKind.Key, edge, InputDevice.Keyboard, InputChannel.Key,
        InputAxis.None, key, PointerButton.None, ControllerButton.None, ControllerAxis.None,
        InputClearReason.None, InputValueKind.Digital, InputPhase.Pressed, InputProvenance.Physical,
        default, default, default, 0f, 0f, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static ProductInputEvent Clear() => new(
        InputEventKind.Clear, InputEdge.None, InputDevice.Runtime, InputChannel.Clear,
        InputAxis.None, KeyboardControl.None, PointerButton.None, ControllerButton.None, ControllerAxis.None,
        InputClearReason.FocusLoss, InputValueKind.None, InputPhase.None, InputProvenance.Physical,
        default, default, default, 0f, 0f, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
