using System.Globalization;
using System.Text.Json;

namespace CraftSurvive.Game.Modules.Options;

/// <summary>One of the game's own options: its id, how it reads, its range in whole steps, and what it does.</summary>
internal sealed record GameOptionSpec(string Id, string Label, string Description, int Minimum, int Maximum, int Step, string Unit, int Default);

/// <summary>
/// The game's own video options (#9759), beside the Engine's renderer settings in the options panel:
/// how far the land is drawn, the field of view, and how strong weather effects are on the screen.
/// They are the player's for the install, not a world's.
/// </summary>
internal readonly record struct GameOptions(int ViewDistanceChunks, int FieldOfViewDegrees, int WeatherEffectsPercent)
{
    internal const string ViewDistance = "viewdistance", FieldOfView = "fieldofview", WeatherEffects = "weathereffects";

    /// <summary>The far field's reach in its chunk columns (128 m each): twelve, a mile and a half, is the game's own.</summary>
    internal static GameOptionSpec ViewDistanceSpec { get; } = new(ViewDistance, "View distance",
        "How far the land is drawn beyond the near ground, in 128 m steps. The haze closes in to match a shorter reach.",
        Minimum: 6, Maximum: 16, Step: 1, Unit: "× 128 m", Default: 12);

    internal static GameOptionSpec FieldOfViewSpec { get; } = new(FieldOfView, "Field of view",
        "The vertical field of view in first person.", Minimum: 55, Maximum: 100, Step: 1, Unit: "°", Default: 70);

    internal static GameOptionSpec WeatherEffectsSpec { get; } = new(WeatherEffects, "Weather effects",
        "How much rain, snow, dust and glass falls on screen, and how strongly the glass storm ripples the view. Weather still acts on the game.",
        Minimum: 0, Maximum: 100, Step: 5, Unit: "%", Default: 100);

    internal static IReadOnlyList<GameOptionSpec> Specs { get; } = [ViewDistanceSpec, FieldOfViewSpec, WeatherEffectsSpec];

    internal static GameOptions Defaults { get; } = new(ViewDistanceSpec.Default, FieldOfViewSpec.Default, WeatherEffectsSpec.Default);

    internal int ValueOf(string id) => id switch
    {
        ViewDistance => ViewDistanceChunks,
        FieldOfView => FieldOfViewDegrees,
        WeatherEffects => WeatherEffectsPercent,
        _ => throw new FormatException($"\"{id}\" is not one of the game's options."),
    };

    /// <summary>The options with one changed. A value outside its range is refused, not clamped: the panel offers only the range.</summary>
    internal GameOptions With(string id, int value)
    {
        GameOptionSpec spec = Specs.FirstOrDefault(spec => spec.Id == id) ?? throw new FormatException($"\"{id}\" is not one of the game's options.");
        if (value < spec.Minimum || value > spec.Maximum) throw new FormatException($"{spec.Label} must be {spec.Minimum} to {spec.Maximum}, not {value}.");
        return id switch
        {
            ViewDistance => this with { ViewDistanceChunks = value },
            FieldOfView => this with { FieldOfViewDegrees = value },
            _ => this with { WeatherEffectsPercent = value },
        };
    }

    /// <summary>The options as the UI draws them: one entry per option, <c>id|label|min|max|step|unit|value|description</c>.</summary>
    internal string Describe()
    {
        GameOptions chosen = this;
        return string.Join(';', Specs.Select(spec => string.Join('|',
        spec.Id, spec.Label, spec.Minimum.ToString(CultureInfo.InvariantCulture), spec.Maximum.ToString(CultureInfo.InvariantCulture),
        spec.Step.ToString(CultureInfo.InvariantCulture), spec.Unit, chosen.ValueOf(spec.Id).ToString(CultureInfo.InvariantCulture), spec.Description)));
    }

    /// <summary>The stored form: a small JSON object of the options by id.</summary>
    internal byte[] Encode()
    {
        using MemoryStream bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();
            foreach (GameOptionSpec spec in Specs) writer.WriteNumber(spec.Id, ValueOf(spec.Id));
            writer.WriteEndObject();
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// Reads the stored form. An option missing, out of range or unreadable keeps its default, so a
    /// newer or older game's file never stops this one starting.
    /// </summary>
    internal static GameOptions Decode(ReadOnlySpan<byte> stored)
    {
        GameOptions options = Defaults;
        using JsonDocument document = JsonDocument.Parse(stored.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object) return options;
        foreach (GameOptionSpec spec in Specs)
        {
            if (document.RootElement.TryGetProperty(spec.Id, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int chosen)
                && chosen >= spec.Minimum && chosen <= spec.Maximum)
            {
                options = options.With(spec.Id, chosen);
            }
        }

        return options;
    }
}
