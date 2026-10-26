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
    FireSetting Fires,
    ScarSetting Scars)
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

        JsonElement budget = Member(root, "budget"), field = Member(root, "field"), ambient = Member(root, "ambient"), fires = Member(root, "fires"),
            scars = Member(root, "scars");
        float earliest = Number(scars, "earliest");
        if (earliest is < 0 or > 1)
        {
            throw new InvalidDataException($"{ContentPath}: scars.earliest must be a share from 0 to 1");
        }

        return new ChemistrySettings(
            Text(Member(root, "pack"), "pack"),
            named,
            fallback,
            Text(Member(root, "water"), "water"),
            [.. Member(root, "writeBack").EnumerateArray().Select((rule, index) => new WriteBackSetting(
                Text(Member(rule, "material"), $"writeBack[{index}].material"),
                Block(Text(Member(rule, "block"), $"writeBack[{index}].block"), $"writeBack[{index}].block"),
                [.. Member(rule, "over").EnumerateArray().Select(over => Block(Text(over, $"writeBack[{index}].over"), $"writeBack[{index}].over"))],
                [.. Member(rule, "beneath").EnumerateArray().Select(under => Block(Text(under, $"writeBack[{index}].beneath"), $"writeBack[{index}].beneath"))]))],
            new WriteBackBudgetSetting(Whole(budget, "keepFreeEntries"), Whole(budget, "cellsPerWrite"), Number(budget, "writeSeconds")),
            new FieldSetting(Whole(field, "reachChunks"), Whole(field, "burningReachChunks"), Whole(field, "loadsPerUpdate"),
                Number(field, "stepSeconds"), Whole(field, "stepsPerUpdate")),
            new AmbientSetting(Number(ambient, "warmHeat"), Number(ambient, "coldHeat"), Number(ambient, "windCellsPerSecond"),
                Number(ambient, "refreshSeconds")),
            new FireSetting([.. Member(fires, "materials").EnumerateArray().Select(material => Text(material, "fires.materials"))],
                Whole(fires, "slots"), Whole(fires, "clusterCells"), Text(Member(fires, "burnt"), "fires.burnt")),
            new ScarSetting(Block(Text(Member(scars, "block"), "scars.block"), "scars.block"), Number(scars, "regrowDays"), earliest,
                Number(scars, "healSeconds"), Whole(scars, "cellsPerHeal"), Number(scars, "healReach")));
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

/// <summary>
/// A cell that becomes <see cref="Material"/> is written as <see cref="Block"/> where the world still holds one of
/// <see cref="Over"/>, and so is the cell under it where that holds one of <see cref="Beneath"/>: the smooth ground takes
/// its look from the cells about each vertex, so a surface written one cell deep would half show the ground beneath it.
/// </summary>
internal sealed record WriteBackSetting(string Material, BlockId Block, IReadOnlyList<BlockId> Over, IReadOnlyList<BlockId> Beneath);

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

/// <summary>
/// Which materials show as fire, how many squares of it are lit and smoke, the cells across one square, and the material a
/// burnt-out cell becomes (its ground shown as char).
/// </summary>
internal sealed record FireSetting(IReadOnlyList<string> Materials, int Slots, int ClusterCells, string Burnt);

/// <summary>
/// The scar a grass fire leaves (#10296): the block burnt ground is written as, the days it takes to grow back (each cell
/// due from the earliest share of them), how often due cells near the player are grown back, how many at once, and how
/// near (metres).
/// </summary>
internal sealed record ScarSetting(BlockId Block, float RegrowDays, float Earliest, float HealSeconds, int CellsPerHeal, float HealReach);
