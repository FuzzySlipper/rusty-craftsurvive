using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The dungeon kit drawn and collided (#10089): each piece's prop mesh loaded once (content/game/models/dungeon-kit),
/// an appearance fact for every placement, and the placements as the space's static collision, each piece one asset
/// and every placement an instance of it, as the overworld's places collide. The space owns this for as long as it is
/// loaded.
/// </summary>
internal sealed class DungeonKitLook : IDisposable
{
    private const string Manifest = "models/dungeon-kit/kit.json";

    /// <summary>Kit stone is matte.</summary>
    private const float Roughness = 0.9f;

    /// <summary>The kit's collision asset and instance ids, clear of the rock's.</summary>
    private const ulong AssetBase = 0x4000UL, InstanceBase = 0x10_0000UL;

    private readonly IEngineContext engine;
    private readonly PropMaterials materials;
    private readonly Dictionary<KitPiece, (MeshResource Mesh, Appearance Look)> pieces = [];
    private readonly List<(KitPiece Piece, Transform Place)> placed = [];

    internal DungeonKitLook(IEngineContext engine, ProductContent content, IReadOnlyList<KitPlacement> placements)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(content);
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0));
        using JsonDocument manifest = JsonDocument.Parse(content.ReadText(Manifest));
        Dictionary<string, string> models = manifest.RootElement.GetProperty("pieces").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("model").GetString()!);
        foreach (KitPiece piece in placements.Select(p => p.Piece).Distinct())
        {
            MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, models[DungeonKit.Name(piece)], (role, texture) =>
                materials.For(role, texture ?? throw new InvalidOperationException($"Dungeon kit piece {piece} has no texture for {role}."))));
            pieces[piece] = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
        }

        foreach (KitPlacement p in placements.Take(ProductIds.DungeonKitObjects))
        {
            (Vector3 at, Quaternion turn, Vector3 scale) = DungeonKit.Place(p);
            placed.Add((p.Piece, new Transform(at, turn, scale)));
        }
    }

    internal int Count => placed.Count;

    internal IEnumerable<AppearanceFact> Facts => placed.Select((p, index) => new AppearanceFact(ProductIds.DungeonKitObject + (ulong)index, false, 0UL,
        p.Place, pieces[p.Piece].Look, true, RenderLayer.Scene, ShadowCasting.Cast));

    /// <summary>The pieces that collide (<see cref="DungeonKit.Collides"/>) as static collision: one asset per piece, an instance per placement.</summary>
    internal void Collide(SpatialSession session)
    {
        List<KitPiece> kinds = [.. pieces.Keys.Where(DungeonKit.Collides)];
        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(
            session,
            kinds.Select((kind, i) => new StaticMeshAsset(AssetBase + (ulong)i, new MeshResourceReference(pieces[kind].Mesh), 0, 0, 0, 0)).ToArray(),
            ReadOnlyMemory<Vector3>.Empty,
            ReadOnlyMemory<Triangle>.Empty,
            placed.Where(p => DungeonKit.Collides(p.Piece))
                .Select((p, i) => new StaticMeshInstance(InstanceBase + (ulong)i, AssetBase + (ulong)kinds.IndexOf(p.Piece), p.Place)).ToArray()));
    }

    /// <summary>Disposes the pieces after the snapshot that last named them, then their materials.</summary>
    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in pieces.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        pieces.Clear();
        materials.Dispose();
    }
}
