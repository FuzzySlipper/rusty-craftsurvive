using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using MapLab;
using Rusty.Engine;

// MapLab (#9814): generate a world map from a seed, size and recipe, then write what it looks like
// and the numbers that say whether its ranges will read on the horizon. Runs the product's own
// generator, without the Engine host.

const string Usage = """
    MapLab --seed N --size METRES [--recipe FILE.json] [--out DIR] [--view X,Z ...] [--pixels N] [--reach-km K]
    MapLab --dump-recipe FILE.json

      --seed       the world seed (decimal or 0x hex)
      --size       the world's size in metres: up to 65536 (regional) or 320000-450000 (continental)
      --recipe     a JSON recipe; any fields it names override the defaults (see --dump-recipe)
      --design     a continent design: a JSON file, builtin:NAME for one the generator ships, or none
                   (default: what the game draws a world of this size to - the frontier peninsula for a continent)
      --out        where to write relief.png, height.png, skyline-N.png and stats.txt (default ./maplab-out)
      --view       a viewpoint in world metres for a skyline; repeatable (default 0,0, where a world starts)
      --pixels     the map images' width and height (default 1024)
      --reach-km   how far a skyline looks (default 150, or the map's size)
    """;

JsonSerializerOptions json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
Dictionary<string, List<string>> options = Parse(args);
if (options.TryGetValue("dump-recipe", out List<string>? dump))
{
    File.WriteAllText(dump[0], JsonSerializer.Serialize(MapRecipe.Default, json) + "\n");
    Console.WriteLine($"wrote the default recipe to {dump[0]}");
    return 0;
}

