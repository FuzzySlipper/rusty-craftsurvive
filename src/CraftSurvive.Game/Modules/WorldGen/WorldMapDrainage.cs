using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>One receiver per coarse node. Negative receivers explicitly distinguish an edge outlet from a closed basin.</summary>
internal readonly record struct DrainageNode(int Receiver, int Catchment, double Bed)
{
    internal const int Outlet = -1;
    internal const int Basin = -2;
}

/// <summary>
/// Product landform policy: downhill catchments, bounded incision, and climate-shaped cross sections.
/// No water simulation or iteration to convergence. The saved forest is resolved before any chunk is sampled.
/// </summary>
internal static class WorldMapDrainage
{
    internal const int MinimumCatchment = 6;
    private const int FullCatchment = 96;
    internal const double DryIncision = 14;
    internal const double FrozenIncision = 10;
    private const double TemperateIncision = 8;
    private const double HeadwaterStrength = 0.25;
    private const double BroadIncisionFraction = 0.2;
    private const double CentreProtectedRadius = 0.12;
    private const double CentreProtectionFade = 0.10;
    private const double PassProtectionStart = 0.15;
    private const double PassProtectionFull = 0.45;
    private const double LandmarkRockStart = 0.80;
    private const double LandmarkRockFull = 0.95;
    private const double DryFloorWidth = 3;
    private const double DryShoulderWidth = 6;
    private const double ValleyFloorWidth = 5;
    private const double ValleyShoulderWidth = 20;
    private const double ChannelWidening = 0.5;
    private const double MaximumSpacingFraction = 0.45;
    private const double ExposureDepth = 2;
    private const double ShoulderPeak = 4;
    private const double FlatDistance = 1;
    private const double DryMoisture = 0.25;
    private const double WetMoisture = 0.45;
    private const double ColdTemperature = 0.2;
    private const double MildTemperature = 0.4;
    private static readonly double DiagonalDistance = Math.Sqrt(2);

    internal static DrainageNode[] Refine(MapSample[] nodes, int segments)
    {
        int side = segments + 1;
        DrainageNode[] routes = new DrainageNode[nodes.Length];
        // Sort a total order once. Equal-height plateaus drain toward the lower index;
        // every edge strictly decreases this order, including flat basin bottoms.
        int[] ascending = Enumerable.Range(0, nodes.Length).OrderBy(i => nodes[i].Elevation).ThenBy(i => i).ToArray();
        foreach (int i in ascending)
        {
            int x = i % side, z = i / side;
            int receiver = DrainageNode.Basin;
            double steepest = -1;
            if (x == 0 || z == 0 || x == segments || z == segments) receiver = DrainageNode.Outlet;
            else
            {
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int next = (z + dz) * side + x + dx;
                    double drop = nodes[i].Elevation - nodes[next].Elevation;
                    if (drop < 0 || (drop == 0 && next >= i)) continue;
                    double slope = drop / (dx == 0 || dz == 0 ? FlatDistance : DiagonalDistance);
                    if (slope > steepest) { steepest = slope; receiver = next; }
                }
            }
            routes[i] = new(receiver, 1, nodes[i].Elevation);
            double nx = x * 2d / segments - 1, nz = z * 2d / segments - 1;
            double centre = 1 - WorldMap.Smooth(Math.Clamp((Math.Sqrt(nx * nx + nz * nz) - CentreProtectedRadius) / CentreProtectionFade, 0, 1));
            double pass = WorldMap.Smooth(Math.Clamp((nodes[i].Passage - PassProtectionStart) / (PassProtectionFull - PassProtectionStart), 0, 1));
            double landmark = WorldMap.Smooth(Math.Clamp((nodes[i].Rock - LandmarkRockStart) / (LandmarkRockFull - LandmarkRockStart), 0, 1));
            nodes[i] = nodes[i] with { Protection = Math.Max(centre, Math.Max(pass, landmark)) };
        }
        // Catchment area in node units: the stylized geography scales with map extent.
        // Reverse topological order includes each tributary exactly once.
        for (int n = ascending.Length - 1; n >= 0; n--)
        {
            int i = ascending[n], receiver = routes[i].Receiver;
            if (receiver >= 0) routes[receiver] = routes[receiver] with { Catchment = routes[receiver].Catchment + routes[i].Catchment };
        }
        foreach (int i in ascending)
        {
            MapSample sample = nodes[i];
            DrainageNode route = routes[i];
            double strength = Strength(route.Catchment);
            double cold = Coldness(sample), dry = Dryness(sample);
            double maximum = TemperateIncision + (FrozenIncision - TemperateIncision) * cold;
            maximum += (DryIncision - maximum) * dry;
            double depth = maximum * strength * (1 - sample.Protection);
            double bed = Math.Max(GenerationConstants.MinimumTerrainHeight, sample.Elevation - depth);
            // A protected downstream pass can limit upstream incision, never be cut through
            // by it. This also prevents a carved reach from running uphill at a junction.
            if (route.Receiver >= 0) bed = Math.Max(bed, routes[route.Receiver].Bed);
            depth = sample.Elevation - bed;
            routes[i] = route with { Bed = bed };
            nodes[i] = sample with { Elevation = sample.Elevation - depth * BroadIncisionFraction,
                Drainage = strength, Erosion = depth / DryIncision,
                Detail = sample.Detail * (1 - sample.Protection) };
        }

