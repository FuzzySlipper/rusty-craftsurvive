using System.Globalization;
using CraftSurvive.Game.Modules.WorldGen;

namespace MapLab;

/// <summary>
/// Measures of a generated map beyond its heights (#9815): whether a design's topology held (the
/// peninsula meets the mainland only through the neck), how prominent its peaks are and how high the
/// saddles between them, how much relief a traveller meets, and how much easy lowland there is.
/// Everything is measured on the tool's sampled grid.
/// </summary>
internal static class Terrain
{
    /// <summary>Local relief is the range of heights within this radius.</summary>
    private const double ReliefRadiusMetres = 5000;
    /// <summary>Lowland is land under this slope (rise over run).</summary>
    private const double GentleSlope = 0.05;
    private const int PeaksReported = 10;

    internal static void Report(Action<string> line, double[,] height, double cell, double radius, double water, ContinentDesign? design)
    {
        int n = height.GetLength(0);
        bool Land(int x, int z) => height[x, z] > water;
        if (design is not null) Topology(line, n, Land, radius, cell, design);
        Peaks(line, height, n, cell, radius, Land);
        Relief(line, height, n, cell, Land);
    }

    private static void Topology(Action<string> line, int n, Func<int, int, bool> land, double radius, double cell, ContinentDesign design)
    {
        (int X, int Z) Pixel(double u, double v) => ((int)((u + 1) / 2 * n), (int)((v + 1) / 2 * n));
        double U(int x) => ((x + 0.5) / n * 2) - 1;
        (int X, int Z) peninsula = Pixel(0, 0);
        DesignLand? mainland = design.Land.FirstOrDefault(l => l.Forbidden);
        if (mainland is null)
        {
            line("  topology: no forbidden mainland in the design");
            return;
        }

        (int X, int Z) inland = Pixel(mainland.Points.Average(p => p[0]), mainland.Points.Average(p => p[1]));
        int[,] component = Components(n, land, (_, _) => true, out int count, out int[] sizes);
        bool connected = component[peninsula.X, peninsula.Z] >= 0 && component[peninsula.X, peninsula.Z] == component[inland.X, inland.Z];
        int[,] outside = Components(n, land, (x, z) => !design.InNeck(U(x), U(z)), out _, out _);
        bool bridge = outside[peninsula.X, peninsula.Z] >= 0 && outside[peninsula.X, peninsula.Z] == outside[inland.X, inland.Z];
        int main = component[peninsula.X, peninsula.Z];
        int islands = Enumerable.Range(0, count).Count(c => c != main && c != component[inland.X, inland.Z] && sizes[c] >= 2);
        // The neck's width: across its band of rows, the fewest land cells in any one row.
        int narrowest = int.MaxValue;
        for (int z = 0; z < n; z++)
        {
            int across = 0, inNeck = 0;
            for (int x = 0; x < n; x++)
            {
                if (!design.InNeck(U(x), U(z))) continue;
                inNeck++;
                if (land(x, z) && component[x, z] == main) across++;
            }

            if (inNeck > 0) narrowest = Math.Min(narrowest, across);
        }

        line(string.Create(CultureInfo.InvariantCulture,
            $"  topology: peninsula meets the mainland {(connected ? "yes" : "NO")}; a bridge outside the neck {(bridge ? "YES" : "no")}; narrowest row of the neck {narrowest * cell / 1000:F0} km of land; islands {islands}"));
    }

