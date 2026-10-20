using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The overworld's trees' meshes and the shelter of the near ones (#9665, #9741). The recipe decides
/// where each tree stands, what it is and how it is turned and sized, and writes only an invisible
/// trunk core into the voxels for bodies to collide with; the trees are drawn as Engine vegetation
/// records from these meshes (<see cref="TreeTiles"/>, #10017). A tree stands only while its core and
/// footing do (TreeFelling): an edit that breaks its core or digs out its footing fells it, clearing the
/// rest of the core in the same edit. The near standing trees' crowns are kept here as query colliders.
/// </summary>
internal sealed class TerrainTrees : IDisposable
{
    private const string ModelsFolder = "models/";
    private const string FarSuffix = "-far";
    private const string LeavesRole = PropMaterials.LeavesRole;

    /// <summary>Crowns shelter this far from the player: the walking residency's retained reach, by default.</summary>
    private long drawMetres = TerrainConstants.RetainedChunkRadius * TerrainConstants.ChunkEdgeLength;

    /// <summary>The walking residency's retained reach (#9759): near trees' crowns shelter out to it.</summary>
    internal long NearMetres
    {
        set
        {
            if (value == drawMetres) return;
            drawMetres = value;
            stale = true;
        }
    }
    /// <summary>The near set is rebuilt when the player has moved this far, or the terrain changed.</summary>
    private const long RebuildMetres = 8;
    /// <summary>How far a trunk is sunk below its ground's surface, so a slope never shows its base.</summary>
    internal const float Sink = 0.25f;

    private const double CellCentre = 0.5;

    // Wind: trunks lean a little from their roots; leaves also flutter, most at the canopy's edge.
    private const float BarkWindBend = 0.006f, LeafWindBend = 0.01f, LeafWindFlutter = 0.12f;
    private const float BarkRoughness = 0.9f, LeafRoughness = 0.8f;
    private static readonly Color White = new(1, 1, 1, 1);

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly WorldFrame frame;

    /// <summary>Plants other than trees drawn outside the vegetation fields (a cave's sinkhole forest, #10129), each loaded once.</summary>
    private readonly Dictionary<(ushort Species, int Variant), Appearance> plantLooks = [];
    private readonly Material bark, leaves;
    /// <summary>Materials for textured parts (procedural trees, #9685).</summary>
    private readonly PropMaterials textured;
    private readonly List<MeshResource> meshes = [];
    private readonly Dictionary<ushort, Appearance[]> kinds = [];
    private readonly Dictionary<ushort, Appearance[]> farKinds = [];
    /// <summary>Each near variant's leaves, as a box in the mesh's metres (null: a variant without leaves).</summary>
    private readonly Dictionary<ushort, (Vector3 Min, Vector3 Max)?[]> crowns = [];
    private SpatialEntityCollider[] canopies = [];
    // Recipe heights and standing checks do not change while streaming: keep them for the near
    // window, forgetting the checks when the edits change, rather than sampling each tree again
    // whenever a chunk arrives.
    private readonly Dictionary<(long X, long Z), double> treeGrounds = [];
    private readonly Dictionary<(long X, long Z), bool> nearStands = [];
    private ulong nearStandsRevision = ulong.MaxValue;
    private readonly List<TerrainTree> candidates = [];
    private (long X, long Z)? builtAround;
    private TerrainRecipe? builtRecipe;
    private ulong builtRevision = ulong.MaxValue;
    private int builtResidents = -1;
    private bool stale = true;

    internal TerrainTrees(IEngineContext engine, ProductContent content, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        textured = new PropMaterials(engine, role => role == LeavesRole
            ? new PropMaterials.Finish(LeafRoughness, LeafWindBend, LeafWindFlutter)
            : new PropMaterials.Finish(BarkRoughness, BarkWindBend, 0));
        bark = engine.Graphics.CreateMaterial(new MaterialRequest(White, default(RenderResourceReference), BarkRoughness, White, Vector3.Zero, 0, false) with
        {
            FlatShading = true,
            WindBend = BarkWindBend,
        });
        leaves = engine.Graphics.CreateMaterial(new MaterialRequest(White, default(RenderResourceReference), LeafRoughness, White, Vector3.Zero, 0, true) with
        {
            FlatShading = true,
            WindBend = LeafWindBend,
            WindFlutter = LeafWindFlutter,
        });
        try
        {
            // Every tree species in the flora catalog (#10032), each variant with its far variant (NAME-far).
            foreach (FloraSpecies species in FloraCatalog.Shared.Species.Where(s => s.Role == FloraRole.Tree))
            {
                string folder = ModelsFolder + species.Folder + "/";
                kinds[species.Id] = [.. species.Variants.Select(name => Load(content, folder + name))];
                farKinds[species.Id] = [.. species.Variants.Select(name => Load(content, folder + name + FarSuffix))];
                crowns[species.Id] = [.. species.Variants.Select(name => PropMesh.RoleBounds(content, folder + name + PropMesh.Suffix, LeavesRole))];
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The crowns of the near standing trees as Engine colliders in the local frame (#9741): what a
    /// query for cover overhead adds, so standing under leaves is shelter. Each is the leaves' box,
    /// turned with the tree and narrowed to <see cref="CanopyShelterShare"/> of its spread, since a
    /// crown's edge is thin. Only for queries: nothing collides with leaves.
    /// </summary>
    internal ReadOnlyMemory<SpatialEntityCollider> Canopies => canopies;

    /// <summary>The share of a crown's spread that shelters: its thin edge does not.</summary>
    private const float CanopyShelterShare = 0.8f;

    /// <summary>Each kind's near and far meshes by variant, which the vegetation tree records draw (#10017).</summary>
    internal IReadOnlyDictionary<ushort, Appearance[]> NearMeshes => kinds;
    internal IReadOnlyDictionary<ushort, Appearance[]> FarMeshes => farKinds;


    /// <summary>
    /// The look of any flora catalog species' variant by name (#10129: a cave's sinkhole forest draws the world's own flora):
    /// a tree's near mesh, or a plant's mesh loaded once in the trees' materials; null for a name the catalog lacks. The
    /// variant wraps round the species' variants.
    /// </summary>
    internal Appearance? FloraLook(string name, int variant)
    {
        if (FloraCatalog.Shared.Species.FirstOrDefault(s => s.Name == name) is not FloraSpecies species || species.Variants.Count == 0)
        {
            return null;
        }

        int which = variant % species.Variants.Count;
        if (kinds.TryGetValue(species.Id, out Appearance[]? trees))
        {
            return trees[which % trees.Length];
        }

        if (!plantLooks.TryGetValue((species.Id, which), out Appearance? look))
        {
            look = Load(content, ModelsFolder + species.Folder + "/" + species.Variants[which]);
            plantLooks[(species.Id, which)] = look;
        }

        return look;
    }

    /// <summary>The world moved: the crowns are rebuilt in the new local frame on the next follow.</summary>
    internal void Invalidate() => stale = true;

    /// <summary>
    /// Keeps the crowns of the trees around the player whose trunk core still stands in a resident
    /// chunk, rebuilt when the player has moved a few metres or the edits or residency changed.
    /// </summary>
    internal void Follow(VoxelAddress center, TerrainRecipe recipe, Func<VoxelAddress, ushort> materialAt,
        Func<VoxelAddress, bool> resident, ulong editRevision, int residentChunks)
    {
        if (!ReferenceEquals(recipe, builtRecipe))
        {
            treeGrounds.Clear();
            nearStands.Clear();
            builtRecipe = recipe;
            stale = true;
        }

        bool nearStale = stale || builtRevision != editRevision || builtResidents != residentChunks
            || builtAround is not (long x, long z) || Math.Abs(center.X - x) >= RebuildMetres || Math.Abs(center.Z - z) >= RebuildMetres;
        if (nearStale)
        {
            BuildNear(center, recipe, materialAt, resident, editRevision);
            builtAround = (center.X, center.Z);
            builtRevision = editRevision;
            builtResidents = residentChunks;
            stale = false;
        }

    }

    private void BuildNear(VoxelAddress center, TerrainRecipe recipe, Func<VoxelAddress, ushort> materialAt,
        Func<VoxelAddress, bool> resident, ulong editRevision)
    {
        if (nearStandsRevision != editRevision)
        {
            nearStands.Clear();
            nearStandsRevision = editRevision;
        }
        long keep = drawMetres + RebuildMetres;
        foreach ((long X, long Z) column in treeGrounds.Keys.Where(column => Math.Abs(column.X - center.X) > keep || Math.Abs(column.Z - center.Z) > keep).ToList())
        {
            treeGrounds.Remove(column);
            nearStands.Remove(column);
        }

        candidates.Clear();
        recipe.TreesIn(center.X - drawMetres, center.Z - drawMetres, center.X + drawMetres, center.Z + drawMetres, candidates);
        List<SpatialEntityCollider> crowned = new(candidates.Count);
        foreach (TerrainTree tree in candidates)
        {
            VoxelAddress core = new(tree.X, tree.GroundY, tree.Z);
            if (crowned.Count >= ProductIds.TreeCanopyLimit || !resident(core))
            {
                continue;
            }

            // Streaming changes which cores are resident, not whether an already checked tree
            // stands. Re-read its core/support after edits, instead of sampling them for every
            // near tree whenever any chunk arrives. This matters for thousands of canopies.
            if (!nearStands.TryGetValue((tree.X, tree.Z), out bool stands))
                nearStands[(tree.X, tree.Z)] = stands = TreeFelling.Stands(tree, materialAt);
            if (!stands) continue;

            if (Crown(tree, recipe, ProductIds.TreeCanopyBase + (ulong)crowned.Count) is SpatialEntityCollider crown) crowned.Add(crown);
        }

        canopies = [.. crowned];
    }


    /// <summary>A standing tree's crown as a query collider: its leaves' box turned, scaled and moved with the tree.</summary>
    private SpatialEntityCollider? Crown(TerrainTree tree, TerrainRecipe recipe, ulong id)
    {
        (Vector3 Min, Vector3 Max)?[] variants = crowns[tree.Species];
        if (variants[Math.Min(variants.Length - 1, (int)(tree.Variant * variants.Length))] is not (Vector3 min, Vector3 max)) return null;
        if (!treeGrounds.TryGetValue((tree.X, tree.Z), out double ground))
        {
            ground = recipe.ContinuousHeightAt(tree.X, tree.Z) + FarGround.GroundTopFace;
            treeGrounds[(tree.X, tree.Z)] = ground;
        }

        Vector3 at = frame.ToLocal(tree.X + CellCentre, ground - Sink, tree.Z + CellCentre);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)tree.Yaw);
        float scale = (float)tree.Scale;
        Vector3 middle = (min + max) / 2, half = (max - min) / 2 * new Vector3(CanopyShelterShare, 1, CanopyShelterShare);
        Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        foreach (float sx in (ReadOnlySpan<float>)[-1, 1])
        {
            foreach (float sz in (ReadOnlySpan<float>)[-1, 1])
            {
                foreach (float sy in (ReadOnlySpan<float>)[-1, 1])
                {
                    Vector3 corner = Vector3.Transform((middle + (half * new Vector3(sx, sy, sz))) * scale, turn) + at;
                    low = Vector3.Min(low, corner);
                    high = Vector3.Max(high, corner);
                }
            }
        }

        return new SpatialEntityCollider(id, low, high, TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll,
            Enabled: true, StaticCollider: true, Trigger: false);
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"trees canopies={canopies.Length} candidates={candidates.Count} groundCache={treeGrounds.Count} meshes={meshes.Count}");

    public void Dispose()
    {
        foreach (Appearance look in plantLooks.Values)
        {
            look.Dispose();
        }

        plantLooks.Clear();
        foreach (Appearance[] variants in kinds.Values)
        {
            foreach (Appearance appearance in variants)
            {
                appearance.Dispose();
            }
        }

        kinds.Clear();
        foreach (Appearance[] variants in farKinds.Values)
        {
            foreach (Appearance appearance in variants)
            {
                appearance.Dispose();
            }
        }

        farKinds.Clear();
        foreach (MeshResource mesh in meshes)
        {
            mesh.Dispose();
        }

        meshes.Clear();
        textured.Dispose();
        leaves.Dispose();
        bark.Dispose();
    }

    /// <summary>One tree mesh: the bark part on the bark material and the leaves on the fluttering one.</summary>
    private Appearance Load(ProductContent content, string path)
    {
        MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, path + PropMesh.Suffix,
            (role, texture) => texture is null ? role == LeavesRole ? leaves : bark : textured.For(role, texture)));
        meshes.Add(mesh);
        return engine.Graphics.CreateMeshAppearance(mesh);
    }
}
