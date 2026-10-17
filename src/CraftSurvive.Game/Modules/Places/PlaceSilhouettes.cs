using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// A place kind's far silhouette (#9992): the kind's own layout (PlaceLayouts) of the kit's far pieces,
/// composed into one mesh on flat ground, so the horizon shows a ring of steles, a broken round wall or a
/// stepped look-out where the place stands, in the same painted stone as up close. One per kind, built on
/// first use from a representative site, since a silhouette a few pixels high cannot show one site's
/// variant. The origin is the site's ground under its centre.
/// </summary>
internal sealed class PlaceSilhouettes : IDisposable
{
    /// <summary>Weathered stone, as up close.</summary>
    private const float Roughness = 0.92f;

    /// <summary>The representative site each kind's silhouette is laid out from: a height and variant mid-range.</summary>
    private static readonly Dictionary<PoiKind, (long Height, long Variant)> Representative = new()
    {
        [PoiKind.StandingStones] = (10, 3),
        [PoiKind.Ruin] = (6, 2),
        [PoiKind.CaveMouth] = (0, 0),
        [PoiKind.DungeonEntrance] = (0, 0),
        [PoiKind.VantagePoint] = (5, 0),
    };

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly IReadOnlyDictionary<string, PlaceKitPiece> kit;
    private readonly PropMaterials materials;
    private readonly Dictionary<PoiKind, (MeshResource Mesh, Appearance Look, float Height)> built = [];

    internal PlaceSilhouettes(IEngineContext engine, ProductContent content)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        kit = PlaceKit.Read(content);
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0), TextureFilter.NearestMipmapped);
    }

    /// <summary>A kind's silhouette and its height above its ground, or null for a kind with no pieces.</summary>
    internal (Appearance Look, float Height)? For(PoiKind kind)
    {
        if (built.TryGetValue(kind, out var made)) return (made.Look, made.Height);
        if (!Representative.TryGetValue(kind, out var at)) return null;
        PoiSite site = new(0, 0, kind, 0, 0, -1, at.Height, at.Variant, 0);
        PropMesh.Builder builder = new();
        foreach (PlacedPart part in PlaceLayouts.For(site))
        {
            if (!kit.TryGetValue(part.Piece, out PlaceKitPiece? piece)) continue;
            // As PlaceMeshes stands it, on flat ground at height 0: the site's centre is (0.5, 0.5).
            Vector3 offset = new((float)(part.X - 0.5), (float)(part.Rise - (piece.Sink * part.Scale.Y)), (float)(part.Z - 0.5));
            Matrix4x4 transform = Matrix4x4.CreateScale(part.Scale) * Matrix4x4.CreateFromQuaternion(part.Rotation) * Matrix4x4.CreateTranslation(offset);
            builder.Add(content, piece.Far, (role, texture) => materials.For(role,
                texture ?? throw new InvalidOperationException($"CraftSurvive place piece '{piece.Name}' part '{role}' names no texture.")), transform);
        }

        if (builder.Top == float.NegativeInfinity) return null;
        MeshResource mesh = engine.Graphics.CreateMeshResource(builder.Build());
        Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
        built[kind] = (mesh, look, builder.Top);
        return (look, builder.Top);
    }

    /// <summary>Disposes the silhouettes; call only after a snapshot that no longer names them.</summary>
    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look, _) in built.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        built.Clear();
        materials.Dispose();
    }
}
