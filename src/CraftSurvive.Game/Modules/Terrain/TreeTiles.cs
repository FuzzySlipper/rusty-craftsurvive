using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.VegetationTiles;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The overworld's trees as Engine vegetation records (#10017). Each tile's trees are the recipe's own
/// (<see cref="TerrainRecipe.TreeInCell"/>), decided on the update's thread just before the tile is committed,
/// because the recipe's keyed draws are an Engine service; a felled tree is left out, and an edit re-asks the
/// tiles under it as ground cover does. Each species variant is one layer keeping its mesh's own bark and leaf
/// materials: the near mesh out to <see cref="NearReach"/>, then its far mesh, then from
/// <see cref="ImpostorStart"/> an Engine-baked impostor of the near mesh to the far reach; each level dithers
/// into the next across <see cref="LodBand"/>. Trunk cores, felling and the canopies' shelter stay with
/// <see cref="TerrainTrees"/>.
/// </summary>
internal sealed class TreeTiles : IDisposable
{
    private const double TileMetres = 128;
    private const uint TileCells = 16;

    /// <summary>The near mesh draws this far, as the near trees do today (the retained residency reach).</summary>
    internal const float NearReach = TerrainConstants.RetainedChunkRadius * TerrainConstants.ChunkEdgeLength;

    /// <summary>The farthest trees can be drawn, for measuring: the far field reaches about this far at the longest view.</summary>
    internal const long MaximumReach = 2_000;

    /// <summary>Trees are drawn as impostor cards from this far.</summary>
    private const float ImpostorStart = 350;

    /// <summary>The metres before a level's start where both levels draw, each copy dithered from one to the other.</summary>
    private const float LodBand = 10;

    /// <summary>The far trees shrink away over this share of their reach.</summary>
    private const float FarFadeShare = 0.1f;

    /// <summary>A tree record's layer key: one per species variant.</summary>
    private const uint LayerBase = 0x100, VariantsPerKind = 4;
    private const uint MaximumTreesPerLayer = 65_536;
    /// <summary>
    /// A tile's tree decisions take about 15 ms on the update's thread (#10032), so one tile is committed an
    /// update: a fresh view fills nearest first over a few seconds rather than stalling an update.
    /// </summary>
    private const int CommitsPerUpdate = 1;
    private const double FocusStepMetres = 4;
    private const float CellCentre = 0.5f;

    private readonly IEngineContext engine;
    private readonly TerrainRecipe recipe;
    private readonly TerrainTrees trees;
    private readonly Func<VoxelAddress, ushort> materialAt;
    private readonly Func<VoxelAddress, bool> edited;
    private readonly VegetationField field;
    private readonly VegetationStreamer streamer;
    private (double X, double Z) focus;
    private long recordsMade, decidedTiles, decidingTicks, slowestTicks;

