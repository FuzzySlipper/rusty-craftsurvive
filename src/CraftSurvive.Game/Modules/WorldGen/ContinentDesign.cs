using System.Reflection;
using System.Text.Json;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A continent drawn by hand, which generation fills in (#9815, Den design/frontier-peninsula). It
/// sketches the big shapes: where land is, the range belts with their crest heights, the passes
/// through them, basins and uplands, and reserved sites. The recipe's noise adds detail and
/// variation and the simulation erodes, drains and weathers the result, so a seed varies the designed
/// continent rather than replacing it.
/// <para>
/// Positions are map-relative: x and z from -1 to 1 across the map, so a design fits any continental
/// size; z is south, as world z is (north is -z). Widths and radii are in the same units; heights are
/// metres. A design is part of the generator, versioned with it: changing one regenerates worlds.
/// </para>
/// </summary>
internal sealed record ContinentDesign
{
    public string Name { get; init; } = "";

    /// <summary>Land areas, drawn as the coast's middle line. Everything well outside them is sea.</summary>
    public IReadOnlyList<DesignLand> Land { get; init; } = [];

    /// <summary>
    /// The coast is undecided within this full width about the drawn outline (map units): noise resolves
    /// it into bays, headlands, estuaries and islands. Deeper inside is land, farther out is sea.
    /// </summary>
    public double CoastBand { get; init; } = 0.08;

    /// <summary>The size of the coast's largest bays and headlands, in map units.</summary>
    public double CoastWavelength { get; init; } = 0.08;

    /// <summary>
    /// Land rises from its resolved shore over about this many metres (varying along the coast, so cliffs
    /// and broad plains both meet the sea). It is measured from the real shore, not the drawn outline.
    /// </summary>
    public double ShoreRampMetres { get; init; } = 8000;

    /// <summary>Areas forced to land or to sea whatever the noise decides: the neck's core, and the moats that keep other bridges from forming.</summary>
    public IReadOnlyList<DesignZone> Fixed { get; init; } = [];

    /// <summary>The only place the peninsula may meet the mainland (checked, #9815).</summary>
    public IReadOnlyList<double[]> Neck { get; init; } = [];

    /// <summary>How much harder a belt's rock is at its crest (0 to 1): resistant rock stands taller and sharper under erosion.</summary>
    public double BeltHardness { get; init; } = 0.35;

    /// <summary>
    /// The highest the land rises away from the belts, in metres: the eroded lowland relief is scaled so
    /// its hills reach about this, and each belt's crest stands on it.
    /// </summary>
    public double LowlandPeak { get; init; } = 900;

    /// <summary>The size of the spurs and side valleys the belts' ridged noise cuts, in map units.</summary>
    public double RidgeWavelength { get; init; } = 0.02;

    /// <summary>Which way is cold and which way the wind blows, as compass bearings in degrees (0 north, 90 east).</summary>
    public DesignClimate? Climate { get; init; }

    public IReadOnlyList<DesignBelt> Belts { get; init; } = [];

    public IReadOnlyList<DesignPass> Passes { get; init; } = [];

    public IReadOnlyList<DesignArea> Areas { get; init; } = [];

    public IReadOnlyList<DesignSite> Sites { get; init; } = [];

    /// <summary>The highest crest any belt asks for, in metres.</summary>
    internal double HighestCrest => Belts.SelectMany(belt => belt.Points).Select(point => point.Length > 2 ? point[2] : 0).DefaultIfEmpty(0).Max();

    internal static JsonSerializerOptions Json { get; } = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>A design shipped with the generator, by name (an embedded <c>Designs/NAME.json</c>).</summary>
    internal static ContinentDesign Builtin(string name)
    {
        Assembly assembly = typeof(ContinentDesign).Assembly;
        string resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith($".{name}.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No built-in continent design '{name}'.");
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        return Parse(new StreamReader(stream).ReadToEnd());
    }

    internal static ContinentDesign Parse(string json) =>
        (JsonSerializer.Deserialize<ContinentDesign>(json, Json) ?? throw new InvalidDataException("An empty continent design.")).Validate();

    internal ContinentDesign Validate()
    {
        if (Land.Count == 0) throw new InvalidDataException($"Design '{Name}' has no land.");
        foreach (DesignLand land in Land)
            if (land.Points.Count < 3 || land.Points.Any(p => p.Length < 2)) throw new InvalidDataException($"Land '{land.Name}' needs three or more [x, z] points.");
        foreach (DesignZone zone in Fixed)
            if (zone.Points.Count < 3) throw new InvalidDataException($"Zone '{zone.Name}' needs three or more [x, z] points.");
        foreach (DesignBelt belt in Belts)
        {
            if (belt.Points.Count < 2 || belt.Points.Any(p => p.Length < 3)) throw new InvalidDataException($"Belt '{belt.Name}' needs two or more [x, z, crest metres] points.");
            if (belt.HalfWidth <= 0) throw new InvalidDataException($"Belt '{belt.Name}' needs a positive half-width.");
        }

        return this;
    }

    /// <summary>
    /// Signed distance to the land's outline, in map units: positive inside land, negative at sea.
    /// Where land areas overlap, the deepest inside wins.
    /// </summary>
    internal double LandDistance(double x, double z) => Land.Max(land => land.SignedDistance(x, z));

    /// <summary>Whether a point is forced to land (true) or sea (false), or left to the coast (null).</summary>
    internal bool? FixedAt(double x, double z)
    {
        foreach (DesignZone zone in Fixed)
            if (DesignGeometry.Inside(zone.Points, x, z)) return zone.Land;
        return null;
    }

    /// <summary>Whether a point lies in the neck, the only place the peninsula may meet the mainland.</summary>
    internal bool InNeck(double x, double z) => Neck.Count >= 3 && DesignGeometry.Inside(Neck, x, z);

    /// <summary>The land area containing a point, or null at sea.</summary>
    internal DesignLand? LandAt(double x, double z) => Land.FirstOrDefault(land => land.SignedDistance(x, z) > 0);

    /// <summary>
    /// The designed crest at a point, in metres: each belt's crest (interpolated along it) under its
    /// cross-section, the highest belt wins; dipped where a pass crosses.
    /// </summary>
    internal double Crest(double x, double z, IReadOnlyList<double>? gains = null)
    {
        double crest = 0;
        for (int b = 0; b < Belts.Count; b++)
        {
            DesignBelt belt = Belts[b];
            (double distance, double height) = belt.Nearest(x, z);
            if (distance >= belt.HalfWidth) continue;
            double across = distance / belt.HalfWidth;
            crest = Math.Max(crest, height * (gains?[b] ?? 1) * Math.Pow(1 - (across * across), belt.Sharpness));
        }

        foreach (DesignPass pass in Passes) crest *= 1 - (pass.Depth * PassNear(pass, x, z));
        return crest;
    }

    /// <summary>
    /// The designed crest at a point and how far up its belt's cross-section the point stands (1 on the
    /// crest line, 0 at the belt's edge), for the belt that rises highest there.
    /// </summary>
    internal (double Crest, double Profile) CrestAndProfile(double x, double z)
    {
        double crest = Crest(x, z);
        double profile = 0;
        foreach (DesignBelt belt in Belts)
        {
            (double distance, _) = belt.Nearest(x, z);
            if (distance < belt.HalfWidth) profile = Math.Max(profile, 1 - (distance / belt.HalfWidth));
        }

        return (crest, profile);
    }

    /// <summary>
    /// How much of a pass's dip reaches a point: a notch through the belt it crosses, narrow along the
    /// belt (its radius) and running right across it, so the way through is a valley, not a crater.
    /// </summary>
    private double PassNear(DesignPass pass, double x, double z)
    {
        (double along, double across, double halfWidth) = Belts.Select(belt => belt.Frame(pass.At[0], pass.At[1], x, z))
            .MinBy(frame => Math.Abs(frame.Across) < frame.HalfWidth * PassThrough ? Math.Abs(frame.Along) : double.PositiveInfinity);
        double reach = halfWidth * PassThrough;
        if (double.IsInfinity(along) || Math.Abs(across) >= reach) return 0;
        double notch = Math.Exp(-(along * along) / (pass.Radius * pass.Radius));
        double fade = 1 - Math.Pow(Math.Abs(across) / reach, 4);
        return notch * fade;
    }

    /// <summary>How far across its belt a pass's notch runs, in the belt's half-widths.</summary>
    private const double PassThrough = 1.5;

    /// <summary>The designed raise (positive) or lowering (negative) of the land at a point, as a share of its uplift.</summary>
    internal double AreaLift(double x, double z)
    {
        double lift = 0;
        foreach (DesignArea area in Areas)
        {
            double dx = x - area.At[0], dz = z - area.At[1];
            lift += area.Lift * Math.Exp(-((dx * dx) + (dz * dz)) / (area.Radius * area.Radius));
        }

        return lift;
    }
}

/// <summary>A land area as a closed outline of [x, z] points. A forbidden area is beyond the playable bounds (#9817).</summary>
internal sealed record DesignLand
{
    public string Name { get; init; } = "";

    public bool Forbidden { get; init; }

    public IReadOnlyList<double[]> Points { get; init; } = [];

    internal double SignedDistance(double x, double z)
    {
        double nearest = double.PositiveInfinity;
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            double[] a = Points[j], b = Points[i];
            nearest = Math.Min(nearest, DesignGeometry.SegmentDistance(x, z, a, b, out _));
            if ((b[1] > z) != (a[1] > z) && x < ((a[0] - b[0]) * (z - b[1]) / (a[1] - b[1])) + b[0]) inside = !inside;
        }

        return inside ? nearest : -nearest;
    }
}

