using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Heightfields;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The far field as an Engine heightfield (#9850, rusty-engine #9842): the same ground as the voxel far
/// field (<see cref="FarField"/>) — the generated height every eight metres, sunk under the near ground
/// about the player, made of what the far rules say (<see cref="FarGround"/>) — drawn as one ring of grid
/// tiles in the scene, with the walking ground's four terrain layers blended by per-vertex weights and
/// water as its own hard-edged material. It stands in the walking session at the world's origin, so a
/// rebase moves nothing, and its tiles are sampled on the streamer's workers.
/// </summary>
internal sealed class FarFieldHeightfield : IFarField
{
    /// <summary>Cells along a tile's side: one far chunk column (128 m).</summary>
    private const uint TileCells = (uint)TerrainConstants.ChunkEdgeLength;
    private const uint GroundIndex = 0, WaterIndex = 1;
    /// <summary>How many sampled tiles are committed to the Engine an update.</summary>
    private const int CommitsPerUpdate = 64;
    /// <summary>The ring's focus moves onto the player once they are this far from it.</summary>
    private const double FocusStepMetres = FarGround.CellMetres;
    /// <summary>The ground's textures repeat in metres (one heightfield unit), as on the walking ground.</summary>
    private const double TextureUnitMetres = 1d;

    /// <summary>What the workers sample by: the sunk zone, replaced before the tiles it changes are invalidated.</summary>
    private sealed record Frame((double X, double Z) SinkCentre, double Inner, double Outer);

    private readonly IEngineContext engine;
    private readonly Func<SpatialSession> walking;
    private readonly Sampler sampler;
    private readonly TerrainGroundMaterials ground;
    private readonly Material water;
    private Heightfield? heightfield;
    private HeightfieldStreamer? streamer;
    private int radiusChunks = FarField.DefaultRadiusChunks;
    private double sinkInner, sinkOuter;
    private (double X, double Z) sinkCentre = (double.NaN, double.NaN);
    private (double X, double Z) focus = (double.NaN, double.NaN);

    /// <param name="walking">The walking session: the heightfield stands at the world's origin in its frame.</param>
    internal FarFieldHeightfield(IEngineContext engine, ProductContent content, TerrainRecipe recipe, Func<SpatialSession> walking)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.walking = walking ?? throw new ArgumentNullException(nameof(walking));
        ArgumentNullException.ThrowIfNull(recipe);
        (sinkInner, sinkOuter) = FarGround.SinkBand(TerrainConstants.RequestedChunkRadius * TerrainConstants.ChunkEdgeLength);
        ground = new TerrainGroundMaterials(engine, content, TextureUnitMetres, GroundTextureSet.Walking);
        sampler = new Sampler(recipe, (int)ground.Settings.TransitionCells);
        try
        {
            water = engine.Graphics.CreateMaterial(new MaterialRequest(MapPalette.River.Linear(), default, 1f, ColourSpace.White,
                Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));
        }
        catch
        {
            ground.Dispose();
            throw;
        }

        Publish();
    }

    public int RadiusChunks
    {
        get => radiusChunks;
        set
        {
            int next = Math.Max(1, value);
            if (next == radiusChunks) return;
            radiusChunks = next;
            // The ring reaches the view distance: made again with the new reach on the next follow.
            Release();
        }
    }

    public double NearEdgeMetres
    {
        set
        {
            (double inner, double outer) = FarGround.SinkBand(value);
            if (inner == sinkInner && outer == sinkOuter) return;
            double before = sinkOuter;
            (sinkInner, sinkOuter) = (inner, outer);
            Publish();
            InvalidateAround(sinkCentre, Math.Max(before, sinkOuter));
        }
    }

    public void Follow(long worldX, long worldZ)
    {
        if (heightfield is null) Build(worldX, worldZ);
        if (Math.Max(Math.Abs(worldX - focus.X), Math.Abs(worldZ - focus.Z)) >= FocusStepMetres)
        {
            focus = (worldX, worldZ);
            engine.Heightfield.SetFocus(new HeightfieldFocusRequest(heightfield!, HeightfieldFocus.At(worldX, worldZ)));
        }

        if (!double.IsNaN(sinkCentre.X)
            && Math.Max(Math.Abs(worldX - sinkCentre.X), Math.Abs(worldZ - sinkCentre.Z)) < FarGround.SinkFollowStepMetres) return;
        (double X, double Z) previous = sinkCentre;
        sinkCentre = (worldX, worldZ);
        // The frame the workers sample by changes first, so nothing invalidated is sampled again by the old one.
        Publish();
        InvalidateAround(previous, sinkOuter);
        InvalidateAround(sinkCentre, sinkOuter);
    }

    public void Advance() => streamer?.Update();

    public string Readout()
    {
        if (heightfield is null || streamer is null) return "farField heightfield=none";
        HeightfieldReadout read = engine.Heightfield.Read(heightfield);
        return string.Create(CultureInfo.InvariantCulture,
            $"farField heightfield tiles={read.DrawnTiles}/{read.ResidentTiles}+{read.PendingTiles}pending vertices={read.Vertices} sampleMB={read.SampleBytes / 1048576.0:F1} meshMB={read.MeshBytes / 1048576.0:F1} inFlight={streamer.InFlight} committed={streamer.Committed} ignored={streamer.Ignored} sampleMs={streamer.SampleMilliseconds:F0} settled={read.Settled && streamer.InFlight == 0} radius={radiusChunks} sink={sinkInner:F0}-{sinkOuter:F0}m centre={sinkCentre.X:F0},{sinkCentre.Z:F0}");
    }

    public void Dispose()
    {
        Release();
        water.Dispose();
        ground.Dispose();
    }

    private void Build(long worldX, long worldZ)
    {
        double reach = (radiusChunks + 1) * (double)FarField.ChunkMetres;
        heightfield = engine.Heightfield.Create(new HeightfieldCreateRequest(TileCells,
            new HeightfieldRing[] { new(FarGround.CellMetres, reach) },
            new HeightfieldMaterial[] { new(GroundIndex, ground.Layered), new(WaterIndex, water) },
            RenderLayer.Scene, Vector3.Zero, 1f, HeightfieldFocus.At(worldX, worldZ))
            with { Session = walking(), Anchor = new WorldOriginGlobalPosition(0, 0, 0, 0, 0, 0) });
        streamer = new HeightfieldStreamer(engine, heightfield, sampler, CommitsPerUpdate);
        focus = (worldX, worldZ);
    }

    private void Release()
    {
        streamer?.Dispose();
        streamer = null;
        heightfield?.Dispose();
        heightfield = null;
        focus = (double.NaN, double.NaN);
    }

    private void Publish() => sampler.Current = new Frame(sinkCentre, sinkInner, sinkOuter);

    private void InvalidateAround((double X, double Z) centre, double extent)
    {
        if (heightfield is null || double.IsNaN(centre.X)) return;
        engine.Heightfield.Invalidate(new HeightfieldInvalidateRequest(heightfield,
            centre.X - extent, centre.Z - extent, centre.X + extent, centre.Z + extent));
    }

    /// <summary>
    /// One tile's samples on a worker: the generated height at every vertex and one more each side, less
    /// the sink, with water's surface over it where water stands; per vertex the far rules' materials about
    /// it, each a terrain layer, weighed by nearness over the ground's transition reach as a voxel chunk's
    /// layer weights are (so patches blend over the same width), or the water index where water stands.
    /// It reads only the recipe's height and geography (the map and region tiles, whose cache is
    /// concurrent; not the recipe's memo tables for trees, column bands, chunk content or crossings,
    /// which only the voxel chunk generator touches) and the published frame.
    /// </summary>
    private sealed class Sampler(TerrainRecipe recipe, int transitionCells) : IHeightfieldSampler
    {
        private Frame frame = new((double.NaN, double.NaN), 0, 1);

        internal Frame Current
        {
            get => Volatile.Read(ref frame);
            set => Volatile.Write(ref frame, value);
        }

        public void Sample(HeightfieldTileRequest tile, HeightfieldTileSamples samples)
        {
            Frame now = Current;
            int side = tile.Side, reach = transitionCells;
            // A lattice reaching the transition (for the weights) and a cell more (for each material's slope).
            int border = reach + 1, span = side + (2 * border);
            double[] ground = new double[span * span];
            double[] waterTop = new double[span * span];
            MapSample[] geography = new MapSample[span * span];
            int At(int i, int j) => ((j + border) * span) + i + border;
            for (int j = -border; j < side + border; j++)
            for (int i = -border; i < side + border; i++)
            {
                int at = At(i, j);
                double x = tile.SampleX(i), z = tile.SampleZ(j);
                if (Math.Abs(x) > recipe.Radius || Math.Abs(z) > recipe.Radius)
                {
                    ground[at] = double.NaN;
                    continue;
                }

                ground[at] = recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z));
                geography[at] = recipe.Geography(x, z);
                waterTop[at] = FarGround.WaterTop(geography[at]);
            }

            for (int j = -1; j <= side; j++)
            for (int i = -1; i <= side; i++)
            {
                int at = At(i, j);
                if (double.IsNaN(ground[at]))
                {
                    samples.SetHeight(i, j, float.NaN);
                    continue;
                }

                double x = tile.SampleX(i), z = tile.SampleZ(j);
                double sink = double.IsNaN(now.SinkCentre.X) ? 0
                    : FarGround.Sink(Math.Max(Math.Abs(x - now.SinkCentre.X), Math.Abs(z - now.SinkCentre.Z)), now.Inner, now.Outer);
                samples.SetHeight(i, j, (float)(Math.Max(ground[at], waterTop[at]) - sink));
            }

            // Each land cell's layer within the transition reach of the tile (-1 for none or water).
            int layerSpan = side + (2 * reach);
            int[] layers = new int[layerSpan * layerSpan];
            for (int j = -reach; j < side + reach; j++)
            for (int i = -reach; i < side + reach; i++)
            {
                int at = At(i, j);
                double height = ground[at];
                int layer = -1;
                if (!double.IsNaN(height) && waterTop[at] <= height)
                {
                    double rise = 0;
                    foreach (int neighbour in (ReadOnlySpan<int>)[at - 1, at + 1, at - span, at + span])
                    {
                        if (!double.IsNaN(ground[neighbour])) rise = Math.Max(rise, Math.Abs(ground[neighbour] - height));
                    }

                    layer = Math.Max(0, TerrainLayers.Layer(FarGround.Natural(geography[at], rise, height, tile.CellSize)));
                }

                layers[((j + reach) * layerSpan) + i + reach] = layer;
            }

            for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int own = layers[((j + reach) * layerSpan) + i + reach];
                if (own < 0)
                {
                    if (!double.IsNaN(ground[At(i, j)])) samples.SetMaterial(i, j, WaterIndex);
                    samples.SetLayerWeights(i, j, Vector4.UnitX);
                    continue;
                }

                // Nearer cells weigh more: reach + 1 at the vertex, falling by one a cell (Chebyshev).
                Vector4 weights = Vector4.Zero;
                for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int layer = layers[((j + dz + reach) * layerSpan) + i + dx + reach];
                    if (layer < 0) continue;
                    weights += Layer(layer) * (reach + 1 - Math.Max(Math.Abs(dx), Math.Abs(dz)));
                }

                samples.SetLayerWeights(i, j, weights / (weights.X + weights.Y + weights.Z + weights.W));
            }
        }

        private static Vector4 Layer(int layer) => layer switch { 0 => Vector4.UnitX, 1 => Vector4.UnitY, 2 => Vector4.UnitZ, _ => Vector4.UnitW };
    }
}
