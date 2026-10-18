using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// Hanging moss and ivy on the places (#10044): painted alpha-cut cards, the depth the flat painted moss cannot
/// give. A curtain hangs from a lip (an edge where a piece's top meets its side), a smaller one lies flat on a side
/// face (ivy on a wall or a stele, in the heart of a painted patch), and a cushion stands at a wall's foot, each only
/// where the surface moss (<see cref="SurfaceMoss.Field"/>) is thick, so a card always
/// continues a painted patch. Every kit piece's lips and feet are found once from its own triangles; the
/// cards of every place standing are one mesh, built again whenever the places are laid out again or the
/// moss's strength changes, around its own centre so the frame origin can move under it.
/// </summary>
internal sealed class PlaceMossCards : IDisposable
{
    private const string AtlasPath = "textures/places/moss-cards.png";
    /// <summary>Flat moss mats seen from above (make-grass-atlas.py --centre): a 2x2 atlas of patches.</summary>
    private const string MatsPath = "textures/places/moss-mats.png";
    /// <summary>Icicle fringes (#10068, scripts/generate-icicles.py): a 2x2 atlas, the top row's icicles shorter than the bottom's.</summary>
    private const string IciclesPath = "textures/places/icicles.png";
    /// <summary>
    /// Icicles hang from this share of the lips once the snow shown on the places is this deep (metres); the longer fringes
    /// (the atlas's bottom row) join once it is <see cref="IciclesLongMetres"/>. A fringe is this wide (64 texels a metre, as the
    /// atlas is painted) and stands a little proud of a moss curtain.
    /// </summary>
    private const float IciclesFromMetres = 0.02f, IciclesLongMetres = 0.1f, IcicleShare = 0.55f, IcicleWidth = 1f, IcicleStandoff = 0.045f;
    /// <summary>Ice is smooth: it catches the light.</summary>
    private const float IceRoughness = 0.3f;
    /// <summary>Icicles hang only from a level top (its normal rises this much, not a rib's curve) with this much wall below it (metres).</summary>
    private const float IcicleLevelTop = 0.9f, IcicleLeastDrop = 0.7f;
    /// <summary>
    /// On an overgrown place the cards' threshold falls by at most this much extra strength: beyond it the blanket covers the
    /// walls, and cards would only coat it.
    /// </summary>
    private const float CardOvergrowthLimit = 1.5f;
    /// <summary>Leaf clusters standing out of the blanket: this wide, tilted this many degrees out from the wall.</summary>
    private const float ClusterMinimum = 0.45f, ClusterMaximum = 0.8f, ClusterTiltDegrees = 55f;

    /// <summary>Mats on tops: one per this many metres, this wide, lying this far off the stone.</summary>
    private const float MatSpacing = 0.55f, MatMinimum = 0.45f, MatMaximum = 0.85f, MatStandoff = 0.015f;
    /// <summary>How far above the threshold the moss must be for a card: cards continue thick moss, not every speck.</summary>
    private const float ThickMargin = 0.0f;
    /// <summary>A card on a side face grows only this far into a painted patch, so its edge stays painted moss.</summary>
    private const float FaceMargin = 0.0f;
    /// <summary>Side faces are thinned more than lips: a patch carries a card or two, not a coat.</summary>
    private const float FaceSpacing = 0.6f;
    /// <summary>A card on a face: its width, and its hang as a share of the width.</summary>
    private const float FaceMinimum = 0.3f, FaceMaximum = 0.6f, FaceHang = 1.3f;
    /// <summary>A face card turns up to this many degrees about its face's normal, so its straight top edge never lines up with its neighbours'.</summary>
    private const float FaceRollDegrees = 35f;
    /// <summary>A top is a face whose normal rises at least this much; a side, one this near the horizontal.</summary>
    private const float TopRise = 0.6f, SideRise = 0.35f;
    /// <summary>Lips and feet are thinned to one per this many metres (a piece's mesh edges are a few centimetres long).</summary>
    private const float AnchorSpacing = 0.55f;
    /// <summary>A foot is a side face this low above the piece's ground (metres).</summary>
    private const float FootHeight = 0.45f;
    /// <summary>Curtain width and its hang as a share of the width; cushion width and height share.</summary>
    private const float CurtainMinimum = 0.8f, CurtainMaximum = 1.4f, CurtainHang = 1.5f, CushionMinimum = 0.6f, CushionMaximum = 1.0f, CushionRise = 0.6f;
    /// <summary>A card stands this far off the stone so it does not fight it for depth.</summary>
    private const float Standoff = 0.03f;
    /// <summary>The cards near the player at most.</summary>
    private const int MaximumCards = 1500;
    private const float Roughness = 0.95f;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly SurfaceMoss moss;
    private readonly PropMaterials materials, ice;
    private readonly Material material, mats, icicles;
    private readonly Dictionary<MeshResource, (Anchor[] Lips, Anchor[] Feet, Anchor[] Faces, Anchor[] Tops)> anchors = [];

