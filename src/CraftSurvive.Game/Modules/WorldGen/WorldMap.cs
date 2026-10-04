using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

internal readonly record struct MapSample(double Elevation, double Temperature, double Moisture,
    double Rock, double Detail, double Passage, double Protection = 0, double Drainage = 0, double Erosion = 0)
{
    internal static MapSample Lerp(MapSample a, MapSample b, double t) => new(
        a.Elevation + (b.Elevation - a.Elevation) * t, a.Temperature + (b.Temperature - a.Temperature) * t,
        a.Moisture + (b.Moisture - a.Moisture) * t, a.Rock + (b.Rock - a.Rock) * t,
        a.Detail + (b.Detail - a.Detail) * t, a.Passage + (b.Passage - a.Passage) * t,
        a.Protection + (b.Protection - a.Protection) * t, a.Drainage + (b.Drainage - a.Drainage) * t,
        a.Erosion + (b.Erosion - a.Erosion) * t);
}

internal readonly record struct MapSite(string Name, double X, double Z, MapSample Geography);

/// <summary>A generated, immutable coarse map. X/Z are metres; north is -Z. Local terrain reads it, never rewrites it.</summary>
internal sealed class WorldMap
{
    internal const int MaximumSegments = 64;
    internal const int MinimumNodeSpacing = 8;
    internal const int MaximumNodes = (MaximumSegments + 1) * (MaximumSegments + 1);
    internal const double MaximumElevation = 48;
    internal const double LocalReliefLimit = 5;
    private const ulong FingerprintBasis = 0xCBF29CE484222325;
    private const ulong FingerprintPrime = 0x100000001B3;
    private const double FrostTemperature = 0.3;
    private const double AridMoisture = 0.38;
    private const double DrySiteTemperature = 0.7;
    private const double PassSiteTemperature = 0.55;
    private const double RidgeSiteRockWeight = 0.3;
    private readonly MapSample[] nodes;
    private readonly DrainageNode[] drainage;

    internal WorldMap(TerrainConfiguration configuration, ReadOnlySpan<MapSample> samples, ReadOnlySpan<DrainageNode> routes)
    {
        Configuration = configuration.Validate();
        Segments = Math.Min(MaximumSegments, configuration.Size / MinimumNodeSpacing);
        if (samples.Length != (Segments + 1) * (Segments + 1)) throw new ArgumentException("Map node count does not match extent.");
        nodes = samples.ToArray();
        drainage = routes.ToArray();
        foreach (MapSample n in nodes)
            if (!double.IsFinite(n.Elevation) || n.Elevation < GenerationConstants.MinimumTerrainHeight || n.Elevation > MaximumElevation
                || !Unit(n.Temperature) || !Unit(n.Moisture) || !Unit(n.Rock) || !Unit(n.Detail) || !Unit(n.Passage) || !Unit(n.Protection) || !Unit(n.Drainage) || !Unit(n.Erosion))
                throw new ArgumentException("Map contains an invalid geographic sample.");
        WorldMapDrainage.Validate(nodes, drainage, Side);
        Fingerprint = ComputeFingerprint();
        Sites = [FindChannelSite("Dry canyon", s => Arid(s) && !Frozen(s),
                FindSite("Dry basin", s => Arid(s) && !Frozen(s), s => s.Moisture + Math.Abs(s.Temperature - DrySiteTemperature))),
            FindSite("Upland pass", s => !Arid(s) && !Frozen(s), s => 1 - s.Passage + Math.Abs(s.Temperature - PassSiteTemperature)),
            FindChannelSite("Frozen valley", Frozen,
                FindSite("Frozen ridge", Frozen, s => s.Temperature + (1 - s.Rock) * RidgeSiteRockWeight))];
    }

    internal TerrainConfiguration Configuration { get; }
    internal int Segments { get; }
    internal int Side => Segments + 1;
    internal double Spacing => Configuration.Size / (double)Segments;
    internal double Radius => Configuration.Size / 2d;
    internal ulong Fingerprint { get; }
    internal IReadOnlyList<MapSite> Sites { get; }
    internal ReadOnlySpan<MapSample> Nodes => nodes;
    internal ReadOnlySpan<DrainageNode> Drainage => drainage;
    internal double Coordinate(int index) => -Radius + index * Spacing;
    internal bool Contains(double x, double z) => double.IsFinite(x) && double.IsFinite(z) && Math.Abs(x) <= Radius && Math.Abs(z) <= Radius;

