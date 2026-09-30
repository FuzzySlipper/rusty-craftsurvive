using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Actions;

namespace CraftSurvive.Game.Tests;

/// <summary>The player-facing UI's requests arrive as untrusted JSON; only well-formed, in-range ones become actions.</summary>
internal static class ActionChecks
{
    internal static void Run()
    {
        Check.Equal(new PlayerAction(PlayerActionKind.Blast, 2), Parse("""{"action":"blast","radius":2}"""), "a blast carries its radius");
        Check.Equal(new PlayerAction(PlayerActionKind.Plate, 3, 4), Parse("""{"action":"plate","width":3,"depth":4}"""), "a floor carries width then depth");
        Check.Equal(new PlayerAction(PlayerActionKind.Wall, 4, 3), Parse("""{"action":"wall","length":4,"height":3}"""), "a wall carries length then height");
        foreach ((string name, PlayerActionKind kind) in new[] { ("door", PlayerActionKind.Door), ("light", PlayerActionKind.Light), ("container", PlayerActionKind.Container), ("undo", PlayerActionKind.Undo) })
        {
            Check.Equal(kind, Parse($$"""{"action":"{{name}}"}""").Kind, $"\"{name}\" is an action");
        }

        string[] refused =
        [
            """{"action":"detonate"}""",
            """{"action":"blast"}""",
            """{"action":"blast","radius":0}""",
            $$"""{"action":"blast","radius":{{PlayerAction.MaximumBlastRadius + 1}}}""",
            $$"""{"action":"plate","width":{{PlayerAction.MaximumStampSide + 1}},"depth":1}""",
            $$"""{"action":"wall","length":2,"height":{{PlayerAction.MaximumWallHeight + 1}}}""",
            """{"action":"blast","radius":1.5}""",
            """{"radius":2}""",
            """["blast"]""",
        ];
        foreach (string payload in refused)
        {
            Check.Throws<FormatException>(() => Parse(payload), $"{payload} must be refused");
        }

        Check.Throws<JsonException>(() => Parse("{not json"), "a payload that is not JSON must be refused");
    }

    private static PlayerAction Parse(string json) => PlayerAction.Parse(Encoding.UTF8.GetBytes(json));
}