    private enum Kind
    {
        Curtain,
        Cushion,
        Face,
        Mat,
        Cluster,
        Icicle,
    }
    private MeshResource? mesh;
    private Appearance? appearance;
    private readonly List<(MeshResource Mesh, Appearance Look)> retiring = [];
    private Vector3 centre;

    /// <summary>Snow this deep (metres) buries the cushions at a place's feet (#10068).</summary>
    private const float CushionsBuriedMetres = 0.06f;

    internal PlaceMossCards(IEngineContext engine, WorldFrame frame, SurfaceMoss moss)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.moss = moss ?? throw new ArgumentNullException(nameof(moss));
        // Cards are the leaves role: alpha-cut, one-sided with both windings; nothing about them sways (stone holds them).
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0));
        material = materials.For(PropMaterials.LeavesRole, AtlasPath);
        mats = materials.For(PropMaterials.LeavesRole, MatsPath);
        ice = new PropMaterials(engine, _ => new PropMaterials.Finish(IceRoughness, 0, 0));
        icicles = ice.For(PropMaterials.LeavesRole, IciclesPath);
    }

    /// <summary>A place on a piece where a card may grow: a point and the outward horizontal its face looks along, in the piece's metres.</summary>
    private readonly record struct Anchor(Vector3 At, Vector3 Out, Vector3 Normal);

    /// <summary>One piece standing, as the cards need it.</summary>
    internal readonly record struct Piece(MeshResource Key, Vector3[] Positions, Triangle[] Triangles, float Sink, Vector3 World, Quaternion Rotation, Vector3 Scale,
        float Overgrowth = 0);

    internal int Cards { get; private set; }

    /// <summary>Cards laid by kind (curtain, cushion, face) and the face anchors considered, for the readout.</summary>
    private readonly int[] byKind = new int[6];
    private int faceAnchors;

    /// <summary>Lays the cards out again over the pieces standing now.</summary>
    internal void Lay(IReadOnlyList<Piece> pieces, Vector3 about)
    {
        centre = about;
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<uint> indices = [], matIndices = [], iceIndices = [];
        int cards = 0;
        Array.Clear(byKind);
        faceAnchors = 0;
        foreach (Piece piece in pieces)
        {
            float threshold = moss.ThresholdAt(Math.Min(piece.Overgrowth, CardOvergrowthLimit)) + ThickMargin;
            float blanket = piece.Overgrowth > 0 ? moss.BlanketThreshold(piece.Overgrowth) : float.PositiveInfinity;
            if (cards >= MaximumCards) break;
            (Anchor[] lips, Anchor[] feet, Anchor[] faces, Anchor[] tops) = AnchorsOf(piece);
            if (moss.Snow >= IciclesFromMetres)
            {
                // Icicles (#10068) along the lips wherever they freeze, moss or none.
                foreach (Anchor lip in lips)
                {
                    if (lip.Normal.Y < IcicleLevelTop || lip.At.Y - piece.Sink < IcicleLeastDrop) continue;
                    Vector3 at = piece.World + Vector3.Transform(lip.At * piece.Scale, piece.Rotation);
                    float pick = Hash(at + new Vector3(0.37f, 0, 0.71f));
                    if (pick >= IcicleShare || cards >= MaximumCards) continue;
                    Icicle(at - centre, Vector3.Normalize(Vector3.Transform(lip.Out, piece.Rotation)), pick / IcicleShare, moss.Snow >= IciclesLongMetres, positions, normals, uvs, iceIndices);
                    byKind[(int)Kind.Icicle]++;
                    cards++;
                }
            }

            foreach ((Anchor[] list, Kind kind) in new[] { (lips, Kind.Curtain), (feet, Kind.Cushion), (faces, Kind.Face), (tops, Kind.Mat) })
            {
                foreach (Anchor anchor in list)
                {
                    Vector3 at = piece.World + Vector3.Transform(anchor.At * piece.Scale, piece.Rotation);
                    Vector3 outward = Vector3.Normalize(Vector3.Transform(anchor.Out, piece.Rotation));
                    Vector3 normal = Vector3.Normalize(Vector3.Transform(anchor.Normal, piece.Rotation));
                    if (kind == Kind.Face) faceAnchors++;
                    float field = moss.Field(at.X, at.Y, at.Z, normal, SurfaceMoss.PlaceGrain, SurfaceMoss.HeightMoisture(anchor.At.Y - piece.Sink));
                    if (!float.IsFinite(field) || field < threshold + (kind == Kind.Face ? FaceMargin : 0) || cards >= MaximumCards) continue;
                    // Snow (#10068) buries the cushions at the feet once it lies deep; mats on tops take the Engine's cover as the stone does.
                    if (kind == Kind.Cushion && moss.Snow >= CushionsBuriedMetres) continue;
                    float pick = Hash(at);
                    Kind laid = kind;
                    if (field >= blanket && kind is Kind.Face or Kind.Mat)
                    {
                        // Under the blanket a face card or a mat would be hidden: leaves stand out of it instead.
                        laid = Kind.Cluster;
                    }

                    if (laid == Kind.Mat) Mat(at - centre, normal, pick, positions, normals, uvs, matIndices);
                    else if (laid == Kind.Cluster) Cluster(at - centre, normal, pick, positions, normals, uvs, indices);
                    else Card(at - centre, outward, normal, laid, pick, positions, normals, uvs, indices);
                    byKind[(int)laid]++;
                    cards++;
                }
            }
        }

        Retire();
        Cards = cards;
        if (cards == 0) return;
        // The Engine refuses an empty group: one for the cards and one for the mats, each only if it has triangles.
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        if (indices.Count > 0)
        {
            groups.Add(new(0, 0, (uint)indices.Count));
            bindings.Add(new(0, material));
        }

        if (matIndices.Count > 0)
        {
            uint slot = (uint)bindings.Count;
            groups.Add(new(slot, (uint)indices.Count, (uint)matIndices.Count));
            bindings.Add(new(slot, mats));
        }

        if (iceIndices.Count > 0)
        {
            uint slot = (uint)bindings.Count;
            groups.Add(new(slot, (uint)(indices.Count + matIndices.Count), (uint)iceIndices.Count));
            bindings.Add(new(slot, icicles));
        }

        MeshResourceCreateRequest request = new(positions.ToArray(), normals.ToArray(), uvs.ToArray(), ReadOnlyMemory<Color>.Empty,
            indices.Concat(matIndices).Concat(iceIndices).ToArray(), groups.ToArray(), bindings.ToArray());
        mesh = engine.Graphics.CreateMeshResource(request);
        appearance = engine.Graphics.CreateMeshAppearance(mesh);
    }

    /// <summary>
    /// One card, both windings: a curtain hanging down the face from a lip, a smaller curtain lying on a side face from
    /// its anchor (centred on it), or a cushion standing at a foot.
    /// </summary>
    private static void Card(Vector3 at, Vector3 outward, Vector3 faceNormal, Kind kind, float pick, List<Vector3> positions, List<Vector3> normals,
        List<Vector2> uvs, List<uint> indices)
    {
        // A face card lies on its face, which may lean or taper (a stele): it hangs down the face's own slope and
        // stands off along the face's normal, instead of hanging plumb into the stone.
        if (kind == Kind.Face) outward = Vector3.Normalize(faceNormal);
        bool hanging = kind != Kind.Cushion;
        Vector3 across = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, outward));
        float width = kind switch
        {
            Kind.Curtain => Lerp(CurtainMinimum, CurtainMaximum, pick),
            Kind.Face => Lerp(FaceMinimum, FaceMaximum, pick),
            _ => Lerp(CushionMinimum, CushionMaximum, pick),
        };
        float height = width * kind switch { Kind.Curtain => CurtainHang, Kind.Face => FaceHang, _ => CushionRise };
        Vector3 down = -Vector3.UnitY;
        if (kind == Kind.Face)
        {
            Vector3 slope = Vector3.Cross(across, outward);
            down = slope.Y < 0 ? Vector3.Normalize(slope) : -Vector3.Normalize(slope);
            Quaternion roll = Quaternion.CreateFromAxisAngle(outward, ((Fraction(pick * 13.7f) * 2) - 1) * FaceRollDegrees * MathF.PI / 180);
            down = Vector3.Transform(down, roll);
            across = Vector3.Transform(across, roll);
            // It hangs from a little above its anchor, so the anchor is in its leafy upper part.
            at -= down * (height * 0.3f);
        }
        // Atlas (make-grass-atlas.py, 2x2, v counted down): curtains in the top row with their attached edge at the
        // cell's base (v 0.5), cushions in the bottom row standing on their base (v 1). A second pick mirrors one.
        bool second = Fraction(pick * 7.13f) >= 0.5f;
        bool mirror = Fraction(pick * 3.71f) >= 0.5f;
        float u0 = second ? 0.5f : 0, u1 = u0 + 0.5f;
        if (mirror) (u0, u1) = (u1, u0);
        Vector3 off = outward * (hanging ? Standoff : Standoff * 4);
        Vector3 baseLeft = at + off - (across * (width / 2)), baseRight = at + off + (across * (width / 2));
        Vector3 rise = hanging ? down * height : (Vector3.UnitY * height) + (outward * height * 0.15f);
        float vBase = hanging ? 0.5f : 1f, vEnd = hanging ? 0f : 0.5f;
        Vector3[] corners = [baseLeft, baseRight, baseRight + rise, baseLeft + rise];
        Vector2[] corner = [new(u0, vBase), new(u1, vBase), new(u1, vEnd), new(u0, vEnd)];
        foreach (Vector3 facing in new[] { outward, -outward })
        {
            uint first = (uint)positions.Count;
            for (int i = 0; i < 4; i++)
            {
                positions.Add(corners[i]);
                normals.Add(facing);
                uvs.Add(corner[i]);
            }

            // The quad's winding faces its normal: counter-clockwise seen from that side.
            Vector3 n = Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]);
            bool counter = Vector3.Dot(n, facing) > 0;
            if (counter) indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            else indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }
    }

    /// <summary>
    /// An icicle fringe hanging plumb from a lip, both windings, one of the atlas's cells: the top row's short icicles,
    /// or once the snow lies deep any of the four. The fringe's band (the cell's base) sits at the lip.
    /// </summary>
    private static void Icicle(Vector3 at, Vector3 outward, float pick, bool lengthened, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<uint> indices)
    {
        Vector3 across = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, outward));
        int cell = (int)(Fraction(pick * 5.17f) * (lengthened ? 4 : 2)) & 3;
        float u0 = (cell % 2) * 0.5f, vBase = cell < 2 ? 0.5f : 1f;
        bool mirror = Fraction(pick * 3.71f) >= 0.5f;
        float u1 = u0 + 0.5f;
        if (mirror) (u0, u1) = (u1, u0);
        Vector3 off = outward * IcicleStandoff;
        Vector3 left = at + off - (across * (IcicleWidth / 2)), right = at + off + (across * (IcicleWidth / 2));
        Vector3 down = -Vector3.UnitY * IcicleWidth;
        Vector3[] corners = [left, right, right + down, left + down];
        Vector2[] uv = [new(u0, vBase), new(u1, vBase), new(u1, vBase - 0.5f), new(u0, vBase - 0.5f)];
        foreach (Vector3 facing in new[] { outward, -outward })
        {
            uint first = (uint)positions.Count;
            for (int i = 0; i < 4; i++)
            {
                positions.Add(corners[i]);
                normals.Add(facing);
                uvs.Add(uv[i]);
            }

            bool counter = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]), facing) > 0;
            if (counter) indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            else indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }
    }

    /// <summary>
    /// A leaf cluster standing out of the ivy blanket: two crossed curtain cards rooted at the surface, tilted out from it
    /// toward the sky, so an overgrown wall has bulk and a broken outline rather than a flat wallpaper of ivy.
    /// </summary>
    private static void Cluster(Vector3 at, Vector3 normal, float pick, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<uint> indices)
    {
        Vector3 n = Vector3.Normalize(normal);
        // Out from the surface and up: on a wall, leaves lean out and droop; on a top, they stand up.
        Vector3 up = Vector3.Normalize(Vector3.Lerp(n, Vector3.UnitY, 0.35f));
        Vector3 side = Vector3.Normalize(Vector3.Cross(MathF.Abs(up.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, up));
        float width = Lerp(ClusterMinimum, ClusterMaximum, pick);
        float spin = Fraction(pick * 4.7f) * MathF.PI;
        bool second = Fraction(pick * 7.13f) >= 0.5f;
        float u0 = second ? 0.5f : 0, u1 = u0 + 0.5f;
        for (int cross = 0; cross < 2; cross++)
        {
            Quaternion turn = Quaternion.CreateFromAxisAngle(up, spin + (cross * MathF.PI / 2));
            Vector3 across = Vector3.Transform(side, turn);
            // Each card leans ClusterTiltDegrees away from the surface normal, toward its own side, so the pair splays.
            Vector3 rise = Vector3.Normalize(Vector3.Transform(up, Quaternion.CreateFromAxisAngle(across, (cross == 0 ? 1 : -1) * ClusterTiltDegrees * MathF.PI / 360)));
            Vector3 face = Vector3.Normalize(Vector3.Cross(across, rise));
            Vector3[] corners = [at - (across * (width / 2)), at + (across * (width / 2)), at + (across * (width / 2)) + (rise * width * 0.8f), at - (across * (width / 2)) + (rise * width * 0.8f)];
            // The curtain sprite's attached edge (v 0.5) at the root, its tendrils (v 0) at the tip.
            Vector2[] uv = [new(u0, 0.5f), new(u1, 0.5f), new(u1, 0f), new(u0, 0f)];
            foreach (Vector3 facing in new[] { face, -face })
            {
                uint first = (uint)positions.Count;
                for (int i = 0; i < 4; i++)
                {
                    positions.Add(corners[i]);
                    normals.Add(facing);
                    uvs.Add(uv[i]);
                }

                bool counter = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]), facing) > 0;
                if (counter) indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
                else indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
            }
        }
    }

    /// <summary>
    /// A flat moss mat lying on a top, turned at random about the top's normal and showing one of the mats atlas's four
    /// patches; one winding, facing up off the stone.
    /// </summary>
    private static void Mat(Vector3 at, Vector3 normal, float pick, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<uint> indices)
    {
        Vector3 n = Vector3.Normalize(normal);
        Vector3 t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ, n));
        Quaternion yaw = Quaternion.CreateFromAxisAngle(n, Fraction(pick * 5.31f) * MathF.Tau);
        Vector3 u = Vector3.Transform(t, yaw), v = Vector3.Cross(n, u);
        float half = Lerp(MatMinimum, MatMaximum, pick) / 2;
        int cell = (int)(Fraction(pick * 9.17f) * 4) & 3;
        float u0 = (cell % 2) * 0.5f, v0 = (cell / 2) * 0.5f;
        Vector3 c = at + (n * MatStandoff);
        Vector3[] corners = [c - (u * half) - (v * half), c + (u * half) - (v * half), c + (u * half) + (v * half), c - (u * half) + (v * half)];
        Vector2[] uv = [new(u0, v0 + 0.5f), new(u0 + 0.5f, v0 + 0.5f), new(u0 + 0.5f, v0), new(u0, v0)];
        uint first = (uint)positions.Count;
        for (int i = 0; i < 4; i++)
        {
            positions.Add(corners[i]);
            normals.Add(n);
            uvs.Add(uv[i]);
        }

        bool counter = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]), n) > 0;
        if (counter) indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        else indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
    }

    /// <summary>A piece's lips, feet, side faces and tops, found once from its triangles.</summary>
    private (Anchor[] Lips, Anchor[] Feet, Anchor[] Faces, Anchor[] Tops) AnchorsOf(Piece piece)
    {
        if (anchors.TryGetValue(piece.Key, out var found)) return found;
        Vector3[] p = piece.Positions;
        // Edges by their ends' positions (rounded to a millimetre), so faces that share an edge but not vertices still meet.
        Dictionary<(long, long, long, long, long, long), List<int>> edges = [];
        Vector3[] faceNormal = new Vector3[piece.Triangles.Length];
        for (int t = 0; t < piece.Triangles.Length; t++)
        {
            Triangle tri = piece.Triangles[t];
            uint[] v = [tri.A, tri.B, tri.C];
            Vector3 cross = Vector3.Cross(p[v[1]] - p[v[0]], p[v[2]] - p[v[0]]);
            // A degenerate triangle has no normal; NaN would slip through every facing test below, so it faces nowhere.
            faceNormal[t] = cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) : Vector3.Zero;
            for (int e = 0; e < 3; e++)
            {
                var key = EdgeKey(p[v[e]], p[v[(e + 1) % 3]]);
                if (!edges.TryGetValue(key, out List<int>? faces)) edges[key] = faces = [];
                faces.Add(t);
            }
        }

        List<Anchor> lips = [], feet = [], onFaces = [], tops = [];
        HashSet<(int, int, int)> faceTaken = [], topTaken = [];
        HashSet<(int, int, int)> taken = [];
        foreach (((long, long, long, long, long, long) key, List<int> faces) in edges)
        {
            int top = faces.FirstOrDefault(f => faceNormal[f].Y >= TopRise, -1);
            int side = faces.FirstOrDefault(f => faceNormal[f] != Vector3.Zero && Math.Abs(faceNormal[f].Y) <= SideRise, -1);
            if (top < 0 || side < 0) continue;
            Vector3 a = new(key.Item1 / 1000f, key.Item2 / 1000f, key.Item3 / 1000f), b = new(key.Item4 / 1000f, key.Item5 / 1000f, key.Item6 / 1000f);
            Vector3 mid = (a + b) / 2;
            if (!taken.Add(Cell(mid))) continue;
            Vector3 flat = new(faceNormal[side].X, 0, faceNormal[side].Z);
            if (flat.LengthSquared() < 1e-6f) continue;
            lips.Add(new(mid, Vector3.Normalize(flat), faceNormal[top]));
        }

        for (int t = 0; t < piece.Triangles.Length; t++)
        {
            Vector3 n = faceNormal[t];
            if (n == Vector3.Zero || Math.Abs(n.Y) > SideRise) continue;
            Triangle tri = piece.Triangles[t];
            Vector3 c = (p[tri.A] + p[tri.B] + p[tri.C]) / 3;
            float above = c.Y - piece.Sink;
            if (above < 0) continue;
            Vector3 flat = Vector3.Normalize(new Vector3(n.X, 0, n.Z));
            if (above > FootHeight)
            {
                // Higher up a side face: ivy lies on it, thinned more than lips.
                (int, int, int) cell = ((int)Math.Floor(c.X / FaceSpacing), (int)Math.Floor(c.Y / FaceSpacing), (int)Math.Floor(c.Z / FaceSpacing));
                if (faceTaken.Add(cell)) onFaces.Add(new(c, flat, n));
                continue;
            }

            Vector3 foot = new(c.X, piece.Sink, c.Z);
            if (!taken.Add(Cell(foot))) continue;
            feet.Add(new(foot, flat, n));
        }

        for (int t = 0; t < piece.Triangles.Length; t++)
        {
            Vector3 n = faceNormal[t];
            if (n.Y < TopRise) continue;
            Triangle tri = piece.Triangles[t];
            Vector3 c = (p[tri.A] + p[tri.B] + p[tri.C]) / 3;
            if (c.Y < piece.Sink) continue;
            (int, int, int) cell = ((int)Math.Floor(c.X / MatSpacing), (int)Math.Floor(c.Y / MatSpacing), (int)Math.Floor(c.Z / MatSpacing));
            if (topTaken.Add(cell)) tops.Add(new(c, Vector3.UnitX, n));
        }

        var result = (lips.ToArray(), feet.ToArray(), onFaces.ToArray(), tops.ToArray());
        anchors[piece.Key] = result;
        return result;
    }

    private static (long, long, long, long, long, long) EdgeKey(Vector3 a, Vector3 b)
    {
        (long, long, long) ka = (Milli(a.X), Milli(a.Y), Milli(a.Z)), kb = (Milli(b.X), Milli(b.Y), Milli(b.Z));
        if (ka.CompareTo(kb) > 0) (ka, kb) = (kb, ka);
        return (ka.Item1, ka.Item2, ka.Item3, kb.Item1, kb.Item2, kb.Item3);
    }

    private static long Milli(float v) => (long)Math.Round(v * 1000);

    private static (int, int, int) Cell(Vector3 at) =>
        ((int)Math.Floor(at.X / AnchorSpacing), (int)Math.Floor(at.Y / AnchorSpacing), (int)Math.Floor(at.Z / AnchorSpacing));

    private static float Hash(Vector3 at)
    {
        uint h = unchecked((uint)Math.Floor(at.X * 7.0) * 73856093u ^ (uint)Math.Floor(at.Y * 7.0) * 19349663u ^ (uint)Math.Floor(at.Z * 7.0) * 83492791u);
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return (h & 0xffffffu) / 16777215f;
    }

    private static float Fraction(float x) => x - MathF.Floor(x);

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    internal IEnumerable<AppearanceFact> Facts => appearance is null ? [] :
        [new AppearanceFact(ProductIds.PlaceMossCardsObject, false, 0, new Transform(frame.ToLocal(centre.X, centre.Y, centre.Z), Quaternion.Identity, Vector3.One),
            appearance, true, RenderLayer.Scene, ShadowCasting.Cast)];

    private void Retire()
    {
        if (mesh is not null && appearance is not null) retiring.Add((mesh, appearance));
        mesh = null;
        appearance = null;
    }

    /// <summary>Disposes the meshes replaced since the last snapshot, which no longer names them.</summary>
    internal void AfterAppearanceSnapshot()
    {
        foreach ((MeshResource m, Appearance look) in retiring)
        {
            look.Dispose();
            m.Dispose();
        }

        retiring.Clear();
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture, $"mossCards={Cards} curtains={byKind[0]} cushions={byKind[1]} faces={byKind[2]}/{faceAnchors} mats={byKind[3]} clusters={byKind[4]} icicles={byKind[5]} pieces={anchors.Count}");

    public void Dispose()
    {
        Retire();
        AfterAppearanceSnapshot();
        materials.Dispose();
        ice.Dispose();
    }
}
