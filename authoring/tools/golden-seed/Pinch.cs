using System.Numerics;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.WorldGen;

namespace GoldenSeed;

/// <summary>
/// Why a cave's route is out of reach: the same fill as the cave view on the field at each stage the generator builds it
/// (its samples as the passages were checked, then the fine forms, then the ruin), against the generator's own clearance.
/// </summary>
internal static class Pinch
{
    internal static void Run(string id, ulong entranceSeed, CaveSetting setting, CavePlacement placement, bool summary, Action<string> log)
    {
        DungeonCandidate cave = DungeonCandidates.Generate(DungeonApproach.Karst, entranceSeed, 0, 1d, null, setting, UnderCaves.Settings());
        UndergroundCave prepared = UnderCaves.Prepare(cave, placement);
        Func<Vector3, float> shape = cave.Layout.Shape!;
        KarstCave.Settings saved = KarstCave.Next;
        KarstCave.Next = UnderCaves.Settings();
        KarstCave.Result result;
        try { result = KarstCave.Build(cave.Seed, null, setting); }
        finally { KarstCave.Next = saved; }

        if (!summary) log(KarstCave.Readout(result));
        float[] open = result.Open;
        float Samples(Vector3 p) => Trilinear(open, p);
        Vector3 start = CaveView.JoinOf(prepared.Layout.MainRoute, cave.Layout.MainRoute) ?? cave.Layout.Arrival;
        float mx = cave.Walkable.SizeX, my = cave.Walkable.SizeY, mz = cave.Walkable.SizeZ;
        (string Name, Func<Vector3, float> Field)[] stages =
        [
            ("samples (as the passages were checked)", Samples),
            ("+ fine forms (the layout's field)", result.At),
            ("+ ruin (the shape the game samples)", shape),
        ];
        List<Vector3> route = result.MainRoute;
        float[] generator = result.Clearance;
        float[] after = KarstCave.Clearance(open);
        CaveView.Reach[] fills = [.. stages.Select(s => CaveView.Reach.Of(s.Field, mx, my, mz, start))];
        if (summary)
        {
            // One line per cave: at each stage, the share of the room a body fits in (a crawl's clearance or more) that the fill
            // reached, and the share of the generator's main route.
            string Shares(CaveView.Reach fill)
            {
                int room = 0, reached = 0;
                for (int i = 0; i < fill.Open.Length; i++)
                {
                    if (!fill.Open[i] || fill.Clearance[i] < KarstCave.CrawlRadius) continue;
                    room++;
                    if (fill.Reached[i]) reached++;
                }

                int along = route.Count(p => Near(fill, p, (f, i) => f.Reached[i]));
                return FormattableString.Invariant($"{reached / (double)Math.Max(1, room):F2} {along / (double)Math.Max(1, route.Count):F2}");
            }

            log($"{id} ruin={KarstCave.LastRuin?.Size.ToString() ?? "none"} widened={result.Passages.Sum(w => w.Widened)} " + string.Join(" | ", fills.Select(Shares)));
            return;
        }

        for (int s = 0; s < stages.Length; s++)
        {
            CaveView.Reach fill = fills[s];
            int reached = fill.Reached.Count(r => r), opened = fill.Open.Count(o => o);
            int along = route.Count(p => Near(fill, p, (f, i) => f.Reached[i]));
            log($"{stages[s].Name}: {reached * 100.0 / opened:F0}% of open samples reached, route {along}/{route.Count} reached");
        }

        // What the ruin closed of the cave's air along the route (within 1.5 m of a route point): each closed sample by cause.
        if (KarstCave.LastRuin is CaveRuin ruin)
        {
            log($"ruin {ruin.Size}: {ruin.Modules.Count} modules, cells {string.Join(" ", ruin.Modules.SelectMany(m => m.Cells()).Distinct())}");
            Dictionary<string, int> causes = [];
            HashSet<(int, int, int)> seen = [];
            const float Near = 1.5f, Step = 0.25f;
            foreach (Vector3 p in route)
            for (float dz = -Near; dz <= Near; dz += Step)
            for (float dy = -Near; dy <= Near; dy += Step)
            for (float dx = -Near; dx <= Near; dx += Step)
            {
                Vector3 q = p + new Vector3(dx, dy, dz);
                if (!seen.Add(((int)(q.X / Step), (int)(q.Y / Step), (int)(q.Z / Step)))) continue;
                if (result.At(q) <= 0f || shape(q) > 0f) continue;
                int x = (int)MathF.Floor(q.X), y = (int)MathF.Floor(q.Y), z = (int)MathF.Floor(q.Z);
                int row = (y - ModularDungeon.BaseY) % ModuleCanvas.StoreyHeight;
                string cause = !ruin.Inside(q)
                    ? ruin.Footings.Contains((x, y, z)) ? "footing under its lowest floor" : "gap closed against its wall or ramp stone"
                    : ruin.Overgrown.ContainsKey((x, y, z)) ? "rock overgrowing a burst"
                    : row == 0 ? "its floor slab (row 0, walked masonry stands)"
                    : row == ModuleCanvas.StoreyHeight - 1 ? "its ceiling slab (top row, walked masonry stands)"
                    : "its wall or fitting (not burst)";
                causes[cause] = causes.GetValueOrDefault(cause) + 1;
            }

            foreach ((string cause, int count) in causes.OrderByDescending(c => c.Value))
                log($"  closed near the route by {cause}: {count * Step * Step * Step:F1} m³");

            // The game's own voxels (the prepared cave the walking session's chunks read) down the column at the first route
            // point the ruin closes.
            DungeonVolume voxels = cave.AllVoxels.Volume;
            foreach (Vector3 p in route.Where(p => result.At(p) > 0f && shape(p) <= 0f).Take(1))
            {
                int x = (int)p.X, z = (int)p.Z;
                log($"  voxels at x {x} z {z} where the ruin first closes the route ({p.X:F1},{p.Y:F1},{p.Z:F1}): "
                    + string.Join(", ", Enumerable.Range((int)p.Y - 4, 9).Select(y => $"y{y} {voxels.At(x, y, z)}")));
            }
        }

        log("route point: generator clearance (as checked / on the final samples), view clearance per stage, field per stage");
        for (int i = 0; i < route.Count; i++)
        {
            Vector3 p = route[i];
            int f = KarstCave.FieldIndex((int)(p.X / KarstCave.FieldStep), (int)(p.Y / KarstCave.FieldStep), (int)(p.Z / KarstCave.FieldStep));
            string views = string.Join(" ", fills.Select(fill => $"{Best(fill, p):F2}{(Near(fill, p, (r, k) => r.Reached[k]) ? "" : "x")}"));
            string fields = string.Join(" ", stages.Select(st => $"{st.Field(p):F2}"));
            log($"{i,3} ({p.X:F1},{p.Y:F1},{p.Z:F1}) gen {generator[f]:F2}/{after[f]:F2}  view {views}  field {fields}");
        }
    }