if (!options.ContainsKey("seed") || !options.ContainsKey("size"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

ulong seed = ParseSeed(options["seed"][0]);
int size = int.Parse(options["size"][0], CultureInfo.InvariantCulture);
MapRecipe recipe = options.TryGetValue("recipe", out List<string>? recipePath)
    ? JsonSerializer.Deserialize<MapRecipe>(File.ReadAllText(recipePath[0]), json) ?? MapRecipe.Default
    : MapRecipe.Default;
// By default, the design the game draws a world of this size to; "none" for the seeded generator alone.
ContinentDesign? design = !options.TryGetValue("design", out List<string>? designArg) ? ContinentDesign.For(size)
    : designArg[0] == "none" ? null
    : designArg[0].StartsWith("builtin:", StringComparison.Ordinal) ? ContinentDesign.Builtin(designArg[0]["builtin:".Length..])
    : ContinentDesign.Parse(File.ReadAllText(designArg[0]));
string output = options.TryGetValue("out", out List<string>? outPath) ? outPath[0] : "maplab-out";
int pixels = options.TryGetValue("pixels", out List<string>? pixelArg) ? int.Parse(pixelArg[0], CultureInfo.InvariantCulture) : 1024;
List<(double X, double Z)> views = options.TryGetValue("view", out List<string>? viewArgs)
    ? [.. viewArgs.Select(ParsePoint)]
    : [(0, 0)];
Directory.CreateDirectory(output);

TerrainConfiguration configuration = new(seed, size);
Stopwatch clock = Stopwatch.StartNew();
WorldMap map = new(configuration, MapSimulation.Run(configuration, recipe, design));
double generationSeconds = clock.Elapsed.TotalSeconds;
double reach = Math.Min(options.TryGetValue("reach-km", out List<string>? reachArg)
    ? double.Parse(reachArg[0], CultureInfo.InvariantCulture) * 1000 : 150_000, map.Radius * 2);

StringBuilder stats = new();
void Line(string text)
{
    Console.WriteLine(text);
    stats.AppendLine(text);
}

Line($"seed={seed} size={size}m generator=v{configuration.GeneratorVersion} fingerprint=0x{map.Fingerprint:x16} recipe={(recipePath is null ? "default" : recipePath[0])} design={design?.Name ?? "none"}");
Line(Invariant($"generated in {generationSeconds:F1}s; continental={map.Scale.Continental} peak={recipe.Simulation.PeakElevation ?? map.Scale.PeakElevation:F0}m ceiling={map.Scale.MaximumElevation:F0}m"));

// The whole map, sampled at the image's resolution.
double cell = map.Radius * 2 / pixels;
double[,] height = new double[pixels, pixels];
MapSample[,] samples = new MapSample[pixels, pixels];
List<double> land = [];
for (int py = 0; py < pixels; py++)
for (int px = 0; px < pixels; px++)
{
    double x = -map.Radius + ((px + 0.5) * cell), z = -map.Radius + ((py + 0.5) * cell);
    MapSample sample = map.Sample(x, z);
    samples[px, py] = sample;
    height[px, py] = Math.Max(sample.Elevation, GenerationConstants.WaterLevel);
    if (sample.Elevation >= GenerationConstants.WaterLevel) land.Add(sample.Elevation);
}

land.Sort();
double Percentile(double p) => land.Count == 0 ? 0 : land[(int)Math.Clamp(p * (land.Count - 1), 0, land.Count - 1)];
Line(Invariant($"land={100.0 * land.Count / (pixels * pixels):F1}% elevation p50={Percentile(0.5):F0}m p90={Percentile(0.9):F0}m p99={Percentile(0.99):F0}m max={Percentile(1):F0}m"));

DrawRelief().Save(Path.Combine(output, "relief.png"));
Terrain.Report(Line, height, cell, map.Radius, GenerationConstants.WaterLevel, design);
if (design is not null)
{
    Image overlay = DrawRelief();
    DrawDesign(overlay, design);
    overlay.Save(Path.Combine(output, "design.png"));
    ReportDesign(design);
}
DrawHeight().Save(Path.Combine(output, "height.png"));

for (int v = 0; v < views.Count; v++)
{
    (double vx, double vz) = views[v];
    Skyline skyline = new(map, vx, vz, reach);
    Line(Invariant($"view {v} at {vx:F0},{vz:F0}: ground {skyline.Eye - 1.7:F0}m"));
    foreach (double km in new[] { 10.0, 30.0, 60.0 })
    {
        (double h, double hx, double hz) = Highest(vx, vz, km * 1000);
        double distance = Math.Sqrt(((hx - vx) * (hx - vx)) + ((hz - vz) * (hz - vz)));
        double angle = Math.Atan2(h - skyline.Eye, Math.Max(distance, 1)) * 180 / Math.PI;
        Line(Invariant($"  highest within {km:F0} km: {h:F0}m ({h - skyline.Eye:+0;-0}m) at {distance / 1000:F1} km bearing {Bearing(hx - vx, hz - vz):F0}, {angle:F2} deg up"));
    }

    string[] names = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
    StringBuilder sectors = new("  skyline by sector (deg up @ km):");
    for (int s = 0; s < 8; s++)
    {
        int from = ((s * 45) - 22) * Skyline.PerDegree, to = ((s * 45) + 23) * Skyline.PerDegree;
        int best = Enumerable.Range(from, to - from).Select(b => ((b % skyline.Degrees.Length) + skyline.Degrees.Length) % skyline.Degrees.Length)
            .MaxBy(b => skyline.Degrees[b]);
        sectors.Append(Invariant($" {names[s]} {skyline.Degrees[best]:F2}@{skyline.Distance[best] / 1000:F0}"));
    }

    Line(sectors.ToString());
    double[] sorted = [.. skyline.Degrees.Order()];
    Line(Invariant($"  skyline median {sorted[sorted.Length / 2]:F2} deg, 90th {sorted[(int)(sorted.Length * 0.9)]:F2} deg, max {sorted[^1]:F2} deg; at 70 deg vertical FOV and 720 px, 1 deg = {720 / 70.0:F1} px"));
    double top = Math.Max(2, Math.Ceiling(sorted[^1] + 0.5));
    skyline.Draw(40, top, reach).Save(Path.Combine(output, $"skyline-{v}.png"));
}

File.WriteAllText(Path.Combine(output, "stats.txt"), stats.ToString());
File.WriteAllText(Path.Combine(output, "recipe.json"), JsonSerializer.Serialize(recipe, json) + "\n");
Console.WriteLine($"wrote {output}/relief.png, height.png, skyline-*.png, stats.txt, recipe.json");
return 0;

// The relief: biome colours (rock blended in), sea and rivers, under a hillshade lit from the north-west,
// with each viewpoint marked.
Image DrawRelief()
{
    // Shading exaggerates slopes more on a coarser image, so a continent's relief still reads.
    double exaggeration = Math.Max(4, cell / 25);
    Vector3d light = Normalise(new(-1, 1.4, -1));
    Image image = new(pixels, pixels);
    for (int py = 0; py < pixels; py++)
    for (int px = 0; px < pixels; px++)
    {
        MapSample sample = samples[px, py];
        Color colour = sample.Elevation < GenerationConstants.WaterLevel ? MapPalette.For(MapBiome.Sea)
            : sample.InRiver ? MapPalette.River
            : MapPalette.Tint(sample);
        double dx = (height[Math.Min(px + 1, pixels - 1), py] - height[Math.Max(px - 1, 0), py]) / (2 * cell);
        double dz = (height[px, Math.Min(py + 1, pixels - 1)] - height[px, Math.Max(py - 1, 0)]) / (2 * cell);
        Vector3d normal = Normalise(new(-dx * exaggeration, 1, -dz * exaggeration));
        double shade = 0.45 + (0.65 * Math.Max(0, Dot(normal, light)));
        image.Set(px, py, (colour.R * shade, colour.G * shade, colour.B * shade));
    }

    foreach ((double vx, double vz) in views) Mark(image, vx, vz);
    return image;
}

// The design over the relief: land outlines (forbidden land in red), belt crest lines, passes and sites.
void DrawDesign(Image image, ContinentDesign drawn)
{
    foreach (DesignLand area in drawn.Land)
        for (int i = 0, j = area.Points.Count - 1; i < area.Points.Count; j = i++)
            Segment(image, area.Points[j], area.Points[i], area.Forbidden ? (0.9, 0.15, 0.1) : (1, 1, 1));
    foreach (DesignBelt belt in drawn.Belts)
        for (int i = 1; i < belt.Points.Count; i++) Segment(image, belt.Points[i - 1], belt.Points[i], (0.35, 0.1, 0.45));
    foreach (DesignPass pass in drawn.Passes) Ring(image, pass.At, pass.Radius, (1, 0.85, 0.1));
    foreach (DesignSite site in drawn.Sites) Ring(image, site.At, 0.008, (1, 0.3, 0.9));
}

void Segment(Image image, double[] a, double[] b, (double, double, double) colour)
{
    int steps = (int)(Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2)) * pixels) + 1;
    for (int s = 0; s <= steps; s++)
    {
        double t = (double)s / steps;
        image.Set(Pixel(a[0] + ((b[0] - a[0]) * t)), Pixel(a[1] + ((b[1] - a[1]) * t)), colour);
    }
}