/// <summary>A mountain belt: a line of [x, z, crest metres] points, its half-width, and how sharply its cross-section peaks (1 a dome, more a ridge).</summary>
internal sealed record DesignBelt
{
    public string Name { get; init; } = "";

    public IReadOnlyList<double[]> Points { get; init; } = [];

    public double HalfWidth { get; init; } = 0.05;

    public double Sharpness { get; init; } = 1.5;

    /// <summary>
    /// A point's place relative to another point on the belt (a pass): how far along the belt's line from
    /// it, and how far across, using the direction of the belt's segment nearest the pass.
    /// </summary>
    internal (double Along, double Across, double HalfWidth) Frame(double fromX, double fromZ, double x, double z)
    {
        int nearest = 1;
        double best = double.PositiveInfinity;
        for (int i = 1; i < Points.Count; i++)
        {
            double d = DesignGeometry.SegmentDistance(fromX, fromZ, Points[i - 1], Points[i], out _);
            if (d < best) (best, nearest) = (d, i);
        }

        if (best > HalfWidth) return (double.PositiveInfinity, double.PositiveInfinity, HalfWidth);
        double ex = Points[nearest][0] - Points[nearest - 1][0], ez = Points[nearest][1] - Points[nearest - 1][1];
        double length = Math.Sqrt((ex * ex) + (ez * ez));
        (ex, ez) = (ex / length, ez / length);
        double dx = x - fromX, dz = z - fromZ;
        return ((dx * ex) + (dz * ez), (dx * -ez) + (dz * ex), HalfWidth);
    }

