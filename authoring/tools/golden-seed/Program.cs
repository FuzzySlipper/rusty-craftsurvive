using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Towns;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using GoldenSeed;
using Rusty.Engine;
using Rusty.Engine.Testing;

// The golden-seed exporter (#11125). Run from the repository root:
//
//   dotnet run --project authoring/tools/golden-seed -c Release [-- export] [--config FILE] [--out DIR] [--cache DIR] [--workers N] [--limit N]
//       [--carry-from IDENTITY [--verify-every N]]
//   dotnet run --project authoring/tools/golden-seed -c Release -- cave poi:CX:CZ [--out DIR] [--cache DIR]
//   dotnet run --project authoring/tools/golden-seed -c Release -- pinch poi:CX:CZ [--summary] [--out DIR]
//
// export generates the world golden-seed.json names with the product's generator and the Engine's keyed draws (this is an
// Engine tool host, so no game runs), and writes every layer to the export folder (default authoring/assets/golden-seed/):
// the continent raster, the rivers, the walked drainage, the towns, lakes and sites, every place, cave way and crossing of
// the continent a region tile at a time, and the walking-detail windows about the towns. A tile is cached under the
// generator's identity (authoring/assets/golden-seed-cache/<identity>/), so a rerun with unchanged generator code reuses it.
// cave generates the cave under one cave mouth of the current export and writes its simplified view for the CMS.
// pinch is a diagnostic: which stage of the generator closed a cave's route (see Pinch.cs).

const int RasterRows = 256;
const double SeaMarginMetres = 10;
const double SeaProbeMetres = 250;
// --carry-from: a tile whose ground, a kilometre round, stands this far above the water on the continent map is copied from
// an earlier identity's cache of the same generator version rather than made again (a version bump redraws every place);
// every --verify-every'th carried tile is made again and must match.
const double DryMarginMetres = 50;
const double DryReachMetres = 1000;
string[] rasterLayers = ["elevation", "temperature", "moisture", "rock", "biome", "water"];
string[] rasterKinds = ["f32", "f32", "f32", "f32", "u8", "u8"];
JsonSerializerOptions indented = new() { WriteIndented = true };

string repo = Repository();
Dictionary<string, string> options = Options(args.Skip(args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? 1 : 0).ToArray());
string command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "export";
string output = Path.GetFullPath(options.GetValueOrDefault("out", Path.Combine(repo, "authoring/assets/golden-seed")));
string cacheRoot = Path.GetFullPath(options.GetValueOrDefault("cache", Path.Combine(repo, "authoring/assets/golden-seed-cache")));

if (command == "cave")
{
    string id = args.Length > 1 ? args[1] : throw new ArgumentException("cave needs a mouth's id, poi:CX:CZ");
    return CaveCommand(id);
}

if (command == "pinch")
{
    // Diagnostic: where a cave's route goes out of reach, stage by stage (see Pinch).
    string id = args.Length > 1 ? args[1] : throw new ArgumentException("pinch needs a mouth's id, poi:CX:CZ");
    (ulong pinchSeed, CaveSetting pinchSetting, CavePlacement pinchPlacement) = Locate(id);
    Pinch.Run(id, pinchSeed, pinchSetting, pinchPlacement, options.ContainsKey("summary"), Console.WriteLine);
    return 0;
}

if (command == "check")
{
    // The cave view's reach on synthetic caves (#11125 R11125-2): two round chambers joined by a throat, which a body passes
    // only when the throat is wider than a crawl. Run by scripts/check-product.sh --managed.
    return CaveView.CheckThroats(Console.WriteLine) ? 0 : 1;
}

