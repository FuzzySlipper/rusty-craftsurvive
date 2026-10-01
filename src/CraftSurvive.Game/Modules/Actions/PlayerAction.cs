using System.Text.Json;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Actions;

/// <summary>What the player asked for from the UI. Every kind acts where the player is aiming.</summary>
internal enum PlayerActionKind
{
    Blast,
    Plate,
    Wall,
    Door,
    Light,
    Container,
    Undo,
    Craft,
    Use,
    Rest,
    Difficulty,
    Enter,
    Leave,
}

/// <summary>
/// One request from the player-facing UI, decoded from the product payload it arrives in. A blast
/// breaks the aimed block; everything else is placed on the aimed block's open face. A floor or wall
/// carries the <see cref="BuildPalette"/> block it is built from.
/// </summary>
internal readonly record struct PlayerAction(PlayerActionKind Kind, int Size1 = 0, int Size2 = 0, BlockId Material = BlockId.Air, string Name = "")
{
    /// <summary>The intent the UI claims, as declared in the product project.</summary>
    internal const string Intent = "craftsurvive.ui";

    /// <summary>The payload contract of the claim.</summary>
    internal const string Contract = "craftsurvive.ui.action.v1";

    internal const int MinimumBlastRadius = 1;
    internal const int MaximumBlastRadius = 3;
    internal const int MaximumStampSide = 8;
    internal const int MaximumWallHeight = 4;

    /// <summary>A charge's radius.</summary>
    internal int Radius => Size1;

    /// <summary>
    /// Decodes a payload such as <c>{"action":"blast","radius":2}</c> or
    /// <c>{"action":"plate","width":3,"depth":3}</c>. The payload is untrusted browser input, so an
    /// unknown action or a size out of range is refused with <see cref="FormatException"/>.
    /// </summary>
    internal static PlayerAction Parse(ReadOnlySpan<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload.ToArray());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out JsonElement action)
            || action.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("A UI action names what it asks for in \"action\".");
        }

        return action.GetString() switch
        {
            "blast" => new(PlayerActionKind.Blast, Size(root, "radius", MinimumBlastRadius, MaximumBlastRadius)),
            "plate" => new(PlayerActionKind.Plate, Size(root, "width", 1, MaximumStampSide), Size(root, "depth", 1, MaximumStampSide), NamedMaterial(root)),
            "wall" => new(PlayerActionKind.Wall, Size(root, "length", 1, MaximumStampSide), Size(root, "height", 1, MaximumWallHeight), NamedMaterial(root)),
            "door" => new(PlayerActionKind.Door),
            "light" => new(PlayerActionKind.Light),
            "container" => new(PlayerActionKind.Container),
            "undo" => new(PlayerActionKind.Undo),
            "craft" => new(PlayerActionKind.Craft, Name: Named(root, "recipe")),
            "use" => new(PlayerActionKind.Use, Name: Named(root, "item")),
            "rest" => new(PlayerActionKind.Rest),
            "enter" => new(PlayerActionKind.Enter),
            "leave" => new(PlayerActionKind.Leave),
            "difficulty" => new(PlayerActionKind.Difficulty, Name: Named(root, "level")),
            string other => throw new FormatException($"\"{other}\" is not an action the UI can ask for."),
            null => throw new FormatException("A UI action names what it asks for in \"action\"."),
        };
    }

    /// <summary>The palette block a build names in "material", or the palette's default when it names none.</summary>
    private static BlockId NamedMaterial(JsonElement root)
    {
        if (!root.TryGetProperty("material", out JsonElement value))
        {
            return BuildPalette.Default;
        }

        return value.ValueKind == JsonValueKind.String && BuildPalette.TryFind(value.GetString()!, out BlockId block)
            ? block
            : throw new FormatException($"\"material\" must be one of {BuildPalette.Names}.");
    }

    /// <summary>A short identifier the request names; the owner it reaches decides whether it exists.</summary>
    private static string Named(JsonElement root, string name)
    {
        const int MaximumNameLength = 32;
        return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 and <= MaximumNameLength } text && text.All(char.IsAsciiLetterLower)
            ? text
            : throw new FormatException($"A UI action of this kind needs a short lower-case \"{name}\".");
    }

    private static int Size(JsonElement root, string name, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int size))
        {
            throw new FormatException($"A UI action of this kind needs a whole-number \"{name}\".");
        }

        return size >= minimum && size <= maximum
            ? size
            : throw new FormatException($"\"{name}\" must be {minimum} to {maximum}, not {size}.");
    }
}
