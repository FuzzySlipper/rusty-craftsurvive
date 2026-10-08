using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// Draws the placed pieces and the placement ghost (#9729), and hands their boxes to the player's
/// step as obstacles. One mesh per kind and material, built from the catalog's boxes and textured
/// with the construction maps (nearest-filtered, as the trees' bark); one translucent ghost mesh per
/// kind. Pieces are drawn as appearance facts in the walking frame; their world anchors follow the
/// frame's rebases.
/// </summary>
internal sealed class PiecePresenter : IDisposable
{
    private const float Roughness = 0.9f;
    private const float GhostRoughness = 1f;
    private static readonly Color GhostColour = new(0.75f, 0.92f, 1f, 0.45f);
    private static readonly Color White = new(1, 1, 1, 0);

    /// <summary>Pieces whose anchors are within this distance of the player collide with them.</summary>
    internal const float CollisionReach = 24f;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly BuildPieceSet pieces;
    private readonly PropMaterials materials;
    private readonly Material ghostMaterial;
    private readonly Dictionary<(PieceKind, PieceMaterial), (MeshResource Mesh, Appearance Look)> looks = [];
    private readonly Dictionary<PieceKind, (MeshResource Mesh, Appearance Look)> ghosts = [];
    private AppearanceFact[] facts = [];
    private long builtRevision = -1;
    private (long X, long Y, long Z) builtOrigin;

    internal PiecePresenter(IEngineContext engine, WorldFrame frame, BuildPieceSet pieces)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.pieces = pieces ?? throw new ArgumentNullException(nameof(pieces));
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0f, 0f));
        ghostMaterial = engine.Graphics.CreateMaterial(new MaterialRequest(GhostColour, default(RenderResourceReference), GhostRoughness,
            GhostColour, Vector3.Zero, 0, true) with { AlphaMode = MaterialAlphaMode.Blend });
    }

    /// <summary>Where the selected piece would go, drawn as a ghost; null hides it.</summary>
    internal PlacedPiece? Ghost { get; set; }

    /// <summary>The pieces' and the ghost's draw facts, for the snapshot.</summary>
    internal AppearanceFact[] Facts()
    {
        if (builtRevision != pieces.Revision || builtOrigin != frame.Origin)
        {
            List<AppearanceFact> drawn = new(pieces.Count);
            foreach (PlacedPiece piece in pieces.Pieces)
            {
                drawn.Add(Fact(ProductIds.BuildPieceBase + (ulong)drawn.Count, piece, Look(piece.Kind, piece.Material), ShadowCasting.Cast));
            }

            facts = [.. drawn];
            builtRevision = pieces.Revision;
            builtOrigin = frame.Origin;
        }

        return Ghost is PlacedPiece ghost
            ? [.. facts, Fact(ProductIds.BuildPieceGhost, ghost, GhostLook(ghost.Kind), ShadowCasting.None)]
            : facts;
    }

    /// <summary>The boxes of pieces near a point (walking frame) as the character step's obstacles.</summary>
    internal CharacterObstacle[] Obstacles(Vector3 local)
    {
        Vector3 world = frame.ToWorld(local);
        List<CharacterObstacle> near = [];
        for (int index = 0; index < pieces.Count; index++)
        {
            PlacedPiece piece = pieces.Pieces[index];
            if (Vector3.DistanceSquared(piece.Anchor, world) > CollisionReach * CollisionReach) continue;
            IReadOnlyList<PieceBox> boxes = PieceCatalog.Boxes(piece.Kind);
            for (int box = 0; box < boxes.Count; box++)
            {
                (Vector3 centre, Quaternion rotation, Vector3 half) = piece.World(boxes[box]);
                near.Add(new CharacterObstacle(ProductIds.BuildPieceObstacleBase + (ulong)((index * PieceObstacleStride) + box),
                    new Transform(frame.ToLocal(centre.X, centre.Y, centre.Z), rotation, Vector3.One), -half, half, true, Vector3.Zero, Vector3.Zero));
            }
        }

        return [.. near];
    }

    /// <summary>Boxes per piece the obstacle ids leave room for (stairs have the most).</summary>
    internal const int PieceObstacleStride = 8;

    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in looks.Values.Concat(ghosts.Values))
        {
            look.Dispose();
            mesh.Dispose();
        }

        looks.Clear();
        ghosts.Clear();
        ghostMaterial.Dispose();
        materials.Dispose();
    }

    private AppearanceFact Fact(ulong id, PlacedPiece piece, Appearance look, ShadowCasting shadows)
    {
        Vector3 anchor = piece.Anchor;
        return new AppearanceFact(id, false, 0, new Transform(frame.ToLocal(anchor.X, anchor.Y, anchor.Z), piece.Rotation, Vector3.One),
            look, true, RenderLayer.Scene, shadows);
    }

    private Appearance Look(PieceKind kind, PieceMaterial material)
    {
        if (looks.TryGetValue((kind, material), out var built)) return built.Look;
        MeshResource mesh = engine.Graphics.CreateMeshResource(Mesh(kind, materials.For("solid", PieceCatalog.Texture(material))));
        Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
        looks[(kind, material)] = (mesh, look);
        return look;
    }

    private Appearance GhostLook(PieceKind kind)
    {
        if (ghosts.TryGetValue(kind, out var built)) return built.Look;
        MeshResource mesh = engine.Graphics.CreateMeshResource(Mesh(kind, ghostMaterial));
        Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
        ghosts[kind] = (mesh, look);
        return look;
    }

    private static MeshResourceCreateRequest Mesh(PieceKind kind, Material material)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<uint> indices = [];
        foreach (PieceBox box in PieceCatalog.Boxes(kind))
        {
            BoxMesh.Add(box, positions, normals, uvs, indices);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(),
            Enumerable.Repeat(White, positions.Count).ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });
    }
}
