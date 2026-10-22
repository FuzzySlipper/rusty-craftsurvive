using System.Text.Json;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>
/// How the world feeds the chemistry field and what the field may write back (#10293), from
/// <c>content/game/chemistry/adapter.json</c>. Read once; a missing or misspelt entry fails at load.
/// </summary>
internal sealed record ChemistrySettings(
    string PackPath,
    IReadOnlyDictionary<BlockId, string> Blocks,
    string DefaultMaterial,
    string WaterMaterial,
    IReadOnlyList<WriteBackSetting> WriteBack,
    WriteBackBudgetSetting Budget,
    FieldSetting Field,
    AmbientSetting Ambient,
    FireSetting Fires)
{
    internal const string ContentPath = "chemistry/adapter.json";

    internal static ChemistrySettings Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        JsonElement root = document.RootElement;
        JsonElement blocks = Member(root, "blocks");
        Dictionary<BlockId, string> named = [];
        string fallback = "";
        foreach (JsonProperty entry in blocks.EnumerateObject())
        {
            string material = Text(entry.Value, $"blocks.{entry.Name}");
            if (entry.Name == "default")
            {
                fallback = material;
            }
            else
            {
                named[Block(entry.Name, $"blocks.{entry.Name}")] = material;
            }
        }

        if (fallback.Length == 0)
        {
            throw new InvalidDataException($"{ContentPath}: blocks needs a \"default\" material");
        }

        JsonElement budget = Member(root, "budget"), field = Member(root, "field"), ambient = Member(root, "ambient"), fires = Member(root, "fires");
        return new ChemistrySettings(
            Text(Member(root, "pack"), "pack"),
            named,
            fallback,
            Text(Member(root, "water"), "water"),
            [.. Member(root, "writeBack").EnumerateArray().Select((rule, index) => new WriteBackSetting(
                Text(Member(rule, "material"), $"writeBack[{index}].material"),
                Block(Text(Member(rule, "block"), $"writeBack[{index}].block"), $"writeBack[{index}].block"),
                [.. Member(rule, "over").EnumerateArray().Select(over => Block(Text(over, $"writeBack[{index}].over"), $"writeBack[{index}].over"))]))],
            new WriteBackBudgetSetting(Whole(budget, "keepFreeEntries"), Whole(budget, "cellsPerWrite"), Number(budget, "writeSeconds")),
            new FieldSetting(Whole(field, "reachChunks"), Whole(field, "burningReachChunks"), Whole(field, "loadsPerUpdate"),
                Number(field, "stepSeconds"), Whole(field, "stepsPerUpdate")),
            new AmbientSetting(Number(ambient, "warmHeat"), Number(ambient, "coldHeat"), Number(ambient, "windCellsPerSecond"),
                Number(ambient, "refreshSeconds")),
            new FireSetting([.. Member(fires, "materials").EnumerateArray().Select(material => Text(material, "fires.materials"))],
                Whole(fires, "slots"), Whole(fires, "clusterCells")));
    }

    private static JsonElement Member(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value)
        ? value
        : throw new InvalidDataException($"{ContentPath}: missing \"{name}\"");

    private static string Text(JsonElement element, string path) => element.ValueKind == JsonValueKind.String
        ? element.GetString()!
        : throw new InvalidDataException($"{ContentPath}: {path} must be text");

    private static float Number(JsonElement element, string name) => Member(element, name) is { ValueKind: JsonValueKind.Number } value
        ? value.GetSingle()
        : throw new InvalidDataException($"{ContentPath}: {name} must be a number");

    private static int Whole(JsonElement element, string name) => Member(element, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out int whole) && whole >= 0
        ? whole
        : throw new InvalidDataException($"{ContentPath}: {name} must be a whole number, not negative");

    private static BlockId Block(string name, string path) => Enum.TryParse(name, ignoreCase: false, out BlockId block) && Enum.IsDefined(block)
        ? block
        : throw new InvalidDataException($"{ContentPath}: {path}: no block named '{name}'");
}

/// <summary>A cell that becomes <see cref="Material"/> is written as <see cref="Block"/> where the world still holds one of <see cref="Over"/>.</summary>
internal sealed record WriteBackSetting(string Material, BlockId Block, IReadOnlyList<BlockId> Over);

/// <summary>
/// How many terrain overlay entries chemistry leaves free for the player's own edits (it writes only while more
/// are free), and how many cells it writes at once, every so many seconds. The overlay keeps its entries for
/// good, so this bound holds across sessions without a count of its own.
/// </summary>
internal sealed record WriteBackBudgetSetting(int KeepFreeEntries, int CellsPerWrite, float WriteSeconds);

/// <summary>
/// The field's reach in chunk columns around the player's, the further reach a burning chunk keeps, how many
/// chunks load in one update, and the field step and how many may run in one update.
/// </summary>
internal sealed record FieldSetting(int ReachChunks, int BurningReachChunks, int LoadsPerUpdate, float StepSeconds, int StepsPerUpdate);

/// <summary>The weather as ambient: air heat from warm to bitter by the Cold channel, wind speed at full Wind, how often it is read.</summary>
internal sealed record AmbientSetting(float WarmHeat, float ColdHeat, float WindCellsPerSecond, float RefreshSeconds);

/// <summary>Which materials show as fire, how many fires the field may show, and the cells across one shown fire stands for.</summary>
internal sealed record FireSetting(IReadOnlyList<string> Materials, int Slots, int ClusterCells);
