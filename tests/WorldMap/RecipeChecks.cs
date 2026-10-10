using System.Text.Json;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The generation recipe (#9814): its defaults are the generator's own, so a world made with them is
/// the world its seed always made; a recipe file names only what it changes; and a change shows.
/// </summary>
internal static class RecipeChecks
{
    /// <summary>A small regional world, so the three generations here take moments.</summary>
    private const int Size = 4096;
    private const ulong Seed = 9814;

    internal static void DefaultsAreTheGeneratorsOwn()
    {
        TerrainConfiguration configuration = new(Seed, Size);
        ulong plain = new WorldMap(configuration, MapSimulation.Run(configuration)).Fingerprint;
        ulong recipe = new WorldMap(configuration, MapSimulation.Run(configuration, MapRecipe.Default)).Fingerprint;
        Check.That(plain == recipe, "the default recipe makes the world the seed always made");

        JsonSerializerOptions json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        MapRecipe? roundTrip = JsonSerializer.Deserialize<MapRecipe>(JsonSerializer.Serialize(MapRecipe.Default, json), json);
        Check.That(roundTrip == MapRecipe.Default, "the default recipe survives a JSON round trip");

        MapRecipe? partial = JsonSerializer.Deserialize<MapRecipe>("""{ "Relief": { "BeltUplift": 3 }, "Simulation": { "PeakElevation": 300 } }""", json);
        Check.That(partial is not null && partial.Relief == MapRecipe.Default.Relief with { BeltUplift = 3 }
            && partial.Simulation == MapRecipe.Default.Simulation with { PeakElevation = 300 },
            "a recipe file changes only the fields it names");

        ulong taller = new WorldMap(configuration, MapSimulation.Run(configuration, partial!)).Fingerprint;
        Check.That(taller != plain, "a changed recipe makes a different world");
    }
}
