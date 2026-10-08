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
/// Beyond the near trees, a far band (#9677) out to <see cref="FarMetres"/> draws each tree's
/// lighter far variant (the same skeleton, fewer and larger leaf cards) on the recipe's ground, so
/// a forest runs on to the fog instead of ending at the residency's edge. Its decisions are kept
/// here per anchor cell and made a slice at a time as the player moves.
/// </summary>
internal sealed class TerrainTrees : IDisposable
{
    internal const string ManifestPath = "models/trees/trees.json";
    private const string MeshFolder = "models/trees/";
    private const string LeavesRole = PropMaterials.LeavesRole;

    /// <summary>Trees are drawn this far from the player: the walking residency's reach.</summary>
    private const long DrawMetres = 144;
    /// <summary>The drawn set is rebuilt when the player has moved this far, or the terrain changed.</summary>
    private const long RebuildMetres = 8;
    /// <summary>How far a trunk is sunk below its ground's surface, so a slope never shows its base.</summary>
    private const float Sink = 0.25f;

    /// <summary>The far band reaches this far (a circle), past the near trees to the fog.</summary>
    internal const long FarMetres = 400;
    /// <summary>The far band's anchor cells are swept again when the player has moved this many cells.</summary>
    private const long FarStepCells = 4;
    /// <summary>At most this many anchor cells are decided per update, so a teleport fills the band over a few frames.</summary>
    private const int FarDecisionsPerUpdate = 1_500;
    /// <summary>Far trees stand on the recipe's ground under the far field's coarser mesh: sunk deeper.</summary>
    private const float FarSink = 1.0f;
    private static readonly long FarReachCells = (FarMetres / GenerationConstants.FeatureCellSize) + 1;
    /// <summary>The anchor cells of the far band about the player's own, nearest first.</summary>
    private static readonly (long X, long Z)[] FarOffsets =
    [
        .. from dx in Enumerable.Range((int)-FarReachCells, (int)((2 * FarReachCells) + 1))
           from dz in Enumerable.Range((int)-FarReachCells, (int)((2 * FarReachCells) + 1))
           where ((long)dx * dx) + ((long)dz * dz) <= FarReachCells * FarReachCells
           orderby ((long)dx * dx) + ((long)dz * dz)
           select ((long)dx, (long)dz),
    ];
    private const double CellCentre = 0.5;
    /// <summary>The natural ground is the top face of its unrounded top cell (TerrainDensity).</summary>
    private const double GroundTopFace = 1.0;

    // Wind: trunks lean a little from their roots; leaves also flutter, most at the canopy's edge.
    private const float BarkWindBend = 0.006f, LeafWindBend = 0.01f, LeafWindFlutter = 0.12f;
    private const float BarkRoughness = 0.9f, LeafRoughness = 0.8f;
    private static readonly Color White = new(1, 1, 1, 1);

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly Material bark, leaves;
    /// <summary>Materials for textured parts (procedural trees, #9685).</summary>
    private readonly PropMaterials textured;
    private readonly List<MeshResource> meshes = [];
    private readonly Dictionary<TreeKind, Appearance[]> kinds = [];
    private readonly Dictionary<TreeKind, Appearance[]> farKinds = [];
    /// <summary>The far band's decisions by anchor cell (null: the cell owns no tree), and which of its trees stand.</summary>
    private readonly Dictionary<(long X, long Z), TerrainTree?> farCells = [];
    private readonly Dictionary<(long X, long Z), bool> farStands = [];
    private readonly HashSet<(long X, long Z)> nearDrawn = [];
    private TerrainRecipe? farRecipe;
    private (long X, long Z)? farSweptAround;
    private bool farPending;
    private ulong farStandsRevision = ulong.MaxValue;
    private AppearanceFact[] nearFacts = [];
    private int farCount;
    private long farReach = FarMetres;

