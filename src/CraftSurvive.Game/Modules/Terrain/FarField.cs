using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The land beyond the drawn chunks (#9548): the same height field the walking ground is
/// generated from, sampled eight metres apart and streamed as dual-contoured chunk columns in a
/// session of its own, drawn with the same ground layers. It has no collision and nothing lives
/// on it; it is the horizon. Under the near ground it is sunk out of sight, rising to its true
/// height short of the near ground's edge so the two meet under the fog. It follows the player
/// a chunk column at a time and moves with the world origin.
/// </summary>
internal sealed class FarField : IDisposable
{
    /// <summary>One far voxel. Eight metres keeps a ridge's shape and a valley's floor at a kilometre.</summary>
    internal const double VoxelMetres = 8d;

    /// <summary>One far chunk column, in metres.</summary>
    internal const int ChunkMetres = (int)VoxelMetres * TerrainConstants.ChunkEdgeLength;

    /// <summary>How many far chunk columns are kept each way from the player's by default: a mile and a half.</summary>
    internal const int DefaultRadiusChunks = 12;
    private int radiusChunks = DefaultRadiusChunks;
    private bool rewant;

    /// <summary>How many far chunk columns are kept each way: the player's view distance (#9759), taken on the next follow.</summary>
    internal int RadiusChunks
    {
        get => radiusChunks;
        set
        {
            radiusChunks = Math.Max(1, value);
            grownRadius = Math.Min(grownRadius, radiusChunks);
            rewant = true;
        }
    }

    /// <summary>The far field grows out from the player this many chunk columns an update, so a new world starts drawing at once.</summary>
    private const int GrowthPerUpdate = 2;

    /// <summary>Far chunks farther than this from the camera are drawn from the Engine's coarse meshes.</summary>
    internal const double CoarseBeyondMetres = 400d;

    /// <summary>How many far chunks are admitted or replaced an update.</summary>
    private const int ChunksPerUpdate = 10;

    /// <summary>
    /// How far the far field is sunk under the near ground, and where: fully within
    /// <see cref="sinkInnerMetres"/> of the player, rising to its true height by
    /// <see cref="sinkOuterMetres"/>, short of the near ground's edge (the requested chunk
    /// radius) so the join is under the fog and never a drop. Six metres covers a far voxel's
    /// error against the metre ground.
    /// </summary>
    private const double SinkMetres = 6d;
    /// <summary>The sunk zone ends this far inside the near ground's edge, and is fully sunk this far inside it.</summary>
    private const double SinkOuterInsideEdgeMetres = 16d, SinkInnerInsideEdgeMetres = 64d;
    private double sinkInnerMetres = (TerrainConstants.RequestedChunkRadius * TerrainConstants.ChunkEdgeLength) - SinkInnerInsideEdgeMetres;
    private double sinkOuterMetres = (TerrainConstants.RequestedChunkRadius * TerrainConstants.ChunkEdgeLength) - SinkOuterInsideEdgeMetres;

    /// <summary>
    /// The near ground's edge, in metres from the player (its requested radius, #9759): the sunk zone
    /// keeps short of it, so the join stays under the fog. A change re-samples the columns either touches.
    /// </summary>
    internal double NearEdgeMetres
    {
        set
        {
            HashSet<(long X, long Z)> changed = double.IsNaN(sinkCentre.X) ? [] : [.. SunkColumns(sinkCentre)];
            sinkInnerMetres = Math.Max(0d, value - SinkInnerInsideEdgeMetres);
            sinkOuterMetres = Math.Max(sinkInnerMetres + 1d, value - SinkOuterInsideEdgeMetres);
            if (double.IsNaN(sinkCentre.X)) return;
            changed.UnionWith(SunkColumns(sinkCentre));
            layer.Invalidate(changed);
        }
    }

    /// <summary>How far the player walks before the sunk zone is moved onto them again.</summary>
    private const double SinkFollowStepMetres = 24d;

    /// <summary>Ground steeper than this, in metres of rise per metre, shows rock in the far field.</summary>
    private const double RockSlope = 0.7d;

    /// <summary>The sea and rivers in the far field, one flat colour.</summary>
    private static readonly Color WaterColour = MapPalette.River;
    private const uint WaterSlot = (uint)BlockId.Water;

    private readonly IEngineContext engine;
    private readonly TerrainRecipe recipe;
    private readonly WorldFrame frame;
    private readonly TerrainGroundMaterials ground;
    private readonly Material water;
    private readonly MapVoxelLayer layer;
    private readonly long radius;
    private (long X, long Z)? centre;
    private int grownRadius;
    private (double X, double Z) sinkCentre = (double.NaN, double.NaN);
    private (long X, long Y, long Z) alignedOrigin;
    private bool disposed;

    internal FarField(IEngineContext engine, ProductContent content, TerrainRecipe recipe, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        radius = recipe.Radius;
        ground = new TerrainGroundMaterials(engine, content, VoxelMetres, GroundTextureSet.Walking);
        try
        {
            water = engine.Graphics.CreateMaterial(new MaterialRequest(WaterColour, default, 1f, WaterColour, Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));
            Dictionary<uint, Material> bindings = [];
            List<(uint Slot, uint Layer)> layered = [];
            foreach (BlockDefinition block in BlockRegistry.MaterialBlocks)
            {
                int index = TerrainLayers.Layer(block.Id);
                if (index < 0) continue;
                bindings[(uint)block.Id] = ground.Layered;
                layered.Add(((uint)block.Id, (uint)index));
            }

            bindings[WaterSlot] = water;
            layer = new MapVoxelLayer(engine, VoxelMetres, Sample, bindings,
                ([.. layered.Select(entry => entry.Slot)], [.. layered.Select(entry => entry.Layer)], ground.Settings.TransitionCells),
                CoarseBeyondMetres);
        }
        catch
        {
            water?.Dispose();
            ground.Dispose();
            throw;
        }

        frame.Rebased += _ => Align();
        Align();
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"farField chunks={layer.ResidentChunks} pending={layer.PendingChunks} radius={grownRadius}/{RadiusChunks} sink={sinkInnerMetres:F0}-{sinkOuterMetres:F0}m centre={centre?.X ?? 0},{centre?.Z ?? 0} workMs={layer.WorkMilliseconds:F0}");

    /// <summary>Keeps the far field centred on a world column, and the sunk zone under the player.</summary>
    internal void Follow(long worldX, long worldZ)
    {
        if (disposed) return;
        (long X, long Z) column = (GridMath.FloorDivide(worldX, ChunkMetres), GridMath.FloorDivide(worldZ, ChunkMetres));
        bool moved = centre != column;
        if (moved) centre = column;
        if (grownRadius < RadiusChunks || moved || rewant)
        {
            rewant = false;
            grownRadius = Math.Min(RadiusChunks, grownRadius + GrowthPerUpdate);
            layer.Want(from z in Range(column.Z - grownRadius, column.Z + grownRadius)
                       from x in Range(column.X - grownRadius, column.X + grownRadius)
                       select (x, z));
        }

        FollowSink(worldX, worldZ);
    }

    /// <summary>Applies a bounded number of queued chunk operations and refreshes the drawn scene.</summary>
    internal void Advance()
    {
        if (disposed) return;
        layer.Advance(ChunksPerUpdate);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        layer.Dispose();
        water.Dispose();
        ground.Dispose();
    }

    /// <summary>Moves the sunk zone onto the player once they have walked a step from its centre, re-sampling the columns it changes.</summary>
    private void FollowSink(long worldX, long worldZ)
    {
        if (!double.IsNaN(sinkCentre.X) && Math.Max(Math.Abs(worldX - sinkCentre.X), Math.Abs(worldZ - sinkCentre.Z)) < SinkFollowStepMetres)
        {
            return;
        }

        (double X, double Z) previous = sinkCentre;
        sinkCentre = (worldX, worldZ);
        HashSet<(long X, long Z)> changed = [.. SunkColumns(sinkCentre)];
        if (!double.IsNaN(previous.X)) changed.UnionWith(SunkColumns(previous));
        layer.Invalidate(changed);
    }

    /// <summary>The far chunk columns a sunk zone at a centre touches.</summary>
    private IEnumerable<(long X, long Z)> SunkColumns((double X, double Z) at)
    {
        long minX = GridMath.FloorDivide((long)Math.Floor(at.X - sinkOuterMetres), ChunkMetres);
        long maxX = GridMath.FloorDivide((long)Math.Ceiling(at.X + sinkOuterMetres), ChunkMetres);
        long minZ = GridMath.FloorDivide((long)Math.Floor(at.Z - sinkOuterMetres), ChunkMetres);
        long maxZ = GridMath.FloorDivide((long)Math.Ceiling(at.Z + sinkOuterMetres), ChunkMetres);
        for (long z = minZ; z <= maxZ; z++)
        for (long x = minX; x <= maxX; x++)
            yield return (x, z);
    }

    /// <summary>How far the far field is sunk at a world point: fully near the player, nothing past the outer edge.</summary>
    private double Sink(double worldX, double worldZ)
    {
        if (double.IsNaN(sinkCentre.X)) return 0d;
        double away = Math.Max(Math.Abs(worldX - sinkCentre.X), Math.Abs(worldZ - sinkCentre.Z));
        double t = Math.Clamp((away - sinkInnerMetres) / (sinkOuterMetres - sinkInnerMetres), 0d, 1d);
        return SinkMetres * (1d - (t * t * (3d - (2d * t))));
    }

    /// <summary>The far field's session shares the walking session's world origin, so the two draw in one frame.</summary>
    private void Align()
    {
        (long X, long Y, long Z) origin = frame.Origin;
        if (origin == alignedOrigin) return;
        using WorldOriginPrepared prepared = engine.WorldOrigin.Prepare(new WorldOriginPrepareRequest(
            layer.Session, origin.X, origin.Y, origin.Z, Array.Empty<WorldOriginEntityRow>()));
        engine.WorldOrigin.Commit(new WorldOriginCommitRequest(prepared));
        alignedOrigin = origin;
    }

    /// <summary>
    /// One far chunk column: each far voxel column takes the generated height at its centre, in
    /// far voxels, less the sink there, and the ground's natural material: rock where the map
    /// says so or the ground is steep, snow or sand by climate, sand at the water, else grass;
    /// water where the sea or a river stands above the ground. Columns beyond the world are empty.
    /// </summary>
    private void Sample(long chunkX, long chunkZ, Span<double> surface, Span<uint> material)
    {
        const int edge = MapVoxelLayer.EdgeLength;
        Span<double> heights = stackalloc double[(edge + 2) * (edge + 2)];
        // The height a voxel beyond each edge, so the slope at the edge is the same as its neighbour's.
        for (int z = -1; z <= edge; z++)
        for (int x = -1; x <= edge; x++)
        {
            long worldX = ((chunkX * edge) + x) * (long)VoxelMetres + ((long)VoxelMetres / 2);
            long worldZ = ((chunkZ * edge) + z) * (long)VoxelMetres + ((long)VoxelMetres / 2);
            heights[((z + 1) * (edge + 2)) + x + 1] = Math.Abs(worldX) > radius || Math.Abs(worldZ) > radius
                ? double.NaN
                : recipe.ContinuousHeightAt(worldX, worldZ);
        }

        for (int z = 0; z < edge; z++)
        for (int x = 0; x < edge; x++)
        {
            int i = (z * edge) + x;
            double height = heights[((z + 1) * (edge + 2)) + x + 1];
            if (double.IsNaN(height))
            {
                surface[i] = double.NaN;
                material[i] = TerrainConstants.EmptyMaterial;
                continue;
            }

            long worldX = ((chunkX * edge) + x) * (long)VoxelMetres + ((long)VoxelMetres / 2);
            long worldZ = ((chunkZ * edge) + z) * (long)VoxelMetres + ((long)VoxelMetres / 2);
            MapSample geography = recipe.Geography(worldX, worldZ);
            double waterTop = geography.InRiver
                ? Math.Max(GenerationConstants.WaterLevel, Math.Floor(geography.RiverSurface))
                : GenerationConstants.WaterLevel;
            double top = Math.Max(height, waterTop);
            surface[i] = (top - Sink(worldX, worldZ)) / VoxelMetres;
            material[i] = waterTop > height ? WaterSlot : (uint)Natural(geography, heights, x, z, height);
        }
    }

    private static BlockId Natural(MapSample geography, ReadOnlySpan<double> heights, int x, int z, double height)
    {
        const int stride = MapVoxelLayer.EdgeLength + 2;
        int centre = ((z + 1) * stride) + x + 1;
        double rise = 0d;
        foreach (int neighbour in (ReadOnlySpan<int>)[centre - 1, centre + 1, centre - stride, centre + stride])
        {
            double other = heights[neighbour];
            if (!double.IsNaN(other)) rise = Math.Max(rise, Math.Abs(other - height));
        }

        if (geography.Rock >= TerrainRecipe.ExposedRockThreshold || rise / VoxelMetres > RockSlope) return BlockId.Stone;
        if (height <= GenerationConstants.WaterLevel + 1) return BlockId.Sand;
        if (WorldMap.Frozen(geography)) return BlockId.Snow;
        if (WorldMap.Arid(geography)) return BlockId.Sand;
        return BlockId.Grass;
    }

    private static IEnumerable<long> Range(long from, long to)
    {
        for (long value = from; value <= to; value++) yield return value;
    }
}
