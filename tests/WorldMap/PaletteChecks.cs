using System.Text.Json;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;
using Rusty.Engine;

/// <summary>
/// The map's and the horizon's colours are steps of the content palette (#9813): each environment, rock and
/// river colour is exactly its ramp's step in content/style/palette.json, so a palette edit shows where it lands.
/// </summary>
internal static class PaletteChecks
{
    internal static void Run()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "content", "style", "palette.json"))) directory = Path.GetDirectoryName(directory);
        Check.That(directory is not null, "the content palette is found from the lane");
        if (directory is null) return;
        using JsonDocument palette = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "content", "style", "palette.json")));
        JsonElement ramps = palette.RootElement.GetProperty("ramps");
        Color Step((string Ramp, int Step) source) => MapPalette.Hex(Convert.ToInt32(ramps.GetProperty(source.Ramp)[source.Step].GetString()!.TrimStart('#'), 16));
        static bool Same(Color a, Color b) => Math.Abs(a.R - b.R) < 1e-6 && Math.Abs(a.G - b.G) < 1e-6 && Math.Abs(a.B - b.B) < 1e-6;

        List<string> off = [];
        foreach (MapBiome biome in Enum.GetValues<MapBiome>())
            if (!Same(MapPalette.For(biome), Step(MapPalette.Source(biome)))) off.Add(biome.ToString());
        if (!Same(MapPalette.Stone, Step(MapPalette.StoneSource))) off.Add("stone");
        if (!Same(MapPalette.River, Step(MapPalette.RiverSource))) off.Add("river");
        Check.That(off.Count == 0, $"every map colour is its palette step ({(off.Count == 0 ? "all" : "off: " + string.Join(", ", off))})");
    }
}
