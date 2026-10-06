using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A continent's region tiles (#9550, Den design/continental-scale). A continent is simulated at a
/// kilometre; walking needs tens of metres. A region tile refines one square of the continent on
/// demand: the continent's smooth geography, seamless relief in world coordinates, and a short
/// local erosion graded to the sea, the tile's border and the continent's drainage network. Tiles
/// overlap and blend across a band, so neighbours meet without a step. Every river comes from the
/// one drainage network (<see cref="MapDrainage"/>), so no river breaks at a tile edge. Tiles are
/// deterministic, so an evicted tile is simply rebuilt.
/// </summary>
internal sealed class MapRegions
{
    /// <summary>Distance between tile centres: each tile owns this 16 km square of the world.</summary>
    internal const double TileSpacing = 16_384;
    /// <summary>Half the width of the band across a tile edge where neighbouring tiles blend.</summary>
    internal const double BlendHalfWidth = 512;
    /// <summary>
    /// A tile's simulated extent: its owned square, the blend band and erosion context beyond it, so
    /// drainage near the band is not shaped by the tile's own border.
    /// </summary>
    internal const int TileExtent = 19_456;
    /// <summary>A tile's lattice: the map spacing over its whole extent.</summary>
    internal static MapGrid TileGrid { get; } = new(TileSegments, TileExtent / (double)TileSegments, TileExtent / 2d);
    private const int TileSegments = (int)(TileExtent / MapGrid.TargetSpacing);
    /// <summary>
    /// Tiles kept built (about 30 MB each): the map's region window and the walker need up to four,
    /// the route ahead a few more, so prefetching never evicts a tile still in use.
    /// </summary>
    private const int CacheLimit = 8;
    /// <summary>Around a party or walker, the tiles within this reach are built ahead of need.</summary>
    internal const double PrefetchRadius = 2048;
    /// <summary>Along a route, tiles are built this far ahead of the party: under the cache, so prefetching never evicts the tiles in use.</summary>
    internal const double RouteAheadMetres = 12_000;
    private const double RouteProbeMetres = 2048;
    private static readonly ConditionalWeakTable<WorldMap, MapRegions> Owned = [];

    private readonly WorldMap continent;
    private readonly Lazy<MapDrainage> drainage;
    private readonly TerrainConfiguration tileConfiguration;
    private readonly MapScale tileScale;
    private readonly ConcurrentDictionary<(int X, int Z), Lazy<RegionTile>> tiles = new();
    private readonly ConcurrentDictionary<(int X, int Z), byte> requested = new();
    private int built, synchronousBuilds;
    /// <summary>Set on a thread building tiles ahead of need, so a build anywhere else counts as one a consumer waited on.</summary>
    [ThreadStatic] private static bool background;
    private double lastBuildMilliseconds, slowestBuildMilliseconds;
    private readonly Lock statsLock = new();
    private readonly LinkedList<(int X, int Z)> recent = [];
    private readonly Lock recentLock = new();

    internal MapRegions(WorldMap continent)
    {
        if (!continent.Scale.Continental) throw new ArgumentException("Only a continent is refined into regions.", nameof(continent));
        this.continent = continent;
        drainage = new(() => new MapDrainage(continent), LazyThreadSafetyMode.ExecutionAndPublication);
        tileConfiguration = continent.Configuration with { Size = TileExtent };
        // A tile simulates at regional resolution but stands at continental elevations.
        tileScale = MapScale.Regional with { PeakElevation = continent.Scale.PeakElevation, MaximumElevation = continent.Scale.MaximumElevation };
    }

    internal WorldMap Continent => continent;

    /// <summary>The one region cache of a continent; it lives as long as the continent's map does.</summary>
    internal static MapRegions For(WorldMap continent) => Owned.GetValue(continent, static map => new MapRegions(map));

    /// <summary>A continent's opening tiles, built with the world so the first walk never waits: the arrival point and its surroundings.</summary>
    internal static void PrepareArrival(WorldMap map)
    {
        if (!map.Scale.Continental) return;
        MapRegions regions = For(map);
        background = true;
        try
        {
            foreach ((int X, int Z) tile in TilesNear(0, 0, PrefetchRadius)) regions.Tile(tile);
        }
        finally { background = false; }
    }

