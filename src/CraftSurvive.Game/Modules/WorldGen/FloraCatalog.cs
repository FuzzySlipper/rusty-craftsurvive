using System.Reflection;
using System.Text.Json;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>What a species is in the world: a tree, a shrub, a ground plant, or deadwood (a fallen log, a stump: #10034).</summary>
internal enum FloraRole : byte
{
    Tree,
    Shrub,
    Ground,
    Deadwood,
}

/// <summary>
/// Where a species grows relative to the forest (#10032): in the closed canopy, in open country (wood
/// pasture, scrub, steppe), under the canopy, or at its edges and in its clearings.
/// </summary>
internal enum FloraHabit : byte
{
    Canopy,
    Open,
    Understory,
    Edge,
}

/// <summary>
/// One climate axis of a species' range (#10032): it grows from <see cref="Minimum"/> to
/// <see cref="Maximum"/>, best at <see cref="Optimum"/>, on the map's 0 to 1 scale.
/// </summary>
internal readonly record struct FloraRange(double Minimum, double Optimum, double Maximum)
{
    /// <summary>How well a value suits the species: 1 at the optimum, falling smoothly to 0 at either end.</summary>
    internal double Suitability(double value)
    {
        if (value <= Minimum || value >= Maximum) return 0;
        double t = value < Optimum ? (value - Minimum) / (Optimum - Minimum) : (Maximum - value) / (Maximum - Optimum);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>
/// A species' ecology (#10032): its climate range, where it grows relative to the forest, how common it
/// is, how bare and rocky a place it bears, and how much it seeks the water's edge.
/// </summary>
/// <param name="Density">For a plant (#10034): how many grow per square metre where the climate and canopy suit it fully.</param>
internal sealed record FloraEcology(FloraRange Temperature, FloraRange Moisture, FloraHabit Habit, double Abundance,
    double RockTolerance, double Riparian, double Density)
{
    /// <summary>How well a place suits the species, from its map sample: its climate, scaled by how common it is.</summary>
    internal double Suitability(MapSample sample) => Fit(sample) * Abundance;

    /// <summary>How well a place's climate and rock suit the species, 0 to 1, before how common it is.</summary>
    internal double Fit(MapSample sample) => sample.Rock > RockTolerance
        ? 0
        : Temperature.Suitability(sample.Temperature) * Moisture.Suitability(sample.Moisture);
}

/// <summary>A species from the flora catalog (#10032): its id in catalog order, role, height, meshes and ecology.</summary>
/// <param name="Variants">Mesh names of the species' variants (each tree's far variant is NAME-far).</param>
/// <param name="Folder">The models subfolder its meshes are in.</param>
internal sealed record FloraSpecies(ushort Id, string Name, FloraRole Role, double Height, IReadOnlyList<string> Variants,
    string Folder, bool Fantastical, FloraEcology? Ecology);

/// <summary>
/// The flora catalog (#10032): every tree and plant the world grows. scripts/generate-flora.py builds the
/// species' meshes and textures from <c>content/game/flora/catalog.json</c>; the same file ships inside the
/// generator, because which species stands where is part of generation, as continent designs are.
/// </summary>
internal sealed class FloraCatalog
{
    private const string ResourceName = "WorldGen.Flora.catalog.json";

    private static readonly Lazy<FloraCatalog> SharedCatalog = new(() =>
    {
        Assembly assembly = typeof(FloraCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The flora catalog ({ResourceName}) is not embedded.");
        return Parse(new StreamReader(stream).ReadToEnd());
    });

    private readonly Dictionary<string, FloraSpecies> byName;

    private FloraCatalog(IReadOnlyList<FloraSpecies> species, ulong fingerprint)
    {
        Species = species;
        Fingerprint = fingerprint;
        byName = species.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    /// <summary>The catalog the generator ships with.</summary>
    internal static FloraCatalog Shared => SharedCatalog.Value;

    /// <summary>Every species, in catalog order: a species' id is its index.</summary>
    internal IReadOnlyList<FloraSpecies> Species { get; }

    /// <summary>
    /// A hash of the catalog's trees, for the generation fingerprint: they decide trunk cores and the forest floor,
    /// so a changed tree is a changed world. Plants and deadwood are presentation only (#10034).
    /// </summary>
    internal ulong Fingerprint { get; }

    internal FloraSpecies this[ushort id] => Species[id];

    internal FloraSpecies Named(string name) =>
        byName.TryGetValue(name, out FloraSpecies? species) ? species : throw new KeyNotFoundException($"No species '{name}' in the flora catalog.");

    internal static FloraCatalog Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        List<FloraSpecies> species = [];
        System.Text.StringBuilder generated = new();
        foreach (JsonProperty entry in document.RootElement.GetProperty("species").EnumerateObject())
        {
            JsonElement resolved = Resolve(document.RootElement.GetProperty("species"), entry.Name);
            if (String(resolved, "role", entry.Name).Equals(nameof(FloraRole.Tree), StringComparison.OrdinalIgnoreCase))
                generated.Append(entry.Name).Append('=').Append(resolved.GetRawText()).Append('\n');
            FloraRole role = Enum.Parse<FloraRole>(String(resolved, "role", entry.Name), ignoreCase: true);
            List<string> variants = [];
            foreach (JsonElement variant in resolved.GetProperty("variants").EnumerateArray())
            {
                int seed = variant.GetProperty("seed").GetInt32();
                variants.Add(variant.TryGetProperty("name", out JsonElement named) ? named.GetString()!
                    : role == FloraRole.Tree ? $"{entry.Name}-p{seed}" : $"{entry.Name}-{seed}");
            }

            if (variants.Count == 0) throw new InvalidDataException($"Species '{entry.Name}' has no variants.");
            species.Add(new FloraSpecies((ushort)species.Count, entry.Name, role, resolved.GetProperty("height").GetDouble(), variants,
                resolved.TryGetProperty("folder", out JsonElement folder) ? folder.GetString()! : "trees",
                resolved.TryGetProperty("fantastical", out JsonElement fantastical) && fantastical.GetBoolean(),
                resolved.TryGetProperty("ecology", out JsonElement ecology) ? Ecology(ecology, entry.Name) : null));
        }

        return new FloraCatalog(species, Hash(generated.ToString()));
    }

    /// <summary>A species' entry with what it inherits (<c>like</c>) beneath it; the generator merges forms, and only whole fields matter here.</summary>
    private static JsonElement Resolve(JsonElement all, string name)
    {
        JsonElement entry = all.GetProperty(name);
        if (!entry.TryGetProperty("like", out JsonElement like)) return entry;
        JsonElement parent = Resolve(all, like.GetString()!);
        Dictionary<string, JsonElement> fields = parent.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        foreach (JsonProperty field in entry.EnumerateObject()) fields[field.Name] = field.Value;
        return JsonSerializer.SerializeToElement(fields);
    }

    private static string String(JsonElement element, string property, string species) =>
        element.TryGetProperty(property, out JsonElement value) ? value.GetString()!
            : throw new InvalidDataException($"Species '{species}' has no {property}.");

    private static FloraEcology Ecology(JsonElement ecology, string species)
    {
        static FloraRange Range(JsonElement element, string name, string species)
        {
            double[] values = element.GetProperty(name).EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (values.Length != 3 || !(values[0] < values[1] && values[1] < values[2]))
                throw new InvalidDataException($"Species '{species}' {name} must be [minimum, optimum, maximum], rising.");
            return new FloraRange(values[0], values[1], values[2]);
        }

        return new FloraEcology(Range(ecology, "temperature", species), Range(ecology, "moisture", species),
            Enum.Parse<FloraHabit>(ecology.GetProperty("habit").GetString()!, ignoreCase: true),
            ecology.TryGetProperty("abundance", out JsonElement abundance) ? abundance.GetDouble() : 1,
            ecology.TryGetProperty("rock", out JsonElement rock) ? rock.GetDouble() : DefaultRockTolerance,
            ecology.TryGetProperty("riparian", out JsonElement riparian) ? riparian.GetDouble() : 0,
            ecology.TryGetProperty("density", out JsonElement density) ? density.GetDouble() : 0);
    }

    /// <summary>Most species keep off bare rock; those that bear it (juniper, mountain pine) say so.</summary>
    private const double DefaultRockTolerance = 0.6;

    private static ulong Hash(string text)
    {
        // FNV-1a over the UTF-8 text: stable across runs and platforms, unlike string.GetHashCode.
        const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;
        ulong hash = Offset;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text)) hash = (hash ^ b) * Prime;
        return hash;
    }
}
