using System.Numerics;
using System.Runtime.CompilerServices;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Which side of a designed continent's bounds a point lies on (#9817).</summary>
internal enum BoundSide { Open, BeyondFrontier, OpenSea, Town }

/// <summary>
/// The bounds of a continent drawn to a design (#9817, owner decisions 2026-10-10): the frontier past
/// the passes, and the open sea. The expedition is turned back at either, on the map and on foot: it may
/// stand in a pass and look down on the mainland, and swim a short way out from any shore, no further.
/// It is one rule for every path; map routing, walking, arrival and the debug teleport all ask it.
/// <para>
/// The open sea is measured from the continent's own lattice: a point whose nearest land node is farther
/// than <see cref="OpenSeaMetres"/> (bilinear between nodes). The sea floor is too shallow to tell the open
/// sea by depth. A region tile's finer coast lies within a lattice spacing of that land.
/// </para>
/// </summary>
internal sealed class FrontierBound
{
    /// <summary>The open sea begins this far from the continent's nearest land node.</summary>
    internal const double OpenSeaMetres = 1500;

    /// <summary>A reserved site stands on the nearest lattice node this far above the water, searched out to this many nodes.</summary>
    private const double SiteDryMetres = 5;
    private const int SiteSearchNodes = 16;

    private static readonly ConditionalWeakTable<WorldMap, FrontierBound> Bounds = [];

    private readonly ContinentDesign design;
    private readonly WorldMap continent;
    private readonly MapGrid grid;
    private readonly float[] fromLand;

    private FrontierBound(WorldMap continent, ContinentDesign design)
    {
        this.design = design;
        this.continent = continent;
        grid = continent.Grid;
        fromLand = DistanceFromLand(grid, continent.Fields.Elevation);
        Sites = [.. design.Sites.Select(site => Stand(site, continent.Fields.Elevation)).OfType<PlacedSite>()];
    }

    /// <summary>
    /// The design's reserved sites where they stand on this continent (the mountain hold, #9817, #9875): each on
    /// the nearest dry, open ground to where it was drawn, since the seed may put a lake or a bay there.
    /// A site with no such ground near is left out.
    /// </summary>
    internal IReadOnlyList<PlacedSite> Sites { get; }

    /// <summary>
    /// The bounds of a whole continent drawn to a design, built once per map; null for a world without one
    /// (a regional world keeps its edge) and for a region tile, whose continent holds them.
    /// </summary>
    internal static FrontierBound? For(WorldMap map) =>
        map.RegionFrame is null && ContinentDesign.For(map.Configuration.Size) is ContinentDesign design
            ? Bounds.GetValue(map, continent => new FrontierBound(continent, design))
            : null;

    /// <summary>Which side of the bounds a world point lies on.</summary>
    internal BoundSide At(double x, double z) =>
        design.BeyondFrontier(x / grid.Radius, z / grid.Radius) ? BoundSide.BeyondFrontier
        : SeaMetres(x, z) > OpenSeaMetres ? BoundSide.OpenSea
        : BoundSide.Open;

    internal bool Open(double x, double z) => At(x, z) == BoundSide.Open;

    /// <summary>How far a world point is from the continent's nearest land node, in metres; infinite off the map.</summary>
    internal double SeaMetres(double x, double z)
    {
        double gx = (x + grid.Radius) / grid.Spacing, gz = (z + grid.Radius) / grid.Spacing;
        if (!(gx >= 0 && gz >= 0 && gx <= grid.Segments && gz <= grid.Segments)) return double.PositiveInfinity;
        int x0 = Math.Min((int)gx, grid.Segments - 1), z0 = Math.Min((int)gz, grid.Segments - 1);
        double fx = gx - x0, fz = gz - z0;
        int i = (z0 * grid.Side) + x0;
        double top = fromLand[i] + ((fromLand[i + 1] - fromLand[i]) * fx);
        double bottom = fromLand[i + grid.Side] + ((fromLand[i + grid.Side + 1] - fromLand[i + grid.Side]) * fx);
        return top + ((bottom - top) * fz);
    }