        return routes;
    }

    private static double Coldness(MapSample sample) => 1 - WorldMap.Smooth(Math.Clamp(
        (sample.Temperature - ColdTemperature) / (MildTemperature - ColdTemperature), 0, 1));
    private static double Dryness(MapSample sample) => (1 - Coldness(sample)) * (1 - WorldMap.Smooth(Math.Clamp(
        (sample.Moisture - DryMoisture) / (WetMoisture - DryMoisture), 0, 1)));

    private static double Strength(int catchment) => catchment < MinimumCatchment ? 0 :
        HeadwaterStrength + (1 - HeadwaterStrength) * Math.Clamp(
            (Math.Sqrt(catchment) - Math.Sqrt(MinimumCatchment)) / (Math.Sqrt(FullCatchment) - Math.Sqrt(MinimumCatchment)), 0, 1);

    /// <summary>Resolve only nearby saved reaches. A reach connects neighbouring nodes; its width is less than half a cell.</summary>
    internal static MapSample Sample(WorldMap map, MapSample broad, double x, double z, int ix, int iz)
    {
        double height = broad.Elevation, floor = 0, erosion = 0, exposure = 0;
        // The extra row/column includes reaches entering this cell from any of its four sides.
        for (int rz = Math.Max(0, iz - 1); rz <= Math.Min(map.Segments, iz + 2); rz++)
        for (int rx = Math.Max(0, ix - 1); rx <= Math.Min(map.Segments, ix + 2); rx++)
        {
            int i = rz * map.Side + rx;
            DrainageNode route = map.Drainage[i];
            if (route.Receiver < 0 || route.Catchment < MinimumCatchment) continue;
            int next = route.Receiver;
            double ax = map.Coordinate(rx), az = map.Coordinate(rz);
            double dx = map.Coordinate(next % map.Side) - ax, dz = map.Coordinate(next / map.Side) - az;
            double t = Math.Clamp(((x - ax) * dx + (z - az) * dz) / (dx * dx + dz * dz), 0, 1);
            double distance = Math.Sqrt(Math.Pow(x - ax - dx * t, 2) + Math.Pow(z - az - dz * t, 2));
            double dry = Dryness(broad);
            double widen = 1 + Strength(route.Catchment) * ChannelWidening;
            double inner = Math.Min((ValleyFloorWidth + (DryFloorWidth - ValleyFloorWidth) * dry) * widen, map.Spacing * MaximumSpacingFraction / 2);
            double outer = Math.Min(inner + (ValleyShoulderWidth + (DryShoulderWidth - ValleyShoulderWidth) * dry) * widen, map.Spacing * MaximumSpacingFraction);
            if (distance >= outer) continue;
            double influence = 1 - WorldMap.Smooth(Math.Clamp((distance - inner) / (outer - inner), 0, 1));
            double bed = route.Bed + (map.Drainage[next].Bed - route.Bed) * t;
            // Protection applies to the entire footprint, not just graph endpoints.
            double cut = Math.Max(0, broad.Elevation - bed) * influence * (1 - broad.Protection);
            height = Math.Min(height, broad.Elevation - cut);
            floor = Math.Max(floor, influence);
            erosion = Math.Max(erosion, cut / DryIncision);
            exposure = Math.Max(exposure, WorldMap.Smooth(Math.Clamp(cut / ExposureDepth, 0, 1))
                * ShoulderPeak * influence * (1 - influence));
        }
        return broad with { Elevation = height, Detail = broad.Detail * (1 - floor),
            Erosion = Math.Max(broad.Erosion, erosion), Rock = Math.Max(broad.Rock, exposure) };
    }

    internal static void Validate(ReadOnlySpan<MapSample> nodes, ReadOnlySpan<DrainageNode> routes, int side)
    {
        if (routes.Length != nodes.Length) throw new ArgumentException("Map drainage count does not match geography.");
        // Validate the forest in linear time, rather than tracing every path independently.
        byte[] state = new byte[routes.Length];
        int[] expectedArea = new int[routes.Length];
        Array.Fill(expectedArea, 1);
        for (int i = 0; i < routes.Length; i++)
        {
            DrainageNode n = routes[i];
            bool edge = i % side == 0 || i / side == 0 || i % side == side - 1 || i / side == side - 1;
            if (!double.IsFinite(n.Bed) || n.Bed < GenerationConstants.MinimumTerrainHeight || n.Bed > nodes[i].Elevation
                || n.Catchment < 1 || n.Catchment > nodes.Length || n.Receiver < DrainageNode.Basin || n.Receiver >= nodes.Length
                || (edge != (n.Receiver == DrainageNode.Outlet))) throw new ArgumentException("Invalid drainage node.");
            if (n.Receiver >= 0)
            {
                int r = n.Receiver;
                if (r == i || Math.Abs(r % side - i % side) > 1 || Math.Abs(r / side - i / side) > 1
                    || routes[r].Bed > n.Bed) throw new ArgumentException("Drainage must descend into an adjacent node.");
                expectedArea[r] += n.Catchment;
            }
        }
        for (int i = 0; i < routes.Length; i++)
        {
            if (routes[i].Catchment != expectedArea[i]) throw new ArgumentException("Drainage catchment does not match its tributaries.");
            int j = i;
            while (j >= 0 && state[j] == 0) { state[j] = 1; j = routes[j].Receiver; }
            if (j >= 0 && state[j] == 1) throw new ArgumentException("Drainage contains a cycle.");
            j = i;
            while (j >= 0 && state[j] == 1) { state[j] = 2; j = routes[j].Receiver; }
        }
    }
}