    // The view's samples within a quarter metre of a point (a route point is a generator sample's centre, a corner of the
    // view's samples).
    private static IEnumerable<int> Around(CaveView.Reach fill, Vector3 p)
    {
        float s = fill.Step;
        int x0 = (int)((p.X - (s / 2)) / s), y0 = (int)((p.Y - (s / 2)) / s), z0 = (int)((p.Z - (s / 2)) / s);
        for (int z = z0; z <= z0 + 1; z++)
        for (int y = y0; y <= y0 + 1; y++)
        for (int x = x0; x <= x0 + 1; x++)
            if (x >= 0 && y >= 0 && z >= 0 && x < fill.SizeX && y < fill.SizeY && z < fill.SizeZ)
                yield return (((z * fill.SizeY) + y) * fill.SizeX) + x;
    }

    private static float Best(CaveView.Reach fill, Vector3 p) => Around(fill, p).Select(i => fill.Open[i] ? fill.Clearance[i] : 0f).DefaultIfEmpty(0f).Max();

    private static bool Near(CaveView.Reach fill, Vector3 p, Func<CaveView.Reach, int, bool> test) => Around(fill, p).Any(i => test(fill, i));

    private static float Trilinear(float[] field, Vector3 p)
    {
        float step = KarstCave.FieldStep;
        float fx = (p.X / step) - 0.5f, fy = (p.Y / step) - 0.5f, fz = (p.Z / step) - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, KarstCave.FieldX - 2), y0 = Math.Clamp((int)MathF.Floor(fy), 0, KarstCave.FieldY - 2), z0 = Math.Clamp((int)MathF.Floor(fz), 0, KarstCave.FieldZ - 2);
        float tx = Math.Clamp(fx - x0, 0f, 1f), ty = Math.Clamp(fy - y0, 0f, 1f), tz = Math.Clamp(fz - z0, 0f, 1f);
        float At(int x, int y, int z) => field[KarstCave.FieldIndex(x, y, z)];
        float Lerp(float a, float b, float t) => a + ((b - a) * t);
        float c00 = Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), tx), c10 = Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), tx);
        float c01 = Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), tx), c11 = Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), tx);
        return Lerp(Lerp(c00, c10, ty), Lerp(c01, c11, ty), tz);
    }
}