    /// <summary>
    /// Points about every <paramref name="spacingMetres"/> along the frontier where it crosses land, for
    /// drawing it on the map (#9817): out at sea the open sea is the bound.
    /// </summary>
    internal IReadOnlyList<Vector2> FrontierOnLand(double spacingMetres)
    {
        List<Vector2> points = [];
        IReadOnlyList<double[]> line = design.Frontier;
        for (int i = 0, j = line.Count - 1; i < line.Count; j = i++)
        {
            Vector2 a = new((float)(line[j][0] * grid.Radius), (float)(line[j][1] * grid.Radius));
            Vector2 b = new((float)(line[i][0] * grid.Radius), (float)(line[i][1] * grid.Radius));
            int steps = Math.Max(1, (int)Math.Round(Vector2.Distance(a, b) / spacingMetres));
            for (int s = 0; s < steps; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)s / steps);
                if (continent.Contains(p.X, p.Y) && continent.Sample(p.X, p.Y).Elevation >= GenerationConstants.WaterLevel) points.Add(p);
            }
        }

        return points;
    }

    private PlacedSite? Stand(DesignSite site, float[] elevation)
    {
        int centre = grid.Nearest(site.At[0] * grid.Radius, site.At[1] * grid.Radius);
        int cx = centre % grid.Side, cz = centre / grid.Side;
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int dz = -SiteSearchNodes; dz <= SiteSearchNodes; dz++)
        for (int dx = -SiteSearchNodes; dx <= SiteSearchNodes; dx++)
        {
            int x = cx + dx, z = cz + dz;
            if (x < 0 || z < 0 || x > grid.Segments || z > grid.Segments) continue;
            int i = (z * grid.Side) + x;
            double distance = (dx * dx) + (dz * dz);
            if (distance >= bestDistance || elevation[i] < GenerationConstants.WaterLevel + SiteDryMetres || !Open(grid.X(i), grid.Z(i))) continue;
            best = i;
            bestDistance = distance;
        }

        return best < 0 ? null : new PlacedSite(site.Name, site.Kind, grid.X(best), grid.Z(best));
    }

    /// <summary>
    /// Each node's distance to the nearest land node in metres, by a two-pass chamfer over the lattice
    /// (straight and diagonal neighbours), within a few percent of the true distance.
    /// </summary>
    private static float[] DistanceFromLand(MapGrid grid, float[] elevation)
    {
        int side = grid.Side;
        float straight = (float)grid.Spacing, diagonal = (float)(grid.Spacing * Math.Sqrt(2));
        float[] distance = new float[grid.Count];
        for (int i = 0; i < grid.Count; i++) distance[i] = elevation[i] >= GenerationConstants.WaterLevel ? 0 : float.PositiveInfinity;
        void Relax(int at, int x, int z, int dx, int dz, float cost)
        {
            int nx = x + dx, nz = z + dz;
            if (nx < 0 || nz < 0 || nx >= side || nz >= side) return;
            distance[at] = Math.Min(distance[at], distance[(nz * side) + nx] + cost);
        }

        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int at = (z * side) + x;
            Relax(at, x, z, -1, 0, straight);
            Relax(at, x, z, 0, -1, straight);
            Relax(at, x, z, -1, -1, diagonal);
            Relax(at, x, z, 1, -1, diagonal);
        }

        for (int z = side - 1; z >= 0; z--)
        for (int x = side - 1; x >= 0; x--)
        {
            int at = (z * side) + x;
            Relax(at, x, z, 1, 0, straight);
            Relax(at, x, z, 0, 1, straight);
            Relax(at, x, z, 1, 1, diagonal);
            Relax(at, x, z, -1, 1, diagonal);
        }

        return distance;
    }
}

/// <summary>A design's reserved site where it stands on a continent, in world metres.</summary>
internal sealed record PlacedSite(string Name, string Kind, double X, double Z);
