using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// One piece of the place kit (#9992): its prop mesh, its far silhouette's prop mesh (a decimated copy on
/// the same texture), and how far its base sinks below the ground it stands on (scaled with the piece).
/// </summary>
internal sealed record PlaceKitPiece(string Name, string Model, string Far, float Sink);

/// <summary>The place kit's manifest, read once by each owner that draws from it.</summary>
internal static class PlaceKit
{
    internal const string Manifest = "models/places/kit.json";

    internal static IReadOnlyDictionary<string, PlaceKitPiece> Read(ProductContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using JsonDocument manifest = JsonDocument.Parse(content.ReadText(Manifest));
        Dictionary<string, PlaceKitPiece> pieces = [];
        foreach (JsonElement piece in manifest.RootElement.GetProperty("pieces").EnumerateArray())
        {
            string name = piece.GetProperty("name").GetString()!, model = piece.GetProperty("model").GetString()!;
            pieces[name] = new(name, model, piece.TryGetProperty("far", out JsonElement far) ? far.GetString()! : model, piece.GetProperty("sink").GetSingle());
        }

        return pieces;
    }
}
