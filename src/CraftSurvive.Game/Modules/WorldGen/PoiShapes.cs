namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The ancient landmarks as signed distance fields (#9671): curved arches, tapered ribs and steles
/// with round openings, after the approved Paperback Sanctum vocabulary, instead of stacked blocks.
/// A shape is a pure function of its site. A voxel whose centre is inside (negative distance) is
/// filled with stone, and the distance itself becomes the voxel's density near the surface, so the
/// Engine's dual contouring reconstructs smooth faceted stone rather than cubes.
///
/// Coordinates are in metres relative to the site: x and z from the centre of the site's voxel,
/// y up from the top face of its ground course.
/// </summary>
internal static class PoiShapes
{
    // Ribs and walls are at least about two metres thick: on one-metre voxels, dual contouring drops
    // thinner stone.

    /// <summary>Distances farther than this from a shape's surface are not reported: they cannot move it.</summary>
    internal const double Band = 3.0;

    // Dungeon entrance: a low platform around the shaft, crossed by two arches that meet over it.
    private const double PlatformHalf = 3.5, PlatformTop = 0.25, PlatformDepth = 1.2, PlatformRound = 0.4;
    private const double ShaftHalf = 1.5;
    private const double GateArchRadius = 4.6, GateRibBase = 1.35, GateRibTop = 1.0;

    // Ruin: a broken curved wall around the site and leaning ribs rising from it.
    private const double RuinRadius = 5.0, RuinWallHalfThickness = 0.85;
    private const double RuinRibBase = 1.05, RuinRibTop = 0.65, RuinRibInward = 0.55, RuinRibRise = 1.3;
    private const int RuinRibs = 3;

    // Standing stones: tapered steles around a ring, the tallest pierced by a round opening.
    private const double SteleBase = 0.95, SteleTop = 0.5, SteleLean = 0.08, PortholeRadius = 0.5, PortholeAt = 0.68;
    private const double CairnRadius = 0.9;

    private const double LookoutRound = 0.3;

    // Cave mouth: a tall arch framing the opening, set in the plane of the mouth.
    private const double CaveArchRadius = 2.9, CaveArchRib = 1.15, CaveArchStretch = 1.5, CaveArchSetBack = 0.8;

    /// <summary>The site's shape distance at a voxel centre, or null for a kind built from blocks.</summary>
    internal static double? DistanceAt(PoiSite site, long x, long y, long z)
    {
        Vector3d p = new(x + 0.5 - (site.X + 0.5), y + 0.5 - (site.Ground + 1), z + 0.5 - (site.Z + 0.5));
        double? shape = site.Kind switch
        {
            PoiKind.DungeonEntrance => Gate(p),
            PoiKind.Ruin => Ruin(site, p),
            PoiKind.StandingStones => Stones(site, p),
            PoiKind.CaveMouth => CaveArch(site, p),
            PoiKind.VantagePoint => Lookout(site, p),
            _ => null,
        };
        // Nothing rises past the declared structure height, whatever a stele's lean or a rib's reach.
        return shape is double d ? Math.Max(d, p.Y - (PoiConstants.MaximumStructureHeight - 0.5)) : null;
    }

    private static double Gate(Vector3d p)
    {
        double platform = RoundBox(p - new Vector3d(0, PlatformTop - (PlatformDepth / 2), 0),
            new Vector3d(PlatformHalf, PlatformDepth / 2, PlatformHalf), PlatformRound);
        double shaft = Box(p - new Vector3d(0, 0, 0), new Vector3d(ShaftHalf, PlatformDepth * 2, ShaftHalf));
        platform = Math.Max(platform, -shaft);
        // Two arches, one in each vertical plane through the centre, their rib thinning toward the top.
        double archX = TaperedArch(p.X, p.Y, p.Z, GateArchRadius, GateRibBase, GateRibTop);
        double archZ = TaperedArch(p.Z, p.Y, p.X, GateArchRadius, GateRibBase, GateRibTop);
        return Math.Min(platform, Math.Min(archX, archZ));
    }

    private static double Ruin(PoiSite site, Vector3d p)
    {
        double radial = Math.Sqrt((p.X * p.X) + (p.Z * p.Z));
        double angle = Math.Atan2(p.Z, p.X);
        // The wall's height falls and rises around the ring: whole in places, a stub in others,
        // gone where the height drops below the ground.
        double phase = site.Variant * 0.9;
        double fraction = 0.5 + (0.35 * Math.Sin((2 * angle) + phase)) + (0.25 * Math.Sin((5 * angle) - (phase * 1.7)));
        double height = site.Height * Math.Clamp(fraction, -0.2, 1.0);
        double wall = Math.Max(Math.Abs(radial - RuinRadius) - RuinWallHalfThickness, Math.Max(p.Y - height, -p.Y - 1.0));
        double ribs = double.MaxValue;
        for (int rib = 0; rib < RuinRibs; rib++)
        {
            double a = phase + (rib * Math.Tau / RuinRibs);
            Vector3d foot = new(Math.Cos(a) * RuinRadius, -0.5, Math.Sin(a) * RuinRadius);
            Vector3d head = new(Math.Cos(a) * RuinRadius * RuinRibInward, site.Height * RuinRibRise, Math.Sin(a) * RuinRadius * RuinRibInward);
            ribs = Math.Min(ribs, RoundCone(p, foot, head, RuinRibBase, RuinRibTop));
        }

        return Math.Min(wall, ribs);
    }

    private static double Stones(PoiSite site, Vector3d p)
    {
        int count = 5 + (int)(site.Variant % 4);
        double shape = Sphere(p - new Vector3d(0, 0.1, 0), CairnRadius);
        for (int index = 0; index < count; index++)
        {
            double a = (Math.Tau * index / count) + (site.Variant * 0.4);
            double height = site.Height - (((index + site.Variant) % 3) * 2);
            height = Math.Max(height, PoiConstants.StoneMinimumHeight - 3);
            Vector3d outward = new(Math.Cos(a), 0, Math.Sin(a));
            Vector3d foot = outward * PoiConstants.StoneRingRadius + new Vector3d(0, -0.5, 0);
            Vector3d head = foot + new Vector3d(outward.X * height * SteleLean, height, outward.Z * height * SteleLean);
            double stele = RoundCone(p, foot, head, SteleBase, SteleTop);
            if (index == 0)
            {
                // The tallest stone carries a round opening through it, looking into the ring.
                Vector3d centre = foot + ((head - foot) * PortholeAt);
                Vector3d local = p - centre;
                double across = Math.Sqrt(Math.Pow(Vector3d.Dot(local, new(0, 1, 0)), 2)
                    + Math.Pow(Vector3d.Dot(local, new(-outward.Z, 0, outward.X)), 2));
                stele = Math.Max(stele, PortholeRadius - across);
            }

            shape = Math.Min(shape, stele);
        }

        return shape;
    }

    private static double CaveArch(PoiSite site, Vector3d p)
    {
        (long forwardX, long forwardZ) = PoiStructures.CardinalOf(site.Aspect);
        double along = (p.X * forwardX) + (p.Z * forwardZ);
        double across = (p.X * forwardZ) - (p.Z * forwardX);
        // An upright ellipse of stone in the mouth's plane, set a little in front of the recess.
        double y = p.Y / CaveArchStretch;
        double ring = Math.Sqrt((across * across) + (y * y)) - CaveArchRadius;
        double arch = Math.Sqrt((ring * ring) + Math.Pow(along + CaveArchSetBack, 2)) - CaveArchRib;
        return Math.Max(arch, -p.Y - 0.5);
    }

    /// <summary>
    /// A stepped look-out: one-metre courses of rounded stone, each a metre in from the one below,
    /// so it is still walked up course by course without climbing.
    /// </summary>
    private static double Lookout(PoiSite site, Vector3d p)
    {
        double shape = double.MaxValue;
        for (long course = 1; course <= site.Height; course++)
        {
            double half = Math.Max(PoiConstants.VantageHalfExtent - (course - 1), 0) + 0.5;
            Vector3d centre = new(0, course - 0.5, 0);
            shape = Math.Min(shape, RoundBox(p - centre, new Vector3d(half, 0.5, half), LookoutRound));
        }

        return shape;
    }

    /// <summary>A half ring standing on the ground in the plane spanned by `u` and up, its rib tapering upward.</summary>
    private static double TaperedArch(double u, double up, double w, double radius, double ribBase, double ribTop)
    {
        double ring = Math.Sqrt((u * u) + (up * up)) - radius;
        double t = Math.Clamp(up / radius, 0, 1);
        double rib = ribBase + ((ribTop - ribBase) * t);
        return Math.Max(Math.Sqrt((ring * ring) + (w * w)) - rib, -up - 0.5);
    }

    private static double Box(Vector3d p, Vector3d half)
    {
        Vector3d q = new(Math.Abs(p.X) - half.X, Math.Abs(p.Y) - half.Y, Math.Abs(p.Z) - half.Z);
        Vector3d outside = new(Math.Max(q.X, 0), Math.Max(q.Y, 0), Math.Max(q.Z, 0));
        return outside.Length + Math.Min(Math.Max(q.X, Math.Max(q.Y, q.Z)), 0);
    }

    private static double RoundBox(Vector3d p, Vector3d half, double round) =>
        Box(p, new Vector3d(half.X - round, half.Y - round, half.Z - round)) - round;

    private static double Sphere(Vector3d p, double radius) => p.Length - radius;

    /// <summary>A capsule from a to b whose radius runs from ra to rb (a round cone).</summary>
    private static double RoundCone(Vector3d p, Vector3d a, Vector3d b, double ra, double rb)
    {
        Vector3d ba = b - a, pa = p - a;
        double t = Math.Clamp(Vector3d.Dot(pa, ba) / Vector3d.Dot(ba, ba), 0, 1);
        return (pa - (ba * t)).Length - (ra + ((rb - ra) * t));
    }

    /// <summary>A double-precision vector: world positions are far from the origin.</summary>
    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        internal double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));
        internal static double Dot(Vector3d a, Vector3d b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    }
}
