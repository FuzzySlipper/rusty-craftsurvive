using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.Dungeons;
using Rusty.Engine;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The dungeon kit's collision as the game composes it (#10089, DungeonKitLook): each piece's prop mesh read from the
/// product content, one static asset per piece and an instance per placement, placed by <see cref="DungeonKit.Place"/>.
/// </summary>
internal static class KitCollision
{
    private const ulong AssetBase = 0x4000UL, InstanceBase = 0x10_0000UL;

    internal static void Admit(IEngineContext engine, SpatialSession session, IReadOnlyList<KitPlacement> placements)
    {
        string models = Path.Combine(ContentRoot(), "models", "dungeon-kit");
        placements = [.. placements.Where(p => DungeonKit.Collides(p.Piece))];
        List<KitPiece> kinds = [.. placements.Select(p => DungeonKit.Collider(p.Piece)).Distinct()];
        List<Vector3> vertices = [];
        List<Triangle> triangles = [];
        List<StaticMeshAsset> assets = [];
        foreach (KitPiece kind in kinds)
        {
            using JsonDocument mesh = JsonDocument.Parse(File.ReadAllText(Path.Combine(models, $"{DungeonKit.Name(kind)}.prop-mesh.json")));
            uint firstVertex = (uint)vertices.Count, firstTriangle = (uint)triangles.Count;
            foreach (JsonElement part in mesh.RootElement.GetProperty("parts").EnumerateArray())
            {
                uint baseVertex = (uint)(vertices.Count - firstVertex);
                float[] p = [.. part.GetProperty("positions").EnumerateArray().Select(v => v.GetSingle())];
                for (int i = 0; i < p.Length; i += 3)
                {
                    vertices.Add(new Vector3(p[i], p[i + 1], p[i + 2]));
                }

                uint[] index = [.. part.GetProperty("indices").EnumerateArray().Select(v => v.GetUInt32())];
                for (int i = 0; i < index.Length; i += 3)
                {
                    triangles.Add(new Triangle(baseVertex + index[i], baseVertex + index[i + 1], baseVertex + index[i + 2]));
                }
            }

            assets.Add(new StaticMeshAsset(AssetBase + (ulong)assets.Count, firstVertex, (uint)vertices.Count - firstVertex, firstTriangle,
                (uint)triangles.Count - firstTriangle));
        }

        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(session, assets.ToArray(), vertices.ToArray(), triangles.ToArray(),
            placements.Select((p, i) =>
            {
                (Vector3 at, Quaternion turn, Vector3 scale) = DungeonKit.Place(p);
                return new StaticMeshInstance(InstanceBase + (ulong)i, AssetBase + (ulong)kinds.IndexOf(DungeonKit.Collider(p.Piece)), new Transform(at, turn, scale));
            }).ToArray()));
    }

    /// <summary>The product's content directory, from this checkout.</summary>
    private static string ContentRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "game");
            if (Directory.Exists(Path.Combine(candidate, "models", "dungeon-kit")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("the product's content/game directory was not found above the test's output");
    }
}
