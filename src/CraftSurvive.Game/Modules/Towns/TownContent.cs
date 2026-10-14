using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>A kit piece's hanging sign: its own mesh, swung about its suspension point.</summary>
internal sealed record KitSign(string Mesh, string[] Materials, Vector3 Pivot, Vector3 Axis);

/// <summary>
/// One placeable piece of the town kit (art/towns/export_kit.py), in its own frame: glTF Y-up, front
/// facing +Z. A building also has an entrance, a selection box and a sign.
/// </summary>
internal sealed record KitPiece(string Key, string Kind, string Mesh, string[] Materials, Vector3 Min, Vector3 Max,
    Vector3[] Lamps, Vector3[] Chimneys, Vector3? Entrance = null, Vector3 SelectionCentre = default, Vector3 SelectionSize = default,
    KitSign? Sign = null);

/// <summary>A composed camera: where the eye stands and looks, in a town's frame, and its vertical field of view.</summary>
internal sealed record TownView(Vector3 Eye, Vector3 Focus, double FieldOfView);

/// <summary>A piece placed in a layout; a service piece is a destination the traveller can enter.</summary>
internal sealed record LayoutPiece(string Piece, Vector3 At, float Yaw, string? Service, TownView? Close);

/// <summary>A layout's landmark slot and the pieces a town may stand there.</summary>
internal sealed record LandmarkSlot(Vector3 At, float Yaw, string[] Choices);

/// <summary>A paved way: its centre line in the town's frame (x, z) and its width.</summary>
internal sealed record TownPath(Vector2[] Points, float Width);

/// <summary>Props scattered near some anchors: how many, how far about each, and their size range.</summary>
internal sealed record ScatterRule(string Prop, int Count, Vector2[] Around, Vector2 Spread, Vector2 Scale);

/// <summary>
/// An authored arrangement in a town's frame (<see cref="TownGround"/>: +X toward the water, the
/// arrival view on +Z), for the kinds of settlement it suits. The traveller stands at its arrival
/// point (x, z) on land; the composed camera's eye may stand out over the water.
/// </summary>
internal sealed record TownLayout(string Key, SettlementKind[] For, TownView View, Vector2 Arrival, LayoutPiece[] Pieces, LandmarkSlot Landmark,
    Vector2[] Paving, TownPath Path, ScatterRule[] Scatter);

/// <summary>Text that holds under some conditions (all of its tags), or always when it names none.</summary>
internal sealed record TownLine(string[] When, string Text);

/// <summary>One thing the traveller can ask for at a service: what it does and what is said.</summary>
internal sealed record ServiceChoice(string Key, string Action, string[] When, TownLine[] Label, TownLine[] Reply, string Detail,
    string Spent, string Full, double Nourishment, double Hours, string Item, int Allotment);

/// <summary>A kind of service a building offers: its names by settlement, its welcome and its choices.</summary>
internal sealed record ServiceDefinition(string Key, IReadOnlyDictionary<SettlementKind, string[]> Names, string Summary, string Role,
    TownLine[] Welcome, ServiceChoice[] Choices);

/// <summary>Someone who keeps a service; the portrait is a content path, or none.</summary>
internal sealed record TownPerson(string Name, string? Portrait);

