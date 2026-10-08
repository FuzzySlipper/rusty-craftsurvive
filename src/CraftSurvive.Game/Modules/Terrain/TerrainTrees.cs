using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The overworld's trees, drawn as stylised low-poly meshes (#9665). The recipe decides where each
/// tree stands, what it is and how it is turned and sized, and writes only an invisible trunk core
/// into the voxels for bodies to collide with; this presents a mesh on every resident core near the
/// player. A tree is drawn only while it stands (TreeFelling): an edit that breaks its core or digs
/// out its footing fells it, clearing the rest of the core in the same edit.
/// </summary>
internal sealed class TerrainTrees : IDisposable
{
    internal const string ManifestPath = "models/trees/trees.json";
    private const string MeshFolder = "models/trees/";
    private const string LeavesRole = "leaves";

    /// <summary>Trees are drawn this far from the player: the walking residency's reach.</summary>
    private const long DrawMetres = 144;
    /// <summary>The drawn set is rebuilt when the player has moved this far, or the terrain changed.</summary>
    private const long RebuildMetres = 8;
    /// <summary>How far a trunk is sunk below its ground's surface, so a slope never shows its base.</summary>
    private const float Sink = 0.25f;
    private const double CellCentre = 0.5;
    /// <summary>The natural ground is the top face of its unrounded top cell (TerrainDensity).</summary>
    private const double GroundTopFace = 1.0;

    // Wind: trunks lean a little from their roots; leaves also flutter, most at the canopy's edge.
    private const float BarkWindBend = 0.006f, LeafWindBend = 0.01f, LeafWindFlutter = 0.12f;
    private const float BarkRoughness = 0.9f, LeafRoughness = 0.8f;
    /// <summary>Leaf cards (#9685) are cut out where their cluster texture is transparent.</summary>
    private const float LeafCardAlphaCutoff = 0.5f;
    private static readonly Color White = new(1, 1, 1, 1);

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly Material bark, leaves;
    /// <summary>Materials for textured parts (procedural trees, #9685), by role and texture.</summary>
    private readonly Dictionary<(string Role, string Texture), Material> textured = [];
    private readonly Dictionary<string, RenderResource> textures = [];
    private readonly List<MeshResource> meshes = [];
    private readonly Dictionary<TreeKind, Appearance[]> kinds = [];
    private readonly List<TerrainTree> candidates = [];
    private AppearanceFact[] facts = [];
    private (long X, long Z)? builtAround;
    private ulong builtRevision = ulong.MaxValue;
    private int builtResidents = -1;
    private bool stale = true;

