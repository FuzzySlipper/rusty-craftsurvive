using System.Globalization;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.VegetationTiles;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Ground cover beyond the voxel scatter (#10016), as two Engine vegetation fields over what the recipe
/// generates (<see cref="VegetationGround"/>), with no voxels behind them. The mid field's 2 m lattice lies
/// over the near ground; the far field's 8 m lattice lies on the far field's own grid, so its copies stand
/// on the triangles drawn there. Every kind is one layer in each field with the voxel scatter's key, seed and
/// density, so its copies hand over one for one: the voxel scatter's fade band is the mid field's near
/// band, and the mid field's fade band the far field's. The far field keeps a falling share of its copies
/// with distance out to the player's reach, the kept ones grown so the cover holds (Engine #10017). Edited
/// columns grow nothing: the mask is applied on the update's thread just before a tile is committed, and an
/// edit re-asks the tiles under it.
/// </summary>
internal sealed class VegetationTiles : IDisposable
{
    /// <summary>The voxel scatter grows this far, inside the full-detail ground, fading over the handover band.</summary>
    internal const float VoxelReach = 40, HandoverFade = 8;

    /// <summary>Ground cover reaches this far by default, and at least past the mid field.</summary>
    internal const float DefaultReachMetres = 256, MinimumReachMetres = 128;

    private const double MidTileMetres = 32;
    private const uint MidCells = 16;

    /// <summary>
    /// Far tiles of eight far cells: on the far field's grid, and small enough that the tiles touching the
    /// reach's circle add little beyond it (a tile's copies all count, corners included).
    /// </summary>
    private const uint FarCells = 8;
    private const double FarTileMetres = FarCells * FarGround.CellMetres;

    /// <summary>A layer's tiles reach past its circle by about this share of a tile, on average.</summary>
    private const float TileOverhangShare = 0.75f;

    /// <summary>The mid field reaches this far: inside the near ground's square, so the circle stays over drawn voxels.</summary>
    internal const float MidReach = 112;

    /// <summary>The far field's copies shrink away over this share of its reach.</summary>
    private const float FarFadeShare = 0.25f;

    /// <summary>The far field keeps every copy at the mid field's reach, falling to this share at its own.</summary>
    private const float FarKeptShare = 0.3f;

    /// <summary>Kept copies grow by the inverse square root of the share kept, up to this.</summary>
    private const float ThinnedMaximumScale = 1.7f;
    private const float BudgetHeadroom = 1.25f;
    private const int CommitsPerUpdate = 16;

    /// <summary>The fields are centred on the player again once they have moved this far.</summary>
    private const double FocusStepMetres = 2;

    private readonly IEngineContext engine;
    private readonly Func<TerrainOverlayState> overlay;
    private readonly VegetationField mid, far;
    private readonly VegetationStreamer midStreamer, farStreamer;
    private (double X, double Y, double Z) focus;

    internal VegetationTiles(IEngineContext engine, TerrainRecipe recipe, SpatialSession session, Func<TerrainOverlayState> overlay,
        VoxelAddress centre)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        focus = Eye(centre);
        VegetationFocus at = VegetationFocus.At(focus.X, focus.Y, focus.Z);
        mid = engine.Vegetation.Create(new VegetationFieldCreateRequest(session, MidTileMetres, MidCells, at));
        far = engine.Vegetation.Create(new VegetationFieldCreateRequest(session, FarTileMetres, FarCells, at));
        Sampler sampler = new(new VegetationGround(recipe));
        midStreamer = new VegetationStreamer(engine, mid, sampler, Mask, CommitsPerUpdate);
        farStreamer = new VegetationStreamer(engine, far, sampler, Mask, CommitsPerUpdate);
    }

    /// <summary>Sets every kind's layer in both fields: the far field out to <paramref name="reach"/>, thinning with distance.</summary>
    internal void Grow(IEnumerable<TerrainScatter.Kind> kinds, float reach)
    {
        foreach (TerrainScatter.Kind kind in kinds)
        {
            VegetationLayerRequest near = new VegetationLayerRequest(mid, kind.Key, kind.Look, kind.Material, kind.Density, MidReach) with
            {
                Grounds = kind.Ground.Aggregate(0UL, (mask, block) => mask | VegetationLayerRequest.Ground(BlockRegistry.Get(block).Slot)),
                Seed = kind.Seed,
                Fade = HandoverFade,
                Near = VoxelReach - HandoverFade,
                NearFade = HandoverFade,
                SlopeLimitDegrees = kind.SlopeDegrees,
                Align = kind.Align,
                ScaleMin = kind.ScaleMin,
                ScaleMax = kind.ScaleMax,
                TintLow = kind.TintLow,
                TintHigh = kind.TintHigh,
                CastsShadows = kind.CastsShadows,
                MaximumInstances = Budget(kind.Density, MidReach, MidTileMetres),
            };
            Set(near);
            Set(near with
            {
                Field = far,
                Reach = reach,
                Fade = reach * FarFadeShare,
                Near = MidReach - HandoverFade,
                // Beyond the mid field a stone's or bush's shadow is a few pixels: not worth its caster pass.
                CastsShadows = false,
                MaximumInstances = Budget(kind.Density, reach, FarTileMetres),
            });
            if (kind.Density > 0)
            {
                engine.Vegetation.SetLayerDetail(new VegetationLayerDetailRequest(far, kind.Key, ReadOnlyMemory<VegetationLod>.Empty, 0,
                    new VegetationThinningPoint[] { new(MidReach, 1f), new(reach, FarKeptShare) }, ThinnedMaximumScale));
            }
        }
    }

    /// <summary>Keeps the fields centred on the player, re-asks the tiles under new edits, and streams tiles.</summary>
    internal void Follow(VoxelAddress centre, (long MinX, long MinZ, long MaxX, long MaxZ)? changed)
    {
        (double X, double Y, double Z) eye = Eye(centre);
        if (Math.Max(Math.Abs(eye.X - focus.X), Math.Max(Math.Abs(eye.Y - focus.Y), Math.Abs(eye.Z - focus.Z))) >= FocusStepMetres)
        {
            focus = eye;
            VegetationFocus at = VegetationFocus.At(eye.X, eye.Y, eye.Z);
            engine.Vegetation.SetFocus(new VegetationFocusRequest(mid, at));
            engine.Vegetation.SetFocus(new VegetationFocusRequest(far, at));
        }

        if (changed is var (minX, minZ, maxX, maxZ))
        {
            // A lattice step around them, for the slope its neighbours read.
            double margin = FarGround.CellMetres + 1;
            foreach (VegetationField field in (ReadOnlySpan<VegetationField>)[mid, far])
                engine.Vegetation.Invalidate(new VegetationInvalidateRequest(field, minX - margin, minZ - margin, maxX + margin, maxZ + margin));
        }

        midStreamer.Update();
        farStreamer.Update();
    }

    /// <summary>Whether both fields have every tile they want, placed: for settling a comparison.</summary>
    internal bool Settled => IsSettled(engine.Vegetation.Read(mid), midStreamer) && IsSettled(engine.Vegetation.Read(far), farStreamer);

    internal string Readout()
    {
        static string Field(string name, VegetationReadout r, VegetationStreamer streamer) => string.Create(CultureInfo.InvariantCulture,
            $"{name}: tiles={r.Tiles} pending={r.PendingTiles} patches={r.Patches} copies={r.Instances} overBudget={r.OverBudget} deferred={r.Deferred} placedLast={r.Placed}/{r.PlacedInstances} placeMs={r.PlacementMicroseconds / 1e3:F2} selectMs={r.SelectionMicroseconds / 1e3:F2} publishMs={r.PublicationMicroseconds / 1e3:F2} invalidated={r.InvalidatedTiles} replaced={r.ReplacedPatches} sampleMB={r.SampleBytes / 1e6:F2} inFlight={streamer.InFlight} committed={streamer.Committed} ignored={streamer.Ignored} sampleMs={streamer.SampleMilliseconds:F0}");
        return string.Create(CultureInfo.InvariantCulture, $"tiles settled={Settled} ")
            + Field("mid", engine.Vegetation.Read(mid), midStreamer) + " | "
            + Field("far", engine.Vegetation.Read(far), farStreamer);
    }

    public void Dispose()
    {
        midStreamer.Dispose();
        farStreamer.Dispose();
        mid.Dispose();
        far.Dispose();
    }

    private static bool IsSettled(VegetationReadout read, VegetationStreamer streamer) =>
        read.PendingTiles == 0 && read.Deferred == 0 && streamer.InFlight == 0;

    private static (double X, double Y, double Z) Eye(VoxelAddress centre) =>
        (centre.X + 0.5, centre.Y + PlayerConstants.StandingEyeHeight, centre.Z + 0.5);

    /// <summary>
    /// A layer's cap: the area its tiles cover at its density, with headroom; a cap, not a target. A tile
    /// that does not fit stays bare and counts as over budget.
    /// </summary>
    private static uint Budget(float density, float reach, double tileMetres)
    {
        double covered = reach + (TileOverhangShare * tileMetres);
        return (uint)Math.Ceiling(Math.PI * covered * covered * density * BudgetHeadroom);
    }

    private void Set(VegetationLayerRequest request)
    {
        if (request.Density > 0) engine.Vegetation.SetLayer(request);
        else engine.Vegetation.RemoveLayer(new VegetationLayerRemoval(request.Field, request.Layer));
    }

    /// <summary>Grows nothing on a vertex within half a cell of an edited column: the player built or dug there.</summary>
    private void Mask(VegetationTileSamples samples)
    {
        VegetationTileRequest tile = samples.Tile;
        TerrainOverlayState edits = overlay();
        double half = tile.CellSize / 2;
        long Low(double at) => (long)Math.Floor(at - half);
        long High(double at) => (long)Math.Ceiling(at + half);
        if (!edits.TouchesColumns(Low(tile.SampleX(0)), Low(tile.SampleZ(0)), High(tile.SampleX(tile.Side - 1)), High(tile.SampleZ(tile.Side - 1))))
            return;

        for (int j = 0; j < tile.Side; j++)
        for (int i = 0; i < tile.Side; i++)
        {
            double x = tile.SampleX(i), z = tile.SampleZ(j);
            bool edited = false;
            for (long cz = Low(z); cz <= High(z) && !edited; cz++)
            for (long cx = Low(x); cx <= High(x) && !edited; cx++)
                edited = edits.TouchesColumn(cx, cz);
            if (edited) samples.SetCover(i, j, 0f);
        }
    }

    /// <summary>One tile's heights and ground classes on a worker, from the recipe alone.</summary>
    internal sealed class Sampler(VegetationGround ground) : IVegetationSampler
    {
        public void Sample(VegetationTileRequest tile, VegetationTileSamples samples)
        {
            int count = tile.HeightSide;
            float[] heights = new float[count * count];
            byte[] slots = new byte[count * count];
            ground.Fill(tile.SampleX(-1), tile.SampleZ(-1), tile.CellSize, count, heights, slots);
            heights.CopyTo(samples.Heights, 0);
            for (int j = 0; j < tile.Side; j++)
            for (int i = 0; i < tile.Side; i++)
                samples.SetGround(i, j, slots[((j + 1) * count) + i + 1]);
        }
    }
}