    /// <summary>How far the far band reaches (0 turns it off), up to <see cref="FarMetres"/>; for measuring its cost.</summary>
    internal long FarReach
    {
        get => farReach;
        set
        {
            farReach = Math.Clamp(value, 0, FarMetres);
            stale = true;
        }
    }
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
            using JsonDocument manifest = JsonDocument.Parse(content.ReadText(ManifestPath));
            foreach (JsonProperty kind in manifest.RootElement.GetProperty("kinds").EnumerateObject())
            {
                TreeKind id = Enum.Parse<TreeKind>(kind.Name, ignoreCase: true);
                kinds[id] = [.. kind.Value.EnumerateArray().Select(name => Load(content, name.GetString()!))];
            }

            foreach (JsonProperty kind in manifest.RootElement.GetProperty("far").EnumerateObject())
            {
                TreeKind id = Enum.Parse<TreeKind>(kind.Name, ignoreCase: true);
                farKinds[id] = [.. kind.Value.EnumerateArray().Select(name => Load(content, name.GetString()!))];
            }

            foreach (TreeKind kind in Enum.GetValues<TreeKind>())
            {
                if (!kinds.TryGetValue(kind, out Appearance[]? variants) || variants.Length == 0
                    || !farKinds.TryGetValue(kind, out Appearance[]? far) || far.Length != variants.Length)
                {
                    throw new InvalidOperationException(
                        $"CraftSurvive tree manifest '{ManifestPath}' needs a mesh and a matching far variant for each {kind}.");
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
    /// Draws the trees around the player: near, those whose trunk core still stands in a resident
    /// chunk; far, every other standing tree out to <see cref="FarMetres"/>. The near set is rebuilt
    /// when the player has moved a few metres or the edits or residency changed; the far band's
    /// decisions are made a slice per update, and its facts follow either change.
    /// </summary>
    internal void Follow(VoxelAddress center, TerrainRecipe recipe, Func<VoxelAddress, ushort> materialAt,
        Func<VoxelAddress, bool> resident, Func<VoxelAddress, bool> edited, ulong editRevision, int residentChunks)
    {
        bool farChanged = SweepFar(center, recipe);
        bool nearStale = stale || builtRevision != editRevision || builtResidents != residentChunks
            || builtAround is not (long x, long z) || Math.Abs(center.X - x) >= RebuildMetres || Math.Abs(center.Z - z) >= RebuildMetres;
        if (nearStale)
        {
            BuildNear(center, recipe, materialAt, resident);
            builtAround = (center.X, center.Z);
            builtRevision = editRevision;
            builtResidents = residentChunks;
            stale = false;
        }

        if (nearStale || farChanged)
        {
            facts = [.. nearFacts, .. BuildFar(center, materialAt, edited, editRevision, nearFacts.Length)];
        }
    }

    private void BuildNear(VoxelAddress center, TerrainRecipe recipe, Func<VoxelAddress, ushort> materialAt, Func<VoxelAddress, bool> resident)
    {
        candidates.Clear();
        nearDrawn.Clear();
        recipe.TreesIn(center.X - DrawMetres, center.Z - DrawMetres, center.X + DrawMetres, center.Z + DrawMetres, candidates);
        List<AppearanceFact> drawn = new(candidates.Count);
        foreach (TerrainTree tree in candidates)
        {
            VoxelAddress core = new(tree.X, tree.GroundY, tree.Z);
            if (drawn.Count >= ProductIds.TreeObjectLimit || !resident(core) || !TreeFelling.Stands(tree, materialAt))
            {
                continue;
            }

            nearDrawn.Add((tree.X, tree.Z));
            drawn.Add(Place(tree, recipe, kinds, Sink, drawn.Count));
        }

        nearFacts = [.. drawn];
    }

    /// <summary>
    /// Decides the far band's anchor cells not yet known, nearest first by sweep order, within this
    /// update's budget, and forgets those now beyond the band. Returns whether anything changed.
    /// </summary>
    private bool SweepFar(VoxelAddress center, TerrainRecipe recipe)
    {
        long cell = GenerationConstants.FeatureCellSize;
        long cx = GridMath.FloorDivide(center.X, cell), cz = GridMath.FloorDivide(center.Z, cell);
        long reach = FarReachCells;
        bool changed = false;
        if (!ReferenceEquals(recipe, farRecipe))
        {
            farCells.Clear();
            farStands.Clear();
            farRecipe = recipe;
            farSweptAround = null;
            changed = true;
        }

        if (farSweptAround is not (long sx, long sz) || Math.Abs(cx - sx) >= FarStepCells || Math.Abs(cz - sz) >= FarStepCells)
        {
            farSweptAround = (cx, cz);
            farPending = true;
            long keep = reach + FarStepCells;
            foreach ((long X, long Z) key in farCells.Keys.Where(key => Math.Abs(key.X - cx) > keep || Math.Abs(key.Z - cz) > keep).ToList())
            {
                farCells.Remove(key);
                farStands.Remove(key);
                changed = true;
            }
        }

        if (!farPending) return changed;
        int budget = FarDecisionsPerUpdate;
        foreach ((long dx, long dz) in FarOffsets)
        {
            (long X, long Z) key = (cx + dx, cz + dz);
            if (farCells.ContainsKey(key)) continue;
            if (budget-- == 0) return true;
            farCells[key] = recipe.TreeInCell(key.X, key.Z);
            changed = true;
        }

        farPending = false;
        return changed;
    }

    /// <summary>
    /// The far band's facts: every decided tree within reach that the near set does not draw and that
    /// stands. A tree whose core and footing were never edited stands as generated; only an edited
    /// one is read back through the materials, so the band never asks the recipe about cores.
    /// </summary>
    private List<AppearanceFact> BuildFar(VoxelAddress center, Func<VoxelAddress, ushort> materialAt,
        Func<VoxelAddress, bool> edited, ulong editRevision, int firstIndex)
    {
        if (farStandsRevision != editRevision)
        {
            farStands.Clear();
            farStandsRevision = editRevision;
        }

        List<AppearanceFact> drawn = [];
        long reach = farReach * farReach;
        foreach (((long X, long Z) key, TerrainTree? decided) in farCells)
        {
            if (decided is not TerrainTree tree || firstIndex + drawn.Count >= ProductIds.TreeObjectLimit
                || nearDrawn.Contains((tree.X, tree.Z))
                || ((tree.X - center.X) * (tree.X - center.X)) + ((tree.Z - center.Z) * (tree.Z - center.Z)) > reach)
            {
                continue;
            }

            if (!farStands.TryGetValue(key, out bool stands))
            {
                stands = !TreeFelling.Core(tree).Append(TreeFelling.Support(tree)).Any(edited) || TreeFelling.Stands(tree, materialAt);
                farStands[key] = stands;
            }

            if (stands)
            {
                drawn.Add(Place(tree, farRecipe!, farKinds, FarSink, firstIndex + drawn.Count));
            }
        }

        farCount = drawn.Count;
        return drawn;
    }

    private AppearanceFact Place(TerrainTree tree, TerrainRecipe recipe, Dictionary<TreeKind, Appearance[]> meshes, float sink, int index)
    {
        Appearance[] variants = meshes[tree.Kind];
        Appearance mesh = variants[Math.Min(variants.Length - 1, (int)(tree.Variant * variants.Length))];
        double ground = recipe.ContinuousHeightAt(tree.X, tree.Z) + GroundTopFace;
        Vector3 at = frame.ToLocal(tree.X + CellCentre, ground - sink, tree.Z + CellCentre);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)tree.Yaw);
        return new(ProductIds.TreeObjectBase + (ulong)index, false, 0, new(at, turn, Vector3.One * (float)tree.Scale), mesh, true, RenderLayer.Scene);
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"trees drawn={facts.Length} near={nearFacts.Length} far={farCount} farCells={farCells.Count} farPending={farPending} candidates={candidates.Count} meshes={meshes.Count}");

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
    private Appearance Load(ProductContent content, string name)
    {
        MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, MeshFolder + name + PropMesh.Suffix,
            (role, texture) => texture is null ? role == LeavesRole ? leaves : bark : textured.For(role, texture)));
        meshes.Add(mesh);
        return engine.Graphics.CreateMeshAppearance(mesh);
    }
}
