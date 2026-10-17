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
    private readonly PropMaterials materials;
    private readonly IReadOnlyDictionary<string, PlaceKitPiece> kit;
    private readonly Dictionary<string, Loaded> loaded = [];
    private readonly List<Standing> standing = [];
    private readonly List<PoiSite> sites = [];
    private (double X, double Z) laidOutAt = (double.NaN, double.NaN);
    private (double X, double Z) collidedAt = (double.NaN, double.NaN);
    private (long X, long Y, long Z) collidedOrigin;
    private int layoutRevision, collidedRevision = -1;

    internal PlaceMeshes(IEngineContext engine, ProductContent content, WorldFrame frame, PoiPlacement placement, Func<double, double, double> groundTop,
        SurfaceMoss? moss = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.placement = placement ?? throw new ArgumentNullException(nameof(placement));
        this.groundTop = groundTop ?? throw new ArgumentNullException(nameof(groundTop));
        // Every place is stone, and grows the surface moss (#10044).
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0, SurfaceMoss.PlaceClumpMetres), moss: moss);
        kit = PlaceKit.Read(content);
    }

    /// <summary>A piece's drawing and its collision shape (the mesh's own triangles, in its metres).</summary>
    private sealed record Loaded(MeshResource Mesh, Appearance Appearance, Vector3[] Positions, Triangle[] Triangles, ulong Asset);

    private sealed record Standing(string Site, Loaded Piece, Vector3 World, Quaternion Rotation, Vector3 Scale);

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
            foreach (PlacedPart part in PlaceLayouts.For(site))
            {
                if (!kit.TryGetValue(part.Piece, out PlaceKitPiece? piece)) continue;
                double ground = part.OnSiteGround ? siteGround : groundTop(part.X, part.Z);
                // The sink scales with the piece: a stele scaled to its height carries its skirt along.
                standing.Add(new(site.Id, Load(piece), new Vector3((float)part.X, (float)(ground + part.Rise - (piece.Sink * part.Scale.Y)), (float)part.Z), part.Rotation, part.Scale));
            }
        }

        layoutRevision++;
    }

    private Loaded Load(PlaceKitPiece piece)
    {
        if (loaded.TryGetValue(piece.Name, out Loaded? found)) return found;
        MeshResourceCreateRequest request = PropMesh.Read(content, piece.Model,
            (role, texture) => materials.For(role, texture ?? throw new InvalidOperationException($"CraftSurvive place piece '{piece.Name}' part '{role}' names no texture.")));
        MeshResource mesh = engine.Graphics.CreateMeshResource(request);
        ReadOnlySpan<uint> indices = request.Indices.Span;
        Triangle[] triangles = new Triangle[indices.Length / 3];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = new Triangle(indices[3 * i], indices[(3 * i) + 1], indices[(3 * i) + 2]);
        Loaded result = new(mesh, engine.Graphics.CreateMeshAppearance(mesh), request.Positions.ToArray(), triangles, CollisionAssetBase + (ulong)loaded.Count);
        loaded[piece.Name] = result;
        return result;
    }

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

    internal string Readout() =>
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
        }

        loaded.Clear();
        materials.Dispose();
    }
}
