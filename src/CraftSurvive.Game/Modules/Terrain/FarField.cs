using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Heightfields;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The land beyond the drawn chunks (#9548), as an Engine heightfield (#9850, rusty-engine #9842): the
/// generated height every eight metres, sunk under the near ground about the player, made of what the far
/// rules say (<see cref="FarGround"/>), drawn as one ring of grid tiles in the scene out to the view
/// distance, with the walking ground's four terrain layers blended by per-vertex weights and water as its
/// own hard-edged material. It has no collision and nothing lives on it. It stands in the walking session
/// at the world's origin, so a rebase moves nothing, and its tiles are sampled on the streamer's workers.
/// </summary>
internal sealed class FarField : IDisposable
{
    /// <summary>One far chunk column, in metres: a tile of sixteen far cells.</summary>
    internal const int ChunkMetres = (int)FarGround.CellMetres * TerrainConstants.ChunkEdgeLength;

    /// <summary>How many far chunk columns it reaches each way from the player's by default: a mile and a half.</summary>
    internal const int DefaultRadiusChunks = 12;

    /// <summary>
    /// The farthest it draws from the player at a radius: whole chunk columns about the player's, so up
    /// to one more column each way, to the corner of the square.
    /// </summary>
    internal static double FarthestMetres(int radiusChunks) => Math.Sqrt(2) * (Math.Max(1, radiusChunks) + 1) * ChunkMetres;

    /// <summary>Cells along a tile's side: one far chunk column.</summary>
    private const uint TileCells = (uint)TerrainConstants.ChunkEdgeLength;
    private const uint GroundIndex = 0, WaterIndex = 1, IceIndex = 2, DryGroundIndex = 3;
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
    private readonly Material water, ice;
    private Heightfield? heightfield;
    private HeightfieldStreamer? streamer;
    private int radiusChunks = DefaultRadiusChunks;
    private double sinkInner, sinkOuter;
    private (double X, double Z) sinkCentre = (double.NaN, double.NaN);
    private (double X, double Z) focus = (double.NaN, double.NaN);

    /// <param name="walking">The walking session: the heightfield stands at the world's origin in its frame.</param>
    /// <param name="waters">The water owner: where its stage has frozen water or left a bed dry (#9878), the far field shows it.</param>
    internal FarField(IEngineContext engine, ProductContent content, TerrainRecipe recipe, Func<SpatialSession> walking, Water.WaterModel waters)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.walking = walking ?? throw new ArgumentNullException(nameof(walking));
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(waters);
        (sinkInner, sinkOuter) = FarGround.SinkBand(TerrainConstants.RequestedChunkRadius * TerrainConstants.ChunkEdgeLength);
        ground = new TerrainGroundMaterials(engine, content, TextureUnitMetres, GroundTextureSet.Walking);
        sampler = new Sampler(recipe, waters, (int)ground.Settings.TransitionCells);
        Material? madeWater = null;
        try
        {
            madeWater = engine.Graphics.CreateMaterial(new MaterialRequest(MapPalette.FarWater.Linear(), default, 1f, ColourSpace.White,
                Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));
            ice = engine.Graphics.CreateMaterial(new MaterialRequest(Water.WaterSurfaces.IceColour, default, Water.WaterSurfaces.IceRoughness, ColourSpace.White,
                Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));
            water = madeWater;
        }
        catch
        {
            madeWater?.Dispose();
            ground.Dispose();
            throw;
        }

        Publish();
    }

    /// <summary>How many far chunk columns it reaches each way: the view distance (#9759), taken on the next follow.</summary>
    internal int RadiusChunks
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

    /// <summary>The near ground's edge, in metres from the player: the sunk zone keeps short of it, so the join stays under the fog.</summary>
    internal double NearEdgeMetres
    {
        set
        {
            (double inner, double outer) = FarGround.SinkBand(value);
            if (inner == sinkInner && outer == sinkOuter) return;
            double before = sinkOuter;
            (sinkInner, sinkOuter) = (inner, outer);
            Publish();
            InvalidateAround(sinkCentre, Math.Max(Changed(before), Changed(sinkOuter)));
        }
    }

    /// <summary>Where the sunk zone is centred, and how far its band reaches (Chebyshev metres): the near water squares to it (#9877).</summary>
    internal (double X, double Z) SinkCentre => sinkCentre;

    internal double SinkOuter => sinkOuter;

    /// <summary>How far a move of the sunk zone changes the far field: its band, or the near water's square, whichever reaches farther.</summary>
    private static double Changed(double outer) =>
        Math.Max(outer, (Water.NearWaterSquare.TileRadius(outer) + 1.0) * Water.NearWaterSquare.TileMetres);

    /// <summary>Keeps the ring centred on the player, and the sunk zone under them.</summary>
    internal void Follow(long worldX, long worldZ)
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
        InvalidateAround(previous, Changed(sinkOuter));
        InvalidateAround(sinkCentre, Changed(sinkOuter));
    }

    /// <summary>Commits a bounded number of sampled tiles and starts sampling the next.</summary>
    internal void Advance() => streamer?.Update();

    internal string Readout()
    {
        if (heightfield is null || streamer is null) return "farField=none";
        HeightfieldReadout read = engine.Heightfield.Read(heightfield);
        return string.Create(CultureInfo.InvariantCulture,
            $"farField tiles={read.DrawnTiles}/{read.ResidentTiles}+{read.PendingTiles}pending vertices={read.Vertices} sampleMB={read.SampleBytes / 1048576.0:F1} meshMB={read.MeshBytes / 1048576.0:F1} inFlight={streamer.InFlight} committed={streamer.Committed} ignored={streamer.Ignored} sampleMs={streamer.SampleMilliseconds:F0} settled={read.Settled && streamer.InFlight == 0} radius={radiusChunks} sink={sinkInner:F0}-{sinkOuter:F0}m centre={sinkCentre.X:F0},{sinkCentre.Z:F0}");
    }

    public void Dispose()
    {
        Release();
        water.Dispose();
        ice.Dispose();
        ground.Dispose();
    }

    private void Build(long worldX, long worldZ)
    {
        double reach = (radiusChunks + 1) * (double)ChunkMetres;
        heightfield = engine.Heightfield.Create(new HeightfieldCreateRequest(TileCells,
            new HeightfieldRing[] { new(FarGround.CellMetres, reach) },
            new HeightfieldMaterial[]
            {
                new(GroundIndex, ground.LayeredOf(FarGroundBlend.LushLayers)), new(WaterIndex, water), new(IceIndex, ice),
                new(DryGroundIndex, ground.LayeredOf(FarGroundBlend.DryLayers)),
            },
            RenderLayer.Scene, Vector3.Zero, 1f, HeightfieldFocus.At(worldX, worldZ))
            with { Session = walking(), Anchor = new WorldOriginGlobalPosition(0, 0, 0, 0, 0, 0) });
        streamer = new HeightfieldStreamer(engine, heightfield, sampler, CommitsPerUpdate);
        focus = (worldX, worldZ);
        if (!visible)
        {
            engine.Heightfield.SetVisible(new HeightfieldVisibilityRequest(heightfield, false));
        }
    }

    /// <summary>Whether the far ground is drawn; hidden, it keeps its tiles and focus, so showing it again costs nothing.</summary>
    private bool visible = true;

    internal void Show(bool drawn)
    {
        visible = drawn;
        if (heightfield is not null)
        {
            engine.Heightfield.SetVisible(new HeightfieldVisibilityRequest(heightfield, drawn));
        }
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

    /// <summary>
    /// The water froze, thawed, ran dry or came back over these areas (#9878): the tiles under them that
    /// the ring holds are sampled again.
    /// </summary>
    internal void InvalidateWater(IReadOnlyList<Water.WaterBounds> changed)
    {
        if (heightfield is null || double.IsNaN(focus.X)) return;
        double reach = (radiusChunks + 1) * (double)ChunkMetres;
        foreach (Water.WaterBounds area in changed)
        {
            if (area.MaxX < focus.X - reach || area.MinX > focus.X + reach || area.MaxZ < focus.Z - reach || area.MinZ > focus.Z + reach) continue;
            engine.Heightfield.Invalidate(new HeightfieldInvalidateRequest(heightfield, area.MinX, area.MinZ, area.MaxX, area.MaxZ));
        }
    }

    private void InvalidateAround((double X, double Z) centre, double extent)
    {
        if (heightfield is null || double.IsNaN(centre.X)) return;
        engine.Heightfield.Invalidate(new HeightfieldInvalidateRequest(heightfield,
            centre.X - extent, centre.Z - extent, centre.X + extent, centre.Z + extent));
    }

    /// <summary>
    /// One tile's samples on a worker: the generated ground's top face at every vertex and one more each side, less
    /// the sink, with water's surface over it where water stands; per vertex the far rules' materials about
    /// it, each a terrain layer, weighed by nearness over the ground's transition reach as a voxel chunk's
    /// layer weights are (so patches blend over the same width), or the water index where water stands.
    /// It reads only the recipe's height and geography (the map and region tiles, whose cache is
    /// concurrent; not the recipe's memo tables for trees, column bands, chunk content or crossings,
    /// which only the voxel chunk generator touches) and the published frame.
    /// </summary>
    private sealed class Sampler(TerrainRecipe recipe, Water.WaterModel waters, int transitionCells) : IHeightfieldSampler
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
            // A lattice reaching the transition and a cell beyond (for the dry set's margin, FarGroundBlend) and a cell
            // more (for each material's slope).
            int border = reach + 2, span = side + (2 * border);
            double[] ground = new double[span * span];
            double[] waterTop = new double[span * span];
            bool[] frozen = new bool[span * span];
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

                ground[at] = recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z)) + FarGround.GroundTopFace;
                geography[at] = recipe.Geography(x, z);
                // Inside the near water's square the near surfaces draw the water (#9877): here only the bed.
                // A flood (#9880) stands over its floodplain beyond the near square as well.
                waterTop[at] = !double.IsNaN(now.SinkCentre.X) && Water.NearWaterSquare.CoversInside(now.SinkCentre, now.Outer, x, z)
                    ? double.NegativeInfinity : Math.Max(FarGround.WaterTop(geography[at]), waters.FloodLevel(x, z));
                // The stage's ice and dry beds (#9878): a dry bed draws as ground, frozen water as ice.
                if (waterTop[at] > ground[at])
                {
                    (bool ice, bool dry) = waters.State(geography[at], x, z);
                    if (dry) waterTop[at] = double.NegativeInfinity;
                    frozen[at] = ice;
                }
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

            // Each land cell's natural ground within the transition reach of the tile and a cell beyond (null for none or water).
            int margin = reach + 1, layerSpan = side + (2 * margin);
            BlockId?[] grounds = new BlockId?[layerSpan * layerSpan];
            for (int j = -margin; j < side + margin; j++)
            for (int i = -margin; i < side + margin; i++)
            {
                int at = At(i, j);
                double height = ground[at];
                BlockId? natural = null;
                if (!double.IsNaN(height) && waterTop[at] <= height)
                {
                    double rise = 0;
                    foreach (int neighbour in (ReadOnlySpan<int>)[at - 1, at + 1, at - span, at + span])
                    {
                        if (!double.IsNaN(ground[neighbour])) rise = Math.Max(rise, Math.Abs(ground[neighbour] - height));
                    }

                    natural = FarGround.Natural(geography[at], rise, height - FarGround.GroundTopFace, tile.CellSize);
                }

                grounds[((j + margin) * layerSpan) + i + margin] = natural;
            }

            for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int vi = i, vj = j;
                BlockId? GroundAt(int dx, int dz) => grounds[((vj + dz + margin) * layerSpan) + vi + dx + margin];
                if (GroundAt(0, 0) is null)
                {
                    if (!double.IsNaN(ground[At(i, j)])) samples.SetMaterial(i, j, frozen[At(i, j)] ? IceIndex : WaterIndex);
                    samples.SetLayerWeights(i, j, Vector4.UnitX);
                    continue;
                }

                // Two four-layer sets, dry meadow in the second (FarGroundBlend): the heightfield weighs four layers a material.
                bool dry = FarGroundBlend.UsesDrySet(GroundAt, reach);
                if (dry) samples.SetMaterial(i, j, DryGroundIndex);
                samples.SetLayerWeights(i, j, FarGroundBlend.Weights(GroundAt, reach, dry));
            }
        }

    }
}