void Ring(Image image, double[] at, double radius, (double, double, double) colour)
{
    for (int k = 0; k < 64; k++)
    {
        double angle = k * Math.Tau / 64;
        image.Set(Pixel(at[0] + (radius * Math.Cos(angle))), Pixel(at[1] + (radius * Math.Sin(angle))), colour);
    }
}

int Pixel(double unit) => (int)Math.Round((unit + 1) / 2 * pixels);

// What the design asked for beside what generation made: each belt's crest, each pass's saddle, each site's ground.
void ReportDesign(ContinentDesign drawn)
{
    double World(double unit) => unit * map.Radius;
    foreach (DesignBelt belt in drawn.Belts)
    {
        double asked = belt.Points.Max(p => p[2]);
        double made = 0;
        for (int i = 1; i < belt.Points.Count; i++)
            for (double t = 0; t <= 1; t += 0.01)
            {
                double[] a = belt.Points[i - 1], b = belt.Points[i];
                made = Math.Max(made, map.Sample(World(a[0] + ((b[0] - a[0]) * t)), World(a[1] + ((b[1] - a[1]) * t))).Elevation);
            }

        Line(Invariant($"  belt {belt.Name}: asked up to {asked:F0}m, highest along its line {made:F0}m"));
    }

    foreach (DesignArea area in drawn.Areas)
    {
        List<double> core = [];
        for (double dz = -area.Radius; dz <= area.Radius; dz += area.Radius / 20)
            for (double dx = -area.Radius; dx <= area.Radius; dx += area.Radius / 20)
                if (area.Weight(area.At[0] + dx, area.At[1] + dz) >= 0.6)
                {
                    double ground = map.Sample(World(area.At[0] + dx), World(area.At[1] + dz)).Elevation;
                    if (ground > GenerationConstants.WaterLevel) core.Add(ground);
                }

        core.Sort();
        string asked = area.Height is double height ? Invariant($"asked {height:F0}m (rugged {area.Ruggedness:F1})") : Invariant($"lift {area.Lift:+0.00;-0.00}");
        Line(core.Count == 0 ? $"  area {area.Name}: {asked}, no land in its core"
            : Invariant($"  area {area.Name}: {asked}; its core median {core[core.Count / 2]:F0}m, 90th {core[(int)(core.Count * 0.9)]:F0}m, highest {core[^1]:F0}m"));
    }

    foreach (DesignPass pass in drawn.Passes)
        Line(Invariant($"  pass {pass.Name}: ground {map.Sample(World(pass.At[0]), World(pass.At[1])).Elevation:F0}m"));
    foreach (DesignSite site in drawn.Sites)
        Line(Invariant($"  site {site.Name} ({site.Kind}): ground {map.Sample(World(site.At[0]), World(site.At[1])).Elevation:F0}m at {World(site.At[0]):F0},{World(site.At[1]):F0}"));
}

