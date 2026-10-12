using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Heightfields;

namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>
/// What the horizon's ground is at any point (#9848): the world map at true height in world metres,
/// for the Engine's heightfield rings (rusty-engine #9842) to sample on its streamer's workers. It reads
/// only data nothing changes under it (the map, its rivers, the erosion filter, built region tiles) and
/// the latest <see cref="Frame"/>, an immutable snapshot of the sink about the player; it calls no Engine
/// service.
/// <para>
/// A continent's rings, finest first:
/// <list type="bullet">
/// <item>31.25 m to about 4 km: region ground where its tiles are built, else the eroded map;</item>
/// <item>250 m to about 64 km: the map with the erosion filter cut in (#9822), bare where the filtered
/// ground is steep, colder where it is lifted (#9813);</item>
/// <item>1 km over the whole continent: the map.</item>
/// </list>
/// An ordinary map is one 32 m ring over the whole map. The Engine geomorphs each ring onto the next,
/// so no tier is sunk beneath another; only the sink about the player under the far field remains
/// (<see cref="HorizonSink"/>).
/// </para>
/// </summary>
internal sealed class HorizonGround : IHeightfieldSampler
{
    /// <summary>Cells along a tile's side, for every ring.</summary>
    internal const uint TileCells = 16;
    /// <summary>A continent's rings: cell widths nest by tile (500 m, 4 km, 16 km), and each reaches well past the finer one.</summary>
    internal const double RegionCellMetres = 31.25, MidCellMetres = 250, ContinentCellMetres = 1_000;
    internal const double RegionReachMetres = 4_000, MidReachMetres = 64_000;
    /// <summary>An ordinary map's one ring: the map view's coarse layer.</summary>
    internal const double MapCellMetres = 32;
    internal const uint RegionRing = 0, MidRing = 1;
    /// <summary>The middle ring's ground turns to bare rock from the first slope, wholly by the second (#9813).</summary>
    private const double RockSlopeFrom = 0.45, RockSlopeFull = 0.95;
    /// <summary>Rock shows from the first share and is whole by the second.</summary>
    private const double RockShowsFrom = 0.15, RockWhole = 0.95;
    /// <summary>A river is drawn this many cells either side of its channel.</summary>
    private const double RiverReach = 0.5, ContinentRiverReach = 0.71;

    /// <summary>The sink about the player and any capture exaggeration, as the latest update left them.</summary>
    internal sealed record Frame((double X, double Z) SinkCentre, double ReachMetres, double Exaggeration, double ExaggerationBase)
    {
        internal static Frame Initial { get; } = new((double.NaN, double.NaN), 0, 1, 0);
    }

    private readonly WorldMap map;
    private readonly MapRegions? regions;
    private readonly ErodedHeights? eroded;
    private readonly double farChunkMetres;
    private Frame frame = Frame.Initial;

    /// <param name="farChunkMetres">The far field's chunk column: the sink eases out inside what it always covers.</param>
    internal HorizonGround(WorldMap map, MapRegions? regions, ErodedHeights? eroded, double farChunkMetres)
    {
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        this.farChunkMetres = farChunkMetres;
        this.regions = regions;
        this.eroded = eroded;
        Rings = RingsReaching(MidReachMetres, CoarsestReach(map));
    }

    /// <summary>The rings, finest first, in world metres.</summary>
    internal HeightfieldRing[] Rings { get; private set; }

    /// <summary>The coarsest ring reaches the far edge of the map from anywhere on it.</summary>
    internal static double CoarsestReach(WorldMap map) => 2 * map.Radius;

    /// <summary>For tuning: the rings with the middle and coarsest reaching this far; the Engine checks they nest.</summary>
    internal HeightfieldRing[] RingsReaching(double midReachMetres, double coarsestReachMetres) => Rings = map.Scale.Continental
        ? [new(RegionCellMetres, RegionReachMetres), new(MidCellMetres, midReachMetres), new(ContinentCellMetres, coarsestReachMetres)]
        : [new(MapCellMetres, coarsestReachMetres)];

