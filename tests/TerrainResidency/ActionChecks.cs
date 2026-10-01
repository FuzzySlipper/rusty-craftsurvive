using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Actions;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Tests;

/// <summary>The player-facing UI's requests arrive as untrusted JSON; only well-formed, in-range ones become actions.</summary>
internal static class ActionChecks
{
    internal static void Run()
    {
        Check.Equal(new PlayerAction(PlayerActionKind.Blast, 2), Parse("""{"action":"blast","radius":2}"""), "a blast carries its radius");
        Check.Equal(new PlayerAction(PlayerActionKind.Plate, 3, 4, BuildPalette.Default), Parse("""{"action":"plate","width":3,"depth":4}"""), "a floor carries width then depth, and the palette's default block");
        Check.Equal(new PlayerAction(PlayerActionKind.Wall, 4, 3, BlockId.Brick), Parse("""{"action":"wall","length":4,"height":3,"material":"brick"}"""), "a wall carries length, height and the palette block it names");
        foreach (BlockId block in BuildPalette.Blocks)
        {
            string name = BlockRegistry.Get(block).Name;
            Check.Equal(block, Parse($$"""{"action":"plate","width":1,"depth":1,"material":"{{name}}"}""").Material, $"the palette's \"{name}\" builds {block}");
            Check.That(BuildPalette.Names.Split(',').Contains(name), $"the published palette names {name}");
            Check.That(BlockRegistry.Get(block).Solid && BlockRegistry.Get(block).Collidable, $"{block} is something to stand on");
        }
        foreach ((string name, PlayerActionKind kind) in new[] { ("door", PlayerActionKind.Door), ("light", PlayerActionKind.Light), ("container", PlayerActionKind.Container), ("undo", PlayerActionKind.Undo) })
        {
            Check.Equal(kind, Parse($$"""{"action":"{{name}}"}""").Kind, $"\"{name}\" is an action");
        }

        Check.Equal(new PlayerAction(PlayerActionKind.Craft, Name: "ration"), Parse("""{"action":"craft","recipe":"ration"}"""), "a craft names its recipe");
        Check.Equal(new PlayerAction(PlayerActionKind.Use, Name: "bandage"), Parse("""{"action":"use","item":"bandage"}"""), "a use names its item");

        string[] refused =
        [
            """{"action":"craft"}""",
            """{"action":"craft","recipe":"Ration"}""",
            """{"action":"use","item":"meat; drop table"}""",
            """{"action":"use","item":7}""",
            """{"action":"detonate"}""",
            """{"action":"blast"}""",
            """{"action":"blast","radius":0}""",
            $$"""{"action":"blast","radius":{{PlayerAction.MaximumBlastRadius + 1}}}""",
            $$"""{"action":"plate","width":{{PlayerAction.MaximumStampSide + 1}},"depth":1}""",
            $$"""{"action":"wall","length":2,"height":{{PlayerAction.MaximumWallHeight + 1}}}""",
            """{"action":"blast","radius":1.5}""",
            """{"radius":2}""",
            """["blast"]""",
            """{"action":"plate","width":1,"depth":1,"material":"bedrock"}""",
            """{"action":"wall","length":1,"height":1,"material":"water"}""",
            """{"action":"plate","width":1,"depth":1,"material":3}""",
        ];
        foreach (string payload in refused)
        {
            Check.Throws<FormatException>(() => Parse(payload), $"{payload} must be refused");
        }

        Check.Throws<JsonException>(() => Parse("{not json"), "a payload that is not JSON must be refused");
    }

    private static PlayerAction Parse(string json) => PlayerAction.Parse(Encoding.UTF8.GetBytes(json));
}