/// <summary>
/// The town content (#9895): the kit, the layouts, the services and their people, and the timber
/// palettes, all read from <c>content/game/towns</c>. A missing reference between them is an authoring
/// error and fails the read.
/// </summary>
internal sealed record TownContent(IReadOnlyDictionary<string, KitPiece> Kit, TownLayout[] Layouts,
    IReadOnlyDictionary<string, ServiceDefinition> Services, IReadOnlyDictionary<string, string> Outcomes, string Hint,
    IReadOnlyDictionary<string, TownPerson[]> People, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Color>> Palettes)
{
    internal const string Root = "towns/";
    private const string KitPath = Root + "kit.json", ServicesPath = Root + "services.json";
    private const string PeoplePath = Root + "people.json", PalettesPath = Root + "palettes.json";
    private static readonly string[] LayoutPaths = [Root + "layouts/port.json", Root + "layouts/landing.json"];

    internal static TownContent Read(ProductContent content)
    {
        Dictionary<string, KitPiece> kit = ReadKit(content);
        TownLayout[] layouts = [.. LayoutPaths.Select(path => ReadLayout(content, path))];
        using JsonDocument services = JsonDocument.Parse(content.ReadText(ServicesPath));
        Dictionary<string, ServiceDefinition> definitions = services.RootElement.GetProperty("services").EnumerateObject()
            .ToDictionary(service => service.Name, service => ReadService(service.Name, service.Value));
        Dictionary<string, string> outcomes = services.RootElement.GetProperty("outcomes").EnumerateObject()
            .ToDictionary(outcome => outcome.Name, outcome => outcome.Value.GetString()!);
        string hint = services.RootElement.GetProperty("hint").GetString()!;
        using JsonDocument people = JsonDocument.Parse(content.ReadText(PeoplePath));
        Dictionary<string, TownPerson[]> keepers = people.RootElement.GetProperty("people").EnumerateObject().ToDictionary(
            service => service.Name, service => service.Value.EnumerateArray().Select(person => new TownPerson(
                person.GetProperty("name").GetString()!, Optional(person, "portrait"))).ToArray());
        using JsonDocument palettes = JsonDocument.Parse(content.ReadText(PalettesPath));
        Dictionary<string, IReadOnlyDictionary<string, Color>> treatments = palettes.RootElement.GetProperty("palettes").EnumerateObject()
            .ToDictionary(palette => palette.Name, palette => (IReadOnlyDictionary<string, Color>)palette.Value.EnumerateObject()
                .ToDictionary(tint => tint.Name, tint => Colour(tint.Value)));

        TownContent read = new(kit, layouts, definitions, outcomes, hint, keepers, treatments);
        read.Check();
        return read;
    }

    /// <summary>The layout a kind of settlement uses, or none when no layout suits it (the mountain hold).</summary>
    internal TownLayout? LayoutFor(SettlementKind kind) => Layouts.FirstOrDefault(layout => layout.For.Contains(kind));

    private void Check()
    {
        foreach (TownLayout layout in Layouts)
        {
            foreach (LayoutPiece piece in layout.Pieces)
            {
                if (!Kit.ContainsKey(piece.Piece)) throw new InvalidDataException($"Town layout '{layout.Key}' places unknown piece '{piece.Piece}'.");
                if (piece.Service is string service && !Services.ContainsKey(service))
                    throw new InvalidDataException($"Town layout '{layout.Key}' offers unknown service '{service}'.");
                if (piece.Service is not null && Kit[piece.Piece].Entrance is null)
                    throw new InvalidDataException($"Town layout '{layout.Key}' offers a service at '{piece.Piece}', which has no entrance.");
            }

            foreach (string landmark in layout.Landmark.Choices)
                if (!Kit.ContainsKey(landmark)) throw new InvalidDataException($"Town layout '{layout.Key}' names unknown landmark '{landmark}'.");
        }

        foreach (string service in Services.Keys)
            if (!People.TryGetValue(service, out TownPerson[]? keepers) || keepers.Length == 0)
                throw new InvalidDataException($"Town service '{service}' has nobody to keep it.");
    }

    private static Dictionary<string, KitPiece> ReadKit(ProductContent content)
    {
        using JsonDocument document = JsonDocument.Parse(content.ReadText(KitPath));
        return document.RootElement.GetProperty("pieces").EnumerateObject().ToDictionary(entry => entry.Name, entry =>
        {
            JsonElement piece = entry.Value;
            JsonElement bounds = piece.GetProperty("bounds");
            KitPiece read = new(entry.Name, piece.GetProperty("kind").GetString()!, piece.GetProperty("mesh").GetString()!,
                Strings(piece.GetProperty("materials")), Vector(bounds.GetProperty("min")), Vector(bounds.GetProperty("max")),
                Vectors(piece.GetProperty("lamps")), Vectors(piece.GetProperty("chimneys")));
            if (piece.TryGetProperty("entrance", out JsonElement entrance))
            {
                JsonElement selection = piece.GetProperty("selection");
                read = read with
                {
                    Entrance = Vector(entrance),
                    SelectionCentre = Vector(selection.GetProperty("centre")),
                    SelectionSize = Vector(selection.GetProperty("size")),
                };
            }

            if (piece.TryGetProperty("sign", out JsonElement sign))
                read = read with
                {
                    Sign = new(sign.GetProperty("mesh").GetString()!, Strings(sign.GetProperty("materials")),
                        Vector(sign.GetProperty("pivot")), Vector3.Normalize(Vector(sign.GetProperty("axis")))),
                };
            return read;
        });
    }

    private static TownLayout ReadLayout(ProductContent content, string path)
    {
        using JsonDocument document = JsonDocument.Parse(content.ReadText(path));
        JsonElement root = document.RootElement;
        JsonElement landmark = root.GetProperty("landmark");
        JsonElement trail = root.GetProperty("path");
        return new(root.GetProperty("key").GetString()!,
            [.. root.GetProperty("for").EnumerateArray().Select(kind => Enum.Parse<SettlementKind>(kind.GetString()!))],
            View(root.GetProperty("view")), Point(root.GetProperty("arrival")),
            [.. root.GetProperty("pieces").EnumerateArray().Select(piece => new LayoutPiece(piece.GetProperty("piece").GetString()!,
                Vector(piece.GetProperty("at")), Number(piece, "yaw"), Optional(piece, "service"),
                piece.TryGetProperty("close", out JsonElement close) ? View(close) : null))],
            new(Vector(landmark.GetProperty("at")), Number(landmark, "yaw"), Strings(landmark.GetProperty("choices"))),
            Points(root.GetProperty("paving")),
            new(Points(trail.GetProperty("points")), trail.GetProperty("width").GetSingle()),
            [.. root.GetProperty("scatter").EnumerateArray().Select(rule => new ScatterRule(rule.GetProperty("prop").GetString()!,
                rule.GetProperty("count").GetInt32(), Points(rule.GetProperty("around")),
                Point(rule.GetProperty("spread")), Point(rule.GetProperty("scale"))))]);
    }

    private static ServiceDefinition ReadService(string key, JsonElement service) => new(key,
        service.GetProperty("names").EnumerateObject().ToDictionary(names => Enum.Parse<SettlementKind>(names.Name), names => Strings(names.Value)),
        service.GetProperty("summary").GetString()!, service.GetProperty("role").GetString()!, Lines(service.GetProperty("welcome")),
        [.. service.GetProperty("choices").EnumerateArray().Select(choice => new ServiceChoice(
            choice.GetProperty("key").GetString()!, choice.GetProperty("action").GetString()!,
            choice.TryGetProperty("when", out JsonElement when) ? Strings(when) : [],
            Lines(choice.GetProperty("label")), Lines(choice.GetProperty("reply")), choice.GetProperty("detail").GetString()!,
            Optional(choice, "spent") ?? "", Optional(choice, "full") ?? "", Number(choice, "nourishment"), Number(choice, "hours"),
            Optional(choice, "item") ?? "", choice.TryGetProperty("allotment", out JsonElement allotment) ? allotment.GetInt32() : 0))]);

    private static TownLine[] Lines(JsonElement lines) => [.. lines.EnumerateArray().Select(line => new TownLine(
        line.TryGetProperty("when", out JsonElement when) ? Strings(when) : [], line.GetProperty("text").GetString()!))];

    private static TownView View(JsonElement view) =>
        new(Vector(view.GetProperty("eye")), Vector(view.GetProperty("focus")), view.GetProperty("fieldOfView").GetDouble());

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetSingle() : 0;

    private static string[] Strings(JsonElement values) => [.. values.EnumerateArray().Select(value => value.GetString()!)];

    private static Vector3 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());

    private static Vector3[] Vectors(JsonElement values) => [.. values.EnumerateArray().Select(Vector)];

    private static Vector2 Point(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle());

    private static Vector2[] Points(JsonElement values) => [.. values.EnumerateArray().Select(Point)];

    private static Color Colour(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle(), value[3].GetSingle());
}
