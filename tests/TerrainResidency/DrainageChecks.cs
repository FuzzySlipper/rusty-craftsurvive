using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

internal static class DrainageChecks
{
    internal static void Run()
    {
        // Deliberately small synthetic geography distinguishes downhill routing from
        // a visually plausible noise mask. The centre reserve/pass stays untouched.
        const int Segments = 16;
        const int Side = Segments + 1;
        MapSample[] original = new MapSample[Side * Side];
        for (int z = 0; z < Side; z++)
        for (int x = 0; x < Side; x++)
            original[z * Side + x] = new(8 + Math.Abs(x - 5) + Math.Abs(z - 11), 0.7, 0.1, 0.2, 0.8, z == 5 ? 1 : 0);
        MapSample[] refined = original.ToArray();
        DrainageNode[] routes = WorldMapDrainage.Refine(refined, Segments);
        WorldMapDrainage.Validate(refined, routes, Side);
        int sink = 11 * Side + 5;
        Check.That(routes[sink].Receiver == DrainageNode.Basin && routes[sink].Catchment > 100,
            "an intentional inland depression retains a connected closed catchment");
        Check.That(refined[sink].Elevation <= original[sink].Elevation, "closed basins are not filled to a spill level");
        Check.That(Enumerable.Range(0, refined.Length).Where(i => refined[i].Protection == 1)
            .All(i => refined[i].Elevation == original[i].Elevation && routes[i].Bed == original[i].Elevation && refined[i].Detail == 0),
            "protected pass and arrival cores retain elevation and forbid local noise");
        Check.That(routes.Where(r => r.Receiver < 0).Sum(r => r.Catchment) == routes.Length,
            "outlets and closed basins account for every contributing node exactly once");
        Check.That(refined.Zip(original).Any(p => p.Second.Elevation - p.First.Elevation > 1), "erosion refines elevations before local sampling");
        TerrainConfiguration syntheticConfig = new(12345, Segments * WorldMap.MinimumNodeSpacing);
        WorldMap synthetic = new(syntheticConfig, refined, routes);
        for (int x = 1; x < Segments; x++)
        {
            double px = synthetic.Coordinate(x), pz = synthetic.Coordinate(5);
            MapSample passage = synthetic.Sample(px, pz);
            Check.That(passage.Elevation == original[5 * Side + x].Elevation && passage.Detail == 0,
                "local channel sampling respects the protected pass, not just its saved nodes");
        }
        DrainageNode[] corrupt = routes.ToArray();
        corrupt[sink] = corrupt[sink] with { Receiver = routes.Length };
        Check.Throws<ArgumentException>(() => new WorldMap(syntheticConfig, refined, corrupt), "restored maps reject invalid receivers");
        corrupt = routes.ToArray();
        corrupt[sink] = corrupt[sink] with { Catchment = 1 };
        Check.Throws<ArgumentException>(() => new WorldMap(syntheticConfig, refined, corrupt), "restored maps reject inconsistent catchment counts");
        corrupt = routes.ToArray();
        corrupt[sink] = corrupt[sink] with { Bed = double.NaN };
        Check.Throws<ArgumentException>(() => new WorldMap(syntheticConfig, refined, corrupt), "restored maps reject nonfinite bed heights");

        // A flat map is a terminating forest, not a loop or an unbounded convergence run.
        MapSample[] flat = Enumerable.Repeat(new MapSample(10, 0.5, 0.5, 0, 0.5, 0), Side * Side).ToArray();
        DrainageNode[] flatRoutes = WorldMapDrainage.Refine(flat, Segments);
        WorldMapDrainage.Validate(flat, flatRoutes, Side);
        Check.That(flatRoutes.Where(r => r.Receiver < 0).Sum(r => r.Catchment) == flat.Length, "equal-height plateaus terminate deterministically");

        foreach (ulong seed in new ulong[] { TerrainConstants.DefaultSeed, 0, 1, 12345, ulong.MaxValue })
        {
            Stopwatch watch = Stopwatch.StartNew();
            WorldMap map = WorldMapGenerator.Generate(new(seed, 8192));
            watch.Stop();
            Console.WriteLine($"drainage seed={seed} width={map.Configuration.Size} nodes={map.Nodes.Length} ms={watch.Elapsed.TotalMilliseconds:F3} "
                + $"fingerprint={map.Fingerprint:x16} basins={map.Drainage.ToArray().Count(r => r.Receiver == DrainageNode.Basin)} "
                + $"channels={map.Drainage.ToArray().Count(r => r.Receiver >= 0 && r.Catchment >= WorldMapDrainage.MinimumCatchment)}");
            foreach (MapSite site in map.Sites) Console.WriteLine($"  {site}");
            Check.That(map.Fingerprint == WorldMapGenerator.Generate(map.Configuration).Fingerprint, "erosion reproduces graph, fields and beds exactly");
            Check.That(map.Drainage.ToArray().Where(r => r.Receiver < 0).Sum(r => r.Catchment) == map.Nodes.Length,
                "all generated catchments terminate at a declared basin or edge outlet");
            Check.That(map.Drainage.ToArray().Any(r => r.Receiver == DrainageNode.Basin), "natural closed basins survive generation");
            for (int i = 0; i < map.Nodes.Length; i++)
            {
                DrainageNode route = map.Drainage[i];
                if (route.Receiver < 0) continue;
                Check.That(route.Bed >= map.Drainage[route.Receiver].Bed, "carved bed never climbs toward its receiving reach");
                if (route.Catchment < WorldMapDrainage.MinimumCatchment) continue;
                int r = route.Receiver;
                double x = (map.Coordinate(i % map.Side) + map.Coordinate(r % map.Side)) / 2;
                double z = (map.Coordinate(i / map.Side) + map.Coordinate(r / map.Side)) / 2;
                MapSample centre = map.Sample(x, z);
                Check.That(centre.Detail <= 0.000001, "local noise cannot dam a channel centreline");
                Check.That(double.IsFinite(centre.Elevation), "local channel geometry is finite");
            }
            // Sample the same reach from either side of each coarse boundary, including
            // where the bounded nearby-edge set changes. This catches omitted neighbours.
            for (int x = 1; x < map.Segments; x++)
            for (int z = 1; z < map.Segments; z++)
            {
                double px = map.Coordinate(x), pz = map.Coordinate(z);
                Check.That(Math.Abs(map.Sample(px - 0.00001, pz).Elevation - map.Sample(px + 0.00001, pz).Elevation) < 0.001,
                    "channel carving agrees across coarse sampling boundaries");
                Check.That(Math.Abs(map.Sample(px, pz - 0.00001).Elevation - map.Sample(px, pz + 0.00001).Elevation) < 0.001,
                    "channel carving agrees across north/south coarse sampling boundaries");
            }
        }
    }
}
