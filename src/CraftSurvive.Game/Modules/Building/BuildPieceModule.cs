using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// Building with pieces (#9729, Den <c>decision-building-hybrid-pieces</c>): the owner of what the
/// player has built, what they have selected, and whether placing builds pieces or works the terrain.
/// <list type="bullet">
/// <item>Read: the player's view (eye and forward, walking frame) each update.</item>
/// <item>Decide: what the view meets first, a piece or the ground, and where the selected piece
/// would go there (<see cref="PieceGeometry.Place"/>).</item>
/// <item>Apply: placing (G, secondary) adds it unless it is occupied or would trap the player;
/// clearing (F, primary) removes an aimed piece, and otherwise falls through to digging.</item>
/// <item>Publish: the ghost and the pieces (<see cref="PiecePresenter"/>), the obstacles the
/// player's step collides with, the save, and a line for the HUD.</item>
/// </list>
/// T switches placing between pieces and the terrain brush (earthworks); Q steps the piece, Z its material.
/// </summary>
internal sealed class BuildPieceModule : IProductModule
{
    private const float Reach = (float)TerrainConstants.EditReach;

    /// <summary>The player as a box for "would trap you": half extents and how far its middle sits below the eye.</summary>
    private static readonly Vector3 BodyHalf = new(0.35f, 0.95f, 0.35f);
    private const float BodyBelowEye = 0.75f;

    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private readonly BuildPieceSet set = new();
    private readonly PiecePresenter presenter;
    private readonly ProductSaveSlot<PlacedPiece[]> slot;
    private long savedRevision;
    private bool started;
    private PieceKind kind = PieceKind.Wall;
    private int materialIndex;
    private long placed, removed, refused;
    private string last = "none";

    internal BuildPieceModule(IEngineContext engine, ProductStore store, TerrainWorld terrain, WorldFrame frame)
    {
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        presenter = new PiecePresenter(engine, frame, set);
        slot = new ProductSaveSlot<PlacedPiece[]>(engine, store, SaveManifest.BuildPieces, new BuildPieceCodec(terrain.SaveIdentity));
    }

    /// <summary>Whether placing builds pieces (true) or works the terrain with the brush.</summary>
    internal bool PiecesMode { get; private set; } = true;

    internal PieceKind Kind => kind;

    internal PieceMaterial Material => PieceCatalog.Materials(kind)[materialIndex];

    internal BuildPieceSet Set => set;

    /// <summary>The HUD's line: what placing does now, and the keys that change it.</summary>
    internal string HudLine => PiecesMode
        ? $"{PieceCatalog.Name(kind)} in {PieceCatalog.Name(Material)} (G places, F removes, Q piece, Z material, T terrain)"
        : "terrain brush (G places, F digs, T pieces)";

    public void Start()
    {
        if (started) return;
        Restore();
        started = true;
    }

    public void Update(ProductStep step)
    {
        if (started && set.Revision != savedRevision)
        {
            Save();
        }
    }

    public void Restart() => Restore();

    public void Dispose()
    {
        if (started && set.Revision != savedRevision)
        {
            Save();
        }

        started = false;
        presenter.Dispose();
    }

    /// <summary>
    /// Takes this update's view and selection keys: steps the piece and material, switches the
    /// mode, and shows the ghost where the selected piece would go.
    /// </summary>
    internal void Steer(Vector3 eye, Vector3 forward, int kindSteps, int materialSteps, bool toggleMode, bool active)
    {
        if (toggleMode) PiecesMode = !PiecesMode;
        if (kindSteps != 0)
        {
            int count = PieceCatalog.Kinds.Count;
            kind = PieceCatalog.Kinds[(((int)kind + kindSteps) % count + count) % count];
            materialIndex = 0;
        }

        if (materialSteps != 0)
        {
            int count = PieceCatalog.Materials(kind).Count;
            materialIndex = ((materialIndex + materialSteps) % count + count) % count;
        }

        presenter.Ghost = active && PiecesMode && Candidate(eye, forward) is { } candidate ? candidate.Piece : null;
    }

    /// <summary>
    /// Offers a place or clear request from the view to the pieces first. Returns whether the pieces
    /// took it; when not, the terrain works it as before.
    /// </summary>
    internal bool TryEdit(TerrainEditKind edit, Vector3 eye, Vector3 forward)
    {
        Vector3 origin = frame.ToWorld(eye);
        PieceHit? pieceHit = PieceGeometry.Cast(set.Pieces, origin, forward, Reach);
        SpatialHit ground = terrain.CastView(eye, forward);
        bool pieceFirst = pieceHit is PieceHit hit && (!ground.Present || hit.Distance <= ground.Distance);
        if (edit == TerrainEditKind.Clear)
        {
            if (!pieceFirst) return false;
            PlacedPiece gone = set.Pieces[pieceHit!.Value.Index];
            set.RemoveAt(pieceHit.Value.Index);
            removed++;
            last = $"removed {PieceCatalog.Name(gone.Kind)}";
            return true;
        }

        if (!PiecesMode) return false;
        if (Candidate(eye, forward) is not { } candidate)
        {
            Refuse("nothing within reach to build on");
            return true;
        }

        if (Traps(candidate.Piece, eye))
        {
            Refuse($"{PieceCatalog.Name(candidate.Piece.Kind)} would stand where you are");
            return true;
        }

        PieceOutcome outcome = set.Add(candidate.Piece);
        if (outcome == PieceOutcome.Placed)
        {
            placed++;
            last = $"placed {PieceCatalog.Name(candidate.Piece.Kind)}";
        }
        else
        {
            Refuse($"{PieceCatalog.Name(candidate.Piece.Kind)}: {outcome.ToString().ToLowerInvariant()}");
        }

        return true;
    }

