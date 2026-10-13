using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The sea and the lakes on the map as flat surfaces at their levels (#9952): a plane over the whole map
/// at sea level, and over each lake's basin at its level, a little over the ground the tiers flatten to
/// those levels. Where the ground rises out of the water it hides the surface, so a shore follows the
/// relief as the tiers draw it rather than the steps of painted cells. Built once for a map.
/// </summary>
internal sealed class MapWaterSurfaces : IDisposable
{
    private const uint MaterialSlot = 0;

    private readonly List<(MeshResource Mesh, Appearance Look)> surfaces = [];
    private readonly Dictionary<LakeKind, Material> lakeMaterials = [];

    /// <summary>A lake's surface is cut where the drawn ground meets its level, sampled at this share of the lattice spacing.</summary>
    private const double ContourStepShare = 0.5;

    /// <summary>
    /// Beyond its basin a lake's water thins this much a lattice spacing (metres), from three quarters of a
    /// spacing out, so where the smoothed ground stays under the level past the rim the edge curves in
    /// rather than stopping in the lattice's steps.
    /// </summary>
    private const double BasinFalloffMetres = 40, BasinFalloffFrom = 0.75;

    /// <summary>The lattice nodes about a basin searched for its nearest node.</summary>
    private const int BasinSearch = 2;

    /// <param name="ground">The drawn ground at a world point (metres), which the water's edge follows.</param>
    /// <param name="place">Where a world point at a water level is drawn, in map units, already lifted over the ground.</param>
    internal MapWaterSurfaces(IEngineContext engine, WorldMap map, Material sea, Func<LakeKind, Material> lakeMaterial,
        Func<double, double, double> ground, Func<double, double, double, Vector3> place)
    {
        ArgumentNullException.ThrowIfNull(engine);
        double r = map.Radius;
        Add(engine, sea, [place(-r, -r, GenerationConstants.WaterLevel), place(r, -r, GenerationConstants.WaterLevel),
            place(-r, r, GenerationConstants.WaterLevel), place(r, r, GenerationConstants.WaterLevel)], [0, 2, 1, 1, 2, 3]);
        double spacing = map.Grid.Spacing, step = spacing * ContourStepShare;
        foreach (Lake lake in map.Lakes)
        {
            if (lake.Basin.Length == 0 || surfaces.Count >= ProductIds.WorldMapWaterLimit) continue;
            // Water stands where the ground is under the lake's level, thinning away beyond the basin.
            HashSet<(long X, long Z)> basin = [];
            double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
            foreach (int node in lake.Basin)
            {
                double x = map.Grid.X(node), z = map.Grid.Z(node);
                (minX, minZ, maxX, maxZ) = (Math.Min(minX, x), Math.Min(minZ, z), Math.Max(maxX, x), Math.Max(maxZ, z));
                basin.Add(((long)Math.Round(x / spacing), (long)Math.Round(z / spacing)));
            }

            double margin = BasinSearch * spacing;
            (minX, minZ, maxX, maxZ) = (minX - margin, minZ - margin, maxX + margin, maxZ + margin);
            int nx = (int)Math.Ceiling((maxX - minX) / step), nz = (int)Math.Ceiling((maxZ - minZ) / step);
            double[] depth = new double[(nx + 1) * (nz + 1)];
            for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                double x = minX + (i * step), z = minZ + (j * step);
                double outside = Math.Max(0, (Beyond(basin, x, z, spacing) / spacing) - BasinFalloffFrom);
                // The square's edge is always dry, so every surface closes inside it.
                bool rim = i == 0 || j == 0 || i == nx || j == nz;
                depth[(j * (nx + 1)) + i] = rim ? -1 : lake.Level - ground(x, z) - (BasinFalloffMetres * outside);
            }

            List<Vector3> positions = [];
            List<uint> indices = [];
            for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                Wet(minX + (i * step), minZ + (j * step), step,
                    [depth[(j * (nx + 1)) + i], depth[(j * (nx + 1)) + i + 1], depth[((j + 1) * (nx + 1)) + i + 1], depth[((j + 1) * (nx + 1)) + i]],
                    (x, z) => place(x, z, lake.Level), positions, indices);
            }

            if (indices.Count == 0) continue;
            if (!lakeMaterials.TryGetValue(lake.Kind, out Material? material)) lakeMaterials[lake.Kind] = material = lakeMaterial(lake.Kind);
            Add(engine, material, positions, indices);
        }
    }

    /// <summary>How far a point is from the nearest node of a basin (metres), searched a few nodes about it.</summary>
    private static double Beyond(HashSet<(long X, long Z)> basin, double x, double z, double spacing)
    {
        long ix = (long)Math.Round(x / spacing), iz = (long)Math.Round(z / spacing);
        double nearest = double.MaxValue;
        for (long dz = -BasinSearch; dz <= BasinSearch; dz++)
        for (long dx = -BasinSearch; dx <= BasinSearch; dx++)
            if (basin.Contains((ix + dx, iz + dz)))
                nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(((ix + dx) * spacing) - x, 2) + Math.Pow(((iz + dz) * spacing) - z, 2)));
        return nearest == double.MaxValue ? (BasinSearch + 1) * spacing : nearest;
    }

    /// <summary>
    /// The wet part of one square (marching squares): its corners where the water is deeper than nothing,
    /// and where each side crosses the shore, as a fan. Corners run x then z around the square.
    /// </summary>
    private static void Wet(double x, double z, double size, double[] depth, Func<double, double, Vector3> place, List<Vector3> positions, List<uint> indices)
    {
        (double X, double Z)[] corners = [(x, z), (x + size, z), (x + size, z + size), (x, z + size)];
        List<Vector3> polygon = [];
        for (int k = 0; k < 4; k++)
        {
            int next = (k + 1) % 4;
            if (depth[k] > 0) polygon.Add(place(corners[k].X, corners[k].Z));
            if ((depth[k] > 0) != (depth[next] > 0))
            {
                double t = depth[k] / (depth[k] - depth[next]);
                polygon.Add(place(corners[k].X + ((corners[next].X - corners[k].X) * t), corners[k].Z + ((corners[next].Z - corners[k].Z) * t)));
            }
        }

        if (polygon.Count < 3) return;
        uint first = (uint)positions.Count;
        positions.AddRange(polygon);
        for (int k = 1; k + 1 < polygon.Count; k++)
        {
            uint a = first, b = first + (uint)k, c = first + (uint)k + 1;
            bool up = Vector3.Cross(positions[(int)b] - positions[(int)a], positions[(int)c] - positions[(int)a]).Y >= 0;
            indices.AddRange(up ? [a, b, c] : [a, c, b]);
        }
    }

    internal IEnumerable<AppearanceFact> Facts => surfaces.Select((surface, i) => new AppearanceFact(ProductIds.WorldMapWaterBase + (ulong)i, false, 0,
        new(Vector3.Zero, Quaternion.Identity, Vector3.One), surface.Look, true, RenderLayer.Scene));

    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in surfaces)
        {
            look.Dispose();
            mesh.Dispose();
        }

        foreach (Material material in lakeMaterials.Values) material.Dispose();
        surfaces.Clear();
        lakeMaterials.Clear();
    }

    private void Add(IEngineContext engine, Material material, List<Vector3> positions, List<uint> indices)
    {
        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(),
            Enumerable.Repeat(Vector3.UnitY, positions.Count).ToArray(), new Vector2[positions.Count],
            Enumerable.Repeat(new Color(1, 1, 1, 1), positions.Count).ToArray(), indices.ToArray(),
            new MeshGroup[] { new(MaterialSlot, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(MaterialSlot, material) }));
        surfaces.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
    }
}