    /// <summary>Clamp the geographic boundary for neighbour/normal sampling; terrain itself remains finite.</summary>
    internal MapSample Sample(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x));
        double gx = Math.Clamp((x + Radius) / Spacing, 0, Segments);
        double gz = Math.Clamp((z + Radius) / Spacing, 0, Segments);
        int ix = Math.Min((int)gx, Segments - 1), iz = Math.Min((int)gz, Segments - 1);
        double tx = Smooth(gx - ix), tz = Smooth(gz - iz);
        MapSample broad = MapSample.Lerp(MapSample.Lerp(nodes[iz * Side + ix], nodes[iz * Side + ix + 1], tx),
            MapSample.Lerp(nodes[(iz + 1) * Side + ix], nodes[(iz + 1) * Side + ix + 1], tx), tz);
        return WorldMapDrainage.Sample(this, broad, Math.Clamp(x, -Radius, Radius), Math.Clamp(z, -Radius, Radius), ix, iz);
    }

    internal static string Region(MapSample sample) => sample.Temperature < FrostTemperature ? "Frozen highlands"
        : sample.Moisture < AridMoisture ? "Dry country" : "Sage uplands";
    internal static bool Frozen(MapSample sample) => sample.Temperature < FrostTemperature;
    internal static bool Arid(MapSample sample) => sample.Moisture < AridMoisture;
    private static bool Unit(double v) => double.IsFinite(v) && v >= 0 && v <= 1;
    internal static double Smooth(double v) => v * v * (3 - 2 * v);

    private MapSite FindSite(string name, Func<MapSample, bool> eligible, Func<MapSample, double> score)
    {
        const int EdgeMargin = 2;
        const double SiteWaterClearance = 2;
        double best = double.MaxValue;
        MapSite selected = default;
        for (int z = EdgeMargin; z <= Segments - EdgeMargin; z++)
        for (int x = EdgeMargin; x <= Segments - EdgeMargin; x++)
        {
            MapSample n = Sample(Coordinate(x), Coordinate(z));
            if (!eligible(n) || n.Elevation < GenerationConstants.WaterLevel + SiteWaterClearance) continue;
            double value = score(n);
            if (value >= best) continue;
            best = value;
            selected = new(name, Coordinate(x), Coordinate(z), n);
        }
        return best < double.MaxValue ? selected : new(name, 0, 0, Sample(0, 0));
    }

    private MapSite FindChannelSite(string name, Func<MapSample, bool> eligible, MapSite fallback)
    {
        const double SiteWaterClearance = 2;
        double best = 0;
        MapSite selected = fallback;
        for (int i = 0; i < drainage.Length; i++)
        {
            int receiver = drainage[i].Receiver;
            if (receiver < 0 || drainage[i].Catchment < WorldMapDrainage.MinimumCatchment) continue;
            double x = (Coordinate(i % Side) + Coordinate(receiver % Side)) / 2;
            double z = (Coordinate(i / Side) + Coordinate(receiver / Side)) / 2;
            MapSample n = Sample(x, z);
            double depth = (nodes[i].Elevation + nodes[receiver].Elevation - drainage[i].Bed - drainage[receiver].Bed) / 2;
            if (!eligible(n) || n.Elevation < GenerationConstants.WaterLevel + SiteWaterClearance || depth <= best) continue;
            best = depth;
            selected = new(name, x, z, n);
        }
        return selected;
    }

    private ulong ComputeFingerprint()
    {
        ulong hash = FingerprintBasis;
        void Mix(ulong v) => hash = unchecked((hash ^ v) * FingerprintPrime);
        Mix(Configuration.Seed); Mix(Configuration.GeneratorVersion); Mix((ulong)Configuration.Size);
        foreach (MapSample n in nodes)
            foreach (double value in (ReadOnlySpan<double>)[n.Elevation, n.Temperature, n.Moisture, n.Rock, n.Detail, n.Passage, n.Protection, n.Drainage, n.Erosion])
                Mix(BitConverter.DoubleToUInt64Bits(value));
        foreach (DrainageNode n in drainage)
        { Mix(unchecked((ulong)n.Receiver)); Mix((ulong)n.Catchment); Mix(BitConverter.DoubleToUInt64Bits(n.Bed)); }
        return hash;
    }
}

