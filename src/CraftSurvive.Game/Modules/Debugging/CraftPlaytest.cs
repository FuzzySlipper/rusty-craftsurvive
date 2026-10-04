using System.Globalization;
using System.Text.Json;
using CraftSurvive.Game.Modules.Player;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

/// <summary>
/// CraftSurvive's answers to the Engine's playtest commands (<c>playtest.observe</c>,
/// <c>playtest.action</c>, <c>playtest.look</c>): what the player is, which ordinary physical
/// control each action is, and a look through the player's own look rules. Queries never act;
/// a harness performs an action by sending its control as input.
/// </summary>
internal static class CraftPlaytest
{
    private const double TapMilliseconds = 100;
    private const double StepMilliseconds = 1000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Each action and its control, in the Engine's physical keyboard codes.</summary>
    private static readonly IReadOnlyDictionary<string, PlaytestAction> Actions = new Dictionary<string, PlaytestAction>
    {
        ["walk-forward"] = new("walk-forward", "key-w", StepMilliseconds, Hold: true),
        ["walk-back"] = new("walk-back", "key-s", StepMilliseconds, Hold: true),
        ["strafe-left"] = new("strafe-left", "key-a", StepMilliseconds, Hold: true),
        ["strafe-right"] = new("strafe-right", "key-d", StepMilliseconds, Hold: true),
        ["jump"] = new("jump", "space", TapMilliseconds, Hold: false),
        ["attack"] = new("attack", "key-j", TapMilliseconds, Hold: false),
        ["clear-terrain"] = new("clear-terrain", "key-f", TapMilliseconds, Hold: false),
        ["place-terrain"] = new("place-terrain", "key-g", TapMilliseconds, Hold: false),
    };

    internal static PlaytestDebugModule Create(Func<PlayerController> player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return new PlaytestDebugModule(
            () => DebugCommandResult.Success(JsonSerializer.Serialize(Observation(player().Observe()), Json)),
            id => Actions.TryGetValue(id, out PlaytestAction? action)
                ? action
                : new PlaytestAction(id, string.Empty, 0, Hold: false, Available: false,
                    Reason: $"unknown action; known actions are {string.Join(", ", Actions.Keys)}"),
            [.. Actions.Keys],
            (yaw, pitch) =>
            {
                player().LookBy(yaw, pitch);
                PlayerObservation after = player().Observe();
                return DebugCommandResult.Success(string.Create(CultureInfo.InvariantCulture,
                    $"yawDegrees={after.YawDegrees:F2};pitchDegrees={after.PitchDegrees:F2}"));
            });
    }

    private static object Observation(PlayerObservation facts) => new
    {
        feet = new { x = facts.Feet.X, y = facts.Feet.Y, z = facts.Feet.Z },
        facts.YawDegrees,
        facts.PitchDegrees,
        facts.Grounded,
        facts.Crouched,
        facts.Health,
        facts.MaximumHealth,
        facts.Level,
        facts.KeyEvents,
        facts.Updates,
    };
}