    internal TreeTiles(IEngineContext engine, TerrainRecipe recipe, SpatialSession session, TerrainTrees trees,
        Func<VoxelAddress, ushort> materialAt, Func<VoxelAddress, bool> edited, VoxelAddress centre)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        this.trees = trees ?? throw new ArgumentNullException(nameof(trees));
        this.materialAt = materialAt ?? throw new ArgumentNullException(nameof(materialAt));
        this.edited = edited ?? throw new ArgumentNullException(nameof(edited));
        focus = (centre.X + CellCentre, centre.Z + CellCentre);
        field = engine.Vegetation.Create(new VegetationFieldCreateRequest(session, TileMetres, TileCells,
            VegetationFocus.At(focus.X, centre.Y, focus.Z)));
        streamer = new VegetationStreamer(engine, field, new VegetationTiles.Sampler(new VegetationGround(recipe)), AddTrees, CommitsPerUpdate);
    }

    /// <summary>Sets every species variant's layer out to <paramref name="farReach"/>: near mesh, far mesh, then impostor.</summary>
    internal void Grow(float farReach)
    {
        foreach ((ushort kind, Appearance[] near) in trees.NearMeshes)
        {
            Appearance[] far = trees.FarMeshes[kind];
            for (int variant = 0; variant < Math.Min(near.Length, (int)VariantsPerKind); variant++)
            {
                uint key = Key(kind, variant);
                engine.Vegetation.SetLayer(new VegetationLayerRequest(field, key, near[variant], default, 0, farReach) with
                {
                    Grounds = ulong.MaxValue,
                    Fade = farReach * FarFadeShare,
                    MaximumInstances = MaximumTreesPerLayer,
                    CastsShadows = true,
                });
                engine.Vegetation.SetLayerDetail(new VegetationLayerDetailRequest(field, key,
                    new[] { VegetationLod.Mesh(far[variant], NearReach), VegetationLod.ImpostorOf(near[variant], ImpostorStart) }, LodBand));
            }
        }
    }

    /// <summary>Keeps the field centred on the player, re-asks the tiles under new edits, and streams tiles.</summary>
    internal void Follow(VoxelAddress centre, (long MinX, long MinZ, long MaxX, long MaxZ)? changed)
    {
        (double X, double Z) at = (centre.X + CellCentre, centre.Z + CellCentre);
        if (Math.Max(Math.Abs(at.X - focus.X), Math.Abs(at.Z - focus.Z)) >= FocusStepMetres)
        {
            focus = at;
            engine.Vegetation.SetFocus(new VegetationFocusRequest(field, VegetationFocus.At(at.X, centre.Y, at.Z)));
        }

        if (changed is var (minX, minZ, maxX, maxZ))
        {
            // A tree's footing is the cell under its trunk: the trunk column itself is enough.
            engine.Vegetation.Invalidate(new VegetationInvalidateRequest(field, minX - 1, minZ - 1, maxX + 1, maxZ + 1));
        }

        streamer.Update();
    }

    internal string Readout()
    {
        VegetationReadout r = engine.Vegetation.Read(field);
        return string.Create(CultureInfo.InvariantCulture,
            $"treeTiles tiles={r.Tiles} pending={r.PendingTiles} patches={r.Patches} copies={r.Instances} overBudget={r.OverBudget} deferred={r.Deferred} placeMs={r.PlacementMicroseconds / 1e3:F2} selectMs={r.SelectionMicroseconds / 1e3:F2} publishMs={r.PublicationMicroseconds / 1e3:F2} skipped={r.SettlesSkipped} reselected={r.SettlesReselected} invalidated={r.InvalidatedTiles} records={recordsMade} decideMs={Milliseconds(decidedTiles == 0 ? 0 : decidingTicks / decidedTiles):F2}/tile slowest={Milliseconds(slowestTicks):F2} committed={streamer.Committed} settled={r.PendingTiles == 0 && r.Deferred == 0 && streamer.InFlight == 0}");
    }

    /// <summary>Draws the trees or not; hidden, they keep their tiles, so showing them again costs nothing.</summary>
    internal void Show(bool drawn) => engine.Vegetation.SetVisible(new VegetationVisibilityRequest(field, drawn));

    public void Dispose()
    {
        streamer.Dispose();
        field.Dispose();
    }

    private static double Milliseconds(long ticks) => ticks * 1e3 / System.Diagnostics.Stopwatch.Frequency;

    private static uint Key(ushort species, int variant) =>
        LayerBase + ((uint)species * VariantsPerKind) + (uint)variant;

    /// <summary>
    /// The tile's standing trees as records in their variant's layer, on the update's thread:
    /// the recipe's decisions use the Engine's keyed draws, and standing reads the edits.
    /// </summary>
    private void AddTrees(VegetationTileSamples samples)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Decide(samples);
        long spent = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        decidedTiles++;
        decidingTicks += spent;
        slowestTicks = Math.Max(slowestTicks, spent);
    }

    private void Decide(VegetationTileSamples samples)
    {
        VegetationTileRequest tile = samples.Tile;
        long cell = recipe.TreeCellMetres;
        double size = tile.TileCells * tile.CellSize;
        long firstX = GridMath.FloorDivide((long)Math.Floor(tile.OriginX), cell), lastX = GridMath.FloorDivide((long)Math.Ceiling(tile.OriginX + size) - 1, cell);
        long firstZ = GridMath.FloorDivide((long)Math.Floor(tile.OriginZ), cell), lastZ = GridMath.FloorDivide((long)Math.Ceiling(tile.OriginZ + size) - 1, cell);
        for (long anchorX = firstX; anchorX <= lastX; anchorX++)
        for (long anchorZ = firstZ; anchorZ <= lastZ; anchorZ++)
        {
            if (recipe.TreeInCell(anchorX, anchorZ) is not TerrainTree tree) continue;
            bool touched = TreeFelling.Core(tree).Append(TreeFelling.Support(tree)).Any(edited);
            if (touched && !TreeFelling.Stands(tree, materialAt)) continue;

            int variants = trees.NearMeshes[tree.Species].Length;
            int variant = Math.Min(variants - 1, (int)(tree.Variant * variants));
            uint key = Key(tree.Species, variant);
            double ground = recipe.ContinuousHeightAt(tree.X, tree.Z) + FarGround.GroundTopFace;
            float x = (float)(tree.X + CellCentre - tile.OriginX), z = (float)(tree.Z + CellCentre - tile.OriginZ);
            samples.Records.Add(new VegetationRecord(key, new Vector3(x, (float)(ground - TerrainTrees.Sink), z), (float)tree.Yaw, (float)tree.Scale));
            recordsMade++;
        }
    }
}
