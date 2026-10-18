using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// The overworld places as textured kit meshes (#9992): owns the place kit's meshes, materials and appearances,
/// stands every site's pieces (PlaceLayouts) within the near ground about the player, and gives the pieces
/// near the player to the walking session's static collision, so the player climbs a vantage point's courses
/// and creatures find their way round a ring as they do round built work. Beyond the near ground the horizon
/// draws the places' silhouettes, as it did. The terrain keeps only the places' holes and the vantage lamp.
/// </summary>
internal sealed class PlaceMeshes : IDisposable
{
    /// <summary>Weathered stone: matt, and nothing about a place moves in the wind.</summary>
    private const float Roughness = 0.92f;
    /// <summary>The pieces are laid out again once the player has walked this far: sites are hundreds of metres apart.</summary>
    private const double LayoutStepMetres = 32;
    /// <summary>
    /// Pieces are solid this far about the player (a square's half side), recomposed once they have walked
    /// <see cref="CollisionStepMetres"/>, as deadwood is: wide enough for the creatures' navigation box.
    /// </summary>
    private const double CollisionMetres = 64, CollisionStepMetres = 16;

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly WorldFrame frame;
    private readonly PoiPlacement placement;
    private readonly Func<double, double, double> groundTop;
    private readonly SurfaceMoss? moss;
    /// <summary>The stone's materials by the place's overgrowth: 0 for the ordinary places, one set per overgrown level.</summary>
    private readonly Dictionary<float, PropMaterials> materials = [];
    private readonly IReadOnlyDictionary<string, PlaceKitPiece> kit;
    private readonly PlaceMossCards? cards;
    private readonly Dictionary<(string Piece, float Overgrowth), Loaded> loaded = [];
    /// <summary>Overgrown places set by live debug (#10044), over the climate's own choice.</summary>
    private readonly Dictionary<string, float> overgrowth = [];
    private readonly Func<double, double, MapSample>? geography;

    // A place's own overgrowth from its climate (#10044): moss needs water and some warmth. Wet country (moisture from
    // OvergrowthMoistureFrom, full by OvergrowthMoistureFull) overgrows its places, frozen country barely, deserts not
    // at all; within a wet region a site's own draw spreads the places from a little extra moss to the ivy-choked few.
    private const double OvergrowthMoistureFrom = 0.45, OvergrowthMoistureFull = 0.85, OvergrowthColdFrom = 0.14, OvergrowthColdFull = 0.35;
    private const float OvergrowthBase = 1f, OvergrowthSpread = 5f, OvergrowthStep = 0.5f;
    private readonly Dictionary<float, (Material Material, MaterialRequest Request)> blanketMaterials = [];
    private RenderResource? blanketTexture;
    private readonly List<Standing> standing = [];
    private readonly List<PoiSite> sites = [];
    private (double X, double Z) laidOutAt = (double.NaN, double.NaN);
    private (double X, double Z) collidedAt = (double.NaN, double.NaN);
    private (long X, long Y, long Z) collidedOrigin;
    private int layoutRevision, collidedRevision = -1;

    internal PlaceMeshes(IEngineContext engine, ProductContent content, WorldFrame frame, PoiPlacement placement, Func<double, double, double> groundTop,
        SurfaceMoss? moss = null, Func<double, double, MapSample>? geography = null)
    {
        this.geography = geography;
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.placement = placement ?? throw new ArgumentNullException(nameof(placement));
        this.groundTop = groundTop ?? throw new ArgumentNullException(nameof(groundTop));
        this.moss = moss;
        kit = PlaceKit.Read(content);
        cards = moss is null ? null : new PlaceMossCards(engine, frame, moss);
    }

    /// <summary>
    /// A piece's drawing and its collision shape (the mesh's own triangles, in its metres); on an overgrown place, also
    /// its ivy blanket (a shell over its walls).
    /// </summary>
    private sealed record Loaded(MeshResource Mesh, Appearance Appearance, Vector3[] Positions, Triangle[] Triangles, ulong Asset,
        float Overgrowth, MeshResource? BlanketMesh, Appearance? Blanket);

    private sealed record Standing(string Site, Loaded Piece, float Sink, Vector3 World, Quaternion Rotation, Vector3 Scale);

    // The ivy blanket (#10044): a shell this far off the stone, its seamless ivy tile repeating every BlanketTileMetres,
    // over every face that does not face down further than BlanketLowest.
    private const string BlanketTexturePath = "textures/places/ivy-blanket.png";
    private const float BlanketOffset = 0.05f, BlanketTileMetres = 2f, BlanketLowest = -0.25f, BlanketRoughness = 0.95f;