    /// <summary>The distance to the belt's line and the crest height there, interpolated along the segment.</summary>
    internal (double Distance, double Crest) Nearest(double x, double z)
    {
        (double Distance, double Crest) best = (double.PositiveInfinity, 0);
        for (int i = 1; i < Points.Count; i++)
        {
            double[] a = Points[i - 1], b = Points[i];
            double distance = DesignGeometry.SegmentDistance(x, z, a, b, out double t);
            if (distance < best.Distance) best = (distance, a[2] + ((b[2] - a[2]) * t));
        }

        return best;
    }
}

/// <summary>A pass: where a belt's crest dips by a share (<see cref="Depth"/>, 0 to 1) over a radius.</summary>
internal sealed record DesignPass
{
    public string Name { get; init; } = "";

    public double[] At { get; init; } = [0, 0];

    public double Radius { get; init; } = 0.02;

    public double Depth { get; init; } = 0.6;
}

/// <summary>An upland (positive lift) or basin (negative) about a point: a share of uplift added or taken, easing out over its radius.</summary>
internal sealed record DesignArea
{
    public string Name { get; init; } = "";

    public double[] At { get; init; } = [0, 0];

    public double Radius { get; init; } = 0.1;

    public double Lift { get; init; }
}

/// <summary>An area forced to land or sea, as a closed outline of [x, z] points.</summary>
internal sealed record DesignZone
{
    public string Name { get; init; } = "";

    public bool Land { get; init; }

    public IReadOnlyList<double[]> Points { get; init; } = [];
}

/// <summary>A design's climate: the bearing the cold lies toward, and the bearing the prevailing wind blows toward.</summary>
internal sealed record DesignClimate
{
    public double ColdToward { get; init; }

    public double WindToward { get; init; } = 90;

    /// <summary>A bearing as an angle in the map's (x, z) plane, where north is -z.</summary>
    internal static double Angle(double bearingDegrees)
    {
        double b = bearingDegrees * Math.PI / 180;
        return Math.Atan2(-Math.Cos(b), Math.Sin(b));
    }
}

/// <summary>A reserved site: somewhere the design places a named thing, such as a frontier town (#9818).</summary>
internal sealed record DesignSite
{
    public string Name { get; init; } = "";

    public string Kind { get; init; } = "";

    public double[] At { get; init; } = [0, 0];
}

internal static class DesignGeometry
{
    /// <summary>Whether a point lies inside a closed outline (even-odd rule).</summary>
    internal static bool Inside(IReadOnlyList<double[]> points, double x, double z)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            double[] a = points[j], b = points[i];
            if ((b[1] > z) != (a[1] > z) && x < ((a[0] - b[0]) * (z - b[1]) / (a[1] - b[1])) + b[0]) inside = !inside;
        }

        return inside;
    }

    /// <summary>The distance from a point to the segment a-b, and where along it the nearest point is (0 at a, 1 at b).</summary>
    internal static double SegmentDistance(double x, double z, double[] a, double[] b, out double t)
    {
        double ex = b[0] - a[0], ez = b[1] - a[1];
        double length = (ex * ex) + (ez * ez);
        t = length <= 0 ? 0 : Math.Clamp((((x - a[0]) * ex) + ((z - a[1]) * ez)) / length, 0, 1);
        double px = a[0] + (ex * t) - x, pz = a[1] + (ez * t) - z;
        return Math.Sqrt((px * px) + (pz * pz));
    }
}