    internal TerrainTrees(IEngineContext engine, ProductContent content, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
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
            using JsonDocument manifest = JsonDocument.Parse(content.ReadText(ManifestPath));
            foreach (JsonProperty kind in manifest.RootElement.GetProperty("kinds").EnumerateObject())
            {
                TreeKind id = Enum.Parse<TreeKind>(kind.Name, ignoreCase: true);
                kinds[id] = [.. kind.Value.EnumerateArray().Select(name => Load(content, name.GetString()!))];
            }

            foreach (TreeKind kind in Enum.GetValues<TreeKind>())
            {
                if (!kinds.TryGetValue(kind, out Appearance[]? variants) || variants.Length == 0)
                {
                    throw new InvalidOperationException($"CraftSurvive tree manifest '{ManifestPath}' has no mesh for {kind}.");
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The drawn trees, for the appearance snapshot.</summary>
    internal AppearanceFact[] Facts => facts;

    internal int Count => facts.Length;

    /// <summary>The terrain changed under the trees, or the world moved: rebuild on the next follow.</summary>
    internal void Invalidate() => stale = true;

    /// <summary>
    /// Draws the trees around the player: those whose trunk core still stands in a resident chunk.
    /// Rebuilt only when the player has moved a few metres, the edits or residency changed.
    /// </summary>
    internal void Follow(VoxelAddress center, TerrainRecipe recipe, Func<VoxelAddress, ushort> materialAt,
        Func<VoxelAddress, bool> resident, ulong editRevision, int residentChunks)
    {
        if (!stale && builtRevision == editRevision && builtResidents == residentChunks
            && builtAround is (long x, long z) && Math.Abs(center.X - x) < RebuildMetres && Math.Abs(center.Z - z) < RebuildMetres)
        {
            return;
        }

        candidates.Clear();
        recipe.TreesIn(center.X - DrawMetres, center.Z - DrawMetres, center.X + DrawMetres, center.Z + DrawMetres, candidates);
        List<AppearanceFact> drawn = new(candidates.Count);
        foreach (TerrainTree tree in candidates)
        {
            VoxelAddress core = new(tree.X, tree.GroundY, tree.Z);
            if (drawn.Count >= ProductIds.TreeObjectLimit || !resident(core) || !TreeFelling.Stands(tree, materialAt))
            {
                continue;
            }

            Appearance[] variants = kinds[tree.Kind];
            Appearance mesh = variants[Math.Min(variants.Length - 1, (int)(tree.Variant * variants.Length))];
            double ground = recipe.ContinuousHeightAt(tree.X, tree.Z) + GroundTopFace;
            Vector3 at = frame.ToLocal(tree.X + CellCentre, ground - Sink, tree.Z + CellCentre);
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)tree.Yaw);
            drawn.Add(new(ProductIds.TreeObjectBase + (ulong)drawn.Count, false, 0,
                new(at, turn, Vector3.One * (float)tree.Scale), mesh, true, RenderLayer.Scene));
        }

        facts = [.. drawn];
        builtAround = (center.X, center.Z);
        builtRevision = editRevision;
        builtResidents = residentChunks;
        stale = false;
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"trees drawn={facts.Length} candidates={candidates.Count} meshes={meshes.Count}");

    public void Dispose()
    {
        foreach (Appearance[] variants in kinds.Values)
        {
            foreach (Appearance appearance in variants)
            {
                appearance.Dispose();
            }
        }

        kinds.Clear();
        foreach (MeshResource mesh in meshes)
        {
            mesh.Dispose();
        }

        meshes.Clear();
        foreach (Material material in textured.Values)
        {
            material.Dispose();
        }

        textured.Clear();
        foreach (RenderResource texture in textures.Values)
        {
            texture.Dispose();
        }

        textures.Clear();
        leaves.Dispose();
        bark.Dispose();
    }

    /// <summary>One tree mesh: the bark part on the bark material and the leaves on the fluttering one.</summary>
    private Appearance Load(ProductContent content, string name)
    {
        MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, MeshFolder + name + PropMesh.Suffix,
            (role, texture) => texture is null ? role == LeavesRole ? leaves : bark : Textured(role, texture)));
        meshes.Add(mesh);
        return engine.Graphics.CreateMeshAppearance(mesh);
    }

    /// <summary>
    /// A procedural tree's material (#9685): its bark or leaf-card texture, nearest-filtered so the
    /// low-resolution texels read as one chunky style. Bark is smooth-shaded by its tube normals;
    /// leaf cards are alpha-cut and one-sided (each card is built with both windings), so both sides
    /// keep the crown-facing normals they were built with.
    /// </summary>
    private Material Textured(string role, string path)
    {
        if (textured.TryGetValue((role, path), out Material? material)) return material;
        if (!textures.TryGetValue(path, out RenderResource? texture))
        {
            RenderResourceInfo info = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.Nearest, TextureWrap.Repeat));
            if (info.Kind != RenderResourceKind.Texture || info.ByteLength == 0)
            {
                throw new InvalidOperationException($"CraftSurvive tree texture '{path}' must be a non-empty Engine texture.");
            }

            texture = info.Handle;
            textures[path] = texture;
        }

        bool isLeaves = role == LeavesRole;
        material = engine.Graphics.CreateMaterial(new MaterialRequest(White, texture, isLeaves ? LeafRoughness : BarkRoughness,
            White, Vector3.Zero, 0, false) with
        {
            AlphaMode = isLeaves ? MaterialAlphaMode.Mask : MaterialAlphaMode.Opaque,
            AlphaCutoff = isLeaves ? LeafCardAlphaCutoff : 0,
            WindBend = isLeaves ? LeafWindBend : BarkWindBend,
            WindFlutter = isLeaves ? LeafWindFlutter : 0,
        });
        textured[(role, path)] = material;
        return material;
    }
}