if (command == "column")
{
    // What each layer of the generator says at a point: the continent map, the region tiles' blend, the walking column.
    double columnX = double.Parse(args[1], CultureInfo.InvariantCulture), columnZ = double.Parse(args[2], CultureInfo.InvariantCulture);
    JsonObject columnConfig = JsonNode.Parse(File.ReadAllText(options.GetValueOrDefault("config", Path.Combine(repo, "authoring/tools/golden-seed/golden-seed.json"))))!.AsObject();
    using EngineTestHost columnHost = EngineTestHost.Create(new() { PersistenceRoot = Path.Combine(Path.GetTempPath(), "golden-seed-" + Guid.NewGuid().ToString("N")) });
    columnHost.Call(engine =>
    {
        TerrainConfiguration columnConfiguration = new(columnConfig["seed"]!.GetValue<ulong>(), columnConfig["size"]!.GetValue<int>());
        WorldMap columnMap = WorldMapGenerator.Generate(columnConfiguration);
        TerrainRecipe columnRecipe = columnConfiguration.CreateRecipe(new EngineTerrainDraws(engine.Random), columnMap);
        Console.WriteLine($"map sample {columnMap.Sample(columnX, columnZ)}");
        Console.WriteLine($"map broad {columnMap.Broad(columnX, columnZ)}");
        if (columnRecipe.Regions is MapRegions regions)
        {
            (int tx, int tz) = MapRegions.TileAt(columnX, columnZ);
            Console.WriteLine($"region tile ({tx},{tz}) blended {regions.Blended(columnX, columnZ)}");
            Console.WriteLine($"region tile own broad {regions.Tile((tx, tz)).Map.Broad(columnX - (tx * MapRegions.TileSpacing), columnZ - (tz * MapRegions.TileSpacing))}");
        }

        Console.WriteLine($"geography {columnRecipe.Geography(columnX, columnZ)}");
        Console.WriteLine($"column {columnRecipe.ColumnAt((long)Math.Floor(columnX), (long)Math.Floor(columnZ))}");
    });
    return 0;
}

if (command == "tile")
{
    // One region tile, for a look at it alone: its page on stdout.
    int tileX = int.Parse(args[1], CultureInfo.InvariantCulture), tileZ = int.Parse(args[2], CultureInfo.InvariantCulture);
    JsonObject tileConfig = JsonNode.Parse(File.ReadAllText(options.GetValueOrDefault("config", Path.Combine(repo, "authoring/tools/golden-seed/golden-seed.json"))))!.AsObject();
    using EngineTestHost tileHost = EngineTestHost.Create(new() { PersistenceRoot = Path.Combine(Path.GetTempPath(), "golden-seed-" + Guid.NewGuid().ToString("N")) });
    tileHost.Call(engine =>
    {
        TerrainConfiguration tileConfiguration = new(tileConfig["seed"]!.GetValue<ulong>(), tileConfig["size"]!.GetValue<int>());
        WorldMap tileMap = WorldMapGenerator.Generate(tileConfiguration);
        WorldExport tileExport = new(tileMap, () => [], new EngineTerrainDraws(engine.Random));
        Console.WriteLine(tileExport.Tile(tileX, tileZ, Math.Max(1, Environment.ProcessorCount - 2)));
    });
    return 0;
}

if (command != "export")
{
    Console.Error.WriteLine("usage: export [--config FILE] [--out DIR] [--cache DIR] [--workers N] [--limit N] | cave poi:CX:CZ | pinch poi:CX:CZ [--summary] | tile X Z | check");
    return 2;
}