/// <summary>Bounded base geography followed by one map-resolution drainage/erosion pass.</summary>
internal static class WorldMapGenerator
{
    private const int NoiseDomain = 4096;
    private const int BroadScale = 900;
    private const int RegionScale = 1600;
    private const double BaseElevation = 6;
    private const double BroadRelief = 16;
    private const double RidgeRelief = 29;
    private const double RidgeWidth = 0.16;
    private const double BasinRelief = 22;
    private const double BasinWidth = 0.23;
    private const double PassWidth = 0.10;
    private const double PassageRelief = 0.82;
    private const double ClimateVariation = 0.16;
    private const double AltitudeCooling = 0.18;
    private const ulong RidgeSalt = 0x482ADB918CE32057;
    private const ulong ClimateSalt = 0x719F4EDDA53286CB;
    private const ulong BasinSalt = 0x2FED832851C79A64;
    private const double CentreReserve = 0.035;
    private const double RidgeOffsetSpan = 0.5;
    private const double PassOffsetSpan = 0.9;
    private const double BasinX = -0.5;
    private const double BasinZ = 0.3;
    private const double BroadRock = 0.3;
    private const double BaseDetail = 0.3;
    private const double RockDetail = 0.7;
    private const double PassDetailReduction = 0.8;
    private const double CentreDetailReduction = 0.85;
    private const double NoiseMidpoint = 0.5;

    internal static WorldMap Generate(TerrainConfiguration configuration)
    {
        configuration.Validate();
        int segments = Math.Min(WorldMap.MaximumSegments, configuration.Size / WorldMap.MinimumNodeSpacing);
        MapSample[] nodes = new MapSample[(segments + 1) * (segments + 1)];
        ulong seed = configuration.Contract.GeographyNoiseSeed;
        double ridgeOffset = (TerrainRecipe.ValueNoise(seed, NoiseDomain, 0, RegionScale) - NoiseMidpoint) * RidgeOffsetSpan;
        double passZ = (TerrainRecipe.ValueNoise(seed ^ RidgeSalt, 0, NoiseDomain, RegionScale) - NoiseMidpoint) * PassOffsetSpan;
        for (int z = 0; z <= segments; z++)
        for (int x = 0; x <= segments; x++)
        {
            double nx = x * 2d / segments - 1, nz = z * 2d / segments - 1;
            double broad = TerrainRecipe.ValueNoise(seed, nx * NoiseDomain, nz * NoiseDomain, BroadScale);
            double region = TerrainRecipe.ValueNoise(seed ^ ClimateSalt, nx * NoiseDomain, nz * NoiseDomain, RegionScale);
            double ridgeAxis = ridgeOffset + Math.Sin(nz * Math.PI) * RidgeWidth;
            double ridge = Math.Exp(-Math.Pow((nx - ridgeAxis) / RidgeWidth, 2));
            double passage = Math.Exp(-Math.Pow((nz - passZ) / PassWidth, 2));
            double basin = Math.Exp(-Math.Pow((nx - BasinX) / BasinWidth, 2) - Math.Pow((nz - BasinZ) / BasinWidth, 2));
            double height = Math.Clamp(BaseElevation + BroadRelief * broad + RidgeRelief * ridge * (1 - passage * PassageRelief)
                - BasinRelief * basin, GenerationConstants.MinimumTerrainHeight, WorldMap.MaximumElevation);
            // A broad quiet starting area; region fields remain continuous and are not a spawn biome.
            double centre = Math.Exp(-(nx * nx + nz * nz) / CentreReserve);
            height = height * (1 - centre) + BaseElevation * centre;
            double temperature = Math.Clamp((nz + 1) / 2 + (region - NoiseMidpoint) * ClimateVariation - height / WorldMap.MaximumElevation * AltitudeCooling, 0, 1);
            double moisture = Math.Clamp((nx + 1) / 2 + (TerrainRecipe.ValueNoise(seed ^ BasinSalt, nx * NoiseDomain, nz * NoiseDomain, RegionScale) - NoiseMidpoint) * ClimateVariation, 0, 1);
            double rock = Math.Clamp(ridge * (1 - passage) + broad * BroadRock, 0, 1);
            double detail = (BaseDetail + rock * RockDetail) * (1 - passage * PassDetailReduction) * (1 - centre * CentreDetailReduction);
            nodes[z * (segments + 1) + x] = new(height, temperature, moisture, rock, detail, passage * ridge);
        }
        DrainageNode[] routes = WorldMapDrainage.Refine(nodes, segments);
        return new(configuration, nodes, routes);
    }
}