    /// <summary>The continent's drainage network, built on first use (seconds; once per world).</summary>
    internal MapDrainage Drainage => drainage.Value;

    /// <summary>The tile owning a world position.</summary>
    internal static (int X, int Z) TileAt(double x, double z) =>
        ((int)Math.Floor(x / TileSpacing + 0.5), (int)Math.Floor(z / TileSpacing + 0.5));

    internal static (double X, double Z) Centre((int X, int Z) tile) => (tile.X * TileSpacing, tile.Z * TileSpacing);

    /// <summary>Whether every tile whose blend reaches a world rectangle is built, so sampling it never waits.</summary>
    internal bool Ready(double minX, double minZ, double maxX, double maxZ)
    {
        (int X, int Z) low = TileAt(minX - BlendHalfWidth, minZ - BlendHalfWidth), high = TileAt(maxX + BlendHalfWidth, maxZ + BlendHalfWidth);
        for (int tz = low.Z; tz <= high.Z; tz++)
        for (int tx = low.X; tx <= high.X; tx++)
            if (!IsReady((tx, tz))) return false;
        return true;
    }

    /// <summary>Queue the tiles a world rectangle needs, off the calling thread.</summary>
    internal void Prefetch(double minX, double minZ, double maxX, double maxZ)
    {
        (int X, int Z) low = TileAt(minX - BlendHalfWidth, minZ - BlendHalfWidth), high = TileAt(maxX + BlendHalfWidth, maxZ + BlendHalfWidth);
        for (int tz = low.Z; tz <= high.Z; tz++)
        for (int tx = low.X; tx <= high.X; tx++)
            Request((tx, tz));
    }

    /// <summary>The drainage network's river nearest a world point, as region sampling draws it.</summary>
    internal RiverInfluence? RiverNear(double x, double z) => Tile(TileAt(x, z)).Rivers.Nearest(x, z);

    /// <summary>Whether a tile is built, without building it.</summary>
    internal bool IsReady((int X, int Z) tile) => tiles.TryGetValue(tile, out Lazy<RegionTile>? lazy) && lazy.IsValueCreated;

    /// <summary>A tile, built on the calling thread if no one has built it yet; concurrent callers share one build.</summary>
    internal RegionTile Tile((int X, int Z) tile)
    {
        Lazy<RegionTile> lazy = tiles.GetOrAdd(tile, key => new(() => Build(key), LazyThreadSafetyMode.ExecutionAndPublication));
        Touch(tile);
        return lazy.Value;
    }

    /// <summary>Build the tiles whose blend reaches within <paramref name="radius"/> of a point, off the calling thread.</summary>
    internal void Prefetch(double x, double z, double radius)
    {
        foreach ((int X, int Z) tile in TilesNear(x, z, radius)) Request(tile);
    }

    /// <summary>
    /// Work ahead of a party (#9550): the tiles around it, then those along the rest of its route up to
    /// <see cref="RouteAheadMetres"/>, nearest first. Cheap to call every update; each tile is queued once.
    /// </summary>
    internal void PrefetchAhead(Vector2 position, IReadOnlyList<Vector2>? route)
    {
        Prefetch(position.X, position.Y, PrefetchRadius);
        if (route is not { Count: > 1 }) return;
        int from = 0;
        for (int k = 1; k < route.Count; k++)
            if (Vector2.DistanceSquared(route[k], position) < Vector2.DistanceSquared(route[from], position)) from = k;
        double ahead = 0, sinceProbe = 0;
        Vector2 last = position;
        for (int k = from; k < route.Count && ahead < RouteAheadMetres; k++)
        {
            float leg = Vector2.Distance(last, route[k]);
            ahead += leg;
            sinceProbe += leg;
            if (sinceProbe >= RouteProbeMetres || k == route.Count - 1)
            {
                sinceProbe = 0;
                Prefetch(route[k].X, route[k].Y, PrefetchRadius);
            }
            last = route[k];
        }
    }

