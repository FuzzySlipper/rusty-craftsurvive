using System.Numerics;
using CraftSurvive.Game.Modules.Places;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A cave's moss and ivy cards drawn (#10092): the overworld places' painted cards (<see cref="PlaceMossCards"/>, #10044)
/// laid where <see cref="KarstCave"/> grew them on the rock, as one mesh in the session: the curtains and cushions in
/// the card atlas, the mats in the mat atlas. The space publishes the mesh with its growths; this owns the
/// materials.
/// </summary>
internal sealed class CaveMossCards : IDisposable
{
    /// <summary>Moss on rock is matte, and nothing about it sways underground.</summary>
    private const float Roughness = 0.95f;

    /// <summary>
    /// Underground the cards take the cave moss's quieter colour (#10129's toned-down moss), not the sunlit places': the painted
    /// atlas times this (linear), as the material's colour.
    /// </summary>
    private static readonly Color Tint = new(0.62f, 0.66f, 0.48f, 1f);

    /// <summary>The cards are alpha-cut where the painted atlas's alpha falls below this, as the places' cards are.</summary>
    private const float AlphaCutoff = 0.5f;

    private readonly IEngineContext engine;
    private readonly List<RenderResource> textures = [];
    private readonly List<Material> materials = [];

    internal CaveMossCards(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>A card material: the painted atlas, alpha-cut, in the cave's tint.</summary>
    private Material Material(string path)
    {
        RenderResource texture = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
        textures.Add(texture);
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(Tint, texture, Roughness, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false)
            with { AlphaMode = MaterialAlphaMode.Mask, AlphaCutoff = AlphaCutoff });
        materials.Add(material);
        return material;
    }

    /// <summary>The cards as one mesh in the session, or null when the cave grew none.</summary>
    internal MeshResourceCreateRequest? Request(IReadOnlyList<MossCard> cards)
    {
        if (cards.Count == 0)
        {
            return null;
        }

        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<uint> indices = [], matIndices = [];
        foreach (MossCard card in cards)
        {
            Vector3 at = DungeonSpace.InSession(card.At);
            if (card.Kind == MossCardKind.Mat)
            {
                PlaceMossCards.Mat(at, card.Normal, card.Pick, positions, normals, uvs, matIndices);
                continue;
            }

            PlaceMossCards.Kind kind = card.Kind switch
            {
                MossCardKind.Curtain => PlaceMossCards.Kind.Curtain,
                MossCardKind.Face => PlaceMossCards.Kind.Face,
                _ => PlaceMossCards.Kind.Cushion,
            };
            PlaceMossCards.Card(at, card.Out, card.Normal, kind, card.Pick, positions, normals, uvs, indices);
        }

        // The Engine refuses an empty group: one for the cards and one for the mats, each only if it has triangles.
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        if (indices.Count > 0)
        {
            groups.Add(new MeshGroup(0, 0, (uint)indices.Count));
            bindings.Add(new MeshMaterialBinding(0, Material(PlaceMossCards.AtlasPath)));
        }

        if (matIndices.Count > 0)
        {
            uint slot = (uint)bindings.Count;
            groups.Add(new MeshGroup(slot, (uint)indices.Count, (uint)matIndices.Count));
            bindings.Add(new MeshMaterialBinding(slot, Material(PlaceMossCards.MatsPath)));
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), ReadOnlyMemory<Color>.Empty,
            indices.Concat(matIndices).ToArray(), groups.ToArray(), bindings.ToArray());
    }

    /// <summary>Disposes the materials and their textures, after the meshes drawn in them.</summary>
    public void Dispose()
    {
        foreach (Material material in materials)
        {
            material.Dispose();
        }

        foreach (RenderResource texture in textures)
        {
            texture.Dispose();
        }

        materials.Clear();
        textures.Clear();
    }
}