    /// <summary>Places a piece at a world grid anchor, for assisted building; returns the outcome.</summary>
    internal string PlaceAt(PieceKind pieceKind, PieceMaterial material, double x, double y, double z, int turn)
    {
        PlacedPiece piece = new(pieceKind, material, (long)Math.Round(x / PlacedPiece.GridMetres), (long)Math.Round(y / PlacedPiece.GridMetres),
            (long)Math.Round(z / PlacedPiece.GridMetres), (byte)(((turn % PlacedPiece.Turns) + PlacedPiece.Turns) % PlacedPiece.Turns));
        PieceOutcome outcome = set.Add(piece);
        if (outcome == PieceOutcome.Placed) placed++;
        else refused++;
        return $"{outcome.ToString().ToLowerInvariant()}; {Readout()}";
    }

    /// <summary>A charge takes the pieces within its reach (until damaged pieces become voxels, #9731).</summary>
    internal int Blast(Vector3 worldCentre, float radius) => set.RemoveWithin(worldCentre, radius);

    internal void Clear()
    {
        set.Restore([]);
    }

    /// <summary>The pieces' boxes near the player, for the character step.</summary>
    internal CharacterObstacle[] Obstacles(Vector3 local) => presenter.Obstacles(local);

    internal AppearanceFact[] Facts() => presenter.Facts();

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"pieces count={set.Count} placed={placed} removed={removed} refused={refused} mode={(PiecesMode ? "pieces" : "terrain")} selected={PieceCatalog.Name(kind)}/{PieceCatalog.Name(Material)} ghost={(presenter.Ghost is PlacedPiece g ? $"{g.X * PlacedPiece.GridMetres:F2},{g.Y * PlacedPiece.GridMetres:F2},{g.Z * PlacedPiece.GridMetres:F2}/t{g.Turn}" : "none")} last={last} restore={slot.RestoreOutcome} saves={slot.Saves}");

    /// <summary>Where the selected piece would go from this view: on the first piece or ground it meets.</summary>
    private (PlacedPiece Piece, float Distance)? Candidate(Vector3 eye, Vector3 forward)
    {
        Vector3 origin = frame.ToWorld(eye);
        PieceHit? pieceHit = PieceGeometry.Cast(set.Pieces, origin, forward, Reach);
        SpatialHit ground = terrain.CastView(eye, forward);
        Vector3 facing = new(forward.X, 0, forward.Z);
        if (facing.LengthSquared() < 1e-6f) facing = Vector3.UnitZ;
        facing = Vector3.Normalize(facing);
        if (pieceHit is PieceHit hit && (!ground.Present || hit.Distance <= ground.Distance))
        {
            return (PieceGeometry.Place(kind, Material, hit.Point, hit.Normal, facing), hit.Distance);
        }

        if (!ground.Present) return null;
        Vector3 point = frame.ToWorld(ground.Point);
        Vector3 normal = ground.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(ground.Normal) : Vector3.UnitY;
        return (PieceGeometry.Place(kind, Material, point, normal, facing), (float)ground.Distance);
    }

    /// <summary>Whether any of the piece's boxes (by their world bounds) overlaps the player's body.</summary>
    private bool Traps(PlacedPiece piece, Vector3 eye)
    {
        Vector3 body = frame.ToWorld(eye) - new Vector3(0, BodyBelowEye, 0);
        foreach (PieceBox box in PieceCatalog.Boxes(piece.Kind))
        {
            (Vector3 centre, Quaternion rotation, Vector3 half) = piece.World(box);
            Vector3 extent = Vector3.Zero;
            foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                extent += Vector3.Abs(Vector3.Transform(axis * Vector3.Dot(axis, half), rotation));
            }

            Vector3 gap = Vector3.Abs(centre - body) - (extent + BodyHalf);
            if (gap.X < 0 && gap.Y < 0 && gap.Z < 0) return true;
        }

        return false;
    }

    private void Refuse(string why)
    {
        refused++;
        last = $"refused: {why}";
    }

    private void Restore()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: PlacedPiece[] saved })
        {
            set.Restore(saved);
        }
        else
        {
            set.Restore([]);
        }

        savedRevision = set.Revision;
    }

    private void Save()
    {
        slot.Save(set.Snapshot());
        savedRevision = set.Revision;
    }
}
