using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.WorldGen;

namespace GoldenSeed;

/// <summary>
/// Generates multi-box karst cave detail directly from an <see cref="UndergroundSystem"/> plan (#11209, CMS-only).
/// Each box is generated with <see cref="KarstCave.Generate"/> from the plan's <see cref="KarstCave.BoundaryPort"/>s (side
/// passages, round 3's shafts between stacked levels, and mouths on a box face), its water led toward the system's
/// resurgence, stitched into one 1 m volume and measured: exact (EDT) clearance from the real way in, which boxes, seams and
/// mouths a standing and a crawling body reach, and per seam whether a fallback tube had to be carved to get there.
/// The reach is a flood that ignores gravity: a shaft counts as passable whatever its height (climbing is #10908's).
/// </summary>
internal static class SystemView
{
    private const float CellMetres = 1f;
    private const float SeamProbeInset = 0.35f;
    private const float SeamSampleStep = 0.5f;
    private const float MouthPortRadius = 2.4f;
    private const float MouthReachMetres = 3f;
    private static readonly (int X, int Y, int Z)[] Faces6 = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    private const byte Rock = 0;
    private const byte Narrow = 1;
    private const byte CutOff = 2;
    private const byte Tight = 3;
    private const byte Crawl = 4;
    private const byte Stand = 5;

