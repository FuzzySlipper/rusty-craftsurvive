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
/// Exports and benchmarks the continent-scale underground cave-system plan (#11209) across coarseness levels.
/// Measures wall time, managed memory allocation/retention, serialized disk footprint, and census breakdowns
/// (biome, hardness, depth band, single vs. multi-box split) so coarseness trade-offs are grounded in hard numbers.
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

    internal static int Run(
        WorldMap map,
        int pixels,
        string outputRoot,
        IReadOnlyList<UndergroundCoarseness> presets,
        int workers,
        Action<string> log)
    {
        string ugRoot = Path.Combine(outputRoot, "underground");
        Directory.CreateDirectory(ugRoot);

        // Export continent-wide limestone solubility raster (matches continent/elevation.f32 dimensions).
        Stopwatch rasterClock = Stopwatch.StartNew();
        WriteSolubilityRaster(map, pixels, Path.Combine(ugRoot, "solubility.f32"));
        log($"  solubility raster ({pixels}x{pixels}): {rasterClock.ElapsedMilliseconds} ms");

        // Enumerate the same 16 km tile lattice as the main golden-seed export.
        int tileHalf = (int)Math.Ceiling((map.Radius - (MapRegions.TileSpacing * 0.5)) / MapRegions.TileSpacing);
        List<(int X, int Z)> tiles = [];
        for (int tz = -tileHalf; tz <= tileHalf; tz++)
        for (int tx = -tileHalf; tx <= tileHalf; tx++)
            tiles.Add((tx, tz));

        // Optional towns from places.json so sample multi-box systems carry nearest-town labels.
        List<(string Name, double X, double Z)> towns = ReadTowns(Path.Combine(outputRoot, "places.json"));

        JsonArray levelManifests = [];
        foreach (UndergroundCoarseness preset in presets)
        {
            JsonObject report = ExportLevel(map, tiles, ugRoot, preset, workers, towns, log);
            levelManifests.Add(report);
        }

        JsonObject manifest = new()
        {
            ["format"] = 1,
            ["provisional"] = true,
            ["note"] = "CMS-only exploratory underground plan (#11209). Numbers measure pure continent-plan generation and serialization, not runtime in-game suitability.",
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
        string ugRoot,
        UndergroundCoarseness preset,
        int workers,
        IReadOnlyList<(string Name, double X, double Z)> towns,
        Action<string> log)
    {
        string levelDir = Path.Combine(ugRoot, preset.Name);
        string tilesDir = Path.Combine(levelDir, "tiles");
        string gridsDir = Path.Combine(levelDir, "grids");
        Directory.CreateDirectory(tilesDir);
        if (preset.IncludeFieldGrid) Directory.CreateDirectory(gridsDir);

        // Clean baseline before measuring allocation and live retained memory.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long heapBefore = GC.GetTotalMemory(true);
        long allocBefore = GC.GetTotalAllocatedBytes(true);

        UndergroundTilePlan[] built = new UndergroundTilePlan[tiles.Count];
        double[] tileMs = new double[tiles.Count];

        Stopwatch generateWall = Stopwatch.StartNew();
        Parallel.For(0, tiles.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
        {
            long t0 = Stopwatch.GetTimestamp();
            built[i] = UndergroundPlan.BuildTile(map, tiles[i].X, tiles[i].Z, preset);
            tileMs[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        });
        generateWall.Stop();

        long allocAfter = GC.GetTotalAllocatedBytes(true);
        // Measure retained memory while the entire continent's underground plan sits live in RAM.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long heapWithAllTiles = GC.GetTotalMemory(true);
        GC.KeepAlive(built);

        long retainedBytes = Math.Max(0, heapWithAllTiles - heapBefore);
        long allocatedBytes = Math.Max(0, allocAfter - allocBefore);

        // Serialize per-tile plans, optional 2D field grids, and continent summary while collecting census metrics (#11209 R11209-1).
        Stopwatch serializeWall = Stopwatch.StartNew();
        long rawJsonBytes = 0;
        long gzipTileBytes = 0;
        long rawGridBytes = 0;
        long gzipGridBytes = 0;
        int landTiles = 0;
        double sumLandTileMs = 0;
        double maxTileMs = 0;

        int totalSystems = 0, singleBox = 0, multiBox = 0, totalBoxes = 0, totalLinks = 0, totalMouths = 0, totalRooms = 0, totalConduits = 0;
        int maxBoxesInSystem = 0;
        Dictionary<int, int> boxesHistogram = [];
        Dictionary<MapBiome, CensusBucket> byBiome = [];
        Dictionary<string, CensusBucket> byHardness = HardnessBuckets.ToDictionary(b => b.Label, _ => new CensusBucket());
        Dictionary<UndergroundDepthBand, int> byDepthBand = new()
        {
            [UndergroundDepthBand.Shallow] = 0,
            [UndergroundDepthBand.Mid] = 0,
            [UndergroundDepthBand.Deep] = 0,
        };
        Dictionary<UndergroundBigSpaceKind, int> byBigSpace = new()
        {
            [UndergroundBigSpaceKind.UndergroundLake] = 0,
            [UndergroundBigSpaceKind.FogSpace] = 0,
            [UndergroundBigSpaceKind.CaveRuin] = 0,
            [UndergroundBigSpaceKind.GreatHall] = 0,
        };

        JsonArray tileIndex = [];
        JsonArray summarySystems = [];
        List<(UndergroundSystem System, double Score)> notableMulti = [];

        for (int i = 0; i < built.Length; i++)
        {
            UndergroundTilePlan tile = built[i];
            double ms = tileMs[i];
            if (ms > maxTileMs) maxTileMs = ms;

            bool hasData = tile.Systems.Count > 0;
            if (hasData)
            {
                landTiles++;
                sumLandTileMs += ms;
            }

            // Write optional 2D karst field grid as compact gzipped binary (solubility u8 + waterTable i16 + depthBand u8).
            if (tile.FieldGrid is UndergroundTileGrid grid && hasData)
            {
                int n = grid.Side * grid.Side;
                byte[] packed = new byte[n * 4];
                Buffer.BlockCopy(grid.Solubility, 0, packed, 0, n);
                Buffer.BlockCopy(grid.WaterTableMetres, 0, packed, n, n * 2);
                Buffer.BlockCopy(grid.DepthBand, 0, packed, n * 3, n);
                rawGridBytes += packed.Length;
                string gridPath = Path.Combine(gridsDir, $"{tile.TileX}_{tile.TileZ}.bin.gz");
                gzipGridBytes += WriteGzipBytes(gridPath, packed);
            }

            if (hasData)
            {
                string tileJson = SerializeTile(tile).ToJsonString();
                byte[] utf8 = Encoding.UTF8.GetBytes(tileJson);
                rawJsonBytes += utf8.Length;
                string tilePath = Path.Combine(tilesDir, $"{tile.TileX}_{tile.TileZ}.json.gz");
                gzipTileBytes += WriteGzipBytes(tilePath, utf8);

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
                totalSystems++;
                if (sys.IsMultiBox) multiBox++; else singleBox++;
                int bCount = sys.Boxes.Count;
                totalBoxes += bCount;
                if (bCount > maxBoxesInSystem) maxBoxesInSystem = bCount;
                boxesHistogram[bCount] = boxesHistogram.GetValueOrDefault(bCount) + 1;
                totalLinks += sys.Links.Count;
                totalMouths += sys.Mouths.Count;

                foreach (UndergroundBox box in sys.Boxes)
                {
                    byDepthBand[box.DepthBand]++;
                    totalRooms += box.Rooms.Count;
                    totalConduits += box.Conduits.Count;
                }

                foreach (UndergroundBigSpace bs in sys.BigSpaces)
                {
                    byBigSpace[bs.Kind]++;
                }

                if (!byBiome.TryGetValue(sys.Biome, out CensusBucket? biomeBucket))
                {
                    byBiome[sys.Biome] = biomeBucket = new CensusBucket();
                }
                biomeBucket.Record(sys);

                foreach (var hb in HardnessBuckets)
                {
                    if (sys.Hardness >= hb.Min && sys.Hardness < hb.Max)
                    {
                        byHardness[hb.Label].Record(sys);
                        break;
                    }
                }

                summarySystems.Add(SerializeSystemSummary(sys));

                if (sys.IsMultiBox)
                {
                    double townDist = NearestTownDistance(towns, sys.AnchorX, sys.AnchorZ);
                    // Favour rich multi-box systems with multiple mouths/big spaces and reasonable proximity to towns.
                    double score = (bCount * 2.5) + (sys.Links.Count * 1.5) + (sys.Mouths.Count * 2.0) + (sys.BigSpaces.Count * 1.8) + (sys.Solubility * 2.0) - (townDist / 120_000.0);
                    notableMulti.Add((sys, score));
                }
            }
        }

        WriteGzipBytes(Path.Combine(levelDir, "tiles-index.json.gz"), Encoding.UTF8.GetBytes(tileIndex.ToJsonString()));
        JsonObject summaryPayload = new()
        {
            ["level"] = preset.Name,
            ["systems"] = summarySystems,
        };
        byte[] summaryUtf8 = Encoding.UTF8.GetBytes(summaryPayload.ToJsonString());
        long summaryGzipBytes = WriteGzipBytes(Path.Combine(levelDir, "summary.json.gz"), summaryUtf8);
        serializeWall.Stop();

        double generateMs = generateWall.Elapsed.TotalMilliseconds;
        double serializeMs = serializeWall.Elapsed.TotalMilliseconds;
        double exportMs = generateMs + serializeMs;

        JsonArray sampleArray = [];
        foreach (var item in notableMulti.OrderByDescending(x => x.Score).Take(16))
        {
            UndergroundSystem s = item.System;
            (string townName, double townKm) = NearestTown(towns, s.AnchorX, s.AnchorZ);
            sampleArray.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["tile"] = new JsonArray(s.TileX, s.TileZ),
                ["x"] = s.AnchorX,
                ["z"] = s.AnchorZ,
                ["boxes"] = s.Boxes.Count,
                ["links"] = s.Links.Count,
                ["mouths"] = s.Mouths.Count,
                ["bigSpaces"] = new JsonArray([.. s.BigSpaces.Select(b => (JsonNode)JsonValue.Create(b.Kind.ToString())!)]),
                ["biome"] = MapBiomes.Name(s.Biome),
                ["maxDepth"] = s.MaxDepthMetres,
                ["solubility"] = s.Solubility,
                ["nearestTown"] = townName,
                ["nearestTownKm"] = Math.Round(townKm, 1),
            });
        }

        double multiShare = totalSystems > 0 ? (double)multiBox / totalSystems : 0.0;
        double avgBoxes = totalSystems > 0 ? (double)totalBoxes / totalSystems : 0.0;
        double avgMultiBoxes = multiBox > 0 ? (double)(totalBoxes - singleBox) / multiBox : 0.0;

        JsonObject biomeJson = [];
        foreach (var pair in byBiome.OrderByDescending(p => p.Value.Systems))
        {
            biomeJson[MapBiomes.Name(pair.Key)] = pair.Value.ToJson();
        }

        JsonObject hardnessJson = [];
        foreach (var hb in HardnessBuckets)
        {
            hardnessJson[hb.Label] = byHardness[hb.Label].ToJson();
        }

        JsonObject histJson = [];
        foreach (var kv in boxesHistogram.OrderBy(k => k.Key))
        {
            histJson[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
        }

        JsonObject depthJson = [];
        foreach (var kv in byDepthBand) depthJson[kv.Key.ToString()] = kv.Value;

        JsonObject bigSpaceJson = [];
        foreach (var kv in byBigSpace) bigSpaceJson[kv.Key.ToString()] = kv.Value;

        log(string.Create(CultureInfo.InvariantCulture,
            $"  level {preset.Name,-8} | export {exportMs,6:F0} ms (gen {generateMs,5:F0} + io {serializeMs,5:F0}) | retained {retainedBytes / (1024.0 * 1024.0),5:F1} MB | alloc {allocatedBytes / (1024.0 * 1024.0),6:F1} MB | disk {(gzipTileBytes + gzipGridBytes) / (1024.0 * 1024.0),5:F2} MB gz | {totalSystems,6} systems ({multiShare * 100,4:F1}% multi, {totalBoxes,6} boxes, {totalMouths,6} mouths)"));

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
                ["rawJsonMB"] = Math.Round(rawJsonBytes / (1024.0 * 1024.0), 2),
                ["gzipTilesMB"] = Math.Round(gzipTileBytes / (1024.0 * 1024.0), 2),
                ["rawGridMB"] = Math.Round(rawGridBytes / (1024.0 * 1024.0), 2),
                ["gzipGridMB"] = Math.Round(gzipGridBytes / (1024.0 * 1024.0), 2),
                ["totalGzipMB"] = Math.Round((gzipTileBytes + gzipGridBytes) / (1024.0 * 1024.0), 2),
                ["summaryGzipMB"] = Math.Round(summaryGzipBytes / (1024.0 * 1024.0), 2),
            },
            ["census"] = new JsonObject
            {
                ["systems"] = totalSystems,
                ["singleBoxSystems"] = singleBox,
                ["multiBoxSystems"] = multiBox,
                ["multiBoxShare"] = Math.Round(multiShare, 4),
                ["totalBoxes"] = totalBoxes,
                ["avgBoxesPerSystem"] = Math.Round(avgBoxes, 2),
                ["avgBoxesPerMultiSystem"] = Math.Round(avgMultiBoxes, 2),
                ["maxBoxesInSystem"] = maxBoxesInSystem,
                ["links"] = totalLinks,
                ["mouths"] = totalMouths,
                ["rooms"] = totalRooms,
                ["conduits"] = totalConduits,
                ["boxesHistogram"] = histJson,
                ["byDepthBand"] = depthJson,
                ["byBigSpace"] = bigSpaceJson,
                ["byBiome"] = biomeJson,
                ["byHardness"] = hardnessJson,
            },
            ["sampleMultiSystems"] = sampleArray,
        };
    }

    private static JsonObject SerializeTile(UndergroundTilePlan tile)
    {
        JsonArray systems = [];
        foreach (UndergroundSystem s in tile.Systems)
        {
            systems.Add(SerializeSystemFull(s));
        }

        JsonObject obj = new()
        {
            ["tile"] = new JsonArray(tile.TileX, tile.TileZ),
            ["level"] = tile.CoarsenessName,
            ["systems"] = systems,
        };
        if (tile.FieldGrid is UndergroundTileGrid g)
        {
            obj["fieldGrid"] = new JsonObject
            {
                ["side"] = g.Side,
                ["spacingMetres"] = g.SpacingMetres,
                ["file"] = $"grids/{tile.TileX}_{tile.TileZ}.bin.gz",
            };
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
                conduits.Add(new JsonObject
                {
                    ["from"] = c.FromRoom,
                    ["to"] = c.ToRoom,
                    ["radius"] = c.Radius,
                    ["water"] = c.WaterLevel,
                    ["loop"] = c.IsLoop,
                });
            }

            boxes.Add(new JsonObject
            {
                ["index"] = b.Index,
                ["grid"] = new JsonArray(b.GridX, b.GridZ),
                ["origin"] = new JsonArray(b.OriginX, b.OriginY, b.OriginZ),
                ["chunks"] = new JsonArray(b.ChunksX, b.ChunksY, b.ChunksZ),
                ["size"] = new JsonArray(b.ChunksX * 16, b.ChunksY * 16, b.ChunksZ * 16),
                ["ground"] = b.GroundElevation,
                ["depth"] = b.DepthBelowSurface,
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
                ["at"] = new JsonArray(m.WorldX, m.WorldY, m.WorldZ),
                ["local"] = new JsonArray(m.LocalEntryX, m.LocalEntryZ),
                ["kind"] = m.Kind.ToString(),
            });
        }

        JsonArray bigSpaces = [];
        foreach (UndergroundBigSpace bs in s.BigSpaces)
        {
            bigSpaces.Add(new JsonObject
            {
                ["box"] = bs.BoxIndex,
                ["kind"] = bs.Kind.ToString(),
                ["local"] = new JsonArray(bs.LocalX, bs.LocalY, bs.LocalZ),
                ["radius"] = bs.RadiusMetres,
            });
        }

        return new JsonObject
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
            ["multi"] = s.IsMultiBox,
            ["boxes"] = boxes,
            ["links"] = links,
            ["mouths"] = mouths,
            ["bigSpaces"] = bigSpaces,
        };
    }

    private static JsonObject SerializeSystemSummary(UndergroundSystem s)
    {
        JsonArray boxes = [];
        foreach (UndergroundBox b in s.Boxes)
        {
            boxes.Add(new JsonArray(b.OriginX, b.OriginZ, b.ChunksX * 16, b.ChunksZ * 16, (int)b.DepthBand));
        }

        JsonArray links = [];
        foreach (UndergroundLink l in s.Links)
        {
            UndergroundBox a = s.Boxes[l.FromBox], b = s.Boxes[l.ToBox];
            links.Add(new JsonArray(
                Math.Round(a.OriginX + (a.ChunksX * 8.0), 1),
                Math.Round(a.OriginZ + (a.ChunksZ * 8.0), 1),
                Math.Round(b.OriginX + (b.ChunksX * 8.0), 1),
                Math.Round(b.OriginZ + (b.ChunksZ * 8.0), 1),
                (int)l.Kind));
        }

        JsonArray mouths = [];
        foreach (UndergroundMouth m in s.Mouths)
        {
            mouths.Add(new JsonArray(m.WorldX, m.WorldZ, (int)m.Kind));
        }

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
            ["d"] = s.MaxDepthMetres,
            ["s"] = s.Solubility,
            ["h"] = s.Hardness,
            ["b"] = MapBiomes.Name(s.Biome),
            ["m"] = s.IsMultiBox ? 1 : 0,
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
                MapSample s = map.Broad(x, z);
                values[(row * pixels) + col] = s.Elevation < GenerationConstants.WaterLevel
                    ? 0f
                    : (float)UndergroundPlan.SolubilityAt(map, x, z);
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
            double dx = t.X - x, dz = t.Z - z;
            double d = Math.Sqrt((dx * dx) + (dz * dz));
            if (d < bestDist) { bestDist = d; bestName = t.Name; }
        }
        return bestDist < double.MaxValue ? (bestName, bestDist / 1000.0) : ("", 0.0);
    }

    private static double NearestTownDistance(IReadOnlyList<(string Name, double X, double Z)> towns, double x, double z)
    {
        double best = 50_000.0;
        foreach (var t in towns)
        {
            double dx = t.X - x, dz = t.Z - z;
            double d = Math.Sqrt((dx * dx) + (dz * dz));
            if (d < best) best = d;
        }
        return best;
    }

    private static void PrintComparisonTable(JsonArray levels, Action<string> log)
    {
        log("");
        log("Underground Plan Coarseness Comparison (provisional, CMS-only):");
        log("| Level | Lattice | Box | Export ms (Gen + IO) | Retained RAM | Gzip Disk | Systems | Multi % | Boxes | Mouths | Links | Rooms |");
        log("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (JsonNode? node in levels)
        {
            if (node is not JsonObject o) continue;
            var st = o["settings"]!.AsObject();
            var m = o["metrics"]!.AsObject();
            var c = o["census"]!.AsObject();
            var bs = st["boxSize"]!.AsArray();
            log(string.Create(CultureInfo.InvariantCulture,
                $"| {o["name"]} | {st["siteSpacingMetres"]} m | {bs[0]}x{bs[1]}x{bs[2]} | {m["exportMs"]} ({m["generateMs"]}+{m["serializeMs"]}) | {m["retainedInMemoryMB"]} MB | {m["totalGzipMB"]} MB | {c["systems"]} | {c["multiBoxShare"]!.GetValue<double>() * 100:F1}% | {c["totalBoxes"]} | {c["mouths"]} | {c["links"]} | {c["rooms"]} |"));
        }
    }

    /// <summary>Verifies every link lies strictly inside both boxes' vertical interiors (#11209 R11209-2).</summary>
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
            double aTop = a.OriginY + (a.ChunksY * 16.0);
            double bTop = b.OriginY + (b.ChunksY * 16.0);
            double localA = l.WorldY - a.OriginY;
            double localB = l.WorldY - b.OriginY;
            if (l.WorldY < a.OriginY + 5.0 || l.WorldY > aTop - 5.0 || l.WorldY < b.OriginY + 5.0 || l.WorldY > bTop - 5.0)
            {
                error = $"{sys.Id}: link {l.FromBox}->{l.ToBox} Y={l.WorldY:F1} outside box A [{a.OriginY:F1}..{aTop:F1}] or B [{b.OriginY:F1}..{bTop:F1}] (local {localA:F1}/{localB:F1})";
                return false;
            }
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
        // Include steep alpine tile (-5, -1) alongside (0, 0) to stress vertical box-link overlap (#11209 R11209-2).
        (int X, int Z)[] sampleTiles = [(0, 0), (-5, -1), (-6, 1)];
        foreach (UndergroundCoarseness preset in UndergroundPlan.Presets)
        {
            int totalSys = 0, totalBoxes = 0, totalLinks = 0;
            bool sameAll = true, boundedAll = true, verticalAll = true;
            string? firstError = null;
            foreach ((int tx, int tz) in sampleTiles)
            {
                UndergroundTilePlan a = UndergroundPlan.BuildTile(map, tx, tz, preset);
                UndergroundTilePlan b = UndergroundPlan.BuildTile(map, tx, tz, preset);
                totalSys += a.Systems.Count;
                totalBoxes += a.Systems.Sum(s => s.Boxes.Count);
                totalLinks += a.Systems.Sum(s => s.Links.Count);
                sameAll &= a.Systems.Count == b.Systems.Count && a.Systems.Zip(b.Systems).All(pair =>
                    pair.First.Id == pair.Second.Id
                    && pair.First.Boxes.Count == pair.Second.Boxes.Count
                    && pair.First.Links.Count == pair.Second.Links.Count);
                boundedAll &= a.Systems.All(s =>
                    s.Boxes.Count >= 1
                    && s.Boxes.Count <= preset.MaxBoxesPerSystem
                    && s.Mouths.Count >= 1);
                foreach (UndergroundSystem s in a.Systems)
                {
                    if (!LinksVerticallyValid(s, out string? err))
                    {
                        verticalAll = false;
                        firstError ??= err;
                    }
                }
            }
            bool held = sameAll && boundedAll && verticalAll;
            ok &= held;
            report(FormattableString.Invariant(
                $"{(held ? "ok" : "FAILED")}: underground plan {preset.Name} (3 tiles incl. steep -5,-1) -> {totalSys} systems, {totalBoxes} boxes, {totalLinks} links, deterministic={sameAll}, bounded={boundedAll}, verticalOverlap={verticalAll}{(firstError is not null ? $" ({firstError})" : "")}"));
        }

        return ok;
    }

    private sealed class CensusBucket
    {
        internal int Systems { get; private set; }
        internal int SingleBox { get; private set; }
        internal int MultiBox { get; private set; }
        internal int Boxes { get; private set; }
        internal int Mouths { get; private set; }
        private double depthSum;
        private double solubilitySum;

        internal void Record(UndergroundSystem s)
        {
            Systems++;
            if (s.IsMultiBox) MultiBox++; else SingleBox++;
            Boxes += s.Boxes.Count;
            Mouths += s.Mouths.Count;
            depthSum += s.MaxDepthMetres;
            solubilitySum += s.Solubility;
        }

        internal JsonObject ToJson() => new()
        {
            ["systems"] = Systems,
            ["singleBox"] = SingleBox,
            ["multiBox"] = MultiBox,
            ["multiShare"] = Systems > 0 ? Math.Round((double)MultiBox / Systems, 3) : 0.0,
            ["boxes"] = Boxes,
            ["mouths"] = Mouths,
            ["avgDepth"] = Systems > 0 ? Math.Round(depthSum / Systems, 1) : 0.0,
            ["avgSolubility"] = Systems > 0 ? Math.Round(solubilitySum / Systems, 3) : 0.0,
        };
    }
}
