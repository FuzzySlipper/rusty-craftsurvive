using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// Built work as the walking session's static collision (#9734): one collision mesh per piece kind
/// (its boxes, pitched ones included, as triangles) with an instance per standing piece at its
/// anchor and turn, and one mesh per remnant from its merged boxes. Because they are the session's
/// own collision, the player's step, creature navigation and the session's casts all meet them
/// exactly - a turned wall and a pitched roof included - with nothing extra per step. Rebuilt whole
/// when the pieces, the remnants or the world origin change (a few hundred instances at most).
/// </summary>
internal sealed class PieceColliders
{
    private const ulong KindAssetBase = 1UL, RemnantAssetBase = 0x1000UL;
    private const ulong PieceInstanceBase = 0x10_0000UL, RemnantInstanceBase = 0x20_0000UL;

    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private long builtPieces = -1, builtRemnants = -1;
    private (long X, long Y, long Z) builtOrigin;

    internal PieceColliders(TerrainWorld terrain, WorldFrame frame)
    {
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    /// <summary>Whether what collides is behind what stands (for the readout).</summary>
    internal int Instances { get; private set; }

    /// <summary>Rebuilds the collision if the pieces, the remnants or the origin moved on since it was built.</summary>
    internal void Follow(BuildPieceSet pieces, RemnantSet remnants, IReadOnlyList<List<(Vector3 Centre, Vector3 Half)>> remnantBoxes)
    {
        if (builtPieces == pieces.Revision && builtRemnants == remnants.Revision && builtOrigin == frame.Origin) return;
        List<StaticMeshAsset> assets = [];
        List<Vector3> vertices = [];
        List<Triangle> triangles = [];
        List<StaticMeshInstance> instances = [];
        HashSet<PieceKind> used = [.. pieces.Pieces.Select(piece => piece.Kind)];
        foreach (PieceKind kind in used)
        {
            uint firstVertex = (uint)vertices.Count, firstTriangle = (uint)triangles.Count;
            foreach (PieceBox box in PieceCatalog.Boxes(kind))
            {
                AddBox(box.Centre, Quaternion.CreateFromAxisAngle(Vector3.UnitX, box.Pitch), box.Half, vertices, triangles, firstVertex);
            }

            assets.Add(new StaticMeshAsset(KindAssetBase + (ulong)kind, firstVertex, (uint)vertices.Count - firstVertex, firstTriangle, (uint)triangles.Count - firstTriangle));
        }

        for (int index = 0; index < pieces.Count; index++)
        {
            PlacedPiece piece = pieces.Pieces[index];
            Vector3 anchor = piece.Anchor;
            instances.Add(new StaticMeshInstance(PieceInstanceBase + (ulong)index, KindAssetBase + (ulong)piece.Kind,
                new Transform(frame.ToLocal(anchor.X, anchor.Y, anchor.Z), piece.Rotation, Vector3.One)));
        }

        for (int index = 0; index < remnantBoxes.Count; index++)
        {
            if (remnantBoxes[index].Count == 0) continue;
            Vector3 origin = remnants.Remnants[index].Bounds().Low;
            uint firstVertex = (uint)vertices.Count, firstTriangle = (uint)triangles.Count;
            foreach ((Vector3 centre, Vector3 half) in remnantBoxes[index])
            {
                AddBox(centre - origin, Quaternion.Identity, half, vertices, triangles, firstVertex);
            }

            ulong asset = RemnantAssetBase + (ulong)index;
            assets.Add(new StaticMeshAsset(asset, firstVertex, (uint)vertices.Count - firstVertex, firstTriangle, (uint)triangles.Count - firstTriangle));
            instances.Add(new StaticMeshInstance(RemnantInstanceBase + (ulong)index, asset,
                new Transform(frame.ToLocal(origin.X, origin.Y, origin.Z), Quaternion.Identity, Vector3.One)));
        }

        terrain.ReplaceStaticCollision(new CollisionReplaceRequest(terrain.Session, assets.ToArray(), vertices.ToArray(), triangles.ToArray(), instances.ToArray()));
        Instances = instances.Count;
        builtPieces = pieces.Revision;
        builtRemnants = remnants.Revision;
        builtOrigin = frame.Origin;
    }

    /// <summary>A box's eight corners and twelve outward triangles; indices are relative to the asset's first vertex.</summary>
    private static void AddBox(Vector3 centre, Quaternion rotation, Vector3 half, List<Vector3> vertices, List<Triangle> triangles, uint assetFirst)
    {
        uint first = (uint)vertices.Count - assetFirst;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new((corner & 1) == 0 ? -half.X : half.X, (corner & 2) == 0 ? -half.Y : half.Y, (corner & 4) == 0 ? -half.Z : half.Z);
            vertices.Add(centre + Vector3.Transform(local, rotation));
        }

        // Corner index bits: 1 = +X, 2 = +Y, 4 = +Z. Each face wound anticlockwise seen from outside.
        int[][] faces =
        [
            [0, 4, 6, 2], // -X
            [1, 3, 7, 5], // +X
            [0, 1, 5, 4], // -Y
            [2, 6, 7, 3], // +Y
            [0, 2, 3, 1], // -Z
            [4, 5, 7, 6], // +Z
        ];
        foreach (int[] face in faces)
        {
            triangles.Add(new Triangle(first + (uint)face[0], first + (uint)face[1], first + (uint)face[2]));
            triangles.Add(new Triangle(first + (uint)face[0], first + (uint)face[2], first + (uint)face[3]));
        }
    }
}
