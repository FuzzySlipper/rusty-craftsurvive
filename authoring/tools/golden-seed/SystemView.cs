using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.WorldGen;

namespace GoldenSeed;

/// <summary>
/// Generates multi-box karst cave detail directly from an <see cref="UndergroundSystem"/> plan (#11209, CMS-only).
/// Each box is generated with <see cref="KarstCave.Generate"/> using the plan's shared-face <see cref="KarstCave.BoundaryPort"/>s,
/// stitched into one combined 3D voxel volume, flood-filled across shared faces, and checked for seam alignment.
/// </summary>
internal static class SystemView
{
    private const float CellMetres = 1f;
    private const float SeamProbeInset = 0.35f;
    private const float SeamSampleStep = 0.5f;
    private static readonly (int X, int Y, int Z)[] Faces6 = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    private const byte Rock = 0;
    private const byte Narrow = 1;
    private const byte CutOff = 2;
    private const byte Tight = 3;
    private const byte Crawl = 4;
    private const byte Stand = 5;

    internal static string Make(JsonObject systemJson, string level)
    {
        Stopwatch totalClock = Stopwatch.StartNew();
        string id = systemJson["id"]!.GetValue<string>();
        double temperature = systemJson["temperature"]!.GetValue<double>();
        double moisture = systemJson["moisture"]!.GetValue<double>();

        JsonArray boxesJson = systemJson["boxes"]!.AsArray();
        JsonArray linksJson = systemJson["links"]!.AsArray();
        JsonArray mouthsJson = systemJson["mouths"]!.AsArray();
        JsonArray bigSpacesJson = systemJson["bigSpaces"]!.AsArray();

        // Parse planned boxes and find combined world bounding box.
        List<PlannedBox> boxes = [];
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (JsonNode? node in boxesJson)
        {
            JsonObject b = node!.AsObject();
            JsonArray origin = b["origin"]!.AsArray();
            JsonArray chunks = b["chunks"]!.AsArray();
            int cx = chunks[0]!.GetValue<int>(), cy = chunks[1]!.GetValue<int>(), cz = chunks[2]!.GetValue<int>();
            double ox = origin[0]!.GetValue<double>(), oy = origin[1]!.GetValue<double>(), oz = origin[2]!.GetValue<double>();
            float sx = cx * 16f, sy = cy * 16f, sz = cz * 16f;
            minX = Math.Min(minX, ox); minY = Math.Min(minY, oy); minZ = Math.Min(minZ, oz);
            maxX = Math.Max(maxX, ox + sx); maxY = Math.Max(maxY, oy + sy); maxZ = Math.Max(maxZ, oz + sz);
            boxes.Add(new PlannedBox(
                b["index"]!.GetValue<int>(),
                ox, oy, oz, cx, cy, cz, sx, sy, sz,
                b["depth"]!.GetValue<float>(),
                b["waterLocalY"]!.GetValue<float>(),
                ulong.Parse(b["seed"]!.GetValue<string>(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }

        // Parse links and build per-box BoundaryPort lists.
        List<PlannedLink> links = [];
        Dictionary<int, List<KarstCave.BoundaryPort>> portsByBox = boxes.ToDictionary(b => b.Index, _ => new List<KarstCave.BoundaryPort>());
        for (int i = 0; i < linksJson.Count; i++)
        {
            JsonObject l = linksJson[i]!.AsObject();
            int from = l["from"]!.GetValue<int>(), to = l["to"]!.GetValue<int>();
            KarstCave.BoxFace fromFace = Enum.Parse<KarstCave.BoxFace>(l["fromFace"]!.GetValue<string>());
            KarstCave.BoxFace toFace = Enum.Parse<KarstCave.BoxFace>(l["toFace"]!.GetValue<string>());
            float faceU = l["faceU"]!.GetValue<float>();
            JsonArray at = l["at"]!.AsArray();
            float wx = at[0]!.GetValue<float>(), wy = at[1]!.GetValue<float>(), wz = at[2]!.GetValue<float>();
            float radius = l["radius"]!.GetValue<float>();
            float waterWorldY = l["waterY"]!.GetValue<float>();
            string kind = l["kind"]!.GetValue<string>();
            ulong seamSeed = ulong.Parse(l["seamSeed"]!.GetValue<string>(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            PlannedBox boxA = boxes[from], boxB = boxes[to];
            float localVA = wy - (float)boxA.OriginY;
            float localVB = wy - (float)boxB.OriginY;
            if (localVA < 4f || localVA > boxA.SizeY - 4f || localVB < 4f || localVB > boxB.SizeY - 4f)
            {
                throw new InvalidOperationException(
                    $"Link L{i} ({from}<->{to}) at world Y={wy:F1} lies outside box A [{boxA.OriginY:F1}..{boxA.OriginY + boxA.SizeY:F1}] or box B [{boxB.OriginY:F1}..{boxB.OriginY + boxB.SizeY:F1}].");
            }
            float localWaterA = waterWorldY - (float)boxA.OriginY;
            float localWaterB = waterWorldY - (float)boxB.OriginY;

            portsByBox[from].Add(new KarstCave.BoundaryPort(fromFace, faceU, localVA, radius, localWaterA, seamSeed, $"L{i}:{from}->{to}"));
            portsByBox[to].Add(new KarstCave.BoundaryPort(toFace, faceU, localVB, radius, localWaterB, seamSeed, $"L{i}:{to}->{from}"));
            links.Add(new PlannedLink(i, from, to, fromFace, toFace, faceU, localVA, localVB, wx, wy, wz, radius, waterWorldY, kind));
        }

        // Map surface mouths to boxes so KarstCave places its first sink at the planned mouth.
        Dictionary<int, Vector2> mouthByBox = [];
        List<(int Box, Vector3 SystemLocal, string Kind)> mouths = [];
        foreach (JsonNode? mNode in mouthsJson)
        {
            JsonObject m = mNode!.AsObject();
            int bi = m["box"]!.GetValue<int>();
            JsonArray local = m["local"]!.AsArray();
            float lx = local[0]!.GetValue<float>(), lz = local[1]!.GetValue<float>();
            mouthByBox.TryAdd(bi, new Vector2(lx, lz));
            PlannedBox b = boxes[bi];
            mouths.Add((bi, new Vector3((float)(b.OriginX - minX) + lx, (float)(b.OriginY - minY) + (b.SizeY - 4f), (float)(b.OriginZ - minZ) + lz), m["kind"]!.GetValue<string>()));
        }

        // Generate each box with KarstCave using its planned ports and water table.
        List<GeneratedBox> generated = new(boxes.Count);
        double totalGenerateMs = 0;
        foreach (PlannedBox b in boxes)
        {
            Stopwatch boxClock = Stopwatch.StartNew();
            KarstCave.Settings settings = new(b.ChunksX, b.ChunksY, b.ChunksZ)
            {
                Underground = true,
                NoCommunities = true,
                NoRuin = true,
                WayIn = mouthByBox.TryGetValue(b.Index, out Vector2 way) ? way : null,
                BoundaryPorts = portsByBox[b.Index],
                WaterTableTarget = b.WaterLocalY,
            };
            CaveSetting caveSetting = new(temperature, moisture, b.Depth);
            (DungeonLayout layout, _, DungeonVerdict verdict, _, KarstCave.Report report) = KarstCave.Generate(b.Seed, 1d, null, caveSetting, settings);
            boxClock.Stop();
            totalGenerateMs += boxClock.Elapsed.TotalMilliseconds;
            generated.Add(new GeneratedBox(b, layout, verdict.Reason, report, boxClock.Elapsed.TotalMilliseconds));
        }

        // Build unified 1 m grid across the entire multi-box system.
        int gx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / CellMetres));
        int gy = Math.Max(1, (int)Math.Ceiling((maxY - minY) / CellMetres));
        int gz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / CellMetres));
        int cellCount = gx * gy * gz;
        float[] values = new float[cellCount];
        Array.Fill(values, -2f);
        short[] ownerBox = new short[cellCount];
        Array.Fill(ownerBox, (short)-1);

        foreach (GeneratedBox gb in generated)
        {
            PlannedBox b = gb.Box;
            Func<Vector3, float> shape = gb.Layout.Shape!;
            int ox = (int)Math.Round((b.OriginX - minX) / CellMetres);
            int oy = (int)Math.Round((b.OriginY - minY) / CellMetres);
            int oz = (int)Math.Round((b.OriginZ - minZ) / CellMetres);
            int bx = (int)Math.Round(b.SizeX / CellMetres);
            int by = (int)Math.Round(b.SizeY / CellMetres);
            int bz = (int)Math.Round(b.SizeZ / CellMetres);

            Parallel.For(0, bz, z =>
            {
                int wz = oz + z;
                if (wz < 0 || wz >= gz) return;
                for (int y = 0; y < by; y++)
                {
                    int wy = oy + y;
                    if (wy < 0 || wy >= gy) continue;
                    for (int x = 0; x < bx; x++)
                    {
                        int wx = ox + x;
                        if (wx < 0 || wx >= gx) continue;
                        Vector3 local = new((x + 0.5f) * CellMetres, (y + 0.5f) * CellMetres, (z + 0.5f) * CellMetres);
                        float v = shape(local);
                        int idx = (((wz * gy) + wy) * gx) + wx;
                        if (ownerBox[idx] < 0 || v > values[idx])
                        {
                            values[idx] = v;
                            ownerBox[idx] = (short)b.Index;
                        }
                    }
                }
            });
        }

        // Compute openness, exact 3D EDT clearance (matching CaveView.Clearance), and unified cross-box BFS reachability from box 0's way in.
        bool[] open = new bool[cellCount];
        for (int i = 0; i < cellCount; i++) open[i] = values[i] >= 0f;
        float[] clearance = CaveView.Clearance(values, open, gx, gy, gz, CellMetres);

        // Seed flood fill at box 0's main-route start (the real way in, shifted into system-local coordinates).
        PlannedBox firstBox = boxes[0];
        Vector3 boxStart = generated[0].Layout.MainRoute.Count > 0
            ? generated[0].Layout.MainRoute[0]
            : new Vector3(firstBox.SizeX * 0.5f, firstBox.SizeY * 0.65f, firstBox.SizeZ * 0.5f);
        Vector3 startLocal = boxStart + new Vector3(
            (float)(firstBox.OriginX - minX),
            (float)(firstBox.OriginY - minY),
            (float)(firstBox.OriginZ - minZ));
        int seedCell = CaveView.Seed(startLocal, open, clearance, gx, gy, gz, CellMetres);
        if (seedCell < 0) seedCell = FindSeed(startLocal, open, clearance, gx, gy, gz);

        bool[] reachedCrawl = Flood(seedCell, clearance, KarstCave.CrawlRadius, gx, gy, gz);
        bool[] reachedTight = Flood(seedCell, clearance, KarstCave.CrawlRadius * 0.65f, gx, gy, gz);
        bool[] reachedAny = Flood(seedCell, open, gx, gy, gz);

        byte[] state = new byte[cellCount];
        int openCells = 0, standCells = 0, crawlCells = 0, tightCells = 0, narrowCells = 0, cutOffCells = 0;
        // Keep strict crawl-reached boxes separate from tight-only reached boxes (#11209 R11209-3).
        (HashSet<int> boxesReachedCrawl, HashSet<int> boxesReachedTight) = ClassifyReachedBoxes(open, reachedCrawl, reachedTight, ownerBox);
        for (int i = 0; i < cellCount; i++)
        {
            if (!open[i]) continue;
            openCells++;
            if (reachedCrawl[i])
            {
                if (clearance[i] >= KarstCave.StandRadius) { state[i] = Stand; standCells++; }
                else { state[i] = Crawl; crawlCells++; }
            }
            else if (reachedTight[i])
            {
                state[i] = Tight;
                tightCells++;
            }
            else if (reachedAny[i])
            {
                state[i] = Narrow;
                narrowCells++;
            }
            else
            {
                state[i] = CutOff;
                cutOffCells++;
            }
        }

        // Project 2D plan and side sections (same format as CaveView for instant visual inspection).
        byte[] planState = new byte[gx * gz];
        short[] planTop = new short[gx * gz];
        Array.Fill(planTop, (short)-1);
        byte[] sideX = new byte[gx * gy];
        byte[] sideZ = new byte[gz * gy];
        for (int z = 0; z < gz; z++)
        for (int y = 0; y < gy; y++)
        for (int x = 0; x < gx; x++)
        {
            byte st = state[(((z * gy) + y) * gx) + x];
            if (st == Rock) continue;
            int pi = (z * gx) + x;
            if (st > planState[pi]) planState[pi] = st;
            if (st >= Tight && y > planTop[pi]) planTop[pi] = (short)y;
            int xi = (y * gx) + x;
            if (st > sideX[xi]) sideX[xi] = st;
            int zi = (y * gz) + z;
            if (st > sideZ[zi]) sideZ[zi] = st;
        }

        // Measure exact seam alignment and true cross-face traversal across every shared-face link (#11209 R11209-3).
        JsonArray seamReports = [];
        int seamsMatched = 0;
        int seamsTraversedCrawl = 0;
        int seamsTraversedTight = 0;
        double sumOverlap = 0;
        foreach (PlannedLink l in links)
        {
            SeamMetrics seam = MeasureSeam(generated[l.From], generated[l.To], l, reachedCrawl, reachedTight, ownerBox, gx, gy, gz, minX, minY, minZ);
            if (seam.Overlap >= 0.70 && seam.ConnectedBothSides) seamsMatched++;
            if (seam.ReachedCrawl) seamsTraversedCrawl++;
            if (seam.ReachedTight) seamsTraversedTight++;
            sumOverlap += seam.Overlap;
            seamReports.Add(new JsonObject
            {
                ["index"] = l.Index,
                ["from"] = l.From,
                ["to"] = l.To,
                ["kind"] = l.Kind,
                ["radius"] = l.Radius,
                ["at"] = new JsonArray(Math.Round(l.WorldX - minX, 1), Math.Round(l.WorldY - minY, 1), Math.Round(l.WorldZ - minZ, 1)),
                ["openShareA"] = Math.Round(seam.OpenShareA, 3),
                ["openShareB"] = Math.Round(seam.OpenShareB, 3),
                ["overlapIoU"] = Math.Round(seam.Overlap, 3),
                ["connectedBothSides"] = seam.ConnectedBothSides,
                ["seamReachedCrawl"] = seam.ReachedCrawl,
                ["seamReachedTight"] = seam.ReachedTight,
            });
        }

        // Package per-box outlines, routes, and generation timings in system-local coordinates.
        JsonArray boxReports = [];
        JsonArray allRoutes = [];
        foreach (GeneratedBox gb in generated)
        {
            PlannedBox b = gb.Box;
            float ox = (float)(b.OriginX - minX), oy = (float)(b.OriginY - minY), oz = (float)(b.OriginZ - minZ);
            JsonArray routePts = [];
            foreach (Vector3 pt in gb.Layout.MainRoute)
            {
                routePts.Add(new JsonArray(Math.Round(pt.X + ox, 1), Math.Round(pt.Y + oy, 1), Math.Round(pt.Z + oz, 1)));
            }
            allRoutes.Add(routePts);
            boxReports.Add(new JsonObject
            {
                ["index"] = b.Index,
                ["offset"] = new JsonArray(Math.Round(ox, 1), Math.Round(oy, 1), Math.Round(oz, 1)),
                ["size"] = new JsonArray(b.SizeX, b.SizeY, b.SizeZ),
                ["depth"] = b.Depth,
                ["waterLocalY"] = b.WaterLocalY,
                ["generateMs"] = Math.Round(gb.GenerateMs, 1),
                ["reachedCrawl"] = boxesReachedCrawl.Contains(b.Index),
                ["reachedTight"] = boxesReachedTight.Contains(b.Index),
                ["verdict"] = gb.Verdict,
            });
        }

        JsonArray mouthMarkers = [];
        foreach (var m in mouths)
        {
            mouthMarkers.Add(new JsonObject
            {
                ["box"] = m.Box,
                ["kind"] = m.Kind,
                ["at"] = new JsonArray(Math.Round(m.SystemLocal.X, 1), Math.Round(m.SystemLocal.Y, 1), Math.Round(m.SystemLocal.Z, 1)),
            });
        }

        JsonArray bigMarkers = [];
        foreach (JsonNode? bsNode in bigSpacesJson)
        {
            JsonObject bs = bsNode!.AsObject();
            int bi = bs["box"]!.GetValue<int>();
            PlannedBox b = boxes[bi];
            JsonArray loc = bs["local"]!.AsArray();
            bigMarkers.Add(new JsonObject
            {
                ["box"] = bi,
                ["kind"] = bs["kind"]!.GetValue<string>(),
                ["radius"] = bs["radius"]!.GetValue<float>(),
                ["at"] = new JsonArray(
                    Math.Round((b.OriginX - minX) + loc[0]!.GetValue<double>(), 1),
                    Math.Round((b.OriginY - minY) + loc[1]!.GetValue<double>(), 1),
                    Math.Round((b.OriginZ - minZ) + loc[2]!.GetValue<double>(), 1)),
            });
        }

        byte[] topBytes = new byte[planTop.Length * sizeof(short)];
        Buffer.BlockCopy(planTop, 0, topBytes, 0, topBytes.Length);
        totalClock.Stop();

        JsonObject payload = new()
        {
            ["id"] = id,
            ["level"] = level,
            ["origin"] = new JsonArray(Math.Round(minX, 1), Math.Round(minY, 1), Math.Round(minZ, 1)),
            ["size"] = new JsonArray(gx, gy, gz),
            ["step"] = CellMetres,
            ["start"] = new JsonArray(Math.Round(startLocal.X, 1), Math.Round(startLocal.Y, 1), Math.Round(startLocal.Z, 1)),
            ["generateMs"] = Math.Round(totalGenerateMs, 1),
            ["totalMs"] = Math.Round(totalClock.Elapsed.TotalMilliseconds, 1),
            ["stats"] = new JsonObject
            {
                ["boxes"] = boxes.Count,
                ["boxesReachedCrawl"] = boxesReachedCrawl.Count,
                ["boxesReachedTight"] = boxesReachedTight.Count,
                ["links"] = links.Count,
                ["seamsMatched"] = seamsMatched,
                ["seamsTraversedCrawl"] = seamsTraversedCrawl,
                ["seamsTraversedTight"] = seamsTraversedTight,
                ["avgSeamOverlapIoU"] = links.Count > 0 ? Math.Round(sumOverlap / links.Count, 3) : 1.0,
                ["openCubicMetres"] = openCells,
                ["standCubicMetres"] = standCells,
                ["crawlCubicMetres"] = crawlCells,
                ["tightCubicMetres"] = tightCells,
                ["narrowCubicMetres"] = narrowCells,
                ["cutOffCubicMetres"] = cutOffCells,
            },
            ["boxes"] = boxReports,
            ["seams"] = seamReports,
            ["routes"] = allRoutes,
            ["mouths"] = mouthMarkers,
            ["bigSpaces"] = bigMarkers,
            ["plan"] = new JsonObject
            {
                ["state"] = Convert.ToBase64String(planState),
                ["top"] = Convert.ToBase64String(topBytes),
            },
            ["sideX"] = Convert.ToBase64String(sideX),
            ["sideZ"] = Convert.ToBase64String(sideZ),
            ["cells"] = new JsonObject
            {
                ["metres"] = CellMetres,
                ["size"] = new JsonArray(gx, gy, gz),
                ["state"] = Convert.ToBase64String(state),
            },
        };

        return payload.ToJsonString();
    }

    private static SeamMetrics MeasureSeam(
        GeneratedBox boxA,
        GeneratedBox boxB,
        PlannedLink link,
        bool[] reachedCrawl,
        bool[] reachedTight,
        short[] ownerBox,
        int gx,
        int gy,
        int gz,
        double minX,
        double minY,
        double minZ)
    {
        Func<Vector3, float> shapeA = boxA.Layout.Shape!;
        Func<Vector3, float> shapeB = boxB.Layout.Shape!;
        bool xFace = link.FromFace is KarstCave.BoxFace.PosX or KarstCave.BoxFace.NegX;
        float xA = link.FromFace == KarstCave.BoxFace.PosX ? boxA.Box.SizeX - SeamProbeInset : SeamProbeInset;
        float xB = link.ToFace == KarstCave.BoxFace.PosX ? boxB.Box.SizeX - SeamProbeInset : SeamProbeInset;
        float zA = link.FromFace == KarstCave.BoxFace.PosZ ? boxA.Box.SizeZ - SeamProbeInset : SeamProbeInset;
        float zB = link.ToFace == KarstCave.BoxFace.PosZ ? boxB.Box.SizeZ - SeamProbeInset : SeamProbeInset;

        float r = MathF.Max(link.Radius, KarstCave.StandRadius + 0.65f);
        int samples = 0, openA = 0, openB = 0, both = 0, either = 0;

        for (float dv = -r; dv <= r; dv += SeamSampleStep)
        for (float du = -r; du <= r; du += SeamSampleStep)
        {
            if ((du * du) + (dv * dv) > r * r) continue;
            samples++;
            Vector3 pA = xFace ? new Vector3(xA, link.LocalVA + dv, link.FaceU + du) : new Vector3(link.FaceU + du, link.LocalVA + dv, zA);
            Vector3 pB = xFace ? new Vector3(xB, link.LocalVB + dv, link.FaceU + du) : new Vector3(link.FaceU + du, link.LocalVB + dv, zB);
            bool a = shapeA(pA) >= 0f;
            bool b = shapeB(pB) >= 0f;
            if (a) openA++;
            if (b) openB++;
            if (a && b) both++;
            if (a || b) either++;
        }

        double shareA = samples > 0 ? (double)openA / samples : 0;
        double shareB = samples > 0 ? (double)openB / samples : 0;
        double iou = either > 0 ? (double)both / either : 0;

        float localX = (float)(link.WorldX - minX);
        float localY = (float)(link.WorldY - minY);
        float localZ = (float)(link.WorldZ - minZ);
        bool seamCrawl = HasCrossFaceConnection(reachedCrawl, ownerBox, gx, gy, gz, link.From, link.To, link.FromFace, localX, localY, localZ, r);
        bool seamTight = seamCrawl || HasCrossFaceConnection(reachedTight, ownerBox, gx, gy, gz, link.From, link.To, link.FromFace, localX, localY, localZ, r);

        return new SeamMetrics(shareA, shareB, iou, openA > 0 && openB > 0 && both > 0, seamCrawl, seamTight);
    }

    /// <summary>
    /// Classifies reached box indices strictly: <c>Crawl</c> includes only boxes with at least one cell reached at full crawl
    /// clearance, while <c>Tight</c> includes boxes reached at tight or crawl clearance (#11209 R11209-3).
    /// </summary>
    internal static (HashSet<int> Crawl, HashSet<int> Tight) ClassifyReachedBoxes(
        bool[] open,
        bool[] reachedCrawl,
        bool[] reachedTight,
        short[] ownerBox)
    {
        HashSet<int> crawl = [];
        HashSet<int> tight = [];
        for (int i = 0; i < open.Length; i++)
        {
            if (!open[i] || ownerBox[i] < 0) continue;
            if (reachedCrawl[i])
            {
                crawl.Add(ownerBox[i]);
                tight.Add(ownerBox[i]);
            }
            else if (reachedTight[i])
            {
                tight.Add(ownerBox[i]);
            }
        }
        return (crawl, tight);
    }

    /// <summary>
    /// Returns true only when <paramref name="reached"/> contains an adjacent pair of cells straddling the shared face plane
    /// between <paramref name="fromBox"/> and <paramref name="toBox"/> inside the port disk (#11209 R11209-3).
    /// A one-sided reach (stopping on one side of the face) returns false.
    /// </summary>
    internal static bool HasCrossFaceConnection(
        bool[] reached,
        short[] ownerBox,
        int gx,
        int gy,
        int gz,
        int fromBox,
        int toBox,
        KarstCave.BoxFace fromFace,
        float localSeamX,
        float localSeamY,
        float localSeamZ,
        float radius)
    {
        bool xFace = fromFace is KarstCave.BoxFace.PosX or KarstCave.BoxFace.NegX;
        float diskR = radius + CellMetres;
        float diskR2 = diskR * diskR;
        int cellRadius = (int)MathF.Ceiling(diskR / CellMetres);
        int sy = (int)Math.Floor(localSeamY / CellMetres);

        if (xFace)
        {
            int faceX = (int)Math.Round(localSeamX / CellMetres);
            int negX = faceX - 1;
            int posX = faceX;
            if (negX < 0 || posX >= gx) return false;
            int sz = (int)Math.Floor(localSeamZ / CellMetres);
            for (int z = Math.Max(0, sz - cellRadius); z <= Math.Min(gz - 1, sz + cellRadius); z++)
            for (int y = Math.Max(0, sy - cellRadius); y <= Math.Min(gy - 1, sy + cellRadius); y++)
            {
                float du = ((z + 0.5f) * CellMetres) - localSeamZ;
                float dv = ((y + 0.5f) * CellMetres) - localSeamY;
                if ((du * du) + (dv * dv) > diskR2) continue;
                int iNeg = (((z * gy) + y) * gx) + negX;
                int iPos = (((z * gy) + y) * gx) + posX;
                if (!reached[iNeg] || !reached[iPos]) continue;
                short oNeg = ownerBox[iNeg], oPos = ownerBox[iPos];
                if ((oNeg == fromBox && oPos == toBox) || (oNeg == toBox && oPos == fromBox)) return true;
            }
        }
        else
        {
            int faceZ = (int)Math.Round(localSeamZ / CellMetres);
            int negZ = faceZ - 1;
            int posZ = faceZ;
            if (negZ < 0 || posZ >= gz) return false;
            int sx = (int)Math.Floor(localSeamX / CellMetres);
            for (int x = Math.Max(0, sx - cellRadius); x <= Math.Min(gx - 1, sx + cellRadius); x++)
            for (int y = Math.Max(0, sy - cellRadius); y <= Math.Min(gy - 1, sy + cellRadius); y++)
            {
                float du = ((x + 0.5f) * CellMetres) - localSeamX;
                float dv = ((y + 0.5f) * CellMetres) - localSeamY;
                if ((du * du) + (dv * dv) > diskR2) continue;
                int iNeg = (((negZ * gy) + y) * gx) + x;
                int iPos = (((posZ * gy) + y) * gx) + x;
                if (!reached[iNeg] || !reached[iPos]) continue;
                short oNeg = ownerBox[iNeg], oPos = ownerBox[iPos];
                if ((oNeg == fromBox && oPos == toBox) || (oNeg == toBox && oPos == fromBox)) return true;
            }
        }

        return false;
    }

    /// <summary>Focused regression check for cross-face seam traversal and tight/crawl separation (#11209 R11209-3).</summary>
    internal static bool CheckSeamMetrics(Action<string> report)
    {
        const int gx = 16, gy = 10, gz = 10;
        const int faceX = 8;
        int n = gx * gy * gz;
        short[] owner = new short[n];
        for (int z = 0; z < gz; z++)
        for (int y = 0; y < gy; y++)
        for (int x = 0; x < gx; x++)
            owner[(((z * gy) + y) * gx) + x] = (short)(x < faceX ? 0 : 1);

        // Case 1: One-sided reach stopping at (faceX - 1, 5, 5); Side B completely unreached -> MUST be false.
        bool[] oneSided = new bool[n];
        oneSided[(((5 * gy) + 5) * gx) + (faceX - 1)] = true;
        bool c1 = !HasCrossFaceConnection(oneSided, owner, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosX, faceX, 5.5f, 5.5f, 2f);

        // Case 2: Adjacent pair straddling face inside port disk -> MUST be true.
        bool[] crossed = (bool[])oneSided.Clone();
        crossed[(((5 * gy) + 5) * gx) + faceX] = true;
        bool c2 = HasCrossFaceConnection(crossed, owner, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosX, faceX, 5.5f, 5.5f, 2f);

        // Case 3: Cross-face pair far outside the port disk (at y=0, z=0) -> MUST be false.
        bool[] offPort = new bool[n];
        offPort[(((0 * gy) + 0) * gx) + (faceX - 1)] = true;
        offPort[(((0 * gy) + 0) * gx) + faceX] = true;
        bool c3 = !HasCrossFaceConnection(offPort, owner, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosX, faceX, 5.5f, 5.5f, 2f);

        // Case 4: Box 1 reached only by tight flood (not crawl) -> crawl={0}, tight={0,1}.
        bool[] open = new bool[n];
        bool[] crawl = new bool[n];
        bool[] tight = new bool[n];
        int cellA = (((5 * gy) + 5) * gx) + (faceX - 1);
        int cellB = (((5 * gy) + 5) * gx) + faceX;
        open[cellA] = open[cellB] = true;
        crawl[cellA] = true;
        tight[cellA] = tight[cellB] = true;
        (HashSet<int> rCrawl, HashSet<int> rTight) = ClassifyReachedBoxes(open, crawl, tight, owner);
        bool c4 = rCrawl.SetEquals([0]) && rTight.SetEquals([0, 1]);

        bool ok = c1 && c2 && c3 && c4;
        report($"{(ok ? "ok" : "FAILED")}: seam traversal metrics (oneSidedRejected={c1}, crossFaceAccepted={c2}, offPortRejected={c3}, tightCrawlSeparated={c4})");
        return ok;
    }

    private static int FindSeed(Vector3 start, bool[] open, float[] clearance, int gx, int gy, int gz)
    {
        int sx = Math.Clamp((int)start.X, 0, gx - 1);
        int sy = Math.Clamp((int)start.Y, 0, gy - 1);
        int sz = Math.Clamp((int)start.Z, 0, gz - 1);
        int best = -1;
        float bestScore = float.MaxValue;
        for (int r = 2; r <= 12 && best < 0; r += 4)
        {
            for (int z = Math.Max(0, sz - r); z <= Math.Min(gz - 1, sz + r); z++)
            for (int y = Math.Max(0, sy - r); y <= Math.Min(gy - 1, sy + r); y++)
            for (int x = Math.Max(0, sx - r); x <= Math.Min(gx - 1, sx + r); x++)
            {
                int i = (((z * gy) + y) * gx) + x;
                if (!open[i] || clearance[i] < KarstCave.CrawlRadius) continue;
                float dist = MathF.Sqrt(((x - sx) * (x - sx)) + ((y - sy) * (y - sy)) + ((z - sz) * (z - sz))) - (clearance[i] * 2f);
                if (dist < bestScore) { bestScore = dist; best = i; }
            }
        }
        if (best >= 0) return best;
        for (int i = 0; i < open.Length; i++) if (open[i]) return i;
        return -1;
    }

    private static bool[] Flood(int seed, float[] clearance, float minClearance, int gx, int gy, int gz)
    {
        bool[] visited = new bool[clearance.Length];
        if (seed < 0) return visited;
        Queue<int> q = new();
        visited[seed] = true;
        q.Enqueue(seed);
        while (q.TryDequeue(out int i))
        {
            int x = i % gx, y = (i / gx) % gy, z = i / (gx * gy);
            foreach ((int dx, int dy, int dz) in Faces6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= gx || ny >= gy || nz >= gz) continue;
                int next = (((nz * gy) + ny) * gx) + nx;
                if (!visited[next] && clearance[next] >= minClearance)
                {
                    visited[next] = true;
                    q.Enqueue(next);
                }
            }
        }
        return visited;
    }

    private static bool[] Flood(int seed, bool[] open, int gx, int gy, int gz)
    {
        bool[] visited = new bool[open.Length];
        if (seed < 0) return visited;
        Queue<int> q = new();
        visited[seed] = true;
        q.Enqueue(seed);
        while (q.TryDequeue(out int i))
        {
            int x = i % gx, y = (i / gx) % gy, z = i / (gx * gy);
            foreach ((int dx, int dy, int dz) in Faces6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 0 || ny < 0 || nz < 0 || nx >= gx || ny >= gy || nz >= gz) continue;
                int next = (((nz * gy) + ny) * gx) + nx;
                if (!visited[next] && open[next])
                {
                    visited[next] = true;
                    q.Enqueue(next);
                }
            }
        }
        return visited;
    }

    private sealed record PlannedBox(int Index, double OriginX, double OriginY, double OriginZ, int ChunksX, int ChunksY, int ChunksZ, float SizeX, float SizeY, float SizeZ, float Depth, float WaterLocalY, ulong Seed);
    private sealed record PlannedLink(int Index, int From, int To, KarstCave.BoxFace FromFace, KarstCave.BoxFace ToFace, float FaceU, float LocalVA, float LocalVB, float WorldX, float WorldY, float WorldZ, float Radius, float WaterWorldY, string Kind);
    private sealed record GeneratedBox(PlannedBox Box, DungeonLayout Layout, string Verdict, KarstCave.Report Report, double GenerateMs);
    private sealed record SeamMetrics(double OpenShareA, double OpenShareB, double Overlap, bool ConnectedBothSides, bool ReachedCrawl, bool ReachedTight);
}