    /// <summary>The frame the workers sample by; set before the tiles it changes are invalidated.</summary>
    internal Frame Current
    {
        get => Volatile.Read(ref frame);
        set => Volatile.Write(ref frame, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public void Sample(HeightfieldTileRequest tile, HeightfieldTileSamples samples)
    {
        Frame now = Current;
        int heightSide = tile.HeightSide, side = tile.Side;
        double cell = tile.CellSize;
        bool continental = map.Scale.Continental;
        bool regional = continental && tile.Ring == RegionRing && regions is not null
            && regions.Ready(tile.SampleX(-1), tile.SampleZ(-1), tile.SampleX(heightSide - 2), tile.SampleZ(heightSide - 2));
        bool filtered = continental && eroded is not null && tile.Ring <= MidRing && !regional;
        MapSample[] ground = new MapSample[heightSide * heightSide];
        for (int j = -1; j <= side; j++)
        for (int i = -1; i <= side; i++)
        {
            double x = tile.SampleX(i), z = tile.SampleZ(j);
            int at = ((j + 1) * heightSide) + i + 1;
            if (Math.Abs(x) > map.Radius || Math.Abs(z) > map.Radius)
            {
                ground[at] = default;
                samples.SetHeight(i, j, float.NaN);
                continue;
            }

            MapSample sample = regional ? regions!.Sample(x, z) : map.Sample(x, z);
            if (filtered) sample = Filtered(x, z, sample);
            ground[at] = sample;
            samples.SetHeight(i, j, (float)(Top(sample, now) - Sink(x, z, now)));
        }

        for (int j = 0; j < side; j++)
        for (int i = 0; i < side; i++)
        {
            int at = ((j + 1) * heightSide) + i + 1;
            double x = tile.SampleX(i), z = tile.SampleZ(j);
            if (float.IsNaN(samples.Height(i, j)))
            {
                samples.SetColor(i, j, MapPalette.For(MapBiome.Sea).Linear());
                continue;
            }

            MapSample sample = ground[at];
            if (filtered)
            {
                // The ground as the filter leaves it is bare where it is steep (#9813), measured on this tile's own lattice.
                double steep = Math.Sqrt(Math.Pow(Slope(ground, at - 1, at + 1, cell), 2) + Math.Pow(Slope(ground, at - heightSide, at + heightSide, cell), 2));
                sample = sample with { Rock = Math.Max(sample.Rock, Math.Clamp((steep - RockSlopeFrom) / (RockSlopeFull - RockSlopeFrom), 0, 1)) };
            }

            RiverInfluence? river = regional ? regions!.RiverNear(x, z) : map.Rivers.Nearest(x, z);
            double reach = tile.Ring == RegionRing || !continental ? RiverReach : ContinentRiverReach;
            samples.SetColor(i, j, Paint(sample, river, cell * reach).Linear());
        }
    }

    /// <summary>
    /// The horizon's ground at a world point as the ring that covers it there draws it, sunk as that is:
    /// where a place on the horizon stands.
    /// </summary>
    internal double GroundMetres(double x, double z, (double X, double Z) focus)
    {
        Frame now = Current;
        double away = Math.Max(Math.Abs(x - focus.X), Math.Abs(z - focus.Z));
        bool continental = map.Scale.Continental;
        bool regional = continental && away < RegionReachMetres && regions is not null && regions.Ready(x, z, x, z);
        MapSample sample = regional ? regions!.Sample(x, z) : map.Sample(x, z);
        if (continental && eroded is not null && !regional && away < MidReachMetres) sample = Filtered(x, z, sample);
        return Top(sample, now) - Sink(x, z, now);
    }

    /// <summary>The map with the erosion filter cut in, colder where it lifts the ground.</summary>
    private MapSample Filtered(double x, double z, MapSample sample)
    {
        double height = eroded!.Height(x, z, sample);
        return sample with
        {
            Elevation = height,
            Temperature = Math.Clamp(sample.Temperature - MapClimate.Cooling(height - sample.Elevation), 0, 1),
        };
    }

    private static double Slope(MapSample[] ground, int before, int after, double cell) =>
        (ground[after].Elevation - ground[before].Elevation) / (2 * cell);

    /// <summary>The top of a map sample as the horizon draws it: water's surface over the ground, and any exaggeration.</summary>
    private static double Top(MapSample sample, Frame now)
    {
        double top = Math.Max(sample.Elevation, WaterRules.Surface(sample));
        return now.Exaggeration == 1 ? top : now.ExaggerationBase + ((top - now.ExaggerationBase) * now.Exaggeration);
    }

    /// <summary>How far the ground is sunk at a point: fully near the player, easing out inside what the far field always covers.</summary>
    private double Sink(double x, double z, Frame now)
    {
        if (double.IsNaN(now.SinkCentre.X)) return 0;
        double away = Math.Max(Math.Abs(x - now.SinkCentre.X), Math.Abs(z - now.SinkCentre.Z));
        return HorizonSink.At(away, now.ReachMetres, farChunkMetres);
    }

    /// <summary>A sample's colour (sRGB, as the palette is written): its environment greyed smoothly toward stone, or water.</summary>
    internal static Color Paint(MapSample sample, RiverInfluence? river, double riverReach)
    {
        if (sample.Elevation < GenerationConstants.WaterLevel) return MapPalette.For(MapBiome.Sea);
        if (sample.InLake && sample.Elevation < sample.LakeSurface) return MapPalette.Lake;
        if (river is RiverInfluence near && near.Distance < near.HalfWidth + riverReach) return MapPalette.River;
        double share = WorldMap.Smooth(Math.Clamp((sample.Rock - RockShowsFrom) / (RockWhole - RockShowsFrom), 0, 1));
        // The environment under the rock, not the biome the rock itself makes: rocky ground classifies as
        // alpine part way up, which would step the colour mid-blend; stone at the top stands for it.
        Color from = MapPalette.For(WorldMap.Biome(sample with { Rock = 0 })), to = MapPalette.Stone;
        float t = (float)share;
        return new(from.R + ((to.R - from.R) * t), from.G + ((to.G - from.G) * t), from.B + ((to.B - from.B) * t), 1);
    }
}