    /// <summary>The 4-connected components of the cells that are land and allowed; -1 where not.</summary>
    private static int[,] Components(int n, Func<int, int, bool> land, Func<int, int, bool> allowed, out int count, out int[] sizes)
    {
        int[,] id = new int[n, n];
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) id[x, z] = -1;
        List<int> size = [];
        Stack<(int, int)> open = new();
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            if (id[x, z] >= 0 || !land(x, z) || !allowed(x, z)) continue;
            int c = size.Count, members = 0;
            size.Add(0);
            open.Push((x, z));
            id[x, z] = c;
            while (open.TryPop(out (int X, int Z) at))
            {
                members++;
                foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int px = at.X + dx, pz = at.Z + dz;
                    if (px < 0 || pz < 0 || px >= n || pz >= n || id[px, pz] >= 0 || !land(px, pz) || !allowed(px, pz)) continue;
                    id[px, pz] = c;
                    open.Push((px, pz));
                }
            }

            size[c] = members;
        }

        count = size.Count;
        sizes = [.. size];
        return id;
    }

    /// <summary>
    /// Topographic prominence: cells are added from the highest down; where two islands of higher ground
    /// first meet, the lower of their summits has its prominence (its height above that key saddle).
    /// </summary>
    private static void Peaks(Action<string> line, double[,] height, int n, double cell, double radius, Func<int, int, bool> land)
    {
        int[] order = [.. Enumerable.Range(0, n * n).Where(i => land(i % n, i / n)).OrderByDescending(i => height[i % n, i / n])];
        int[] parent = new int[n * n];
        int[] summit = new int[n * n];
        Array.Fill(parent, -1);
        List<(int Summit, double Prominence, double Saddle)> found = [];
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        foreach (int i in order)
        {
            parent[i] = i;
            summit[i] = i;
            int x = i % n, z = i / n;
            foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
            {
                int px = x + dx, pz = z + dz;
                if (px < 0 || pz < 0 || px >= n || pz >= n) continue;
                int j = (pz * n) + px;
                if (parent[j] < 0) continue;
                int a = Find(i), b = Find(j);
                if (a == b) continue;
                double ha = height[summit[a] % n, summit[a] / n], hb = height[summit[b] % n, summit[b] / n];
                (int keep, int lose) = ha >= hb ? (a, b) : (b, a);
                double saddle = height[x, z];
                found.Add((summit[lose], height[summit[lose] % n, summit[lose] / n] - saddle, saddle));
                parent[lose] = keep;
            }
        }

        // The highest summit of each landmass never meets higher ground: its prominence is its height.
        foreach (int root in order.Select(Find).Distinct())
            found.Add((summit[root], height[summit[root] % n, summit[root] / n] - 0, 0));
        double World(int index) => (-radius) + ((index + 0.5) * cell);
        foreach ((int top, double prominence, double saddle) in found.OrderByDescending(f => f.Prominence).Take(PeaksReported))
        {
            line(string.Create(CultureInfo.InvariantCulture,
                $"  peak {height[top % n, top / n]:F0}m at {World(top % n) / 1000:F0},{World(top / n) / 1000:F0} km: prominence {prominence:F0}m over a saddle at {saddle:F0}m"));
        }
    }

    private static void Relief(Action<string> line, double[,] height, int n, double cell, Func<int, int, bool> land)
    {
        int reach = Math.Max(1, (int)Math.Round(ReliefRadiusMetres / cell));
        List<double> relief = [];
        int gentle = 0, total = 0;
        for (int z = 0; z < n; z += 2)
        for (int x = 0; x < n; x += 2)
        {
            if (!land(x, z)) continue;
            total++;
            double low = double.MaxValue, high = double.MinValue;
            for (int dz = -reach; dz <= reach; dz += Math.Max(1, reach / 4))
            for (int dx = -reach; dx <= reach; dx += Math.Max(1, reach / 4))
            {
                int px = Math.Clamp(x + dx, 0, n - 1), pz = Math.Clamp(z + dz, 0, n - 1);
                low = Math.Min(low, height[px, pz]);
                high = Math.Max(high, height[px, pz]);
            }

            relief.Add(high - low);
            double sx = (height[Math.Min(x + 1, n - 1), z] - height[Math.Max(x - 1, 0), z]) / (2 * cell);
            double sz = (height[x, Math.Min(z + 1, n - 1)] - height[x, Math.Max(z - 1, 0)]) / (2 * cell);
            if (Math.Sqrt((sx * sx) + (sz * sz)) < GentleSlope) gentle++;
        }

        relief.Sort();
        line(string.Create(CultureInfo.InvariantCulture,
            $"  local relief within {ReliefRadiusMetres / 1000:F0} km: median {relief[relief.Count / 2]:F0}m, 90th {relief[(int)(relief.Count * 0.9)]:F0}m, max {relief[^1]:F0}m; gentle land (slope under {GentleSlope * 100:F0}%) {100.0 * gentle / Math.Max(total, 1):F0}%"));
    }
}
