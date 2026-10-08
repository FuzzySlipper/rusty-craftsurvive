using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// Building with pieces (#9729, Den <c>decision-building-hybrid-pieces</c>): the owner of what the
/// player has built, what they have selected, and whether placing builds pieces or works the terrain.
/// <list type="bullet">
/// <item>Read: the player's view (eye and forward, walking frame) each update.</item>
/// <item>Decide: what the view meets first, a piece or the ground, and where the selected piece
/// would go there: at the nearest socket of an aimed piece (<see cref="PieceGeometry.Snap"/>),
/// else on the aimed ground (<see cref="PieceGeometry.Place"/>); and whether it may stand there.</item>
/// <item>Apply: placing (G, secondary) adds it unless it is occupied or would trap the player;
/// clearing (F, primary) removes an aimed piece, and otherwise falls through to digging.</item>
/// <item>Publish: the ghost, tinted by whether the piece may stand (#9730), and the pieces
/// (<see cref="PiecePresenter"/>), their collision as the walking session's (<see cref="PieceColliders"/>), the place and
/// break cues, the save, and a line for the HUD.</item>
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
    private readonly Cues cues;
    private readonly WorldFrame frame;
    private readonly BuildPieceSet set = new();
    private readonly PiecePresenter presenter;
    private readonly ProductSaveSlot<PlacedPiece[]> slot;
    private long savedRevision;

    /// <summary>Pieces a charge has bitten into (#9731): their description, drawing, collision boxes and save.</summary>
    private readonly RemnantSet remnants = new();
    private readonly RemnantVoxels remnantVoxels;
    private readonly PieceColliders colliders;
    private readonly ProductSaveSlot<PieceRemnant[]> remnantSlot;
    private readonly List<List<(Vector3 Centre, Vector3 Half)>> remnantBoxes = [];
    private long savedRemnantRevision, boxedRemnantRevision = -1;
    private long destroyed, bitten, collapsed;

    /// <summary>Support (#9733): set by a removal, a charge or a terrain edit; the next update lets what is not held up fall.</summary>
    private bool supportDirty;
    private long fell;
    private readonly Action<IReadOnlyList<TerrainVoxelEdit>> onTerrainEdited;
    private bool started;
    private PieceKind kind = PieceKind.Wall;
    private int materialIndex;
    private long placed, removed, refused;
    private string last = "none";

    internal BuildPieceModule(IEngineContext engine, ProductContent content, ProductStore store, TerrainWorld terrain, WorldFrame frame, Cues cues)
    {
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        presenter = new PiecePresenter(engine, frame, set);
        slot = new ProductSaveSlot<PlacedPiece[]>(engine, store, SaveManifest.BuildPieces, new BuildPieceCodec(terrain.SaveIdentity));
        remnantSlot = new ProductSaveSlot<PieceRemnant[]>(engine, store, SaveManifest.BuildRemnants, new RemnantCodec(terrain.SaveIdentity));
        remnantVoxels = new RemnantVoxels(engine, content, frame, remnants);
        colliders = new PieceColliders(terrain, frame);
        onTerrainEdited = _ => supportDirty = true;
        terrain.Edited += onTerrainEdited;
    }

    internal RemnantSet Remnants => remnants;

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

        if (started && remnants.Revision != savedRemnantRevision)
        {
            SaveRemnants();
        }

        if (started && supportDirty)
        {
            supportDirty = false;
            Fall();
        }

        if (started) colliders.Follow(set, remnants, RemnantBoxes());
    }

    public void Restart() => Restore();

    public void Dispose()
    {
        if (started && set.Revision != savedRevision)
        {
            Save();
        }

        if (started && remnants.Revision != savedRemnantRevision)
        {
            SaveRemnants();
        }

        started = false;
        terrain.Edited -= onTerrainEdited;
        presenter.Dispose();
        remnantVoxels.Dispose();
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

        if (active && PiecesMode && Candidate(eye, forward) is { } candidate)
        {
            presenter.Ghost = candidate.Piece;
            presenter.GhostValid = Verdict(candidate.Piece, eye) is null;
        }
        else
        {
            presenter.Ghost = null;
        }
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
        PieceHit? remnantHit = CastRemnants(origin, forward);
        bool remnantFirst = remnantHit is PieceHit r && (!ground.Present || r.Distance <= ground.Distance)
            && (pieceHit is not PieceHit p || r.Distance < p.Distance);
        if (edit == TerrainEditKind.Clear && remnantFirst)
        {
            // Taking down what is left of a bitten piece takes all of it.
            PieceRemnant gone = remnants.RemoveAt(remnantHit!.Value.Index);
            remnantVoxels.Redraw([gone.Bounds()]);
            supportDirty = true;
            removed++;
            last = $"removed what was left of a {PieceCatalog.Name(gone.Piece.Kind)}";
            cues.RaiseAt(Cue.Break, frame.ToLocal(remnantHit.Value.Point.X, remnantHit.Value.Point.Y, remnantHit.Value.Point.Z));
            return true;
        }

        if (edit == TerrainEditKind.Clear)
        {
            if (!pieceFirst) return false;
            PlacedPiece gone = set.Pieces[pieceHit!.Value.Index];
            set.RemoveAt(pieceHit.Value.Index);
            supportDirty = true;
            removed++;
            last = $"removed {PieceCatalog.Name(gone.Kind)}";
            cues.RaiseAt(Cue.Break, Middle(gone));
            return true;
        }

        if (!PiecesMode) return false;
        if (Candidate(eye, forward) is not { } candidate)
        {
            Refuse("nothing within reach to build on");
            return true;
        }

        if (Verdict(candidate.Piece, eye) is string why)
        {
            Refuse($"{PieceCatalog.Name(candidate.Piece.Kind)} {why}");
            return true;
        }

        set.Add(candidate.Piece);
        placed++;
        last = $"placed {PieceCatalog.Name(candidate.Piece.Kind)}";
        Trample(candidate.Piece);
        cues.RaiseAt(Cue.Place, Middle(candidate.Piece));
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

    /// <summary>
    /// A charge (#9731): pieces near its centre are destroyed, pieces it only reaches become remnants
    /// with its crater bitten out, and remnants it reaches take the crater too; the voxels redraw what
    /// changed. Returns how many intact pieces it took.
    /// </summary>
    internal int Blast(Vector3 worldCentre, float radius)
    {
        int before = set.Count;
        PieceBlast blast = remnants.Blast(set, new Crater(worldCentre, radius));
        destroyed += blast.Destroyed;
        bitten += blast.Remnants;
        collapsed += blast.Collapsed;
        if (blast.Changed.Count > 0) remnantVoxels.Redraw(blast.Changed);
        if (blast.Destroyed + blast.Remnants + blast.Collapsed > 0) supportDirty = true;
        if (blast.Destroyed + blast.Remnants + blast.Collapsed > 0)
        {
            last = $"charge destroyed {blast.Destroyed}, broke {blast.Remnants}, brought down {blast.Collapsed}";
        }

        return before - set.Count;
    }

    internal void Clear()
    {
        set.Restore([]);
        remnants.Restore([]);
        remnantVoxels.RedrawAll();
    }

    /// <summary>The remnants' merged boxes, rebuilt when the remnants change.</summary>
    private List<List<(Vector3 Centre, Vector3 Half)>> RemnantBoxes()
    {
        if (boxedRemnantRevision != remnants.Revision)
        {
            remnantBoxes.Clear();
            remnantBoxes.AddRange(remnants.Remnants.Select(remnant => remnant.Boxes()));
            boxedRemnantRevision = remnants.Revision;
        }

        return remnantBoxes;
    }

    /// <summary>The nearest remnant box a ray meets within reach.</summary>
    private PieceHit? CastRemnants(Vector3 origin, Vector3 direction)
    {
        PieceHit? best = null;
        List<List<(Vector3 Centre, Vector3 Half)>> boxes = RemnantBoxes();
        for (int index = 0; index < boxes.Count; index++)
        {
            foreach ((Vector3 centre, Vector3 half) in boxes[index])
            {
                if (PieceGeometry.RayBox(origin, direction, centre, Quaternion.Identity, half, out float distance, out Vector3 normal)
                    && distance <= Reach && (best is not PieceHit current || distance < current.Distance))
                {
                    best = new PieceHit(index, distance, origin + (direction * distance), normal);
                }
            }
        }

        return best;
    }

    internal AppearanceFact[] Facts() => presenter.Facts();

    /// <summary>The remnants, one line each: kind, anchor, craters and boxes, for diagnosis.</summary>
    internal string RemnantsReadout() => string.Join("; ", remnants.Remnants.Select((remnant, index) => string.Create(CultureInfo.InvariantCulture,
        $"{PieceCatalog.Name(remnant.Piece.Kind)}@{remnant.Piece.Anchor.X:F2},{remnant.Piece.Anchor.Y:F2},{remnant.Piece.Anchor.Z:F2}/t{remnant.Piece.Turn} craters={remnant.Craters.Count} boxes={RemnantBoxes()[index].Count}")));

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"pieces count={set.Count} placed={placed} removed={removed} refused={refused} mode={(PiecesMode ? "pieces" : "terrain")} remnants={remnants.Count} fell={fell} chunks={remnantVoxels.ResidentChunks} colliders={colliders.Instances} destroyed={destroyed} broken={bitten} collapsed={collapsed} selected={PieceCatalog.Name(kind)}/{PieceCatalog.Name(Material)} ghost={(presenter.Ghost is PlacedPiece g ? $"{g.X * PlacedPiece.GridMetres:F2},{g.Y * PlacedPiece.GridMetres:F2},{g.Z * PlacedPiece.GridMetres:F2}/t{g.Turn}" : "none")} last={last} restore={slot.RestoreOutcome} saves={slot.Saves}");

    /// <summary>What this view meets: the nearest piece and the ground, with their distances, for diagnosis.</summary>
    internal string AimReadout(Vector3 eye, Vector3 forward)
    {
        PieceHit? pieceHit = PieceGeometry.Cast(set.Pieces, frame.ToWorld(eye), forward, Reach);
        SpatialHit ground = terrain.CastView(eye, forward);
        PieceHit? remnantHit = CastRemnants(frame.ToWorld(eye), forward);
        return string.Create(CultureInfo.InvariantCulture,
            $"eye={frame.ToWorld(eye)} forward={forward} piece={(pieceHit is PieceHit hit ? $"{PieceCatalog.Name(set.Pieces[hit.Index].Kind)}@{hit.Distance:F2} point={hit.Point}" : "none")} remnant={(remnantHit is PieceHit broken ? $"{PieceCatalog.Name(remnants.Remnants[broken.Index].Piece.Kind)}@{broken.Distance:F2} point={broken.Point}" : "none")} ground={(ground.Present ? $"{ground.Distance:F2} toi={ground.TimeOfImpact:F3} point={frame.ToWorld(ground.Point)} normal={ground.Normal}" : "none")}");
    }

    /// <summary>Where the selected piece would go from this view: on the first piece or ground it meets.</summary>
    /// <summary>How far under a box's underside solid ground may lie and still bear it.</summary>
    private const float BearingDepth = 0.35f;
    /// <summary>How far apart the ground is sampled under a box's footprint.</summary>
    private const float BearingStep = 0.5f;
    /// <summary>Cues for a collapse: at most this many pieces are heard and seen falling at once.</summary>
    private const int FallCues = 6;

    /// <summary>
    /// Lets what is not held up fall (#9733): every piece and remnant no longer connected, through
    /// touching pieces, to one resting on the ground breaks and is taken away, with splinters and
    /// dust where it was. One pass finds the whole cascade.
    /// </summary>
    private void Fall()
    {
        List<IReadOnlyList<(Vector3 Centre, Vector3 Half)>> nodes = [.. set.Pieces.Select(piece => (IReadOnlyList<(Vector3, Vector3)>)PieceGeometry.Bounds(piece).ToList())];
        nodes.AddRange(RemnantBoxes());
        bool[] standing = PieceSupport.Standing(nodes, index => Grounded(nodes[index]));
        int pieceCount = set.Count, heard = 0, down = 0;
        List<(Vector3 Low, Vector3 High)> redraw = [];
        for (int index = nodes.Count - 1; index >= 0; index--)
        {
            if (standing[index]) continue;
            Vector3 where;
            if (index >= pieceCount)
            {
                PieceRemnant gone = remnants.RemoveAt(index - pieceCount);
                redraw.Add(gone.Bounds());
                where = (gone.Bounds().Low + gone.Bounds().High) / 2;
                where = frame.ToLocal(where.X, where.Y, where.Z);
            }
            else
            {
                where = Middle(set.Pieces[index]);
                set.RemoveAt(index);
            }

            down++;
            if (++heard <= FallCues) cues.RaiseAt(Cue.Break, where);
        }

        if (redraw.Count > 0) remnantVoxels.Redraw(redraw);
        if (down > 0)
        {
            fell += down;
            last = $"{down} piece(s) fell with nothing to hold them up";
        }
    }

    /// <summary>Whether a piece would be held up where it stands: on the ground, or touching what stands.</summary>
    private bool HeldUp(PlacedPiece piece)
    {
        List<(Vector3 Centre, Vector3 Half)> boxes = [.. PieceGeometry.Bounds(piece)];
        if (Grounded(boxes)) return true;
        foreach (PlacedPiece other in set.Pieces)
        {
            if (Vector3.DistanceSquared(other.Anchor, piece.Anchor) > 36f) continue;
            if (PieceSupport.Touch(boxes, [.. PieceGeometry.Bounds(other)])) return true;
        }

        foreach (List<(Vector3 Centre, Vector3 Half)> remnant in RemnantBoxes())
        {
            if (PieceSupport.Touch(boxes, remnant)) return true;
        }

        return false;
    }

    /// <summary>Whether any of the boxes bears on solid ground: a collidable cell within <see cref="BearingDepth"/> under its underside, or one it stands in.</summary>
    private bool Grounded(IReadOnlyList<(Vector3 Centre, Vector3 Half)> boxes)
    {
        foreach ((Vector3 centre, Vector3 half) in boxes)
        {
            float bottom = centre.Y - half.Y;
            long low = (long)MathF.Floor(bottom - BearingDepth), high = (long)MathF.Floor(bottom + 0.05f);
            for (float x = centre.X - half.X; x <= centre.X + half.X + 0.001f; x += Math.Min(BearingStep, Math.Max(half.X * 2, 0.01f)))
            {
                for (float z = centre.Z - half.Z; z <= centre.Z + half.Z + 0.001f; z += Math.Min(BearingStep, Math.Max(half.Z * 2, 0.01f)))
                {
                    for (long y = low; y <= high; y++)
                    {
                        ushort material = terrain.MaterialAt(new VoxelAddress((long)MathF.Floor(x), y, (long)MathF.Floor(z)));
                        if (Content.BlockRegistry.TryGetBySlot(material, out Content.BlockDefinition block) && block.Collidable) return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>How far under a floor's or stairs' underside the ground it tramples may lie.</summary>
    private const float TrampleDepth = 0.75f;

    /// <summary>
    /// A floor or stairs laid on the ground tramples the grass beneath it (#9730): the grass cells
    /// just under its footprint become dirt, which draws the same ground but grows no scatter, so
    /// meadow grass does not grow up through the boards. The trampled patch stays when the piece goes.
    /// </summary>
    private void Trample(PlacedPiece piece)
    {
        if (piece.Kind is not (PieceKind.Floor or PieceKind.Stairs)) return;
        List<VoxelAddress> trampled = [];
        foreach ((Vector3 centre, Vector3 half) in PieceGeometry.Bounds(piece))
        {
            long bottom = (long)MathF.Floor(centre.Y - half.Y - 0.01f), deepest = (long)MathF.Floor(centre.Y - half.Y - TrampleDepth);
            for (long x = (long)MathF.Floor(centre.X - half.X); x < (long)MathF.Ceiling(centre.X + half.X); x++)
            {
                for (long z = (long)MathF.Floor(centre.Z - half.Z); z < (long)MathF.Ceiling(centre.Z + half.Z); z++)
                {
                    for (long y = bottom; y >= deepest; y--)
                    {
                        VoxelAddress cell = new(x, y, z);
                        ushort material = terrain.MaterialAt(cell);
                        if (material == TerrainConstants.EmptyMaterial) continue;
                        if (material == TerrainConstants.GrassMaterial && !trampled.Contains(cell)) trampled.Add(cell);
                        break;
                    }
                }
            }
        }

        if (trampled.Count > 0) terrain.TryEditCells(trampled, TerrainEditKind.Set, TerrainConstants.DirtMaterial);
    }

    /// <summary>Why the piece may not stand there (for the refusal and the ghost's tint), or null if it may.</summary>
    private string? Verdict(PlacedPiece piece, Vector3 eye) =>
        Traps(piece, eye) ? "would stand where you are"
        : set.Check(piece) == PieceOutcome.Placed && !HeldUp(piece) ? "would not be held up"
        : set.Check(piece) switch
        {
            PieceOutcome.Placed => null,
            PieceOutcome.Occupied => "is already there",
            PieceOutcome.Overlaps => "would cut through what stands there",
            PieceOutcome.Full => "cannot be built: the world holds as many pieces as it can",
            PieceOutcome.NotAllowed => "cannot be made of that",
            PieceOutcome outcome => outcome.ToString().ToLowerInvariant(),
        };

    /// <summary>Where a piece's cue is seen and heard: the middle of its boxes, in the walking frame.</summary>
    private Vector3 Middle(PlacedPiece piece)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach ((Vector3 centre, _) in PieceGeometry.Bounds(piece))
        {
            sum += centre;
            count++;
        }

        Vector3 world = sum / Math.Max(1, count);
        return frame.ToLocal(world.X, world.Y, world.Z);
    }

    private (PlacedPiece Piece, float Distance)? Candidate(Vector3 eye, Vector3 forward)
    {
        Vector3 origin = frame.ToWorld(eye);
        PieceHit? pieceHit = PieceGeometry.Cast(set.Pieces, origin, forward, Reach);
        SpatialHit ground = terrain.CastView(eye, forward);
        Vector3 facing = new(forward.X, 0, forward.Z);
        if (facing.LengthSquared() < 1e-6f) facing = Vector3.UnitZ;
        facing = Vector3.Normalize(facing);
        if (CastRemnants(origin, forward) is PieceHit broken && (!ground.Present || broken.Distance <= ground.Distance)
            && (pieceHit is not PieceHit nearer || broken.Distance < nearer.Distance))
        {
            // Aimed at what is left of a bitten piece: stand on its face (it offers no sockets).
            return (PieceGeometry.Place(kind, Material, broken.Point, broken.Normal, facing), broken.Distance);
        }

        if (pieceHit is PieceHit hit && (!ground.Present || hit.Distance <= ground.Distance))
        {
            // Aimed at a piece: attach at its nearest socket for the kind, else stand on the aimed face.
            PlacedPiece host = set.Pieces[hit.Index];
            return (PieceGeometry.Snap(host, kind, Material, hit.Point, facing) ?? PieceGeometry.Place(kind, Material, hit.Point, hit.Normal, facing), hit.Distance);
        }

        if (!ground.Present) return null;
        Vector3 point = frame.ToWorld(ground.Point);
        // The voxel cast reports the face it met but not always a normal; the face's axis serves.
        Vector3 normal = ground.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(ground.Normal) : FaceNormal(ground.Face);
        return (PieceGeometry.Place(kind, Material, point, normal, facing), (float)ground.Distance);
    }

    private static Vector3 FaceNormal(SpatialFace face) => face switch
    {
        SpatialFace.PosX => Vector3.UnitX,
        SpatialFace.NegX => -Vector3.UnitX,
        SpatialFace.NegY => -Vector3.UnitY,
        SpatialFace.PosZ => Vector3.UnitZ,
        SpatialFace.NegZ => -Vector3.UnitZ,
        _ => Vector3.UnitY,
    };

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
        cues.Raise(Cue.Refused);
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
        remnants.Restore(remnantSlot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: PieceRemnant[] broken } ? broken : []);
        savedRemnantRevision = remnants.Revision;
        remnantVoxels.RedrawAll();
    }

    private void SaveRemnants()
    {
        remnantSlot.Save(remnants.Snapshot());
        savedRemnantRevision = remnants.Revision;
    }

    private void Save()
    {
        slot.Save(set.Snapshot());
        savedRevision = set.Revision;
    }
}