    private RegionTile Build((int X, int Z) key)
    {
        MapDrainage network = Drainage;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        RegionTile tile = RegionTile.Build(continent, network, tileConfiguration, tileScale, Centre(key));
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        lock (statsLock)
        {
            built++;
            if (!background) synchronousBuilds++;
            lastBuildMilliseconds = ms;
            slowestBuildMilliseconds = Math.Max(slowestBuildMilliseconds, ms);
        }
        return tile;
    }

    /// <summary>Region tile facts for diagnostics: how many are built, cached and pending, and what a build costs.</summary>
    internal string Readout()
    {
        lock (statsLock)
            return FormattableString.Invariant(
                $"regions built={built} synchronous={synchronousBuilds} cached={tiles.Count(pair => pair.Value.IsValueCreated)} pending={Pending} lastBuildMs={lastBuildMilliseconds:F0} slowestBuildMs={slowestBuildMilliseconds:F0} drainage={(drainage.IsValueCreated ? "ready" : "pending")}");
    }

    /// <summary>Tiles a consumer had to build on its own thread because no prefetch had built them yet.</summary>
    internal int SynchronousBuilds { get { lock (statsLock) return synchronousBuilds; } }

    /// <summary>Tiles queued but not yet built.</summary>
    internal int Pending => requested.Count(pair => !IsReady(pair.Key));