    /// <summary>Every place stone, growing the surface moss (#10044) at a place's overgrowth.</summary>
    private PropMaterials MaterialsFor(float extra)
    {
        if (materials.TryGetValue(extra, out PropMaterials? found)) return found;
        found = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0, SurfaceMoss.PlaceGrain, extra), moss: moss);
        materials[extra] = found;
        return found;
    }

    /// <summary>
    /// A site's overgrowth from its climate and its own draw (#10044): 0 in dry or frozen country; in wet country from
    /// <see cref="OvergrowthBase"/> to the full <see cref="SurfaceMoss.MaximumOvergrowth"/> for a few, in half steps (one
    /// material set per step).
    /// </summary>
    internal float ClimateOvergrowth(PoiSite site)
    {
        if (geography is null) return 0;
        MapSample here = geography(site.X + 0.5, site.Z + 0.5);
        double wet = SmoothStep(OvergrowthMoistureFrom, OvergrowthMoistureFull, here.Moisture)
            * SmoothStep(OvergrowthColdFrom, OvergrowthColdFull, here.Temperature);
        if (wet <= 0) return 0;
        // The site's own draw, squared so heavy overgrowth is rare.
        double draw = SiteDraw(site.Id);
        double extra = wet * (OvergrowthBase + (OvergrowthSpread * draw * draw));
        return Math.Clamp(MathF.Round((float)extra / OvergrowthStep) * OvergrowthStep, 0, SurfaceMoss.MaximumOvergrowth);
    }

    private static double SiteDraw(string id)
    {
        uint h = 2166136261u;
        foreach (char c in id) h = unchecked((h ^ c) * 16777619u);
        h = unchecked((h ^ (h >> 15)) * 2246822519u);
        return (h & 0xffffffu) / 16777215.0;
    }

    private static double SmoothStep(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>
    /// Sets a site's overgrowth (0 to <see cref="SurfaceMoss.MaximumOvergrowth"/>) over its climate's, or returns it to
    /// its climate's with a negative value, and lays the places out again.
    /// </summary>
    internal string SetOvergrowth(string siteId, float extra)
    {
        if (extra < 0) overgrowth.Remove(siteId);
        else overgrowth[siteId] = Math.Clamp(extra, 0, SurfaceMoss.MaximumOvergrowth);
        laidOutAt = (double.NaN, double.NaN);
        return string.Join(' ', overgrowth.Select(o => $"{o.Key}={o.Value:F1}")) is { Length: > 0 } list ? $"overgrown (set) {list}" : "overgrown (set) none";
    }

    /// <summary>Pieces standing now, for the readout.</summary>
    internal int Pieces => standing.Count;

    /// <summary>Lays the places out again about the player when they have moved on: every site within the near ground.</summary>
    internal void Follow(Vector3 player, double nearEdgeMetres)
    {
        if (!double.IsNaN(laidOutAt.X) && Math.Max(Math.Abs(player.X - laidOutAt.X), Math.Abs(player.Z - laidOutAt.Z)) < LayoutStepMetres) return;
        laidOutAt = (player.X, player.Z);
        sites.Clear();
        placement.CollectSitesNear((long)Math.Floor(player.X), (long)Math.Floor(player.Z), nearEdgeMetres * Math.Sqrt(2), sites);
        standing.Clear();
        foreach (PoiSite site in sites)
        {
            if (Math.Max(Math.Abs(site.X + 0.5 - player.X), Math.Abs(site.Z + 0.5 - player.Z)) > nearEdgeMetres) continue;
            double siteGround = groundTop(site.X + 0.5, site.Z + 0.5);
            float extra = overgrowth.TryGetValue(site.Id, out float set) ? set : ClimateOvergrowth(site);
            foreach (PlacedPart part in PlaceLayouts.For(site))
            {
                if (!kit.TryGetValue(part.Piece, out PlaceKitPiece? piece)) continue;
                double ground = part.OnSiteGround ? siteGround : groundTop(part.X, part.Z);
                // The sink scales with the piece: a stele scaled to its height carries its skirt along.
                standing.Add(new(site.Id, Load(piece, extra), piece.Sink, new Vector3((float)part.X, (float)(ground + part.Rise - (piece.Sink * part.Scale.Y)), (float)part.Z), part.Rotation, part.Scale));
            }
        }

        layoutRevision++;
        LayCards(player);
    }

    /// <summary>Lays the hanging moss cards again over the pieces standing (#10044): after a layout, or when the moss changed.</summary>
    internal void LayCards(Vector3 about) => cards?.Lay([.. standing.Select(s => new PlaceMossCards.Piece(s.Piece.Mesh, s.Piece.Positions, s.Piece.Triangles,
        s.Sink, s.World, s.Rotation, s.Scale, s.Piece.Overgrowth))], new Vector3(about.X, standing.Count > 0 ? standing[0].World.Y : about.Y, about.Z));

    /// <summary>Lays the cards again where they were last laid (the moss's strength changed).</summary>
    internal void RelayCards() => LayCards(new Vector3((float)laidOutAt.X, 0, (float)laidOutAt.Z));

    internal IEnumerable<AppearanceFact> CardFacts => cards?.Facts ?? [];

    internal void AfterAppearanceSnapshot() => cards?.AfterAppearanceSnapshot();

    private Loaded Load(PlaceKitPiece piece, float extra)
    {
        if (loaded.TryGetValue((piece.Name, extra), out Loaded? found)) return found;
        PropMaterials stone = MaterialsFor(extra);
        MeshResourceCreateRequest read = PropMesh.Read(content, piece.Model,
            (role, texture) => stone.For(role, texture ?? throw new InvalidOperationException($"CraftSurvive place piece '{piece.Name}' part '{role}' names no texture.")));
        // The stone's vertex alpha carries its moisture (#10044): wet at the ground, drier up the piece (in its own metres
        // above its sunk base, as PlaceMossCards reads it). Nothing about stone moves in the wind, so the alpha is free.
        ReadOnlySpan<Vector3> at = read.Positions.Span;
        Color[] colours = read.Colors.ToArray();
        for (int i = 0; i < colours.Length; i++) colours[i] = colours[i] with { A = SurfaceMoss.HeightMoisture(at[i].Y - piece.Sink) / SurfaceMoss.MoistureScale };
        MeshResourceCreateRequest request = read with { Colors = colours };
        MeshResource mesh = engine.Graphics.CreateMeshResource(request);
        ReadOnlySpan<uint> indices = request.Indices.Span;
        Triangle[] triangles = new Triangle[indices.Length / 3];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = new Triangle(indices[3 * i], indices[(3 * i) + 1], indices[(3 * i) + 2]);
        MeshResource? blanketMesh = extra > 0 && moss is not null ? engine.Graphics.CreateMeshResource(Blanket(request, BlanketMaterial(extra))) : null;
        Loaded result = new(mesh, engine.Graphics.CreateMeshAppearance(mesh), request.Positions.ToArray(), triangles, CollisionAssetBase + (ulong)loaded.Count,
            extra, blanketMesh, blanketMesh is null ? null : engine.Graphics.CreateMeshAppearance(blanketMesh));
        loaded[(piece.Name, extra)] = result;
        return result;
    }

    /// <summary>The ivy blanket's material for a place's overgrowth: the seamless ivy tile, the blanket shader.</summary>
    private Material BlanketMaterial(float extra)
    {
        if (blanketMaterials.TryGetValue(extra, out var made)) return made.Material;
        blanketTexture ??= engine.Graphics.OpenResource(new RenderResourceRequest(BlanketTexturePath, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
        MaterialRequest request = new MaterialRequest(new Color(1, 1, 1, 1), blanketTexture, BlanketRoughness, new Color(1, 1, 1, 1), Vector3.Zero, 0, false)
            with { Shader = moss!.BlanketShader(SurfaceMoss.PlaceGrain, extra) };
        Material material = engine.Graphics.CreateMaterial(request);
        moss.Register(material, request, SurfaceMoss.PlaceGrain, extra, blanket: true);
        blanketMaterials[extra] = (material, request);
        return material;
    }

    /// <summary>
    /// A piece's ivy blanket: its own triangles that do not face down, each vertex pushed out along its normal by
    /// <see cref="BlanketOffset"/>, with the ivy tile projected on each triangle's dominant plane (in the piece's metres).
    /// The blanket shader shows it only where the moss field is strong.
    /// </summary>
    private static MeshResourceCreateRequest Blanket(MeshResourceCreateRequest piece, Material material)
    {
        ReadOnlySpan<Vector3> p = piece.Positions.Span, n = piece.Normals.Span;
        ReadOnlySpan<Color> col = piece.Colors.Span;
        ReadOnlySpan<uint> idx = piece.Indices.Span;
        List<Color> colours = [];
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<uint> indices = [];
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            Vector3 a = p[(int)idx[t]], b = p[(int)idx[t + 1]], c = p[(int)idx[t + 2]];
            Vector3 face = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (face.Y < BlanketLowest) continue;
            Vector3 abs = Vector3.Abs(face);
            uint first = (uint)positions.Count;
            for (int k = 0; k < 3; k++)
            {
                uint v = idx[t + k];
                Vector3 at = p[(int)v] + (Vector3.Normalize(n[(int)v]) * BlanketOffset);
                positions.Add(at);
                normals.Add(n[(int)v]);
                colours.Add(col[(int)v]);
                Vector2 plane = abs.Y >= abs.X && abs.Y >= abs.Z ? new(at.X, at.Z) : abs.X >= abs.Z ? new(at.Z, -at.Y) : new(at.X, -at.Y);
                uvs.Add(plane / BlanketTileMetres);
            }

            indices.AddRange([first, first + 1, first + 2]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), colours.ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });
    }

    /// <summary>The overgrown places' ivy blankets, standing with their pieces.</summary>
    internal IEnumerable<AppearanceFact> BlanketFacts => standing.Take((int)ProductIds.PlaceMeshLimit)
        .Select((s, index) => (s, index)).Where(e => e.s.Piece.Blanket is not null)
        .Select(e => new AppearanceFact(ProductIds.PlaceBlanketBase + (ulong)e.index, false, 0,
            new Transform(frame.ToLocal(e.s.World.X, e.s.World.Y, e.s.World.Z), e.s.Rotation, e.s.Scale), e.s.Piece.Blanket!, true, RenderLayer.Scene, ShadowCasting.Cast));

    internal IEnumerable<AppearanceFact> Facts => standing.Take((int)ProductIds.PlaceMeshLimit).Select((s, index) =>
        new AppearanceFact(ProductIds.PlaceMeshBase + (ulong)index, false, 0, new Transform(frame.ToLocal(s.World.X, s.World.Y, s.World.Z), s.Rotation, s.Scale),
            s.Piece.Appearance, true, RenderLayer.Scene, ShadowCasting.Cast));

    /// <summary>The places' collision ids, clear of the built work's, the ice's and deadwood's.</summary>
    private const ulong CollisionAssetBase = 0x6000_0000UL, CollisionInstanceBase = 0x6000_0000UL;

    /// <summary>Whether the pieces' collision about the player is behind (laid out again, walked on, or the origin moved).</summary>
    internal bool CollisionStale(Vector3 player) =>
        collidedRevision != layoutRevision || collidedOrigin != frame.Origin || double.IsNaN(collidedAt.X)
        || Math.Max(Math.Abs(player.X - collidedAt.X), Math.Abs(player.Z - collidedAt.Z)) >= CollisionStepMetres;

    /// <summary>
    /// Appends the pieces within <see cref="CollisionMetres"/> of the player to a static-collision composition:
    /// an asset per kit piece used, an instance per piece standing. Returns how many pieces it added.
    /// </summary>
    internal int AddCollision(Vector3 player, List<StaticMeshAsset> assets, List<Vector3> vertices, List<Triangle> triangles, List<StaticMeshInstance> instances)
    {
        collidedAt = (player.X, player.Z);
        collidedRevision = layoutRevision;
        collidedOrigin = frame.Origin;
        HashSet<ulong> shaped = [];
        int added = 0;
        foreach (Standing s in standing)
        {
            if (Math.Max(Math.Abs(s.World.X - player.X), Math.Abs(s.World.Z - player.Z)) > CollisionMetres) continue;
            if (shaped.Add(s.Piece.Asset))
            {
                assets.Add(new StaticMeshAsset(s.Piece.Asset, (uint)vertices.Count, (uint)s.Piece.Positions.Length, (uint)triangles.Count, (uint)s.Piece.Triangles.Length));
                vertices.AddRange(s.Piece.Positions);
                triangles.AddRange(s.Piece.Triangles);
            }

            instances.Add(new StaticMeshInstance(CollisionInstanceBase + (ulong)added, s.Piece.Asset,
                new Transform(frame.ToLocal(s.World.X, s.World.Y, s.World.Z), s.Rotation, s.Scale)));
            added++;
        }

        return added;
    }

    internal string Readout() => (cards?.Readout() ?? "mossCards=none") + " " + OvergrowthReadout() + " " + PieceReadout();

    private string OvergrowthReadout() => "overgrowth " + string.Join(' ', standing.GroupBy(s => s.Site).Take(8)
        .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}={g.First().Piece.Overgrowth:F1}")));

    private string PieceReadout() =>
        string.Create(CultureInfo.InvariantCulture, $"places sites={sites.Count} pieces={standing.Count} kinds={loaded.Count} at={laidOutAt.X:F0},{laidOutAt.Z:F0} ")
        + string.Join(' ', standing.GroupBy(s => s.Site).Take(8).Select(g => $"{g.Key}:{g.Count()}"));

    /// <summary>Disposes the kit; call only after a snapshot that no longer names its appearances.</summary>
    public void Dispose()
    {
        standing.Clear();
        foreach (Loaded piece in loaded.Values)
        {
            piece.Appearance.Dispose();
            piece.Mesh.Dispose();
            piece.Blanket?.Dispose();
            piece.BlanketMesh?.Dispose();
        }

        loaded.Clear();
        cards?.Dispose();
        foreach ((Material material, _) in blanketMaterials.Values)
        {
            moss?.Unregister(material);
            material.Dispose();
        }

        blanketMaterials.Clear();
        blanketTexture?.Dispose();
        foreach (PropMaterials stone in materials.Values) stone.Dispose();
        materials.Clear();
    }
}