JsonObject config = JsonNode.Parse(File.ReadAllText(options.GetValueOrDefault("config", Path.Combine(repo, "authoring/tools/golden-seed/golden-seed.json"))))!.AsObject();
ulong seed = config["seed"]!.GetValue<ulong>();
int size = config["size"]!.GetValue<int>();
int pixels = config["pixels"]?.GetValue<int>() ?? 1024;
int workers = int.Parse(options.GetValueOrDefault("workers", Math.Max(1, Environment.ProcessorCount - 2).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
int limit = int.Parse(options.GetValueOrDefault("limit", int.MaxValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
string pair = EnginePair(repo);

string scratch = Path.Combine(Path.GetTempPath(), "golden-seed-" + Guid.NewGuid().ToString("N"));
using EngineTestHost host = EngineTestHost.Create(new() { PersistenceRoot = scratch });
int exit = 0;
host.Call(engine =>
{
    Stopwatch total = Stopwatch.StartNew(), clock = Stopwatch.StartNew();
    Dictionary<string, double> timings = [];
    double Lap(string name)
    {
        double seconds = clock.Elapsed.TotalSeconds;
        timings[name] = Math.Round(seconds, 1);
        clock.Restart();
        return seconds;
    }

    TerrainConfiguration configuration = new(seed, size);
    WorldMap map = WorldMapGenerator.Generate(configuration);
    MapRegions? regions = map.Scale.Continental ? MapRegions.For(map) : null;
    _ = regions?.Drainage;
    Log($"generated seed {seed} at {size} m in {Lap("generate"):F1} s (fingerprint {map.Fingerprint:x16})");

    EngineTerrainDraws draws = new(engine.Random);
    TerrainRecipe recipe = configuration.CreateRecipe(draws, map);
    ulong terrainFingerprint = TerrainGenerationFingerprint.Compute(configuration.CreateRecipe(new EngineTerrainDraws(engine.Random), map), TerrainGenerationFingerprint.Startup);
    string identity = Hash($"{seed}|{size}|{map.Fingerprint:x16}|{terrainFingerprint:x16}|{GoldenSeedSource.Stamp:x16}|{pair}");
    string tileCache = Path.Combine(cacheRoot, identity, "tiles");
    Directory.CreateDirectory(tileCache);
    // What made the cache: the generator version decides whether an earlier identity's tiles can be carried.
    File.WriteAllText(Path.Combine(cacheRoot, identity, "identity.json"), new JsonObject
    {
        ["generatorVersion"] = configuration.GeneratorVersion, ["map"] = map.Fingerprint.ToString("x16", CultureInfo.InvariantCulture),
        ["terrain"] = terrainFingerprint.ToString("x16", CultureInfo.InvariantCulture), ["sources"] = GoldenSeedSource.Stamp.ToString("x16", CultureInfo.InvariantCulture), ["enginePair"] = pair,
    }.ToJsonString());
    Log($"generator identity {identity} (terrain {terrainFingerprint:x16}, sources {GoldenSeedSource.Stamp:x16}, pair {pair})");

    // The towns as the game seats them: on the recipe's continuous ground (Towns.cs, TownGroundAt).
    TownContent townContent = TownContent.Read(TownContentFiles(repo));
    ExportedTown[] towns = [.. Settlements.For(map)
        .Select(settlement => TownSite.For(settlement, townContent, (x, z) => recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z)) + FarGround.GroundTopFace))
        .OfType<TownSite>().Select(WorldExportDebugModule.Exported)];
    WorldExport export = new(map, () => towns, draws);

    string work = output + ".partial";
    if (Directory.Exists(work)) Directory.Delete(work, true);
    Directory.CreateDirectory(Path.Combine(work, "continent"));
    Directory.CreateDirectory(Path.Combine(work, "windows"));
    Directory.CreateDirectory(Path.Combine(work, "tiles"));
    JsonObject header = JsonNode.Parse(export.Header())!.AsObject();
    JsonObject places = JsonNode.Parse(export.Places())!.AsObject();
    File.WriteAllText(Path.Combine(work, "places.json"), places.ToJsonString());

    MemoryStream[] layers = [.. rasterLayers.Select(_ => new MemoryStream())];
    for (int first = 0; first < pixels; first += RasterRows)
    {
        using JsonDocument page = JsonDocument.Parse(export.Raster(pixels, first, Math.Min(RasterRows, pixels - first)));
        for (int k = 0; k < rasterLayers.Length; k++) layers[k].Write(page.RootElement.GetProperty(rasterLayers[k]).GetBytesFromBase64());
    }

    for (int k = 0; k < rasterLayers.Length; k++) File.WriteAllBytes(Path.Combine(work, "continent", $"{rasterLayers[k]}.{rasterKinds[k]}"), layers[k].ToArray());
    Lap("raster");

    File.WriteAllText(Path.Combine(work, "map-rivers.json"), Paged(export.MapRivers));
    File.WriteAllText(Path.Combine(work, "drainage.json"), Paged(export.Drainage));
    Lap("rivers");

    // Every region tile over the world, row by row; a tile with no ground above the sea has no places.
    long radius = size / 2;
    (int lowX, int lowZ) = MapRegions.TileAt(-radius, -radius);
    (int highX, int highZ) = MapRegions.TileAt(radius, radius);
    JsonArray index = [];
    JsonArray caves = [];
    int built = 0, reused = 0, sea = 0, failed = 0, carried = 0;
    string? carryFrom = options.GetValueOrDefault("carry-from");
    if (carryFrom is not null)
    {
        // A version bump redraws every keyed draw (TerrainGeneratorContract.MixVersion): every place, cave and crossing moves,
        // so no tile of another version can be carried, however dry.
        string earlierIdentity = Path.Combine(cacheRoot, carryFrom, "identity.json");
        uint? earlierVersion = File.Exists(earlierIdentity) ? JsonNode.Parse(File.ReadAllText(earlierIdentity))!["generatorVersion"]!.GetValue<uint>() : null;
        if (earlierVersion != configuration.GeneratorVersion)
        {
            Log($"--carry-from {carryFrom}: its generator version is {earlierVersion?.ToString(CultureInfo.InvariantCulture) ?? "unrecorded"}, this one {configuration.GeneratorVersion}; a version bump redraws every place, so nothing is carried");
            carryFrom = null;
        }
    }

    int verifyEvery = int.Parse(options.GetValueOrDefault("verify-every", "25"), CultureInfo.InvariantCulture);
    List<(int X, int Z, string Json)> carriedTiles = [];
    Stopwatch tiling = Stopwatch.StartNew();
    for (int tz = lowZ; tz <= highZ; tz++)
    for (int tx = lowX; tx <= highX; tx++)
    {
        string name = $"{tx}_{tz}";
        if (AllSea(map, tx, tz, radius))
        {
            sea++;
            index.Add(new JsonObject { ["tile"] = new JsonArray(tx, tz), ["sea"] = true });
            continue;
        }

        string cached = Path.Combine(tileCache, name + ".json.gz");
        Stopwatch one = Stopwatch.StartNew();
        string json;
        string? earlier = carryFrom is null ? null : Path.Combine(cacheRoot, carryFrom, "tiles", name + ".json.gz");
        // Where this tile's page is copied from into the export: the cache, or for a carried tile the earlier cache.
        string source = cached;
        if (File.Exists(cached))
        {
            json = ReadGzip(cached);
            reused++;
        }
        else if (earlier is not null && File.Exists(earlier) && Dry(map, tx, tz))
        {
            // A change that reaches only ground near the water leaves a dry tile as it was (checked below). It enters this
            // identity's cache only once the check holds (R11148-1): a carry that fails leaves the cache as it found it, so a
            // retry makes the tile afresh rather than reusing it.
            json = ReadGzip(earlier);
            source = earlier;
            carried++;
            carriedTiles.Add((tx, tz, json));
        }
        else
        {
            if (built >= limit) continue;
            try
            {
                json = export.Tile(tx, tz, workers);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A tile the generator cannot make is a finding, not the end of the export: the atlas marks it.
                failed++;
                index.Add(new JsonObject { ["tile"] = new JsonArray(tx, tz), ["failed"] = failure.Message });
                Log($"tile {name} FAILED: {failure.Message}");
                continue;
            }

            WriteGzip(cached, json);
            built++;
        }

        File.Copy(source, Path.Combine(work, "tiles", name + ".json.gz"));
        JsonObject tile = JsonNode.Parse(json)!.AsObject();
        JsonObject kinds = [];
        foreach (JsonNode? place in tile["places"]!.AsArray())
        {
            string kind = ((PoiKind)place![2]!.GetValue<long>()).ToString();
            kinds[kind] = (kinds[kind]?.GetValue<int>() ?? 0) + 1;
        }

        foreach (JsonNode? cave in tile["caves"]!.AsArray())
        {
            JsonObject row = cave!.AsObject();
            caves.Add(new JsonObject
            {
                ["id"] = row["id"]!.GetValue<string>(), ["x"] = row["x"]!.GetValue<long>(), ["z"] = row["z"]!.GetValue<long>(),
                ["ground"] = row["ground"]!.GetValue<long>(), ["seed"] = row["seed"]!.GetValue<string>(), ["setting"] = row["setting"]!.DeepClone(),
                ["tile"] = new JsonArray(tx, tz),
            });
        }

        index.Add(new JsonObject
        {
            ["tile"] = new JsonArray(tx, tz), ["places"] = kinds, ["caves"] = tile["caves"]!.AsArray().Count, ["crossings"] = tile["crossings"]!.AsArray().Count,
        });
        Log(FormattableString.Invariant($"tile {name}: {tile["places"]!.AsArray().Count} places, {tile["caves"]!.AsArray().Count} caves, {tile["crossings"]!.AsArray().Count} crossings in {one.Elapsed.TotalSeconds:F1} s ({built} built, {reused} cached, {carried} carried, {tiling.Elapsed.TotalMinutes:F1} min)"));
    }

    // The carried tiles were made by the earlier generator: a spread of them is made again by this one and must match.
    int verified = 0;
    for (int k = 0; k < carriedTiles.Count; k += Math.Max(1, verifyEvery))
    {
        (int vx, int vz, string earlierJson) = carriedTiles[k];
        if (export.Tile(vx, vz, workers) != earlierJson)
        {
            Log($"tile {vx}_{vz} carried from {carryFrom} differs when made again: the change reaches dry ground, so carrying is wrong; nothing carried entered the cache or the export; rerun without --carry-from");
            Directory.Delete(work, true);
            exit = 1;
            return;
        }

        verified++;
        Log($"carried tile {vx}_{vz} made again: identical ({verified} verified)");
    }

    // The check held: the carried tiles join this identity's cache.
    foreach ((int cx2, int cz2, string carriedJson) in carriedTiles)
    {
        WriteGzip(Path.Combine(tileCache, $"{cx2}_{cz2}.json.gz"), carriedJson);
    }

    File.WriteAllText(Path.Combine(work, "tiles", "index.json"), index.ToJsonString());
    File.WriteAllText(Path.Combine(work, "caves.json"), new JsonObject { ["caves"] = caves }.ToJsonString());
    Lap("tiles");

    // The walking-detail windows about each town: the walking ground's height and biome.
    JsonObject spec = config["windows"]?.AsObject() ?? [];
    long half = spec["half"]?.GetValue<long>() ?? 4096, step = spec["step"]?.GetValue<long>() ?? 16;
    List<(string Name, double X, double Z, string? Town)> wanted = spec["towns"]?.GetValue<bool>() ?? true
        ? [.. towns.Select(town => (town.Name, town.X, town.Z, (string?)town.Key))] : [];
    foreach (JsonNode? extra in spec["extra"]?.AsArray() ?? [])
        wanted.Add((extra!["name"]!.GetValue<string>(), extra["x"]!.GetValue<double>(), extra["z"]!.GetValue<double>(), null));
    JsonArray windows = [];
    foreach ((string windowName, double x, double z, string? town) in wanted)
    {
        string id = Slug(windowName);
        JsonObject detail = JsonNode.Parse(export.Window((long)Math.Round(x), (long)Math.Round(z), half, step))!.AsObject();
        File.WriteAllBytes(Path.Combine(work, "windows", id + ".height.i16"), Convert.FromBase64String(detail["height"]!.GetValue<string>()));
        File.WriteAllBytes(Path.Combine(work, "windows", id + ".biome.u8"), Convert.FromBase64String(detail["biome"]!.GetValue<string>()));
        detail.Remove("height");
        detail.Remove("biome");
        detail["id"] = id;
        detail["name"] = windowName;
        detail["town"] = town;
        File.WriteAllText(Path.Combine(work, "windows", id + ".json"), detail.ToJsonString());
        windows.Add(new JsonObject
        {
            ["id"] = id, ["name"] = windowName, ["town"] = town, ["x"] = detail["x"]!.DeepClone(), ["z"] = detail["z"]!.DeepClone(),
            ["half"] = half, ["step"] = step, ["side"] = detail["side"]!.DeepClone(), ["places"] = detail["places"]!.AsArray().Count,
            ["crossings"] = detail["crossings"]!.AsArray().Count, ["caves"] = detail["places"]!.AsArray().Count(p => p!["cave"] is not null),
        });
    }

    Lap("windows");
    JsonObject manifest = new()
    {
        ["format"] = 2,
        ["world"] = header,
        ["config"] = config.DeepClone(),
        ["exportedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
        ["source"] = new JsonObject { ["commit"] = Git(repo, "rev-parse HEAD"), ["dirty"] = Git(repo, "status --porcelain -- src content/game").Length > 0, ["enginePair"] = pair },
        ["identity"] = new JsonObject
        {
            ["key"] = identity, ["map"] = map.Fingerprint.ToString("x16", CultureInfo.InvariantCulture),
            ["terrain"] = terrainFingerprint.ToString("x16", CultureInfo.InvariantCulture), ["sources"] = GoldenSeedSource.Stamp.ToString("x16", CultureInfo.InvariantCulture),
        },
        ["continent"] = new JsonObject
        {
            ["pixels"] = pixels, ["metresPerPixel"] = header["radius"]!.GetValue<double>() * 2 / pixels,
            ["layers"] = new JsonObject(rasterLayers.Select((name, k) => KeyValuePair.Create(name, (JsonNode?)rasterKinds[k]))),
            ["origin"] = "north-west corner (-radius, -radius); rows run +Z (south), columns +X (east); little-endian",
        },
        ["tiles"] = new JsonObject
        {
            ["spacing"] = MapRegions.TileSpacing, ["land"] = index.Count - sea, ["sea"] = sea, ["built"] = built, ["cached"] = reused,
            ["failed"] = failed, ["carried"] = carried, ["carriedFrom"] = carryFrom, ["carriedVerified"] = verified,
            ["caves"] = caves.Count, ["complete"] = built + reused + carried + sea == (highX - lowX + 1) * (highZ - lowZ + 1),
        },
        ["windows"] = windows,
        ["timings"] = new JsonObject(timings.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
    };
    File.WriteAllText(Path.Combine(work, "manifest.json"), manifest.ToJsonString(indented) + "\n");

    string previous = output + ".previous";
    if (Directory.Exists(previous)) Directory.Delete(previous, true);
    if (Directory.Exists(output)) Directory.Move(output, previous);
    Directory.Move(work, output);
    if (Directory.Exists(previous)) Directory.Delete(previous, true);
    Log(FormattableString.Invariant($"exported seed {seed} at {size} m to {output} in {total.Elapsed.TotalMinutes:F1} min: {built} tiles built, {reused} cached, {carried} carried ({verified} verified), {sea} sea, {failed} failed, {caves.Count} caves"));
});
if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
return exit;

int CaveCommand(string id)
{
    JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!.AsObject();
    string identity = manifest["identity"]?["key"]?.GetValue<string>() ?? throw new InvalidOperationException("The export has no generator identity; rerun the export.");
    // A cave view is the generator's cave reduced by this tool: cached under both, so a change to either makes it afresh.
    string directory = Path.Combine(cacheRoot, identity, "caves-" + GoldenSeedSource.ToolStamp.ToString("x16", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, id.Replace(':', '_') + ".json.gz");
    if (!File.Exists(file))
    {
        (ulong seed, CaveSetting setting, CavePlacement placement) = Locate(id);
        WriteGzip(file, CaveView.Make(id, seed, setting, placement));
    }

    Console.WriteLine(file);
    return 0;
}

// A cave in the export: its seed and setting as the export recorded them, and the mouth as its tile recorded it, placed as
// the game places its cave.
(ulong Seed, CaveSetting Setting, CavePlacement Placement) Locate(string id)
{
    JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")))!.AsObject();
    JsonNode row = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "caves.json")))!["caves"]!.AsArray().FirstOrDefault(cave => cave!["id"]!.GetValue<string>() == id)
        ?? throw new ArgumentException($"The export has no cave under {id}.");
    JsonArray tileAt = row["tile"]!.AsArray();
    JsonArray place = JsonNode.Parse(ReadGzip(Path.Combine(output, "tiles", $"{tileAt[0]}_{tileAt[1]}.json.gz")))!["places"]!.AsArray()
        .Select(p => p!.AsArray()).First(p => PoiSite.IdFor(p[0]!.GetValue<long>(), p[1]!.GetValue<long>()) == id);
    long[] v = [.. place.Select(p => p!.GetValue<long>())];
    PoiSite site = new(v[0], v[1], (PoiKind)v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
    long radius = manifest["world"]!["size"]!.GetValue<long>() / 2;
    CavePlacement placement = CavePlacement.Place(site, radius) ?? throw new InvalidOperationException($"{id} has no way underground.");
    JsonArray setting = row["setting"]!.AsArray();
    return (ulong.Parse(row["seed"]!.GetValue<string>(), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        new(setting[0]!.GetValue<double>(), setting[1]!.GetValue<double>(), setting[2]!.GetValue<float>()), placement);
}

static bool AllSea(WorldMap map, int tileX, int tileZ, long radius)
{
    double half = MapRegions.TileSpacing / 2;
    for (double z = (tileZ * MapRegions.TileSpacing) - half; z < (tileZ * MapRegions.TileSpacing) + half; z += SeaProbeMetres)
    for (double x = (tileX * MapRegions.TileSpacing) - half; x < (tileX * MapRegions.TileSpacing) + half; x += SeaProbeMetres)
    {
        if (Math.Abs(x) > radius || Math.Abs(z) > radius) continue;
        if (map.Sample(x, z).Elevation >= GenerationConstants.WaterLevel - SeaMarginMetres) return false;
    }

    return true;
}

static bool Dry(WorldMap map, int tileX, int tileZ)
{
    double half = (MapRegions.TileSpacing / 2) + DryReachMetres;
    for (double z = (tileZ * MapRegions.TileSpacing) - half; z <= (tileZ * MapRegions.TileSpacing) + half; z += SeaProbeMetres)
    for (double x = (tileX * MapRegions.TileSpacing) - half; x <= (tileX * MapRegions.TileSpacing) + half; x += SeaProbeMetres)
    {
        if (!map.Contains(x, z) || map.Sample(x, z).Elevation < GenerationConstants.WaterLevel + DryMarginMetres) return false;
    }

    return true;
}

static string Paged(Func<int, string> page)
{
    JsonArray all = [];
    int pages = 1;
    for (int k = 0; k < pages; k++)
    {
        JsonObject one = JsonNode.Parse(page(k))!.AsObject();
        pages = one["pages"]!.GetValue<int>();
        foreach (JsonNode? reach in one["reaches"]!.AsArray()) all.Add(reach!.DeepClone());
    }

    return new JsonObject { ["reaches"] = all }.ToJsonString();
}

static ProductContent TownContentFiles(string repo)
{
    // What the Towns lane reads: the town content and the few files its pieces reference outside it.
    string content = Path.Combine(repo, "content/game");
    IEnumerable<string> files = Directory.EnumerateFiles(Path.Combine(content, "towns"), "*", SearchOption.AllDirectories)
        .Concat(new[] { "models/scatter/fern-1.prop-mesh.json", "textures/fire-smoke.png", "textures/trees/fern-leaves.png" }.Select(file => Path.Combine(content, file)));
    ProductContentFile[] read = [.. files.Where(File.Exists).Select(file => new ProductContentFile(
        Encoding.UTF8.GetBytes(Path.GetRelativePath(content, file).Replace('\\', '/')), File.ReadAllBytes(file)))];
    return new(read);
}

static string Repository()
{
    for (string? directory = Directory.GetCurrentDirectory(); directory is not null; directory = Path.GetDirectoryName(directory))
        if (File.Exists(Path.Combine(directory, "authoring/tools/golden-seed/golden-seed.json"))) return directory;
    throw new InvalidOperationException("Run the exporter from inside the repository.");
}

static string EnginePair(string repo)
{
    string props = File.ReadAllText(Path.Combine(repo, "Directory.Build.props"));
    int start = props.IndexOf("<RustyEnginePackageVersion>", StringComparison.Ordinal) + "<RustyEnginePackageVersion>".Length;
    return props[start..props.IndexOf('<', start)];
}

static string Git(string repo, string arguments)
{
    using Process process = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = repo, RedirectStandardOutput = true })!;
    string text = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return text;
}

static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

static string Slug(string name) => System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

static string ReadGzip(string path)
{
    using GZipStream stream = new(File.OpenRead(path), CompressionMode.Decompress);
    using StreamReader reader = new(stream, Encoding.UTF8);
    return reader.ReadToEnd();
}

static void WriteGzip(string path, string text)
{
    string partial = path + ".partial";
    using (GZipStream stream = new(File.Create(partial), CompressionLevel.SmallestSize))
        stream.Write(Encoding.UTF8.GetBytes(text));
    File.Move(partial, path, true);
}

static Dictionary<string, string> Options(string[] values)
{
    Dictionary<string, string> found = [];
    for (int k = 0; k < values.Length; k++)
    {
        if (!values[k].StartsWith("--", StringComparison.Ordinal)) continue;
        found[values[k][2..]] = k + 1 < values.Length && !values[k + 1].StartsWith("--", StringComparison.Ordinal) ? values[++k] : "true";
    }

    return found;
}

static void Log(string text) => Console.WriteLine(text);
