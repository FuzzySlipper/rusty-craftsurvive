using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// The rigid pieces wildlife wears and holds (#10956): shells and blades hung from a body's joints, as equipment, so
/// one body can wear different shells. Each piece is a prop mesh in its joint's own frame
/// (authoring/tools/creatures/make_shells.py writes them and the pieces file that lists each look's). It opens each
/// piece once, shares it between every body wearing it, and says which pieces a body wears and under which ids.
/// The Engine carries a piece with its joint: there is no product follow loop.
/// </summary>
internal sealed class WildlifeWornPieces : IDisposable
{
    private const float PaintRoughness = 0.9f;
    private static readonly Color White = new(1, 1, 1, 1);

    /// <summary>Spreads animals' ids over the looks; any odd constant would do.</summary>
    private const ulong LookSalt = 0x9E37_79B9_7F4A_7C15UL;

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly Dictionary<string, Set> sets = [];
    private readonly Dictionary<string, Opened> opened = [];

    private sealed record Piece(string Mesh, string Joint);

    private sealed record Set(IReadOnlyDictionary<string, Piece[]> Looks, Piece[] Held);

    private sealed record Opened(MeshResource Mesh, Appearance Look, List<(RenderResource Texture, Material Material)> Paints);

    /// <summary>One piece a body wears: the object that draws it and the joint it hangs from.</summary>
    internal sealed record Part(ulong ObjectId, Appearance Look, string Joint);

    internal WildlifeWornPieces(IEngineContext engine, ProductContent content)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
    }

    /// <summary>A test aid (#10956): every body made from now wears this look, where its kind has it; null for each its own.</summary>
    internal string? Forced { get; set; }

    /// <summary>The look an animal wears and its pieces, drawn under the ids of its body's slot.</summary>
    internal (string Look, Part[] Parts) For(WildlifeWorn worn, int memberId, ulong seed, int slot)
    {
        Set set = SetOf(worn.Pieces);
        string look = Forced is { } forced && set.Looks.ContainsKey(forced) ? forced : worn.Pick(Share(memberId, seed));
        if (!set.Looks.TryGetValue(look, out Piece[]? pieces)) throw new InvalidDataException($"worn: '{worn.Pieces}' has no look '{look}'");
        Piece[] all = [.. pieces, .. set.Held];
        if (all.Length > WildlifeWorn.MostPieces) throw new InvalidDataException($"worn: look '{look}' has {all.Length} pieces; a body has room for {WildlifeWorn.MostPieces}");
        ulong first = ProductIds.WildlifeWornBase + ((ulong)slot * WildlifeWorn.MostPieces);
        return (look, [.. all.Select((piece, index) => new Part(first + (ulong)index, Open(piece.Mesh), piece.Joint))]);
    }

    /// <summary>Opens every piece of a kind's looks now, with the world, rather than as a hitch when the first one wakes.</summary>
    internal void Prepare(WildlifeWorn worn)
    {
        Set set = SetOf(worn.Pieces);
        foreach (Piece piece in set.Looks.Values.SelectMany(pieces => pieces).Concat(set.Held)) Open(piece.Mesh);
    }

    /// <summary>The looks a kind's pieces file offers, for a test aid's answer.</summary>
    internal IEnumerable<string> Looks(WildlifeWorn worn) => SetOf(worn.Pieces).Looks.Keys;

    /// <summary>An even share in [0, 1) from an animal's id, so its look holds while it is awake.</summary>
    private static double Share(int memberId, ulong seed)
    {
        ulong mixed = (((ulong)memberId + 1) * LookSalt) ^ seed;
        mixed ^= mixed >> 33;
        mixed *= LookSalt;
        mixed ^= mixed >> 29;
        return (mixed >> 11) * (1.0 / (1UL << 53));
    }

    private Set SetOf(string path)
    {
        if (sets.TryGetValue(path, out Set? set)) return set;
        using JsonDocument document = JsonDocument.Parse(content.ReadText(path));
        static Piece Read(JsonElement piece) => new(piece.GetProperty("piece").GetString()!, piece.GetProperty("joint").GetString()!);
        Dictionary<string, Piece[]> looks = document.RootElement.GetProperty("looks").EnumerateObject()
            .ToDictionary(look => look.Name, look => look.Value.EnumerateArray().Select(Read).ToArray());
        Piece[] held = document.RootElement.TryGetProperty("held", out JsonElement heldPieces)
            ? [.. heldPieces.EnumerateObject().Select(piece => Read(piece.Value))]
            : [];
        return sets[path] = new Set(looks, held);
    }

    private Appearance Open(string path)
    {
        if (opened.TryGetValue(path, out Opened? open)) return open.Look;
        List<(RenderResource, Material)> paints = [];
        MeshResourceCreateRequest request = PropMesh.Read(content, path, (role, texture) =>
        {
            if (texture is null) throw new InvalidDataException($"worn piece '{path}' part '{role}' names no texture");
            RenderResource handle = engine.Graphics.OpenResource(new(texture, TextureFilter.Nearest, TextureWrap.Clamp)).Handle;
            Material material = engine.Graphics.CreateMaterial(new MaterialRequest(White, handle, PaintRoughness, White, Vector3.Zero, 0, false));
            paints.Add((handle, material));
            return material;
        });
        MeshResource mesh = engine.Graphics.CreateMeshResource(request);
        Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
        opened[path] = new Opened(mesh, look, paints);
        return look;
    }

    public void Dispose()
    {
        foreach (Opened open in opened.Values)
        {
            open.Look.Dispose();
            open.Mesh.Dispose();
            foreach ((RenderResource texture, Material material) in open.Paints)
            {
                material.Dispose();
                texture.Dispose();
            }
        }

        opened.Clear();
    }
}