    /// <summary>
    /// The system's boxes generated as the walking session generates them (#11219: the plan's seeds, settings and ports through
    /// <see cref="PlannedCaveSettings"/>, the karst generator through <see cref="DungeonCandidates"/>), stitched and measured.
    /// </summary>
    internal static string Make(UndergroundSystem system, string level, float measureStep = FineGrid.DefaultStep)
    {
        Stopwatch totalClock = Stopwatch.StartNew();
        string id = system.Id;
        List<PlannedBox> boxes = [];
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (UndergroundBox b in system.Boxes)
        {
            float sx = b.ChunksX * 16f, sy = b.ChunksY * 16f, sz = b.ChunksZ * 16f;
            minX = Math.Min(minX, b.OriginX); minY = Math.Min(minY, b.OriginY); minZ = Math.Min(minZ, b.OriginZ);
            maxX = Math.Max(maxX, b.OriginX + sx); maxY = Math.Max(maxY, b.OriginY + sy); maxZ = Math.Max(maxZ, b.OriginZ + sz);
            boxes.Add(new PlannedBox(b.Index, b.OriginX, b.OriginY, b.OriginZ, b.ChunksX, b.ChunksY, b.ChunksZ, sx, sy, sz, b.DepthBelowSurface, b.WaterTableLocalY, b.BoxSeed));
        }

        Dictionary<int, List<KarstCave.BoundaryPort>> portsByBox = PlannedCaveSettings.PortsByBox(system);
        List<PlannedLink> links = [];
        for (int i = 0; i < system.Links.Count; i++)
        {
            UndergroundLink l = system.Links[i];
            bool vertical = l.Kind == UndergroundLinkKind.Shaft;
            float localVA = vertical ? l.FaceV : l.WorldY - (float)boxes[l.FromBox].OriginY;
            float localVB = vertical ? l.FaceV : l.WorldY - (float)boxes[l.ToBox].OriginY;
            links.Add(new PlannedLink(i, l.FromBox, l.ToBox, (KarstCave.BoxFace)l.FromFace, (KarstCave.BoxFace)l.ToFace, l.FaceU, localVA, localVB,
                l.WorldX, l.WorldY, l.WorldZ, l.RadiusMetres, l.WaterLevelWorldY, l.Kind.ToString(), vertical));
        }

        Dictionary<int, Vector2> wayInByBox = [];
        List<PlannedMouth> mouths = [];
        for (int i = 0; i < system.Mouths.Count; i++)
        {
            UndergroundMouth m = system.Mouths[i];
            KarstCave.BoxFace face = (KarstCave.BoxFace)m.Face;
            if (face == KarstCave.BoxFace.PosY)
            {
                wayInByBox.TryAdd(m.BoxIndex, new Vector2(m.PortU, m.PortV));
                mouths.Add(new PlannedMouth(i, m.BoxIndex, face, m.Kind.ToString(), null, m.TendrilMetres));
                continue;
            }

            KarstCave.BoundaryPort port = portsByBox[m.BoxIndex].First(p => p.Label == PlannedCaveSettings.MouthLabel(i));
            mouths.Add(new PlannedMouth(i, m.BoxIndex, face, m.Kind.ToString(), port, m.TendrilMetres));
        }

        Dictionary<int, Vector2?> springByBox = [];
        List<GeneratedBox> generated = new(boxes.Count);
        double totalGenerateMs = 0;
        foreach (PlannedBox b in boxes)
        {
            Stopwatch boxClock = Stopwatch.StartNew();
            (ulong boxSeed, CaveSetting caveSetting, KarstCave.Settings settings) = PlannedCaveSettings.For(system, b.Index, UnderCaves.Settings());
            springByBox[b.Index] = settings.SpringAt;
            DungeonCandidate candidate = DungeonCandidates.Generate(DungeonApproach.Karst, boxSeed, 0, 1d, null, caveSetting, settings);
            boxClock.Stop();
            totalGenerateMs += boxClock.Elapsed.TotalMilliseconds;
            generated.Add(new GeneratedBox(b, candidate.Layout, candidate.Verdict.Reason, candidate.Karst!, boxClock.Elapsed.TotalMilliseconds));
        }

        // The output grid (1 m, the CMS's views) over the system's bounds.
        int gx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / CellMetres));
        int gy = Math.Max(1, (int)Math.Ceiling((maxY - minY) / CellMetres));
        int gz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / CellMetres));
        int cellCount = gx * gy * gz;
        Vector3 Local(PlannedBox b, Vector3 p) => p + new Vector3((float)(b.OriginX - minX), (float)(b.OriginY - minY), (float)(b.OriginZ - minZ));

        // The reach is measured at KarstCave's own field step, a padded grid per box with its neighbours' rock and passages
        // in the padding, so the clearance (EDT) across a face is exact and memory stays a box's worth at a time.
        // (Rounds 1-2 measured on the 1 m output grid, which under-reads a standing-width passage.)
        FineGrid fine = FineGrid.Build(generated.Select(g => (g.Box, g.Layout.Shape!)).ToList(), minX, minY, minZ, measureStep);

        // The way in: the sinkhole's arrival if the system has one, else its first side mouth's port.
        PlannedMouth entry = mouths.FirstOrDefault(m => m.Port is null) ?? mouths[0];
        PlannedBox entryBox = boxes[entry.Box];
        Vector3 entryInBox = entry.Port is KarstCave.BoundaryPort ep
            ? ep.AnchorPoint(entryBox.SizeX, entryBox.SizeY, entryBox.SizeZ)
            : generated[entry.Box].Layout.MainRoute.Count > 0 ? generated[entry.Box].Layout.MainRoute[0] : new Vector3(entryBox.SizeX * 0.5f, entryBox.SizeY * 0.65f, entryBox.SizeZ * 0.5f);
        Vector3 startLocal = Local(entryBox, entryInBox);
        (int Box, int Cell) seed = fine.Seed(boxes.IndexOf(entryBox), entryInBox);

        bool[][] fineStand = fine.Flood(seed, KarstCave.StandRadius);
        bool[][] fineCrawl = fine.Flood(seed, KarstCave.CrawlRadius);
        bool[][] fineTight = fine.Flood(seed, KarstCave.CrawlRadius * 0.65f);
        bool[][] fineAny = fine.Flood(seed, -1f);

        // Down to the 1 m output, for display only (every measure reads the fine samples): a cell is open where any of its
        // samples is, its state the best of theirs.
        bool[] open = new bool[cellCount];
        byte[] state = new byte[cellCount];
        double fineVolume = fine.Step * fine.Step * fine.Step;
        double openM3 = 0, standM3 = 0, crawlM3 = 0, tightM3 = 0, narrowM3 = 0, cutOffM3 = 0;
        HashSet<int> boxesReachedStand = [], boxesReachedCrawl = [], boxesReachedTight = [];
        for (int bi = 0; bi < fine.Boxes.Count; bi++)
        {
            FineGrid.Box fb = fine.Boxes[bi];
            for (int z = 0; z < fb.Z; z++)
            for (int y = 0; y < fb.Y; y++)
            for (int x = 0; x < fb.X; x++)
            {
                int i = (((z * fb.Y) + y) * fb.X) + x;
                if (!fb.Open[i]) continue;
                byte st;
                openM3 += fineVolume;
                if (fineCrawl[bi][i])
                {
                    if (fb.Clearance[i] >= KarstCave.StandRadius) { st = Stand; standM3 += fineVolume; }
                    else { st = Crawl; crawlM3 += fineVolume; }
                }
                else if (fineTight[bi][i]) { st = Tight; tightM3 += fineVolume; }
                else if (fineAny[bi][i]) { st = Narrow; narrowM3 += fineVolume; }
                else { st = CutOff; cutOffM3 += fineVolume; }
                if (fineStand[bi][i]) boxesReachedStand.Add(fb.Index);
                if (fineCrawl[bi][i]) boxesReachedCrawl.Add(fb.Index);
                if (fineTight[bi][i]) boxesReachedTight.Add(fb.Index);

                int wx = fb.MetreX + (int)(x * fine.Step), wy = fb.MetreY + (int)(y * fine.Step), wz = fb.MetreZ + (int)(z * fine.Step);
                if (wx >= gx || wy >= gy || wz >= gz) continue;
                int w = (((wz * gy) + wy) * gx) + wx;
                open[w] = true;
                if (st > state[w]) state[w] = st;
            }
        }

        boxesReachedTight.UnionWith(boxesReachedCrawl);
        int openCells = (int)Math.Round(openM3), standCells = (int)Math.Round(standM3), crawlCells = (int)Math.Round(crawlM3);
        int tightCells = (int)Math.Round(tightM3), narrowCells = (int)Math.Round(narrowM3), cutOffCells = (int)Math.Round(cutOffM3);

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

        // What the walkability passes did per route (KarstCave's report), by port label.
        Dictionary<string, KarstCave.PortRoute> routes = [];
        foreach (GeneratedBox gb in generated)
        foreach (KarstCave.PortRoute r in gb.Report.PortRoutes)
            routes[$"{gb.Box.Index}|{r.Name}"] = r;

        JsonObject RouteJson(int box, string label)
        {
            if (!routes.TryGetValue($"{box}|port:{label}", out KarstCave.PortRoute? r)) return new JsonObject { ["missing"] = true };
            return new JsonObject
            {
                ["before"] = Math.Round(r.Before, 2),
                ["after"] = Math.Round(r.After, 2),
                ["widened"] = r.Widened,
                ["anchorTubeMetres"] = Math.Round(r.AnchorTubeMetres, 1),
                ["pocketConnector"] = r.PocketConnector,
                ["standFallback"] = r.StandFallback,
            };
        }

        bool Fired(int box, string label) => routes.TryGetValue($"{box}|port:{label}", out KarstCave.PortRoute? r) && (r.PocketConnector || r.StandFallback);

        JsonArray seamReports = [];
        int seamsMatched = 0, seamsCrawl = 0, seamsTight = 0, seamsStand = 0, seamsFallback = 0;
        double sumOverlap = 0;
        JsonArray shafts = [];
        foreach (PlannedLink l in links)
        {
            SeamMetrics seam = MeasureSeam(generated[l.From], generated[l.To], l, fine, fineStand, fineCrawl, fineTight, minX, minY, minZ);
            if (seam.Overlap >= 0.70 && seam.ConnectedBothSides) seamsMatched++;
            if (seam.ReachedStand) seamsStand++;
            if (seam.ReachedCrawl) seamsCrawl++;
            if (seam.ReachedTight) seamsTight++;
            string labelA = $"L{l.Index}:{l.From}->{l.To}", labelB = $"L{l.Index}:{l.To}->{l.From}";
            bool fallback = Fired(l.From, labelA) || Fired(l.To, labelB);
            if (fallback) seamsFallback++;
            sumOverlap += seam.Overlap;
            JsonObject report = new()
            {
                ["index"] = l.Index,
                ["from"] = l.From,
                ["to"] = l.To,
                ["kind"] = l.Kind,
                ["vertical"] = l.Vertical,
                ["radius"] = l.Radius,
                ["at"] = new JsonArray(Math.Round(l.WorldX - minX, 1), Math.Round(l.WorldY - minY, 1), Math.Round(l.WorldZ - minZ, 1)),
                ["openShareA"] = Math.Round(seam.OpenShareA, 3),
                ["openShareB"] = Math.Round(seam.OpenShareB, 3),
                ["overlapIoU"] = Math.Round(seam.Overlap, 3),
                ["connectedBothSides"] = seam.ConnectedBothSides,
                ["seamReachedStand"] = seam.ReachedStand,
                ["seamReachedCrawl"] = seam.ReachedCrawl,
                ["seamReachedTight"] = seam.ReachedTight,
                ["fallbackFired"] = fallback,
                ["routeA"] = RouteJson(l.From, labelA),
                ["routeB"] = RouteJson(l.To, labelB),
            };
            if (l.Vertical)
            {
                float pitch = ShaftPitch(open, gx, gy, gz, l.WorldX - (float)minX, l.WorldY - (float)minY, l.WorldZ - (float)minZ, l.Radius);
                report["pitchMetres"] = Math.Round(pitch, 1);
                shafts.Add(new JsonObject { ["link"] = l.Index, ["pitchMetres"] = Math.Round(pitch, 1) });
            }

            seamReports.Add(report);
        }

        // Each mouth: reached from the way in, standing or crawling, and what its own port route needed.
        JsonArray mouthMarkers = [];
        int mouthsStand = 0, mouthsCrawl = 0, mouthsFallback = 0;
        foreach (PlannedMouth m in mouths)
        {
            PlannedBox b = boxes[m.Box];
            Vector3 at = m.Port is KarstCave.BoundaryPort mp
                ? Local(b, mp.AnchorPoint(b.SizeX, b.SizeY, b.SizeZ))
                : m == entry ? startLocal : Local(b, new Vector3(wayInByBox[m.Box].X, b.SizeY - 4f, wayInByBox[m.Box].Y));
            bool stand = fine.Near(fineStand, at, MouthReachMetres), crawl = fine.Near(fineCrawl, at, MouthReachMetres);
            if (stand) mouthsStand++;
            if (crawl) mouthsCrawl++;
            bool fired = m.Port is not null && Fired(m.Box, $"M{m.Index}");
            if (fired) mouthsFallback++;
            JsonObject marker = new()
            {
                ["box"] = m.Box,
                ["kind"] = m.Kind,
                ["face"] = m.Face.ToString(),
                ["entry"] = m == entry,
                ["tendril"] = m.Tendril,
                ["at"] = new JsonArray(Math.Round(at.X, 1), Math.Round(at.Y, 1), Math.Round(at.Z, 1)),
                ["reachedStand"] = stand,
                ["reachedCrawl"] = crawl,
                ["fallbackFired"] = fired,
            };
            if (m.Port is not null) marker["route"] = RouteJson(m.Box, $"M{m.Index}");
            mouthMarkers.Add(marker);
        }

        JsonArray boxReports = [];
        JsonArray allRoutes = [];
        int requiredRoutes = 0, standFallbacks = 0, pocketConnectors = 0;
        foreach (GeneratedBox gb in generated)
        {
            PlannedBox b = gb.Box;
            float ox = (float)(b.OriginX - minX), oy = (float)(b.OriginY - minY), oz = (float)(b.OriginZ - minZ);
            JsonArray routePts = [];
            foreach (Vector3 pt in gb.Layout.MainRoute) routePts.Add(new JsonArray(Math.Round(pt.X + ox, 1), Math.Round(pt.Y + oy, 1), Math.Round(pt.Z + oz, 1)));
            allRoutes.Add(routePts);
            requiredRoutes += gb.Report.PortRoutes.Count;
            standFallbacks += gb.Report.PortRoutes.Count(r => r.StandFallback);
            pocketConnectors += gb.Report.PortRoutes.Count(r => r.PocketConnector);
            KarstCave.PortRoute? main = gb.Report.PortRoutes.FirstOrDefault(r => r.Name == "spring");
            boxReports.Add(new JsonObject
            {
                ["index"] = b.Index,
                ["offset"] = new JsonArray(Math.Round(ox, 1), Math.Round(oy, 1), Math.Round(oz, 1)),
                ["size"] = new JsonArray(b.SizeX, b.SizeY, b.SizeZ),
                ["depth"] = b.Depth,
                ["waterLocalY"] = b.WaterLocalY,
                ["springAt"] = springByBox[b.Index] is Vector2 sp ? new JsonArray(Math.Round(sp.X, 1), Math.Round(sp.Y, 1)) : null,
                ["generateMs"] = Math.Round(gb.GenerateMs, 1),
                ["reachedStand"] = boxesReachedStand.Contains(b.Index),
                ["reachedCrawl"] = boxesReachedCrawl.Contains(b.Index),
                ["reachedTight"] = boxesReachedTight.Contains(b.Index),
                ["requiredRoutes"] = gb.Report.PortRoutes.Count,
                ["standFallbacks"] = gb.Report.PortRoutes.Count(r => r.StandFallback),
                ["pocketConnectors"] = gb.Report.PortRoutes.Count(r => r.PocketConnector),
                ["mainRouteFallback"] = main?.StandFallback ?? false,
                ["verdict"] = gb.Verdict,
            });
        }

        // The gravity-aware reach check (#10908): on 1 m cells sampled as the game samples them (each box's shape at its voxel
        // centres), the way a body with weight takes - walking, stepping, dropping one way, climbing pitches onto ledges - from
        // each side mouth to each other, with no edits.
        JsonArray walkerLegs = [], walkerReach = [];
        int walkerFound = 0, walkerPairs = 0;
        {
            bool[] cells = new bool[cellCount];
            Array.Fill(cells, true);
            foreach (GeneratedBox gb in generated)
            {
                PlannedBox b = gb.Box;
                Func<Vector3, float> shape = gb.Layout.Shape!;
                int ox = (int)Math.Round(b.OriginX - minX), oy = (int)Math.Round(b.OriginY - minY), oz = (int)Math.Round(b.OriginZ - minZ);
                Parallel.For(0, (int)b.SizeZ, z =>
                {
                    for (int y = 0; y < (int)b.SizeY; y++)
                    for (int x = 0; x < (int)b.SizeX; x++)
                    {
                        int wx = ox + x, wy = oy + y, wz = oz + z;
                        if (wx >= gx || wy >= gy || wz >= gz) continue;
                        cells[(((wz * gy) + wy) * gx) + wx] = shape(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) < 0f;
                    }
                });
            }

            CaveWalker walker = new(gx, gy, gz, cells);
            int ownerAt((int X, int Y, int Z) c)
            {
                foreach (PlannedBox b in boxes)
                {
                    double lx = c.X + 0.5 - (b.OriginX - minX), ly = c.Y + 0.5 - (b.OriginY - minY), lz = c.Z + 0.5 - (b.OriginZ - minZ);
                    if (lx >= 0 && ly >= 0 && lz >= 0 && lx < b.SizeX && ly < b.SizeY && lz < b.SizeZ) return b.Index;
                }

                return -1;
            }

            List<(PlannedMouth Mouth, (int X, int Y, int Z)? At)> ends = [];
            foreach (PlannedMouth m in mouths.Where(m => m.Port is not null))
            {
                PlannedBox b = boxes[m.Box];
                Vector3 a = Local(b, m.Port!.Value.AnchorPoint(b.SizeX, b.SizeY, b.SizeZ));
                ends.Add((m, walker.Settle((int)MathF.Floor(a.X), (int)MathF.Floor(a.Y), (int)MathF.Floor(a.Z), across: 3, down: 12)));
            }

            // What each mouth reaches: how many standing cells, and the lowest, per box (a diagnostic for failed pairs).
            foreach (var from in ends)
            {
                if (from.At is not (int, int, int) f) { walkerReach.Add(new JsonObject { ["from"] = $"M{from.Mouth.Index}", ["settled"] = false }); continue; }
                HashSet<(int X, int Y, int Z)> r = walker.Reach(f);
                JsonObject perBox = [];
                foreach (var group in r.GroupBy(c => ownerAt(c)).Where(g => g.Key >= 0).OrderBy(g => g.Key))
                {
                    perBox[group.Key.ToString(CultureInfo.InvariantCulture)] = group.Count();
                }

                walkerReach.Add(new JsonObject { ["from"] = $"M{from.Mouth.Index}", ["at"] = $"{f.Item1},{f.Item2},{f.Item3}", ["cells"] = r.Count, ["byBox"] = perBox });
            }

            foreach (var from in ends)
            foreach (var to in ends)
            {
                if (from.Mouth == to.Mouth) continue;
                walkerPairs++;
                CaveWalker.Way? way = from.At is (int, int, int) f && to.At is (int, int, int) t ? walker.Find(f, t, edits: false) : null;
                if (way is not null) walkerFound++;
                walkerLegs.Add(new JsonObject
                {
                    ["from"] = $"M{from.Mouth.Index}",
                    ["to"] = $"M{to.Mouth.Index}",
                    ["found"] = way is not null,
                    ["metres"] = way?.Cells.Count ?? 0,
                    ["drops"] = way?.Drops ?? 0,
                    ["climbs"] = way?.Climbs ?? 0,
                    ["searched"] = way?.Expanded ?? 0,
                });
            }
        }

        JsonArray bigMarkers = [];
        foreach (UndergroundBigSpace bs in system.BigSpaces)
        {
            PlannedBox b = boxes[bs.BoxIndex];
            bigMarkers.Add(new JsonObject
            {
                ["box"] = bs.BoxIndex,
                ["kind"] = bs.Kind.ToString(),
                ["radius"] = bs.RadiusMetres,
                ["at"] = new JsonArray(
                    Math.Round((b.OriginX - minX) + bs.LocalX, 1),
                    Math.Round((b.OriginY - minY) + bs.LocalY, 1),
                    Math.Round((b.OriginZ - minZ) + bs.LocalZ, 1)),
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
            ["measureStep"] = fine.Step,
            ["start"] = new JsonArray(Math.Round(startLocal.X, 1), Math.Round(startLocal.Y, 1), Math.Round(startLocal.Z, 1)),
            ["generateMs"] = Math.Round(totalGenerateMs, 1),
            ["totalMs"] = Math.Round(totalClock.Elapsed.TotalMilliseconds, 1),
            ["stats"] = new JsonObject
            {
                ["boxes"] = boxes.Count,
                ["boxesReachedStand"] = boxesReachedStand.Count,
                ["boxesReachedCrawl"] = boxesReachedCrawl.Count,
                ["boxesReachedTight"] = boxesReachedTight.Count,
                ["links"] = links.Count,
                ["shafts"] = links.Count(l => l.Vertical),
                ["seamsMatched"] = seamsMatched,
                ["seamsTraversedStand"] = seamsStand,
                ["seamsTraversedCrawl"] = seamsCrawl,
                ["seamsTraversedTight"] = seamsTight,
                ["seamsWithFallback"] = seamsFallback,
                ["mouths"] = mouths.Count,
                ["mouthsReachedStand"] = mouthsStand,
                ["mouthsReachedCrawl"] = mouthsCrawl,
                ["mouthsWithFallback"] = mouthsFallback,
                ["requiredRoutes"] = requiredRoutes,
                ["standFallbacks"] = standFallbacks,
                ["pocketConnectors"] = pocketConnectors,
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
            ["shafts"] = shafts,
            ["walker"] = new JsonObject { ["pairs"] = walkerPairs, ["found"] = walkerFound, ["legs"] = walkerLegs, ["reach"] = walkerReach,
                ["carve"] = new JsonArray([.. generated.SelectMany(g => g.Report.WalkerLegs.Select(l => (JsonNode)new JsonObject
                {
                    ["box"] = g.Box.Index, ["route"] = l.Route, ["back"] = l.Back, ["found"] = l.Found, ["cells"] = l.Cells,
                    ["drops"] = l.Drops, ["climbs"] = l.Climbs, ["dug"] = l.Dug, ["floored"] = l.Floored,
                }))]) },
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

    /// <summary>The tallest unbroken open column through a shaft's port disk: the pitch a body climbs between the levels.</summary>
    private static float ShaftPitch(bool[] open, int gx, int gy, int gz, float x, float y, float z, float radius)
    {
        int r = (int)MathF.Ceiling(radius / CellMetres);
        int cx = (int)(x / CellMetres), cz = (int)(z / CellMetres), face = (int)MathF.Round(y / CellMetres);
        int best = 0;
        for (int iz = Math.Max(0, cz - r); iz <= Math.Min(gz - 1, cz + r); iz++)
        for (int ix = Math.Max(0, cx - r); ix <= Math.Min(gx - 1, cx + r); ix++)
        {
            if (face <= 0 || face >= gy || !open[(((iz * gy) + face) * gx) + ix] || !open[(((iz * gy) + face - 1) * gx) + ix]) continue;
            int low = face - 1, high = face;
            while (low > 0 && open[(((iz * gy) + low - 1) * gx) + ix]) low--;
            while (high < gy - 1 && open[(((iz * gy) + high + 1) * gx) + ix]) high++;
            best = Math.Max(best, high - low + 1);
        }

        return best * CellMetres;
    }

    private static SeamMetrics MeasureSeam(
        GeneratedBox boxA,
        GeneratedBox boxB,
        PlannedLink link,
        FineGrid fine,
        bool[][] reachedStand,
        bool[][] reachedCrawl,
        bool[][] reachedTight,
        double minX,
        double minY,
        double minZ)
    {
        Func<Vector3, float> shapeA = boxA.Layout.Shape!;
        Func<Vector3, float> shapeB = boxB.Layout.Shape!;
        float r = MathF.Max(link.Radius, KarstCave.StandRadius + 0.65f);
        int samples = 0, openA = 0, openB = 0, both = 0, either = 0;
        for (float dv = -r; dv <= r; dv += SeamSampleStep)
        for (float du = -r; du <= r; du += SeamSampleStep)
        {
            if ((du * du) + (dv * dv) > r * r) continue;
            samples++;
            Vector3 pA = SeamPoint(link.FromFace, boxA.Box, link.FaceU + du, link.LocalVA + dv);
            Vector3 pB = SeamPoint(link.ToFace, boxB.Box, link.FaceU + du, link.LocalVB + dv);
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
        float localX = (float)(link.WorldX - minX), localY = (float)(link.WorldY - minY), localZ = (float)(link.WorldZ - minZ);
        // On the measuring grid itself: a reached display cell on each side does not mean a reached sample pair across the face (R11209-4).
        Vector3 seamAt = new(localX, localY, localZ);
        bool Crosses(bool[][] reached) => fine.Crosses(reached, link.From, link.To, link.FromFace, seamAt, r);
        bool stand = Crosses(reachedStand);
        bool crawl = stand || Crosses(reachedCrawl);
        bool tight = crawl || Crosses(reachedTight);
        return new SeamMetrics(shareA, shareB, iou, openA > 0 && openB > 0 && both > 0, stand, crawl, tight);
    }

    /// <summary>A point just inside a box's face at in-face coordinates (u, v), as <see cref="KarstCave.BoundaryPort"/> has them.</summary>
    private static Vector3 SeamPoint(KarstCave.BoxFace face, PlannedBox box, float u, float v) => face switch
    {
        KarstCave.BoxFace.PosX => new Vector3(box.SizeX - SeamProbeInset, v, u),
        KarstCave.BoxFace.NegX => new Vector3(SeamProbeInset, v, u),
        KarstCave.BoxFace.PosZ => new Vector3(u, v, box.SizeZ - SeamProbeInset),
        KarstCave.BoxFace.NegZ => new Vector3(u, v, SeamProbeInset),
        KarstCave.BoxFace.PosY => new Vector3(u, box.SizeY - SeamProbeInset, v),
        _ => new Vector3(u, SeamProbeInset, v),
    };

    /// <summary>
    /// Classifies reached box indices strictly: <c>Crawl</c> includes only boxes with at least one cell reached at full crawl
    /// clearance, while <c>Tight</c> includes boxes reached at tight or crawl clearance (#11209 R11209-3).
    /// </summary>
    internal static (HashSet<int> Crawl, HashSet<int> Tight) ClassifyReachedBoxes(bool[] open, bool[] reachedCrawl, bool[] reachedTight, short[] ownerBox)
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
    /// between <paramref name="fromBox"/> and <paramref name="toBox"/> inside the port disk (#11209 R11209-3; round 3 adds the
    /// floor/roof plane of a shaft). A one-sided reach (stopping on one side of the face) returns false.
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
        float radius,
        float cell = CellMetres)
    {
        float diskR = radius + cell;
        float diskR2 = diskR * diskR;
        int cellRadius = (int)MathF.Ceiling(diskR / cell);
        // The face's axis (0 x, 1 y, 2 z) and the two in-face axes.
        int axis = fromFace switch
        {
            KarstCave.BoxFace.PosX or KarstCave.BoxFace.NegX => 0,
            KarstCave.BoxFace.PosY or KarstCave.BoxFace.NegY => 1,
            _ => 2,
        };
        float[] seam = [localSeamX, localSeamY, localSeamZ];
        int[] size = [gx, gy, gz];
        int a1 = axis == 0 ? 1 : 0, a2 = axis == 2 ? 1 : 2;
        int facePlane = (int)Math.Round(seam[axis] / cell);
        if (facePlane - 1 < 0 || facePlane >= size[axis]) return false;
        int c1 = (int)Math.Floor(seam[a1] / cell), c2 = (int)Math.Floor(seam[a2] / cell);
        int[] p = new int[3];
        int Index() => (((p[2] * gy) + p[1]) * gx) + p[0];
        for (int i = Math.Max(0, c1 - cellRadius); i <= Math.Min(size[a1] - 1, c1 + cellRadius); i++)
        for (int j = Math.Max(0, c2 - cellRadius); j <= Math.Min(size[a2] - 1, c2 + cellRadius); j++)
        {
            float d1 = ((i + 0.5f) * cell) - seam[a1];
            float d2 = ((j + 0.5f) * cell) - seam[a2];
            if ((d1 * d1) + (d2 * d2) > diskR2) continue;
            p[a1] = i;
            p[a2] = j;
            p[axis] = facePlane - 1;
            int iNeg = Index();
            p[axis] = facePlane;
            int iPos = Index();
            if (!reached[iNeg] || !reached[iPos]) continue;
            short oNeg = ownerBox[iNeg], oPos = ownerBox[iPos];
            if ((oNeg == fromBox && oPos == toBox) || (oNeg == toBox && oPos == fromBox)) return true;
        }

        return false;
    }

    /// <summary>Focused regression check for cross-face seam traversal (side faces and round 3's shafts) and tight/crawl separation.</summary>
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

        bool[] oneSided = new bool[n];
        oneSided[(((5 * gy) + 5) * gx) + (faceX - 1)] = true;
        bool c1 = !HasCrossFaceConnection(oneSided, owner, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosX, faceX, 5.5f, 5.5f, 2f);

        bool[] crossed = (bool[])oneSided.Clone();
        crossed[(((5 * gy) + 5) * gx) + faceX] = true;
        bool c2 = HasCrossFaceConnection(crossed, owner, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosX, faceX, 5.5f, 5.5f, 2f);

        bool[] offPort = new bool[n];
        offPort[(((0 * gy) + 0) * gx) + (faceX - 1)] = true;
        offPort[(((0 * gy) + 0) * gx) + faceX] = true;
        bool c3 = !HasCrossFaceConnection(offPort, owner, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosX, faceX, 5.5f, 5.5f, 2f);

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

        // A shaft: box 0 below y = 5, box 1 above; a reached pair straddling the plane in the disk crosses, one cell alone does not.
        const int faceY = 5;
        short[] stacked = new short[n];
        for (int z = 0; z < gz; z++)
        for (int y = 0; y < gy; y++)
        for (int x = 0; x < gx; x++)
            stacked[(((z * gy) + y) * gx) + x] = (short)(y < faceY ? 0 : 1);
        bool[] shaft = new bool[n];
        shaft[(((5 * gy) + (faceY - 1)) * gx) + 6] = true;
        bool c5 = !HasCrossFaceConnection(shaft, stacked, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosY, 6.5f, faceY, 5.5f, 2f);
        shaft[(((5 * gy) + faceY) * gx) + 6] = true;
        bool c6 = HasCrossFaceConnection(shaft, stacked, gx, gy, gz, 0, 1, KarstCave.BoxFace.PosY, 6.5f, faceY, 5.5f, 2f);

        // R11209-4: the crossing is measured on the 0.5 m measuring grid, not after it is reduced to 1 m display cells. Two
        // one-chunk boxes meeting at x = 16 m; reached samples at x = 15.25 and 16.75 m (not adjacent across the face, the
        // boundary samples at 15.75 and 16.25 m unreached) share 1 m cells 15 and 16, which reduction would call a crossing.
        PlannedBox left = new(0, 0, 0, 0, 1, 1, 1, 16f, 16f, 16f, 0f, 0f, 0UL);
        PlannedBox right = new(1, 16, 0, 0, 1, 1, 1, 16f, 16f, 16f, 0f, 0f, 0UL);
        FineGrid grid = FineGrid.Build([(left, _ => 1f), (right, _ => 1f)], 0, 0, 0, FineGrid.DefaultStep);
        bool[][] apart = grid.Empty();
        void Mark(bool[][] reached, float x, float y, float z)
        {
            (int box, int cell) = grid.Sample(new Vector3(x, y, z));
            reached[box][cell] = true;
        }

        Mark(apart, 15.25f, 5.25f, 5.25f);
        Mark(apart, 16.75f, 5.75f, 5.75f);
        Vector3 seamAt = new(16f, 5.5f, 5.5f);
        bool c7 = !grid.Crosses(apart, 0, 1, KarstCave.BoxFace.PosX, seamAt, 2f);
        bool[] reduced = new bool[32 * 16 * 16];
        short[] reducedOwner = new short[reduced.Length];
        for (int i = 0; i < reduced.Length; i++) reducedOwner[i] = (short)(i % 32 < 16 ? 0 : 1);
        reduced[(((5 * 16) + 5) * 32) + 15] = reduced[(((5 * 16) + 5) * 32) + 16] = true;
        bool reductionWouldCross = HasCrossFaceConnection(reduced, reducedOwner, 32, 16, 16, 0, 1, KarstCave.BoxFace.PosX, 16f, 5.5f, 5.5f, 2f);
        Mark(apart, 15.75f, 5.25f, 5.25f);
        Mark(apart, 16.25f, 5.25f, 5.25f);
        bool c8 = grid.Crosses(apart, 0, 1, KarstCave.BoxFace.PosX, seamAt, 2f);
        bool c9 = grid.Near(apart, new Vector3(18f, 5f, 5f), 3f) && !grid.Near(apart, new Vector3(24f, 12f, 12f), 3f);

        bool ok = c1 && c2 && c3 && c4 && c5 && c6 && c7 && reductionWouldCross && c8 && c9;
        report($"{(ok ? "ok" : "FAILED")}: seam traversal metrics (oneSidedRejected={c1}, crossFaceAccepted={c2}, offPortRejected={c3}, tightCrawlSeparated={c4}, shaftOneSidedRejected={c5}, shaftCrossAccepted={c6}, fineApartRejected={c7} (1 m reduction would accept: {reductionWouldCross}), fineAdjacentAccepted={c8}, fineNear={c9})");
        return ok;
    }

    /// <summary>
    /// A system's boxes sampled at KarstCave's field step for measuring reach (#11209 round 3): each box its own grid, its EDT
    /// clearance worked out over the box padded with what lies beyond its faces (a neighbour's samples, rock elsewhere), and
    /// floods that cross from box to box through the shared faces.
    /// </summary>
    private sealed class FineGrid
    {
        internal const float DefaultStep = 0.5f;
        private const float PadMetres = 3f;
        internal float Step { get; private init; }
        private int Pad => (int)MathF.Ceiling(PadMetres / Step);

        internal sealed record Box(int Index, int MetreX, int MetreY, int MetreZ, int X, int Y, int Z, bool[] Open, float[] Clearance);

        internal List<Box> Boxes { get; } = [];
        private readonly Dictionary<(int, int, int), int> byLattice = [];
        private int cellsX, cellsY, cellsZ;

        internal static FineGrid Build(List<(PlannedBox Box, Func<Vector3, float> Shape)> boxes, double minX, double minY, double minZ, float step)
        {
            FineGrid grid = new() { Step = step };
            float Step = step;
            int Pad = grid.Pad;
            PlannedBox first = boxes[0].Box;
            grid.cellsX = (int)Math.Round(first.SizeX / Step);
            grid.cellsY = (int)Math.Round(first.SizeY / Step);
            grid.cellsZ = (int)Math.Round(first.SizeZ / Step);
            List<(int MX, int MY, int MZ)> offsets = [];
            for (int i = 0; i < boxes.Count; i++)
            {
                PlannedBox b = boxes[i].Box;
                if (b.ChunksX != first.ChunksX || b.ChunksY != first.ChunksY || b.ChunksZ != first.ChunksZ)
                {
                    throw new InvalidOperationException("The system view measures boxes of one size.");
                }

                (int mx, int my, int mz) = ((int)Math.Round(b.OriginX - minX), (int)Math.Round(b.OriginY - minY), (int)Math.Round(b.OriginZ - minZ));
                offsets.Add((mx, my, mz));
                grid.byLattice[(FloorDiv(mx, (int)b.SizeX), FloorDiv(my, (int)b.SizeY), FloorDiv(mz, (int)b.SizeZ))] = i;
            }

            int px = grid.cellsX + (2 * Pad), py = grid.cellsY + (2 * Pad), pz = grid.cellsZ + (2 * Pad);
            float sizeX = first.SizeX, sizeY = first.SizeY, sizeZ = first.SizeZ;
            for (int i = 0; i < boxes.Count; i++)
            {
                (int mx, int my, int mz) = offsets[i];
                float[] values = new float[px * py * pz];
                Parallel.For(0, pz, z =>
                {
                    for (int y = 0; y < py; y++)
                    for (int x = 0; x < px; x++)
                    {
                        // The sample's place in the system, then the box that holds it.
                        float sx = mx + ((x - Pad + 0.5f) * Step), sy = my + ((y - Pad + 0.5f) * Step), sz = mz + ((z - Pad + 0.5f) * Step);
                        int owner = grid.byLattice.GetValueOrDefault(((int)MathF.Floor(sx / sizeX), (int)MathF.Floor(sy / sizeY), (int)MathF.Floor(sz / sizeZ)), -1);
                        float v = -2f;
                        if (owner >= 0)
                        {
                            (int ox, int oy, int oz) = offsets[owner];
                            v = boxes[owner].Shape(new Vector3(sx - ox, sy - oy, sz - oz));
                        }

                        values[(((z * py) + y) * px) + x] = v;
                    }
                });
                bool[] paddedOpen = new bool[values.Length];
                for (int k = 0; k < values.Length; k++) paddedOpen[k] = values[k] >= 0f;
                float[] paddedClearance = CaveView.Clearance(values, paddedOpen, px, py, pz, Step);

                bool[] open = new bool[grid.cellsX * grid.cellsY * grid.cellsZ];
                float[] clearance = new float[open.Length];
                for (int z = 0; z < grid.cellsZ; z++)
                for (int y = 0; y < grid.cellsY; y++)
                for (int x = 0; x < grid.cellsX; x++)
                {
                    int from = ((((z + Pad) * py) + y + Pad) * px) + x + Pad;
                    int to = (((z * grid.cellsY) + y) * grid.cellsX) + x;
                    open[to] = paddedOpen[from];
                    clearance[to] = paddedClearance[from];
                }

                grid.Boxes.Add(new Box(boxes[i].Box.Index, mx, my, mz, grid.cellsX, grid.cellsY, grid.cellsZ, open, clearance));
            }

            return grid;
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        /// <summary>The box (its place in <see cref="Boxes"/>) and sample at a sample's coordinates across the system, or -1.</summary>
        private (int Box, int Cell) At(int sx, int sy, int sz)
        {
            int nb = byLattice.GetValueOrDefault((FloorDiv(sx, cellsX), FloorDiv(sy, cellsY), FloorDiv(sz, cellsZ)), -1);
            if (nb < 0) return (-1, -1);
            Box b = Boxes[nb];
            int x = sx - (int)Math.Round(b.MetreX / Step), y = sy - (int)Math.Round(b.MetreY / Step), z = sz - (int)Math.Round(b.MetreZ / Step);
            return (nb, (((z * b.Y) + y) * b.X) + x);
        }

        /// <summary>
        /// Whether a flood crossed a seam: a pair of reached samples, one in each box, adjacent across the face plane inside the
        /// port's disk (system metres). Measured on the samples themselves: a slab of them about the seam goes to
        /// <see cref="HasCrossFaceConnection"/> at this grid's step.
        /// </summary>
        internal bool Crosses(bool[][] reached, int fromBox, int toBox, KarstCave.BoxFace face, Vector3 seamAt, float radius)
        {
            float reach = radius + (2f * Step);
            int x0 = (int)MathF.Floor((seamAt.X - reach) / Step), y0 = (int)MathF.Floor((seamAt.Y - reach) / Step), z0 = (int)MathF.Floor((seamAt.Z - reach) / Step);
            int x1 = (int)MathF.Ceiling((seamAt.X + reach) / Step), y1 = (int)MathF.Ceiling((seamAt.Y + reach) / Step), z1 = (int)MathF.Ceiling((seamAt.Z + reach) / Step);
            int sx = x1 - x0 + 1, sy = y1 - y0 + 1, sz = z1 - z0 + 1;
            bool[] slab = new bool[sx * sy * sz];
            short[] owner = new short[slab.Length];
            for (int z = 0; z < sz; z++)
            for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
            {
                int i = (((z * sy) + y) * sx) + x;
                (int box, int cell) = At(x0 + x, y0 + y, z0 + z);
                owner[i] = box < 0 ? (short)-1 : (short)Boxes[box].Index;
                slab[i] = box >= 0 && reached[box][cell];
            }

            return HasCrossFaceConnection(slab, owner, sx, sy, sz, fromBox, toBox, face,
                seamAt.X - (x0 * Step), seamAt.Y - (y0 * Step), seamAt.Z - (z0 * Step), radius, Step);
        }

        /// <summary>Whether any reached sample lies within a distance of a point (system metres).</summary>
        internal bool Near(bool[][] reached, Vector3 at, float metres)
        {
            int r = (int)MathF.Ceiling(metres / Step);
            int cx = (int)MathF.Floor(at.X / Step), cy = (int)MathF.Floor(at.Y / Step), cz = (int)MathF.Floor(at.Z / Step);
            for (int z = cz - r; z <= cz + r; z++)
            for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                (int box, int cell) = At(x, y, z);
                if (box >= 0 && reached[box][cell]) return true;
            }

            return false;
        }

        /// <summary>A flood's reach with every sample unreached, for setting samples by hand (the regression check).</summary>
        internal bool[][] Empty() => [.. Boxes.Select(b => new bool[b.Open.Length])];

        /// <summary>The sample at a point (system metres), for setting samples by hand (the regression check).</summary>
        internal (int Box, int Cell) Sample(Vector3 at) => At((int)MathF.Floor(at.X / Step), (int)MathF.Floor(at.Y / Step), (int)MathF.Floor(at.Z / Step));

        /// <summary>The roomiest open sample about a point in a box (its metres), as the cave view seeds its fill.</summary>
        internal (int Box, int Cell) Seed(int box, Vector3 inBox)
        {
            Box b = Boxes[box];
            int cell = CaveView.Seed(inBox, b.Open, b.Clearance, b.X, b.Y, b.Z, Step);
            return (box, cell);
        }

        /// <summary>The samples reached from the seed through samples at least this clear (any open sample for a negative clearance).</summary>
        internal bool[][] Flood((int Box, int Cell) seed, float minimum)
        {
            bool[][] reached = [.. Boxes.Select(b => new bool[b.Open.Length])];
            if (seed.Cell < 0) return reached;
            bool Passable(Box b, int i) => b.Open[i] && (minimum < 0f || b.Clearance[i] >= minimum);
            if (!Passable(Boxes[seed.Box], seed.Cell) && minimum >= 0f) return reached;
            Queue<(int Box, int Cell)> queue = new();
            reached[seed.Box][seed.Cell] = true;
            queue.Enqueue(seed);
            while (queue.TryDequeue(out (int Box, int Cell) at))
            {
                Box b = Boxes[at.Box];
                int x = at.Cell % b.X, y = (at.Cell / b.X) % b.Y, z = at.Cell / (b.X * b.Y);
                foreach ((int dx, int dy, int dz) in Faces6)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    int nb = at.Box;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= b.X || ny >= b.Y || nz >= b.Z)
                    {
                        // Across a face: into the box on the other side, if there is one.
                        int sx = (int)Math.Round(b.MetreX / Step) + nx, sy = (int)Math.Round(b.MetreY / Step) + ny, sz = (int)Math.Round(b.MetreZ / Step) + nz;
                        nb = byLattice.GetValueOrDefault((FloorDiv(sx, cellsX), FloorDiv(sy, cellsY), FloorDiv(sz, cellsZ)), -1);
                        if (nb < 0) continue;
                        Box other = Boxes[nb];
                        nx = sx - (int)Math.Round(other.MetreX / Step);
                        ny = sy - (int)Math.Round(other.MetreY / Step);
                        nz = sz - (int)Math.Round(other.MetreZ / Step);
                    }

                    Box target = Boxes[nb];
                    int next = (((nz * target.Y) + ny) * target.X) + nx;
                    if (!reached[nb][next] && Passable(target, next))
                    {
                        reached[nb][next] = true;
                        queue.Enqueue((nb, next));
                    }
                }
            }

            return reached;
        }
    }

    private sealed record PlannedBox(int Index, double OriginX, double OriginY, double OriginZ, int ChunksX, int ChunksY, int ChunksZ, float SizeX, float SizeY, float SizeZ, float Depth, float WaterLocalY, ulong Seed);
    private sealed record PlannedLink(int Index, int From, int To, KarstCave.BoxFace FromFace, KarstCave.BoxFace ToFace, float FaceU, float LocalVA, float LocalVB, float WorldX, float WorldY, float WorldZ, float Radius, float WaterWorldY, string Kind, bool Vertical);
    private sealed record PlannedMouth(int Index, int Box, KarstCave.BoxFace Face, string Kind, KarstCave.BoundaryPort? Port, float Tendril);
    private sealed record GeneratedBox(PlannedBox Box, DungeonLayout Layout, string Verdict, KarstCave.Report Report, double GenerateMs);
    private sealed record SeamMetrics(double OpenShareA, double OpenShareB, double Overlap, bool ConnectedBothSides, bool ReachedStand, bool ReachedCrawl, bool ReachedTight);
}