// Elevation as grey, from the sea to the highest point.
Image DrawHeight()
{
    double top = Math.Max(Percentile(1), GenerationConstants.WaterLevel + 1);
    Image image = new(pixels, pixels);
    for (int py = 0; py < pixels; py++)
    for (int px = 0; px < pixels; px++)
    {
        double t = (height[px, py] - GenerationConstants.WaterLevel) / (top - GenerationConstants.WaterLevel);
        image.Set(px, py, (t, t, t));
    }

    foreach ((double vx, double vz) in views) Mark(image, vx, vz);
    return image;
}

void Mark(Image image, double x, double z)
{
    int cx = (int)((x + map.Radius) / cell), cz = (int)((z + map.Radius) / cell);
    for (int d = -6; d <= 6; d++)
    {
        image.Set(cx + d, cz, (1, 0.1, 0.1));
        image.Set(cx, cz + d, (1, 0.1, 0.1));
    }
}

(double Height, double X, double Z) Highest(double x, double z, double radius)
{
    double step = Math.Max(cell, radius / 400);
    (double Height, double X, double Z) best = (double.MinValue, x, z);
    for (double pz = z - radius; pz <= z + radius; pz += step)
    for (double px = x - radius; px <= x + radius; px += step)
    {
        if (((px - x) * (px - x)) + ((pz - z) * (pz - z)) > radius * radius) continue;
        if (Math.Abs(px) > map.Radius || Math.Abs(pz) > map.Radius) continue;
        double h = map.Sample(px, pz).Elevation;
        if (h > best.Height) best = (h, px, pz);
    }

    return best;
}

static double Bearing(double dx, double dz) => ((Math.Atan2(dx, -dz) * 180 / Math.PI) + 360) % 360;

static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

static Vector3d Normalise(Vector3d v)
{
    double length = Math.Sqrt(Dot(v, v));
    return new(v.X / length, v.Y / length, v.Z / length);
}

static double Dot(Vector3d a, Vector3d b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

static ulong ParseSeed(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
    ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
    : ulong.Parse(text, CultureInfo.InvariantCulture);

static (double X, double Z) ParsePoint(string text)
{
    string[] parts = text.Split(',');
    return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
}

static Dictionary<string, List<string>> Parse(string[] args)
{
    Dictionary<string, List<string>> parsed = [];
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
        string key = args[i][2..];
        if (!parsed.TryGetValue(key, out List<string>? values)) parsed[key] = values = [];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) values.Add(args[++i]);
    }

    return parsed;
}

internal readonly record struct Vector3d(double X, double Y, double Z);
