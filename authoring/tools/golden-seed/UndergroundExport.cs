using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace GoldenSeed;

/// <summary>
/// Exports and benchmarks the continent-scale underground cave-system plan (#11209) at each coarseness level: wall time,
/// memory, disk, and a census (round 3: depth below the walked ground, vertical span and levels, depth by elevation band, the
/// spread by biome, elevation and moisture band against each band's share of land, mouths by kind and level, styles, the
/// regional floor's share, and box overlaps between systems).
/// </summary>
internal static class UndergroundExport
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly (string Label, double Min, double Max)[] HardnessBuckets =
    [
        ("0.00-0.25", 0.00, 0.25),
        ("0.25-0.50", 0.25, 0.50),
        ("0.50-0.75", 0.50, 0.75),
        ("0.75-1.00", 0.75, 1.01),
    ];

    // The bands the round 3 brief measured the spread in.
    private static readonly (string Label, double Min, double Max)[] ElevationBands =
    [
        ("under 100 m", double.NegativeInfinity, 100),
        ("100-600 m", 100, 600),
        ("600-2,000 m", 600, 2_000),
        ("over 2,000 m", 2_000, double.PositiveInfinity),
    ];

    private static readonly (string Label, double Min, double Max)[] MoistureBands =
    [
        ("under 0.3", double.NegativeInfinity, 0.3),
        ("0.3-0.6", 0.3, 0.6),
        ("0.6-0.9", 0.6, 0.9),
        ("0.9 and over", 0.9, double.PositiveInfinity),
    ];

    /// <summary>Land is sampled on this lattice for each band's share of it.</summary>
    private const double LandSampleMetres = 1_000;
    private const double MinimumLandAboveSea = 5;
    private const float BoxCoverMetres = 12f;
    private const int DepthBinMetres = 32;
    private const int SamplesPerCategory = 3;

    internal static int Run(
        WorldMap map,
        int pixels,
        string outputRoot,
        IReadOnlyList<UndergroundCoarseness> presets,
        int workers,
        Func<double, double, double>? groundAt,
        string groundName,
        Action<string> log)
    {
        string ugRoot = Path.Combine(outputRoot, "underground");
        Directory.CreateDirectory(ugRoot);

        Stopwatch rasterClock = Stopwatch.StartNew();
        WriteSolubilityRaster(map, pixels, Path.Combine(ugRoot, "solubility.f32"));
        log($"  limestone raster ({pixels}x{pixels}): {rasterClock.ElapsedMilliseconds} ms");

        int tileHalf = (int)Math.Ceiling((map.Radius - (MapRegions.TileSpacing * 0.5)) / MapRegions.TileSpacing);
        List<(int X, int Z)> tiles = [];
        for (int tz = -tileHalf; tz <= tileHalf; tz++)
        for (int tx = -tileHalf; tx <= tileHalf; tx++)
            tiles.Add((tx, tz));

        List<(string Name, double X, double Z)> towns = ReadTowns(Path.Combine(outputRoot, "places.json"));
        Stopwatch landClock = Stopwatch.StartNew();
        LandShares land = LandShares.Measure(map);
        log($"  land shares ({land.SampleKm2:F0} km² per sample, {land.LandKm2:F0} km² of land): {landClock.ElapsedMilliseconds} ms");

        JsonArray levelManifests = [];
        foreach (UndergroundCoarseness preset in presets)
        {
            levelManifests.Add(ExportLevel(map, tiles, tileHalf, ugRoot, preset, workers, groundAt, towns, land, log));
        }

        JsonObject manifest = new()
        {
            ["format"] = 3,
            ["provisional"] = true,
            ["note"] = "CMS-only exploratory underground plan (#11209, round 3). Numbers measure the continent plan and its serialisation, not runtime suitability.",
            ["ground"] = groundName,
            ["world"] = new JsonObject
            {
                ["seed"] = map.Configuration.Seed,
                ["size"] = map.Configuration.Size,
                ["radius"] = map.Radius,
                ["fingerprint"] = map.Fingerprint.ToString("x16", CultureInfo.InvariantCulture),
                ["tileSpacing"] = MapRegions.TileSpacing,
                ["totalTiles"] = tiles.Count,
            },
            ["solubilityRaster"] = new JsonObject
            {
                ["file"] = "underground/solubility.f32",
                ["pixels"] = pixels,
                ["metresPerPixel"] = (map.Radius * 2.0) / pixels,
                ["karstThreshold"] = UndergroundPlan.KarstThreshold,
            },
            ["levels"] = levelManifests,
        };

        File.WriteAllText(Path.Combine(ugRoot, "manifest.json"), manifest.ToJsonString(Indented));
        PrintComparisonTable(levelManifests, log);
        return 0;
    }

    private static JsonObject ExportLevel(
        WorldMap map,
        IReadOnlyList<(int X, int Z)> tiles,
        int tileHalf,
        string ugRoot,
        UndergroundCoarseness preset,
        int workers,
        Func<double, double, double>? groundAt,
        IReadOnlyList<(string Name, double X, double Z)> towns,
        LandShares land,
        Action<string> log)
    {
        string levelDir = Path.Combine(ugRoot, preset.Name);
        string tilesDir = Path.Combine(levelDir, "tiles");
        string gridsDir = Path.Combine(levelDir, "grids");
        if (Directory.Exists(tilesDir)) Directory.Delete(tilesDir, recursive: true);
        Directory.CreateDirectory(tilesDir);
        if (preset.IncludeFieldGrid) Directory.CreateDirectory(gridsDir);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long heapBefore = GC.GetTotalMemory(true);
        long allocBefore = GC.GetTotalAllocatedBytes(true);

        UndergroundTilePlan[] built = new UndergroundTilePlan[tiles.Count];
        double[] tileMs = new double[tiles.Count];

        // A row of tiles at a time, so the region tiles the walked ground needs stay built (each reaches into its neighbours).
        Stopwatch generateWall = Stopwatch.StartNew();
        int side = (tileHalf * 2) + 1;
        // One planner for the level: tiles share their neighbours' skeletons and uncut systems (#11219).
        UndergroundPlanner planner = new(map, preset, groundAt);
        for (int row = 0; row < side; row++)
        {
            Parallel.For(row * side, (row + 1) * side, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                long t0 = Stopwatch.GetTimestamp();
                built[i] = planner.BuildTile(tiles[i].X, tiles[i].Z);
                tileMs[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            });
        }

        generateWall.Stop();

        long allocAfter = GC.GetTotalAllocatedBytes(true);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long heapWithAllTiles = GC.GetTotalMemory(true);
        GC.KeepAlive(built);
        long retainedBytes = Math.Max(0, heapWithAllTiles - heapBefore);
        long allocatedBytes = Math.Max(0, allocAfter - allocBefore);

        Stopwatch serializeWall = Stopwatch.StartNew();
        long rawJsonBytes = 0, gzipTileBytes = 0, rawGridBytes = 0, gzipGridBytes = 0;
        int landTiles = 0;
        double sumLandTileMs = 0, maxTileMs = 0;

        Census census = new(land);
        JsonArray tileIndex = [];
        JsonArray summarySystems = [];

        for (int i = 0; i < built.Length; i++)
        {
            UndergroundTilePlan tile = built[i];
            double ms = tileMs[i];
            if (ms > maxTileMs) maxTileMs = ms;
            census.RejectedForOverlap += tile.RejectedForOverlap;
            census.CutForOverlap += tile.CutForOverlap;
            bool hasData = tile.Systems.Count > 0;
            if (hasData)
            {
                landTiles++;
                sumLandTileMs += ms;
            }

            if (tile.FieldGrid is UndergroundTileGrid grid && hasData)
            {
                int n = grid.Side * grid.Side;
                byte[] packed = new byte[n * 4];
                Buffer.BlockCopy(grid.Solubility, 0, packed, 0, n);
                Buffer.BlockCopy(grid.WaterTableMetres, 0, packed, n, n * 2);
                Buffer.BlockCopy(grid.DepthBand, 0, packed, n * 3, n);
                rawGridBytes += packed.Length;
                gzipGridBytes += WriteGzipBytes(Path.Combine(gridsDir, $"{tile.TileX}_{tile.TileZ}.bin.gz"), packed);
            }

            if (hasData)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(SerializeTile(tile).ToJsonString());
                rawJsonBytes += utf8.Length;
                gzipTileBytes += WriteGzipBytes(Path.Combine(tilesDir, $"{tile.TileX}_{tile.TileZ}.json.gz"), utf8);
                tileIndex.Add(new JsonObject
                {
                    ["tile"] = new JsonArray(tile.TileX, tile.TileZ),
                    ["systems"] = tile.Systems.Count,
                    ["boxes"] = tile.Systems.Sum(s => s.Boxes.Count),
                    ["mouths"] = tile.Systems.Sum(s => s.Mouths.Count),
                });
            }

            foreach (UndergroundSystem sys in tile.Systems)
            {
                VerifySystemGeometry(sys);
                census.Record(sys);
                summarySystems.Add(SerializeSystemSummary(sys));
            }
        }

        census.CountOverlaps(built.SelectMany(t => t.Systems));

        WriteGzipBytes(Path.Combine(levelDir, "tiles-index.json.gz"), Encoding.UTF8.GetBytes(tileIndex.ToJsonString()));
        byte[] summaryUtf8 = Encoding.UTF8.GetBytes(new JsonObject { ["level"] = preset.Name, ["systems"] = summarySystems }.ToJsonString());
        long summaryGzipBytes = WriteGzipBytes(Path.Combine(levelDir, "summary.json.gz"), summaryUtf8);
        serializeWall.Stop();

        double generateMs = generateWall.Elapsed.TotalMilliseconds;
        double serializeMs = serializeWall.Elapsed.TotalMilliseconds;
        double exportMs = generateMs + serializeMs;

        JsonArray samples = PickSamples(built.SelectMany(t => t.Systems).ToList(), towns);
        JsonObject censusJson = census.ToJson();

        log(string.Create(CultureInfo.InvariantCulture,
            $"  level {preset.Name,-8} | export {exportMs,7:F0} ms (gen {generateMs,6:F0} + io {serializeMs,5:F0}) | retained {retainedBytes / (1024.0 * 1024.0),5:F1} MB | disk {(gzipTileBytes + gzipGridBytes) / (1024.0 * 1024.0),5:F2} MB gz | {census.Systems,6} systems ({census.MultiBox * 100.0 / Math.Max(1, census.Systems),4:F1}% multi, {census.Boxes,6} boxes, {census.Mouths,6} mouths, {census.OverlapPairs} overlaps)"));

        return new JsonObject
        {
            ["name"] = preset.Name,
            ["description"] = preset.Description,
            ["settings"] = new JsonObject
            {
                ["siteSpacingMetres"] = preset.SiteSpacingMetres,
                ["boxSize"] = new JsonArray(preset.BoxMetresX, preset.BoxMetresY, preset.BoxMetresZ),
                ["boxChunks"] = new JsonArray(preset.BoxChunksX, preset.BoxChunksY, preset.BoxChunksZ),
                ["minSystemSeparationMetres"] = preset.MinSystemSeparationMetres,
                ["maxBoxesPerSystem"] = preset.MaxBoxesPerSystem,
                ["maxLevels"] = preset.MaxLevels,
                ["targetMultiBoxShare"] = preset.TargetMultiBoxShare,
                ["subChambersPerBox"] = preset.SubChambersPerBox,
                ["includeFieldGrid"] = preset.IncludeFieldGrid,
            },
            ["metrics"] = new JsonObject
            {
                ["wallMs"] = Math.Round(exportMs, 1),
                ["exportMs"] = Math.Round(exportMs, 1),
                ["generateMs"] = Math.Round(generateMs, 1),
                ["serializeMs"] = Math.Round(serializeMs, 1),
                ["avgLandTileMs"] = landTiles > 0 ? Math.Round(sumLandTileMs / landTiles, 2) : 0.0,
                ["maxTileMs"] = Math.Round(maxTileMs, 2),
                ["landTiles"] = landTiles,
                ["totalTiles"] = tiles.Count,
                ["allocatedMB"] = Math.Round(allocatedBytes / (1024.0 * 1024.0), 2),
                ["retainedInMemoryMB"] = Math.Round(retainedBytes / (1024.0 * 1024.0), 2),
                ["retainedPerLandTileKB"] = landTiles > 0 ? Math.Round((retainedBytes / 1024.0) / landTiles, 1) : 0.0,
                // With the walked ground the retained memory includes the region tiles the exporter keeps built (about 30 MB each).
                ["retainedIncludesRegionTiles"] = groundAt is not null,
                ["rawJsonMB"] = Math.Round(rawJsonBytes / (1024.0 * 1024.0), 2),
                ["gzipTilesMB"] = Math.Round(gzipTileBytes / (1024.0 * 1024.0), 2),
                ["rawGridMB"] = Math.Round(rawGridBytes / (1024.0 * 1024.0), 2),
                ["gzipGridMB"] = Math.Round(gzipGridBytes / (1024.0 * 1024.0), 2),
                ["totalGzipMB"] = Math.Round((gzipTileBytes + gzipGridBytes) / (1024.0 * 1024.0), 2),
                ["summaryGzipMB"] = Math.Round(summaryGzipBytes / (1024.0 * 1024.0), 2),
            },
            ["census"] = censusJson,
            ["sampleMultiSystems"] = samples,
        };
    }

    /// <summary>Systems worth opening, a few of each kind the round asked about, with why each was picked.</summary>
    private static JsonArray PickSamples(List<UndergroundSystem> all, IReadOnlyList<(string Name, double X, double Z)> towns)
    {
        List<(UndergroundSystem System, string Why)> picks = [];
        HashSet<string> taken = [];
        void Take(IEnumerable<UndergroundSystem> ordered, string why)
        {
            foreach (UndergroundSystem s in ordered.Where(s => !taken.Contains(s.Id)).Take(SamplesPerCategory))
            {
                taken.Add(s.Id);
                picks.Add((s, why));
            }
        }

        static int DistinctMouthLevels(UndergroundSystem s) => s.Mouths.Select(m => m.Level).Distinct().Count();
        Take(all.Where(s => s.Levels >= 3).OrderByDescending(s => s.TopY - s.FloorY).ThenByDescending(DistinctMouthLevels).ThenBy(s => s.Id, StringComparer.Ordinal), "deepest stacked");
        Take(all.Where(s => DistinctMouthLevels(s) >= 2).OrderByDescending(DistinctMouthLevels).ThenByDescending(s => s.Mouths.Count).ThenBy(s => s.Id, StringComparer.Ordinal), "mouths at several levels");
        Take(all.Where(s => s.Mouths.Any(m => m.Kind == UndergroundMouthKind.Cliff)).OrderBy(s => s.Id, StringComparer.Ordinal), "cliff mouth");
        Take(all.Where(s => s.Style == UndergroundStyle.Relict && s.Levels >= 2).OrderByDescending(s => s.Levels).ThenBy(s => s.Id, StringComparer.Ordinal), "dry relict");
        Take(all.Where(s => s.Style == UndergroundStyle.Hypogene).OrderByDescending(s => s.Levels).ThenBy(s => s.Id, StringComparer.Ordinal), "hypogene (dry, perched)");
        Take(all.Where(s => s.Style == UndergroundStyle.Ice && s.Levels >= 2).OrderByDescending(s => s.Levels).ThenBy(s => s.Id, StringComparer.Ordinal), "ice");
        Take(all.Where(s => s.Levels == 1 && s.Boxes.Count >= 3).OrderByDescending(s => s.Boxes.Count).ThenBy(s => s.Id, StringComparer.Ordinal), "single level");

        JsonArray result = [];
        foreach ((UndergroundSystem s, string why) in picks)
        {
            (string townName, double townKm) = NearestTown(towns, s.AnchorX, s.AnchorZ);
            result.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["why"] = why,
                ["tile"] = new JsonArray(s.TileX, s.TileZ),
                ["x"] = s.AnchorX,
                ["z"] = s.AnchorZ,
                ["ground"] = s.GroundElevation,
                ["boxes"] = s.Boxes.Count,
                ["levels"] = s.Levels,
                ["spanMetres"] = Math.Round(s.TopY - s.FloorY, 1),
                ["floorBelowGround"] = Math.Round(s.GroundElevation - s.FloorY, 1),
                ["links"] = s.Links.Count,
                ["mouths"] = s.Mouths.Count,
                ["mouthKinds"] = new JsonArray([.. s.Mouths.Select(m => (JsonNode)JsonValue.Create($"{m.Kind}@L{m.Level}")!)]),
                ["style"] = s.Style.ToString(),
                ["bigSpaces"] = new JsonArray([.. s.BigSpaces.Select(b => (JsonNode)JsonValue.Create(b.Kind.ToString())!)]),
                ["biome"] = MapBiomes.Name(s.Biome),
                ["maxDepth"] = s.MaxDepthMetres,
                ["solubility"] = s.Solubility,
                ["nearestTown"] = townName,
                ["nearestTownKm"] = Math.Round(townKm, 1),
            });
        }

        return result;
    }

    private static JsonObject SerializeTile(UndergroundTilePlan tile)
    {
        JsonArray systems = [];
        foreach (UndergroundSystem s in tile.Systems) systems.Add(SerializeSystemFull(s));
        JsonObject obj = new()
        {
            ["tile"] = new JsonArray(tile.TileX, tile.TileZ),
            ["level"] = tile.CoarsenessName,
            ["systems"] = systems,
        };
        if (tile.FieldGrid is UndergroundTileGrid g)
        {
            obj["fieldGrid"] = new JsonObject { ["side"] = g.Side, ["spacingMetres"] = g.SpacingMetres, ["file"] = $"grids/{tile.TileX}_{tile.TileZ}.bin.gz" };
        }

        return obj;
    }

    internal static JsonObject SerializeSystemFull(UndergroundSystem s)
    {
        JsonArray boxes = [];
        foreach (UndergroundBox b in s.Boxes)
        {
            JsonArray rooms = [];
            foreach (UndergroundRoom r in b.Rooms)
            {
                rooms.Add(new JsonObject
                {
                    ["id"] = r.Id,
                    ["kind"] = r.Kind.ToString(),
                    ["at"] = new JsonArray(r.X, r.Y, r.Z),
                    ["radii"] = new JsonArray(r.RadiusX, r.RadiusY, r.RadiusZ),
                    ["water"] = r.WaterLevel,
                });
            }

            JsonArray conduits = [];
            foreach (UndergroundConduit c in b.Conduits)
            {
                conduits.Add(new JsonObject { ["from"] = c.FromRoom, ["to"] = c.ToRoom, ["radius"] = c.Radius, ["water"] = c.WaterLevel, ["loop"] = c.IsLoop });
            }

            boxes.Add(new JsonObject
            {
                ["index"] = b.Index,
                ["grid"] = new JsonArray(b.GridX, b.GridY, b.GridZ),
                ["origin"] = new JsonArray(b.OriginX, Math.Round(b.OriginY, 1), b.OriginZ),
                ["chunks"] = new JsonArray(b.ChunksX, b.ChunksY, b.ChunksZ),
                ["size"] = new JsonArray(b.ChunksX * 16, b.ChunksY * 16, b.ChunksZ * 16),
                ["ground"] = b.GroundElevation,
                ["groundMin"] = b.GroundMinimum,
                ["depth"] = b.DepthBelowSurface,
                ["topBelowGround"] = Math.Round(b.GroundElevation - b.TopY, 1),
                ["waterLocalY"] = b.WaterTableLocalY,
                ["depthBand"] = b.DepthBand.ToString(),
                ["seed"] = b.BoxSeed.ToString("x16", CultureInfo.InvariantCulture),
                ["rooms"] = rooms,
                ["conduits"] = conduits,
            });
        }

        JsonArray links = [];
        foreach (UndergroundLink l in s.Links)
        {
            links.Add(new JsonObject
            {
                ["from"] = l.FromBox,
                ["to"] = l.ToBox,
                ["fromFace"] = l.FromFace.ToString(),
                ["toFace"] = l.ToFace.ToString(),
                ["faceU"] = l.FaceU,
                ["faceV"] = l.FaceV,
                ["at"] = new JsonArray(l.WorldX, l.WorldY, l.WorldZ),
                ["radius"] = l.RadiusMetres,
                ["waterY"] = l.WaterLevelWorldY,
                ["kind"] = l.Kind.ToString(),
                ["seamSeed"] = l.SeamSeed.ToString("x16", CultureInfo.InvariantCulture),
            });
        }

        JsonArray mouths = [];
        foreach (UndergroundMouth m in s.Mouths)
        {
            mouths.Add(new JsonObject
            {
                ["box"] = m.BoxIndex,
                ["face"] = m.Face.ToString(),
                ["portU"] = m.PortU,
                ["portV"] = m.PortV,
                ["portY"] = m.PortWorldY,
                ["at"] = new JsonArray(m.WorldX, m.WorldY, m.WorldZ),
                ["tendril"] = m.TendrilMetres,
                ["slope"] = m.SlopeAtMouth,
                ["level"] = m.Level,
                ["kind"] = m.Kind.ToString(),
            });
        }

        JsonArray bigSpaces = [];
        foreach (UndergroundBigSpace bs in s.BigSpaces)
        {
            bigSpaces.Add(new JsonObject { ["box"] = bs.BoxIndex, ["kind"] = bs.Kind.ToString(), ["local"] = new JsonArray(bs.LocalX, bs.LocalY, bs.LocalZ), ["radius"] = bs.RadiusMetres });
        }

        JsonObject obj = new()
        {
            ["id"] = s.Id,
            ["tile"] = new JsonArray(s.TileX, s.TileZ),
            ["seed"] = s.Seed.ToString("x16", CultureInfo.InvariantCulture),
            ["x"] = s.AnchorX,
            ["z"] = s.AnchorZ,
            ["ground"] = s.GroundElevation,
            ["biome"] = MapBiomes.Name(s.Biome),
            ["hardness"] = s.Hardness,
            ["solubility"] = s.Solubility,
            ["discharge"] = s.Discharge,
            ["temperature"] = s.Temperature,
            ["moisture"] = s.Moisture,
            ["maxDepth"] = s.MaxDepthMetres,
            ["style"] = s.Style.ToString(),
            ["baseLevelY"] = s.BaseLevelY,
            ["relief"] = s.ReliefMetres,
            ["levels"] = s.Levels,
            ["reachesBaseLevel"] = s.ReachesBaseLevel,
            ["fromRegionalFloor"] = s.FromRegionalFloor,
            ["floorY"] = Math.Round(s.FloorY, 1),
            ["topY"] = Math.Round(s.TopY, 1),
            ["multi"] = s.IsMultiBox,
            ["boxes"] = boxes,
            ["links"] = links,
            ["mouths"] = mouths,
            ["bigSpaces"] = bigSpaces,
        };
        if (s.Section is UndergroundSection section)
        {
            obj["section"] = new JsonObject
            {
                ["axis"] = section.Axis.ToString(),
                ["start"] = section.StartMetres,
                ["step"] = section.StepMetres,
                ["ground"] = new JsonArray([.. section.Ground.Select(g => (JsonNode)JsonValue.Create(g)!)]),
            };
        }

        return obj;
    }

    private static JsonObject SerializeSystemSummary(UndergroundSystem s)
    {
        JsonArray boxes = [];
        foreach (UndergroundBox b in s.Boxes) boxes.Add(new JsonArray(b.OriginX, b.OriginZ, b.ChunksX * 16, b.ChunksZ * 16, (int)b.DepthBand, b.GridY));

        JsonArray links = [];
        foreach (UndergroundLink l in s.Links)
        {
            UndergroundBox a = s.Boxes[l.FromBox], b = s.Boxes[l.ToBox];
            links.Add(new JsonArray(
                Math.Round(a.OriginX + (a.ChunksX * 8.0), 1), Math.Round(a.OriginZ + (a.ChunksZ * 8.0), 1),
                Math.Round(b.OriginX + (b.ChunksX * 8.0), 1), Math.Round(b.OriginZ + (b.ChunksZ * 8.0), 1),
                (int)l.Kind));
        }

        JsonArray mouths = [];
        foreach (UndergroundMouth m in s.Mouths) mouths.Add(new JsonArray(m.WorldX, m.WorldZ, (int)m.Kind, m.Level));

        JsonArray big = [];
        foreach (UndergroundBigSpace bs in s.BigSpaces)
        {
            UndergroundBox b = s.Boxes[bs.BoxIndex];
            big.Add(new JsonArray(Math.Round(b.OriginX + bs.LocalX, 1), Math.Round(b.OriginZ + bs.LocalZ, 1), (int)bs.Kind));
        }

        return new JsonObject
        {
            ["id"] = s.Id,
            ["tile"] = new JsonArray(s.TileX, s.TileZ),
            ["x"] = s.AnchorX,
            ["z"] = s.AnchorZ,
            ["d"] = Math.Round(s.GroundElevation - s.FloorY, 1),
            ["s"] = s.Solubility,
            ["h"] = s.Hardness,
            ["b"] = MapBiomes.Name(s.Biome),
            ["m"] = s.IsMultiBox ? 1 : 0,
            ["st"] = (int)s.Style,
            ["lv"] = s.Levels,
            ["f"] = s.FromRegionalFloor ? 1 : 0,
            ["boxes"] = boxes,
            ["links"] = links,
            ["mouths"] = mouths,
            ["big"] = big,
        };
    }

    private static void WriteSolubilityRaster(WorldMap map, int pixels, string path)
    {
        double step = (map.Radius * 2.0) / pixels;
        float[] values = new float[pixels * pixels];
        Parallel.For(0, pixels, row =>
        {
            double z = -map.Radius + ((row + 0.5) * step);
            for (int col = 0; col < pixels; col++)
            {
                double x = -map.Radius + ((col + 0.5) * step);
                values[(row * pixels) + col] = map.Broad(x, z).Elevation < GenerationConstants.WaterLevel ? 0f : (float)UndergroundPlan.SolubilityAt(map, x, z);
            }
        });

        byte[] raw = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        File.WriteAllBytes(path, raw);
    }

    private static long WriteGzipBytes(string path, byte[] bytes)
    {
        string partial = path + ".partial";
        using (FileStream fs = File.Create(partial))
        using (GZipStream gz = new(fs, CompressionLevel.Optimal))
        {
            gz.Write(bytes, 0, bytes.Length);
        }

        long length = new FileInfo(partial).Length;
        File.Move(partial, path, true);
        return length;
    }

    private static List<(string Name, double X, double Z)> ReadTowns(string placesPath)
    {
        List<(string, double, double)> result = [];
        if (!File.Exists(placesPath)) return result;
        JsonNode? root = JsonNode.Parse(File.ReadAllText(placesPath));
        foreach (JsonNode? town in root?["towns"]?.AsArray() ?? [])
        {
            if (town is null) continue;
            result.Add((town["name"]!.GetValue<string>(), town["x"]!.GetValue<double>(), town["z"]!.GetValue<double>()));
        }
        return result;
    }

    private static (string Name, double Km) NearestTown(IReadOnlyList<(string Name, double X, double Z)> towns, double x, double z)
    {
        string bestName = "";
        double bestDist = double.MaxValue;
        foreach (var t in towns)
        {
            double d = Math.Sqrt(((t.X - x) * (t.X - x)) + ((t.Z - z) * (t.Z - z)));
            if (d < bestDist) { bestDist = d; bestName = t.Name; }
        }
        return bestDist < double.MaxValue ? (bestName, bestDist / 1000.0) : ("", 0.0);
    }

    private static void PrintComparisonTable(JsonArray levels, Action<string> log)
    {
        log("");
        log("Underground plan (provisional, CMS-only):");
        log("| Level | Lattice | Export ms (gen + io) | Systems | Multi % | Boxes | Mouths | Links | Overlaps | Median floor below ground | Max floor |");
        log("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (JsonNode? node in levels)
        {
            if (node is not JsonObject o) continue;
            var st = o["settings"]!.AsObject();
            var m = o["metrics"]!.AsObject();
            var c = o["census"]!.AsObject();
            var floor = c["depth"]!["boxFloorBelowGround"]!;
            log(string.Create(CultureInfo.InvariantCulture,
                $"| {o["name"]} | {st["siteSpacingMetres"]} m | {m["exportMs"]} ({m["generateMs"]}+{m["serializeMs"]}) | {c["systems"]} | {c["multiBoxShare"]!.GetValue<double>() * 100:F1}% | {c["totalBoxes"]} | {c["mouths"]} | {c["links"]} | {c["overlapPairs"]} | {floor["p50"]} m | {floor["max"]} m |"));
        }
    }

    /// <summary>
    /// The plan's geometry invariants (#11209 rounds 2-3): every side link lies inside both boxes' heights; every shaft joins a
    /// box to the one stacked directly on it; every box sits on its system's lattice with no two in one cell.
    /// </summary>
    internal static bool LinksVerticallyValid(UndergroundSystem sys, out string? error)
    {
        foreach (UndergroundLink l in sys.Links)
        {
            if (l.FromBox < 0 || l.FromBox >= sys.Boxes.Count || l.ToBox < 0 || l.ToBox >= sys.Boxes.Count || l.FromBox == l.ToBox)
            {
                error = $"{sys.Id}: invalid box index in link {l.FromBox}->{l.ToBox}";
                return false;
            }

            UndergroundBox a = sys.Boxes[l.FromBox], b = sys.Boxes[l.ToBox];
            if (l.Kind == UndergroundLinkKind.Shaft)
            {
                bool stacked = a.GridX == b.GridX && a.GridZ == b.GridZ && b.GridY == a.GridY + 1 && Math.Abs(b.OriginY - a.TopY) < 0.01
                    && l.FromFace == UndergroundFace.PosY && l.ToFace == UndergroundFace.NegY;
                if (!stacked)
                {
                    error = $"{sys.Id}: shaft {l.FromBox}->{l.ToBox} does not join a box to the one stacked on it";
                    return false;
                }

                continue;
            }

            if (l.WorldY < a.OriginY + 5.0 || l.WorldY > a.TopY - 5.0 || l.WorldY < b.OriginY + 5.0 || l.WorldY > b.TopY - 5.0)
            {
                error = $"{sys.Id}: link {l.FromBox}->{l.ToBox} Y={l.WorldY:F1} outside box A [{a.OriginY:F1}..{a.TopY:F1}] or B [{b.OriginY:F1}..{b.TopY:F1}]";
                return false;
            }
        }

        if (sys.Boxes.Select(b => (b.GridX, b.GridY, b.GridZ)).Distinct().Count() != sys.Boxes.Count)
        {
            error = $"{sys.Id}: two boxes share a lattice cell";
            return false;
        }

        error = null;
        return true;
    }

    private static void VerifySystemGeometry(UndergroundSystem sys)
    {
        if (!LinksVerticallyValid(sys, out string? error))
        {
            throw new InvalidOperationException($"Underground plan geometry invariant violated: {error}");
        }
    }

    /// <summary>Deterministic &amp; geometric sanity check for <see cref="UndergroundPlan"/> run by <c>golden-seed check</c>.</summary>
    internal static bool CheckPlan(Action<string> report)
    {
        TerrainConfiguration cfg = new(19UL, 390_000);
        WorldMap map = WorldMapGenerator.Generate(cfg);
        bool ok = true;
        // A lowland tile, a steep alpine one and a dry one (#11209 R11209-2, round 3).
        (int X, int Z)[] sampleTiles = [(0, 0), (-5, -1), (-6, 1)];
        foreach (UndergroundCoarseness preset in UndergroundPlan.Presets)
        {
            int totalSys = 0, totalBoxes = 0, totalLinks = 0, shafts = 0, stacked = 0;
            bool sameAll = true, boundedAll = true, geometryAll = true, buriedAll = true;
            string? firstError = null;
            foreach ((int tx, int tz) in sampleTiles)
            {
                UndergroundTilePlan a = UndergroundPlan.BuildTile(map, tx, tz, preset);
                UndergroundTilePlan b = UndergroundPlan.BuildTile(map, tx, tz, preset);
                totalSys += a.Systems.Count;
                totalBoxes += a.Systems.Sum(s => s.Boxes.Count);
                totalLinks += a.Systems.Sum(s => s.Links.Count);
                shafts += a.Systems.Sum(s => s.Links.Count(l => l.Kind == UndergroundLinkKind.Shaft));
                stacked += a.Systems.Count(s => s.Levels > 1);
                sameAll &= a.Systems.Count == b.Systems.Count && a.Systems.Zip(b.Systems).All(pair =>
                    pair.First.Id == pair.Second.Id
                    && pair.First.Boxes.Select(x => (x.OriginX, x.OriginY, x.OriginZ)).SequenceEqual(pair.Second.Boxes.Select(x => (x.OriginX, x.OriginY, x.OriginZ)))
                    && pair.First.Links.Count == pair.Second.Links.Count
                    && pair.First.Mouths.Count == pair.Second.Mouths.Count);
                boundedAll &= a.Systems.All(s => s.Boxes.Count >= 1 && s.Boxes.Count <= preset.MaxBoxesPerSystem && s.Levels <= preset.MaxLevels && s.Mouths.Count >= 1);
                foreach (UndergroundSystem s in a.Systems)
                {
                    if (!LinksVerticallyValid(s, out string? err))
                    {
                        geometryAll = false;
                        firstError ??= err;
                    }

                    // Every box under its cover of the (smooth) ground the check plans on.
                    foreach (UndergroundBox box in s.Boxes)
                    {
                        if (box.GroundMinimum < box.TopY + 3.99)
                        {
                            buriedAll = false;
                            firstError ??= $"{s.Id} box {box.Index}: top {box.TopY:F1} under ground {box.GroundMinimum:F1}";
                        }
                    }
                }
            }

            bool held = sameAll && boundedAll && geometryAll && buriedAll && shafts > 0;
            ok &= held;
            report(FormattableString.Invariant(
                $"{(held ? "ok" : "FAILED")}: underground plan {preset.Name} (3 tiles incl. steep -5,-1) -> {totalSys} systems ({stacked} stacked), {totalBoxes} boxes, {totalLinks} links ({shafts} shafts), deterministic={sameAll}, bounded={boundedAll}, geometry={geometryAll}, buried={buriedAll}{(firstError is not null ? $" ({firstError})" : "")}"));
        }

        ok &= CheckOrderIndependence(map, report);
        return ok;
    }

    /// <summary>
    /// The plan does not depend on the order systems are asked for (#11219): one planner builds a tile's every system in id
    /// order; another first builds the neighbouring tiles from the far side, then each of the tile's systems alone in reverse.
    /// Every system must come out byte for byte the same.
    /// </summary>
    private static bool CheckOrderIndependence(WorldMap map, Action<string> report)
    {
        UndergroundCoarseness preset = UndergroundPlan.Presets[0];
        (int X, int Z) tile = (-5, -1);
        UndergroundPlanner inOrder = new(map, preset);
        Dictionary<string, string> first = inOrder.BuildTile(tile.X, tile.Z).Systems.ToDictionary(s => s.Id, s => SerializeSystemFull(s).ToJsonString());
        UndergroundPlanner reversed = new(map, preset);
        foreach ((int dx, int dz) in new[] { (1, 1), (1, 0), (0, 1), (-1, -1) })
        {
            _ = reversed.BuildTile(tile.X + dx, tile.Z + dz);
        }

        int same = 0, compared = 0;
        foreach (UndergroundSite site in reversed.Skeleton(tile.X, tile.Z).Sites.Reverse())
        {
            if (reversed.SystemAt(site) is not UndergroundSystem system) continue;
            compared++;
            if (first.TryGetValue(system.Id, out string? json) && json == SerializeSystemFull(system).ToJsonString()) same++;
        }

        bool held = compared > 0 && same == compared && compared == first.Count;
        report(FormattableString.Invariant($"{(held ? "ok" : "FAILED")}: underground plan order independence ({preset.Name}, tile {tile.X},{tile.Z}): {same}/{compared} systems byte-identical whatever is built first ({first.Count} in id order)"));
        return held;
    }

    /// <summary>How the continent's land divides among the census bands, sampled on a lattice.</summary>
    internal sealed class LandShares
    {
        internal double SampleKm2 { get; private set; }
        internal double LandKm2 { get; private set; }
        internal Dictionary<string, double> BiomeKm2 { get; } = [];
        internal Dictionary<string, double> ElevationKm2 { get; } = [];
        internal Dictionary<string, double> MoistureKm2 { get; } = [];
        internal Dictionary<string, double> BiomeKarstKm2 { get; } = [];
        internal Dictionary<string, double> ElevationKarstKm2 { get; } = [];
        internal Dictionary<string, double> MoistureKarstKm2 { get; } = [];
        internal double KarstKm2 { get; private set; }

        internal static LandShares Measure(WorldMap map)
        {
            double km2 = LandSampleMetres * LandSampleMetres / 1_000_000.0;
            LandShares shares = new() { SampleKm2 = km2 };
            double land = 0;
            for (double z = -map.Radius + (LandSampleMetres * 0.5); z < map.Radius; z += LandSampleMetres)
            for (double x = -map.Radius + (LandSampleMetres * 0.5); x < map.Radius; x += LandSampleMetres)
            {
                MapSample s = map.Broad(x, z);
                if (s.Elevation < GenerationConstants.WaterLevel + MinimumLandAboveSea) continue;
                land += km2;
                bool karst = UndergroundPlan.SolubilityAt(map, x, z) >= UndergroundPlan.KarstThreshold;
                string biome = MapBiomes.Name(MapBiomes.Classify(s));
                string elevation = Band(ElevationBands, s.Elevation);
                string moisture = Band(MoistureBands, s.Moisture);
                Add(shares.BiomeKm2, biome, km2);
                Add(shares.ElevationKm2, elevation, km2);
                Add(shares.MoistureKm2, moisture, km2);
                if (karst)
                {
                    shares.KarstKm2 += km2;
                    Add(shares.BiomeKarstKm2, biome, km2);
                    Add(shares.ElevationKarstKm2, elevation, km2);
                    Add(shares.MoistureKarstKm2, moisture, km2);
                }
            }

            shares.LandKm2 = land;
            return shares;
        }

        private static void Add(Dictionary<string, double> into, string key, double value) => into[key] = into.GetValueOrDefault(key) + value;
    }

    private static string Band((string Label, double Min, double Max)[] bands, double value) =>
        bands.First(b => value >= b.Min && value < b.Max).Label;

    /// <summary>The level's census (round 3).</summary>
    private sealed class Census(LandShares land)
    {
        internal int Systems;
        internal int MultiBox;
        internal int Boxes;
        internal int Mouths;
        internal int Links;
        internal int Shafts;
        internal int Rooms;
        internal int Conduits;
        internal int FromFloor;
        internal int ReachesBase;
        internal int RejectedForOverlap;
        internal int CutForOverlap;
        internal int OverlapPairs;
        internal int FloorClamped;
        internal int MaxBoxes;
        private readonly Dictionary<int, int> boxesHistogram = [];
        private readonly Dictionary<int, int> levelsHistogram = [];
        private readonly Dictionary<string, int> mouthKinds = [];
        private readonly Dictionary<int, int> mouthLevelsHistogram = [];
        private readonly Dictionary<string, int> styles = [];
        private readonly Dictionary<string, int> linkKinds = [];
        private readonly Dictionary<UndergroundDepthBand, int> depthBands = [];
        private readonly Dictionary<UndergroundBigSpaceKind, int> bigSpaces = [];
        private readonly List<double> topBelow = [], floorBelow = [], spans = [], tendrils = [], reliefs = [], mouthSlopes = [];
        private readonly Dictionary<string, Bucket> byBiome = [], byElevation = [], byMoisture = [], byHardness = [];
        private readonly Dictionary<string, List<double>> floorByElevation = [], spanByElevation = [];
        private readonly Dictionary<string, Dictionary<string, int>> styleByBiome = [];

        internal void Record(UndergroundSystem s)
        {
            Systems++;
            if (s.IsMultiBox) MultiBox++;
            Boxes += s.Boxes.Count;
            MaxBoxes = Math.Max(MaxBoxes, s.Boxes.Count);
            Mouths += s.Mouths.Count;
            Links += s.Links.Count;
            Shafts += s.Links.Count(l => l.Kind == UndergroundLinkKind.Shaft);
            if (s.FromRegionalFloor) FromFloor++;
            if (s.ReachesBaseLevel) ReachesBase++;
            if (s.FloorY <= -GenerationConstants.TerrainDepth + 6.01) FloorClamped++;
            Increment(boxesHistogram, s.Boxes.Count);
            Increment(levelsHistogram, s.Levels);
            Increment(mouthLevelsHistogram, s.Mouths.Select(m => m.Level).Distinct().Count());
            Increment(styles, s.Style.ToString());
            reliefs.Add(s.ReliefMetres);
            foreach (UndergroundMouth m in s.Mouths)
            {
                Increment(mouthKinds, m.Kind.ToString());
                if (m.Face != UndergroundFace.PosY)
                {
                    tendrils.Add(m.TendrilMetres);
                    mouthSlopes.Add(m.SlopeAtMouth);
                }
            }

            foreach (UndergroundLink l in s.Links) Increment(linkKinds, l.Kind.ToString());
            foreach (UndergroundBox b in s.Boxes)
            {
                Rooms += b.Rooms.Count;
                Conduits += b.Conduits.Count;
                depthBands[b.DepthBand] = depthBands.GetValueOrDefault(b.DepthBand) + 1;
                topBelow.Add(b.GroundElevation - b.TopY);
                floorBelow.Add(b.GroundElevation - b.OriginY);
            }

            foreach (UndergroundBigSpace bs in s.BigSpaces) bigSpaces[bs.Kind] = bigSpaces.GetValueOrDefault(bs.Kind) + 1;
            double span = s.TopY - s.FloorY;
            spans.Add(span);

            string biome = MapBiomes.Name(s.Biome);
            string elevation = Band(ElevationBands, s.GroundElevation);
            string moisture = Band(MoistureBands, s.Moisture);
            Get(byBiome, biome).Record(s);
            Get(byElevation, elevation).Record(s);
            Get(byMoisture, moisture).Record(s);
            Get(byHardness, HardnessBuckets.First(h => s.Hardness >= h.Min && s.Hardness < h.Max).Label).Record(s);
            if (!floorByElevation.TryGetValue(elevation, out List<double>? f)) floorByElevation[elevation] = f = [];
            f.Add(s.GroundElevation - s.FloorY);
            if (!spanByElevation.TryGetValue(elevation, out List<double>? sp)) spanByElevation[elevation] = sp = [];
            sp.Add(span);
            if (!styleByBiome.TryGetValue(biome, out Dictionary<string, int>? sb)) styleByBiome[biome] = sb = [];
            Increment(sb, s.Style.ToString());
        }

        /// <summary>Box pairs from different systems closer than the boxes keep apart, across the whole continent.</summary>
        internal void CountOverlaps(IEnumerable<UndergroundSystem> systems)
        {
            const double cell = 512;
            Dictionary<(long, long), List<(string Id, UndergroundBox Box)>> grid = [];
            foreach (UndergroundSystem s in systems)
            foreach (UndergroundBox b in s.Boxes)
            {
                long x0 = (long)Math.Floor(b.OriginX / cell), x1 = (long)Math.Floor((b.OriginX + (b.ChunksX * 16)) / cell);
                long z0 = (long)Math.Floor(b.OriginZ / cell), z1 = (long)Math.Floor((b.OriginZ + (b.ChunksZ * 16)) / cell);
                for (long gz = z0; gz <= z1; gz++)
                for (long gx = x0; gx <= x1; gx++)
                {
                    if (!grid.TryGetValue((gx, gz), out var list)) grid[(gx, gz)] = list = [];
                    list.Add((s.Id, b));
                }
            }

            HashSet<(string, string)> pairs = [];
            foreach (var list in grid.Values)
            for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                (string ia, UndergroundBox a) = list[i];
                (string ib, UndergroundBox b) = list[j];
                if (ia == ib) continue;
                bool near = a.OriginX < b.OriginX + (b.ChunksX * 16) + 8 && b.OriginX < a.OriginX + (a.ChunksX * 16) + 8
                    && a.OriginZ < b.OriginZ + (b.ChunksZ * 16) + 8 && b.OriginZ < a.OriginZ + (a.ChunksZ * 16) + 8
                    && a.OriginY < b.TopY + 8 && b.OriginY < a.TopY + 8;
                if (near) pairs.Add(string.CompareOrdinal(ia, ib) < 0 ? (ia, ib) : (ib, ia));
            }

            OverlapPairs = pairs.Count;
        }

        internal JsonObject ToJson()
        {
            JsonObject Spread(Dictionary<string, Bucket> buckets, Dictionary<string, double> landKm2, Dictionary<string, double> karstKm2, IEnumerable<string> order)
            {
                JsonObject result = [];
                foreach (string key in order)
                {
                    Bucket b = buckets.GetValueOrDefault(key) ?? new Bucket();
                    double area = landKm2.GetValueOrDefault(key);
                    result[key] = b.ToJson(Systems).With("landKm2", Math.Round(area, 0))
                        .With("landShare", land.LandKm2 > 0 ? Math.Round(area / land.LandKm2, 4) : 0.0)
                        .With("karstShareOfBand", area > 0 ? Math.Round(karstKm2.GetValueOrDefault(key) / area, 3) : 0.0)
                        .With("systemsPer1000Km2", area > 0 ? Math.Round(b.Systems * 1000.0 / area, 1) : 0.0);
                }

                return result;
            }

            JsonObject depthByElevation = [];
            foreach ((string label, _, _) in ElevationBands)
            {
                List<double> floors = floorByElevation.GetValueOrDefault(label) ?? [];
                List<double> sp = spanByElevation.GetValueOrDefault(label) ?? [];
                depthByElevation[label] = new JsonObject
                {
                    ["systems"] = floors.Count,
                    ["floorBelowGroundP50"] = Quantile(floors, 0.5),
                    ["floorBelowGroundP90"] = Quantile(floors, 0.9),
                    ["floorBelowGroundMax"] = floors.Count > 0 ? Math.Round(floors.Max(), 1) : 0,
                    ["spanP50"] = Quantile(sp, 0.5),
                    ["spanP90"] = Quantile(sp, 0.9),
                    ["spanMax"] = sp.Count > 0 ? Math.Round(sp.Max(), 1) : 0,
                };
            }

            JsonObject styleBiome = [];
            foreach (var kv in styleByBiome.OrderBy(k => k.Key, StringComparer.Ordinal)) styleBiome[kv.Key] = Dict(kv.Value);

            return new JsonObject
            {
                ["systems"] = Systems,
                ["singleBoxSystems"] = Systems - MultiBox,
                ["multiBoxSystems"] = MultiBox,
                ["multiBoxShare"] = Systems > 0 ? Math.Round((double)MultiBox / Systems, 4) : 0,
                ["totalBoxes"] = Boxes,
                ["avgBoxesPerSystem"] = Systems > 0 ? Math.Round((double)Boxes / Systems, 2) : 0,
                ["avgBoxesPerMultiSystem"] = MultiBox > 0 ? Math.Round((double)(Boxes - (Systems - MultiBox)) / MultiBox, 2) : 0,
                ["maxBoxesInSystem"] = MaxBoxes,
                ["links"] = Links,
                ["shafts"] = Shafts,
                ["mouths"] = Mouths,
                ["rooms"] = Rooms,
                ["conduits"] = Conduits,
                ["fromRegionalFloor"] = FromFloor,
                ["reachesBaseLevel"] = ReachesBase,
                ["floorClampedAtWorldFloor"] = FloorClamped,
                ["rejectedForOverlap"] = RejectedForOverlap,
                ["cutForOverlap"] = CutForOverlap,
                ["overlapPairs"] = OverlapPairs,
                ["boxesHistogram"] = Dict(boxesHistogram),
                ["levelsHistogram"] = Dict(levelsHistogram),
                ["mouthLevelsHistogram"] = Dict(mouthLevelsHistogram),
                ["mouthKinds"] = Dict(mouthKinds),
                ["linkKinds"] = Dict(linkKinds),
                ["styles"] = Dict(styles),
                ["styleByBiome"] = styleBiome,
                ["byDepthBand"] = new JsonObject
                {
                    ["Shallow"] = depthBands.GetValueOrDefault(UndergroundDepthBand.Shallow),
                    ["Mid"] = depthBands.GetValueOrDefault(UndergroundDepthBand.Mid),
                    ["Deep"] = depthBands.GetValueOrDefault(UndergroundDepthBand.Deep),
                },
                ["byBigSpace"] = new JsonObject
                {
                    ["UndergroundLake"] = bigSpaces.GetValueOrDefault(UndergroundBigSpaceKind.UndergroundLake),
                    ["FogSpace"] = bigSpaces.GetValueOrDefault(UndergroundBigSpaceKind.FogSpace),
                    ["CaveRuin"] = bigSpaces.GetValueOrDefault(UndergroundBigSpaceKind.CaveRuin),
                    ["GreatHall"] = bigSpaces.GetValueOrDefault(UndergroundBigSpaceKind.GreatHall),
                },
                ["depth"] = new JsonObject
                {
                    ["boxTopBelowGround"] = Quantiles(topBelow),
                    ["boxFloorBelowGround"] = Quantiles(floorBelow),
                    ["systemSpan"] = Quantiles(spans),
                    ["relief"] = Quantiles(reliefs),
                    ["boxFloorHistogram"] = Histogram(floorBelow),
                    ["systemSpanHistogram"] = Histogram(spans),
                    ["byElevationBand"] = depthByElevation,
                },
                ["sideMouths"] = new JsonObject
                {
                    ["tendril"] = Quantiles(tendrils),
                    ["slope"] = Quantiles(mouthSlopes),
                },
                ["land"] = new JsonObject
                {
                    ["landKm2"] = Math.Round(land.LandKm2, 0),
                    ["karstKm2"] = Math.Round(land.KarstKm2, 0),
                    ["karstShare"] = land.LandKm2 > 0 ? Math.Round(land.KarstKm2 / land.LandKm2, 3) : 0,
                    ["systemsPer1000Km2"] = land.LandKm2 > 0 ? Math.Round(Systems * 1000.0 / land.LandKm2, 1) : 0,
                    ["systemsPer1000KarstKm2"] = land.KarstKm2 > 0 ? Math.Round(Systems * 1000.0 / land.KarstKm2, 1) : 0,
                },
                ["byBiome"] = Spread(byBiome, land.BiomeKm2, land.BiomeKarstKm2, land.BiomeKm2.OrderByDescending(k => k.Value).Select(k => k.Key)),
                ["byElevationBand"] = Spread(byElevation, land.ElevationKm2, land.ElevationKarstKm2, ElevationBands.Select(b => b.Label)),
                ["byMoistureBand"] = Spread(byMoisture, land.MoistureKm2, land.MoistureKarstKm2, MoistureBands.Select(b => b.Label)),
                ["byHardness"] = Spread(byHardness, [], [], HardnessBuckets.Select(b => b.Label)),
            };
        }

        private static Bucket Get(Dictionary<string, Bucket> into, string key)
        {
            if (!into.TryGetValue(key, out Bucket? b)) into[key] = b = new Bucket();
            return b;
        }

        private static void Increment<T>(Dictionary<T, int> into, T key) where T : notnull => into[key] = into.GetValueOrDefault(key) + 1;

        private static JsonObject Dict<T>(Dictionary<T, int> from) where T : notnull
        {
            JsonObject o = [];
            foreach (var kv in from.OrderBy(k => k.Key)) o[Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!] = kv.Value;
            return o;
        }

        private static double Quantile(List<double> values, double q)
        {
            if (values.Count == 0) return 0;
            List<double> sorted = [.. values.Order()];
            return Math.Round(sorted[Math.Clamp((int)Math.Floor(q * (sorted.Count - 1)), 0, sorted.Count - 1)], 1);
        }

        private static JsonObject Quantiles(List<double> values) => new()
        {
            ["count"] = values.Count,
            ["p10"] = Quantile(values, 0.1),
            ["p50"] = Quantile(values, 0.5),
            ["p90"] = Quantile(values, 0.9),
            ["max"] = values.Count > 0 ? Math.Round(values.Max(), 1) : 0,
        };

        private static JsonObject Histogram(List<double> values)
        {
            SortedDictionary<int, int> bins = [];
            foreach (double v in values)
            {
                int bin = (int)Math.Floor(v / DepthBinMetres) * DepthBinMetres;
                bins[bin] = bins.GetValueOrDefault(bin) + 1;
            }

            JsonObject o = [];
            foreach (var kv in bins) o[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            return o;
        }
    }

    private sealed class Bucket
    {
        internal int Systems;
        private int multi, boxes, mouths, floorFilled, stacked;
        private double floorSum, solubilitySum;

        internal void Record(UndergroundSystem s)
        {
            Systems++;
            if (s.IsMultiBox) multi++;
            if (s.Levels > 1) stacked++;
            if (s.FromRegionalFloor) floorFilled++;
            boxes += s.Boxes.Count;
            mouths += s.Mouths.Count;
            floorSum += s.GroundElevation - s.FloorY;
            solubilitySum += s.Solubility;
        }

        internal JsonObject ToJson(int total) => new()
        {
            ["systems"] = Systems,
            ["systemShare"] = total > 0 ? Math.Round((double)Systems / total, 4) : 0,
            ["singleBox"] = Systems - multi,
            ["multiBox"] = multi,
            ["multiShare"] = Systems > 0 ? Math.Round((double)multi / Systems, 3) : 0.0,
            ["stacked"] = stacked,
            ["fromRegionalFloor"] = floorFilled,
            ["boxes"] = boxes,
            ["mouths"] = mouths,
            ["avgDepth"] = Systems > 0 ? Math.Round(floorSum / Systems, 1) : 0.0,
            ["avgSolubility"] = Systems > 0 ? Math.Round(solubilitySum / Systems, 3) : 0.0,
        };
    }
}

internal static class JsonObjectWith
{
    internal static JsonObject With(this JsonObject o, string key, double value)
    {
        o[key] = value;
        return o;
    }
}
