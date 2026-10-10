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

    /// <summary>
    /// The continent design (#9815): the built-in frontier peninsula parses and validates; its fixed zones
    /// override the coast; the coast band is decided by noise but fixed per seed; and a belt's gain lifts it.
    /// </summary>
    internal static void DesignsDrawTheContinent()
    {
        ContinentDesign design = ContinentDesign.Builtin("frontier-peninsula");
        Check.That(design.Land.Count(l => l.Forbidden) == 1 && design.Belts.Count > 0 && design.Neck.Count >= 3,
            "the frontier peninsula has a forbidden mainland, belts and a neck");
        Check.That(MapRelief.DesignedLand(design, Seed, 0.4, -0.74, design.CoastBand / 2), "the neck's core is land whatever the noise");
        Check.That(!MapRelief.DesignedLand(design, Seed, -0.5, -0.9, design.CoastBand / 2), "the western gulf is sea whatever the noise");
        Check.That(MapRelief.DesignedLand(design, Seed, 0, 0, design.CoastBand / 2), "deep inside the outline is land");
        Check.That(!MapRelief.DesignedLand(design, Seed, -0.99, 0.99, design.CoastBand / 2), "far outside it is sea");

        // Along the west coast's band, the noise decides: some land, some sea, the same each time for a seed.
        bool[] coast = [.. Enumerable.Range(0, 200).Select(k => MapRelief.DesignedLand(design, Seed, -0.84, -0.4 + (k * 0.004), design.CoastBand / 2))];
        bool[] again = [.. Enumerable.Range(0, 200).Select(k => MapRelief.DesignedLand(design, Seed, -0.84, -0.4 + (k * 0.004), design.CoastBand / 2))];
        Check.That(coast.Contains(true) && coast.Contains(false), "on the drawn outline the coast is undecided: noise makes both bays and headlands");
        Check.That(coast.SequenceEqual(again), "and decides it the same way every time");

        double[] doubled = [.. design.Belts.Select(_ => 1.0)];
        doubled[0] = 2;
        double[] onBelt = design.Belts[0].Points[1];
        Check.That(Math.Abs(design.Crest(onBelt[0], onBelt[1], doubled) - (2 * design.Crest(onBelt[0], onBelt[1]))) < 1e-6,
            "a belt's calibration gain scales its crest");
        Check.That(ContinentDesign.Parse(System.Text.Json.JsonSerializer.Serialize(design, ContinentDesign.Json)).Belts.Count == design.Belts.Count,
            "a design survives a JSON round trip");

        // Areas (#9815 review): a height makes a flat-topped area calibrated like a belt; a lift only scales the plain.
        ContinentDesign massif = design with
        {
            Areas = [.. design.Areas, new DesignArea { Name = "massif", At = [-0.55, -0.35], Radius = 0.2, Height = 5000, Ruggedness = 0.8 }],
        };
        DesignArea added = massif.Areas[^1];
        double[] areaGains = [.. massif.Areas.Select(_ => 1.0)];
        Check.That(added.Weight(-0.55 + 0.1, -0.35) > 0.9 && added.Weight(-0.55 + 0.3, -0.35) < 0.05,
            "a height area is whole across most of its radius and gone past it");
        Check.That(massif.AreaLift(-0.55, -0.35) == design.AreaLift(-0.55, -0.35), "a height area adds no plain lift");
        double flat = massif.AreaUplift(-0.55, -0.35, areaGains, massif.HighestAsked, 1);
        double broken = massif.AreaUplift(-0.55, -0.35, areaGains, massif.HighestAsked, 0);
        Check.That(flat > 0 && broken < flat * 0.25, "a rugged area's uplift follows the ridged noise");
        areaGains[^1] = 2;
        Check.That(Math.Abs(massif.AreaUplift(-0.55, -0.35, areaGains, massif.HighestAsked, 1) - (2 * flat)) < 1e-9, "an area's calibration gain scales its uplift");
        Check.That(massif.Ruggedness(-0.55, -0.35) > 0.75 && massif.HighestAsked == design.HighestAsked, "rugged areas harden the rock; the highest asked is still the Wall's");
    }
}