    private void Request((int X, int Z) tile)
    {
        if (IsReady(tile) || !requested.TryAdd(tile, 0)) return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            background = true;
            try { state.Regions.Tile(state.Tile); }
            finally { background = false; }
        }, (Regions: this, Tile: tile), false);
    }

    internal static IEnumerable<(int X, int Z)> TilesNear(double x, double z, double radius)
    {
        (int X, int Z) low = TileAt(x - radius - BlendHalfWidth, z - radius - BlendHalfWidth);
        (int X, int Z) high = TileAt(x + radius + BlendHalfWidth, z + radius + BlendHalfWidth);
        for (int tz = low.Z; tz <= high.Z; tz++)
        for (int tx = low.X; tx <= high.X; tx++)
            yield return (tx, tz);
    }

    /// <summary>
    /// Geography at a world position: the owning tile's, blended with its neighbours' across the band
    /// at tile edges, with the drainage network's rivers drawn over it.
    /// </summary>
    internal MapSample Sample(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x));
        (int X, int Z) own = TileAt(x, z);
        Span<(int Tile, double Weight)> across = stackalloc (int, double)[2], along = stackalloc (int, double)[2];
        int countX = Blend(x - own.X * TileSpacing, own.X, across);
        int countZ = Blend(z - own.Z * TileSpacing, own.Z, along);
        double h = 0, t = 0, m = 0, r = 0, d = 0, p = 0;
        for (int b = 0; b < countZ; b++)
        for (int a = 0; a < countX; a++)
        {
            double w = across[a].Weight * along[b].Weight;
            (int X, int Z) key = (across[a].Tile, along[b].Tile);
            (double cx, double cz) = Centre(key);
            MapSample s = Tile(key).Map.Broad(x - cx, z - cz);
            h += w * s.Elevation; t += w * s.Temperature; m += w * s.Moisture;
            r += w * s.Rock; d += w * s.Detail; p += w * s.Protection;
        }
        MapSample blended = new(h, t, m, r, d, p);
        // Every tile shapes the network's reaches identically, so the owning tile's share answers for all.
        return Tile(own).Rivers.Nearest(x, z) is RiverInfluence river ? WorldMap.Channel(blended, river) : blended;
    }

    /// <summary>The tiles along one axis whose blend reaches an offset from the owning tile's centre, with weights summing to one.</summary>
    private static int Blend(double offset, int own, Span<(int Tile, double Weight)> result)
    {
        double inside = TileSpacing / 2 - Math.Abs(offset);
        if (inside >= BlendHalfWidth)
        {
            result[0] = (own, 1);
            return 1;
        }
        double weight = WorldMap.Smooth(Math.Clamp((inside + BlendHalfWidth) / (2 * BlendHalfWidth), 0, 1));
        result[0] = (own, weight);
        result[1] = (own + Math.Sign(offset), 1 - weight);
        return 2;
    }

    private void Touch((int X, int Z) tile)
    {
        (int X, int Z)? evicted = null;
        lock (recentLock)
        {
            if (recent.First?.Value == tile) return;
            recent.Remove(tile);
            recent.AddFirst(tile);
            if (recent.Count > CacheLimit)
            {
                evicted = recent.Last!.Value;
                recent.RemoveLast();
            }
        }
        if (evicted is { } key)
        {
            tiles.TryRemove(key, out _);
            requested.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// The relief a continent's kilometre lattice cannot carry, in world coordinates so every tile and the
/// drainage network agree on it: hills and spurs a few hundred metres across, stronger in rugged and
/// steep country, fading out at the coast. A coarser lattice takes only the octaves it resolves.
/// </summary>
internal static class RegionRelief
{
    internal const int Octaves = 5;
    private const double Wavelength = 2400;
    private const double Persistence = 0.5;
    private const double RuggedMetres = 70;
    private const double PlainShare = 0.2;
    /// <summary>
    /// Further relief in metres per unit of the continent's slope: steep country carries spurs and ravines
    /// strong enough to break drainage into a branching network, not parallel rills down one face.
    /// </summary>
    private const double SlopeMetres = 1200;
    private const double SlopeProbeMetres = 500;
    private const double CoastFadeMetres = 20;
    /// <summary>How far below the continent's surface a valley may cut, at the least.</summary>
    private const double MinimumIncision = 9;
    private const ulong Salt = 0x5BD1E9955BD1E995UL;
    // Rain over a region follows the continent's moisture.
    private const double RainBase = 0.4;
    private const double RainMoisture = 0.8;

    /// <summary>The refined height at a point, how deep a valley there may cut below the continent, and the continent's own sample.</summary>
    internal static (double Height, double Incision, MapSample Broad) At(WorldMap continent, double x, double z, int octaves)
    {
        MapSample broad = continent.Broad(x, z);
        double fallX = continent.Broad(x + SlopeProbeMetres, z).Elevation - continent.Broad(x - SlopeProbeMetres, z).Elevation;
        double fallZ = continent.Broad(x, z + SlopeProbeMetres).Elevation - continent.Broad(x, z - SlopeProbeMetres).Elevation;
        double slope = Math.Sqrt(fallX * fallX + fallZ * fallZ) / (2 * SlopeProbeMetres);
        double roughness = RuggedMetres * (PlainShare + (1 - PlainShare) * broad.Detail) + SlopeMetres * slope;
        double amplitude = roughness * Ease(GenerationConstants.WaterLevel, GenerationConstants.WaterLevel + CoastFadeMetres, broad.Elevation);
        ulong seed = continent.Configuration.Contract.GeographyNoiseSeed ^ Salt;
        double noise = MapNoise.Fbm(seed, x / Wavelength, z / Wavelength, Octaves, Persistence, octaves);
        return (broad.Elevation + amplitude * noise, Math.Max(MinimumIncision, roughness), broad);
    }

    internal static double Rain(MapSample broad) => RainBase + RainMoisture * broad.Moisture;

    /// <summary>How many octaves a lattice of this spacing resolves: those at least two nodes per wavelength.</summary>
    internal static int ResolvedOctaves(double spacing)
    {
        int octaves = 0;
        for (double wavelength = Wavelength; octaves < Octaves && wavelength >= 2 * spacing; wavelength /= MapNoise.Lacunarity) octaves++;
        return Math.Max(1, octaves);
    }

    /// <summary>Cuts below the continent's surface approach the incision depth smoothly, so valley floors keep their fall.</summary>
    internal static double LimitIncision(double height, double surface, double incision)
    {
        double cut = surface - height;
        return cut > 0 ? surface - incision * Math.Tanh(cut / incision) : height;
    }

    private static double Ease(double start, double end, double value) => WorldMap.Smooth(Math.Clamp((value - start) / (end - start), 0, 1));
}

/// <summary>One region tile (#9550): its refined geography in tile-local metres, and the drainage network's reaches that cross it, in world metres.</summary>
internal sealed record RegionTile(WorldMap Map, MapRivers Rivers)
{
    // A short local evolution cuts the relief into valleys graded to the tile's base level.
    private const int ErosionSteps = 14;
    private const double TalusSlope = 0.84;
    private const int TalusIterations = 12;
    /// <summary>A network river holds the nodes this close to its centreline at its surface, so the tile's valleys grade to it.</summary>
    private const double RiverHoldMetres = 24;
    /// <summary>Beyond the tile's extent, how far a river's banks may reach into it.</summary>
    private const double RiverReachMetres = 128;

    internal static RegionTile Build(WorldMap continent, MapDrainage drainage, TerrainConfiguration configuration, MapScale scale, (double X, double Z) centre)
    {
        MapGrid grid = MapRegions.TileGrid;
        int count = grid.Count;
        double reach = grid.Radius + RiverReachMetres;
        MapRivers rivers = drainage.Within(centre.X - reach, centre.Z - reach, centre.X + reach, centre.Z + reach);
        double[] height = new double[count], surface = new double[count], incision = new double[count], hardness = new double[count], rain = new double[count];
        float[] temperature = new float[count], moisture = new float[count];
        bool[] sea = new bool[count], outlet = new bool[count];
        Parallel.For(0, count, i =>
        {
            double x = centre.X + grid.X(i), z = centre.Z + grid.Z(i);
            (double refined, double cut, MapSample broad) = RegionRelief.At(continent, x, z, RegionRelief.Octaves);
            height[i] = refined;
            surface[i] = broad.Elevation;
            incision[i] = cut;
            sea[i] = broad.Elevation < GenerationConstants.WaterLevel;
            // Base level: the sea, the network's rivers at their surface, and the tile's own border.
            if (rivers.Nearest(x, z) is RiverInfluence river && river.Distance < Math.Max(river.HalfWidth, RiverHoldMetres))
            {
                // At the water surface exactly: lower would leave ground below a river its banks then wall in.
                height[i] = Math.Max(GenerationConstants.MinimumTerrainHeight, river.Surface);
                outlet[i] = true;
            }
            outlet[i] |= sea[i] || grid.IsEdge(i);
            hardness[i] = Math.Clamp(continent.HardnessAt(x, z), 0, 1);
            rain[i] = RegionRelief.Rain(broad);
            temperature[i] = (float)Math.Clamp(broad.Temperature, 0, 1);
            moisture[i] = (float)Math.Clamp(broad.Moisture, 0, 1);
        });

        MapRelief relief = MapRelief.FromHeights(grid, height, hardness, sea, outlet);
        MapErosion.Evolve(relief, _ => rain, ErosionSteps, ErosionSteps);
        double[] h = relief.Height;
        for (int i = 0; i < count; i++)
            if (!outlet[i])
                h[i] = Math.Clamp(RegionRelief.LimitIncision(h[i], surface[i], incision[i]), GenerationConstants.MinimumTerrainHeight, scale.MaximumElevation - 1);
        MapErosion.Relax(grid, h, outlet, TalusSlope, TalusIterations);

        MapFlow.Fill(grid, h, outlet, MapFlow.FillGradient);
        float[] elevation = h.Select(v => (float)Math.Min(v, scale.MaximumElevation - 1)).ToArray();
        double[] routed = elevation.Select(v => (double)v).ToArray();
        MapFlow flow = MapFlow.Route(grid, routed, outlet, rain, MapFlow.FillGradient);
        MapFields fields = new(grid, elevation, temperature, moisture, hardness.Select(v => (float)v).ToArray(),
            flow.Discharge.Select(v => (float)v).ToArray());
        return new(new WorldMap(configuration, fields, new MapRegionFrame(centre.X, centre.Z, scale, grid)), rivers);
    }
}
