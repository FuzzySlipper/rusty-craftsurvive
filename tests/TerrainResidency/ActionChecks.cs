using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Actions;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Inventory;

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

        foreach (string name in new[] { "canyonfull", "uplandsfull", "tundrafull" })
            Check.Equal(new PlayerAction(PlayerActionKind.Landscape, Name: name),
                Parse($$"""{"action":"landscape","name":"{{name}}"}"""), "loaded study IDs pass the UI action contract");

        Check.Equal(new PlayerAction(PlayerActionKind.Craft, Name: "ration"), Parse("""{"action":"craft","recipe":"ration"}"""), "a craft names its recipe");
        Check.Equal(new PlayerAction(PlayerActionKind.Enter), Parse("""{"action":"enter"}"""), "entering a dungeon names nothing");
        Check.Equal(new PlayerAction(PlayerActionKind.Leave), Parse("""{"action":"leave"}"""), "leaving a dungeon names nothing");
        Check.Equal(new PlayerAction(PlayerActionKind.Rest), Parse("""{"action":"rest"}"""), "a rest names nothing");
        Check.Equal(new PlayerAction(PlayerActionKind.Difficulty, Name: "harsh"), Parse("""{"action":"difficulty","level":"harsh"}"""), "a difficulty names its level");
        Check.Equal(new PlayerAction(PlayerActionKind.Use, Name: "bandage"), Parse("""{"action":"use","item":"bandage"}"""), "a use names its item");
        Check.Equal(12, Parse("""{"action":"use","item":"meat","slot":12}""").Slot, "a use may name the slot it takes from");
        Check.Equal(-1, Parse("""{"action":"use","item":"meat"}""").Slot, "a use that names no slot takes from any");
        Check.Equal(new PlayerAction(PlayerActionKind.Move, 3, 20, Count: 5), Parse("""{"action":"move","from":3,"to":20,"count":5}"""), "a move names its slots and count");
        Check.Equal(0, Parse("""{"action":"move","from":3,"to":20}""").Count, "a move that names no count takes the whole stack");
        Check.Equal(new PlayerAction(PlayerActionKind.Select, 4), Parse("""{"action":"select","slot":4}"""), "a selection names its hotbar slot");

        string[] refused =
        [
            """{"action":"craft"}""",
            """{"action":"craft","recipe":"Ration"}""",
            """{"action":"use","item":"meat; drop table"}""",
            """{"action":"use","item":7}""",
            $$"""{"action":"use","item":"meat","slot":{{InventorySlots.Count}}}""",
            """{"action":"move","from":3}""",
            $$"""{"action":"move","from":0,"to":{{InventorySlots.Count}}}""",
            """{"action":"move","from":0,"to":1,"count":100}""",
            """{"action":"move","from":-1,"to":1}""",
            $$"""{"action":"select","slot":{{InventorySlots.HotbarSlots}}}""",
            """{"action":"select"}""",
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
