using System.Numerics;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// One kit piece of a place as it stands at its site: the piece's name in the place kit, where its origin
/// stands (world metres; X and Z exact, the height either the site's ground or the piece's own ground,
/// plus a rise), how it is turned and how it is scaled. A piece's origin is the bottom of its mesh; the
/// kit says how far each piece sinks below the ground it stands on.
/// </summary>
internal readonly record struct PlacedPart(string Piece, double X, double Z, bool OnSiteGround, double Rise, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// The places as kit pieces (#9992): a pure function of a site, as the voxel builder was, so every caller
/// that asks about the same site gets the same pieces. The layouts are the ones the per-voxel landmark
/// shapes had (#9671): a ring of leaning steles of several heights, the tallest pierced and facing in, with
/// a cairn at the centre; a ruin's broken curved wall whose height rises and falls around the ring, with
/// ribs leaning inward toward a roof that is gone; a cave mouth's arch facing out of the slope; a descent's
/// platform with two arches crossing over its shaft; and a vantage point's stepped courses.
///
/// Site coordinates: the site voxel's centre is (X + 0.5, Z + 0.5) and its ground's top face is Ground + 1.
/// </summary>
internal static class PlaceLayouts
{
    internal const string PiercedStele = "stele-pierced", TallStele = "stele-plain-a", BrokenStele = "stele-plain-b";
    internal const string Cairn = "cairn", RuinWall = "ruin-wall", RuinRib = "ruin-rib";
    internal const string CaveArch = "cave-arch", EntranceArch = "entrance-arch", EntrancePlatform = "entrance-platform";
    internal const string VantagePrefix = "vantage-h";

    /// <summary>Each kit piece's natural height (metres above its sunk base) that a layout scales against.</summary>
    internal const double PiercedSteleHeight = 10, TallSteleHeight = 8, BrokenSteleHeight = 6;

    /// <summary>A ruin wall piece follows this much of the ring (degrees), and its height averages this (metres).</summary>
    internal const double RuinWallArcDegrees = 72, RuinWallMeanHeight = 3.25, RuinWallMinimumScale = 0.45, RuinWallMaximumScale = 1.5;

    /// <summary>Below this share of the site height a stretch of ruin wall is gone.</summary>
    internal const double RuinWallGoneBelow = 0.2;

    internal const double RuinRibLength = 7;

    // The layouts' measures, as the landmark shapes had them (#9671).
    private const double SteleLean = 0.08, CairnScaleStep = 0.08;
    private const double RuinRadius = 5.0, RuinRibInward = 0.55, RuinRibRise = 1.3;
    private const int RuinRibs = 3, RuinWalls = 5;
    private const double CaveArchSetBack = 0.8;

    internal static IEnumerable<PlacedPart> For(PoiSite site) => site.Kind switch
    {
        PoiKind.StandingStones => Stones(site),
        PoiKind.Ruin => Ruin(site),
        PoiKind.CaveMouth => Cave(site),
        PoiKind.DungeonEntrance => Entrance(site),
        PoiKind.VantagePoint => Vantage(site),
        _ => [],
    };

    /// <summary>The ring's steles and its cairn. Every stele stands on its own ground: the ring is wider than a slope is level.</summary>
    private static IEnumerable<PlacedPart> Stones(PoiSite site)
    {
        (double cx, double cz) = Centre(site);
        int count = 5 + (int)(site.Variant % 4);
        for (int index = 0; index < count; index++)
        {
            double a = (Math.Tau * index / count) + (site.Variant * 0.4);
            double height = Math.Max(site.Height - (((index + site.Variant) % 3) * 2), PoiConstants.StoneMinimumHeight - 3);
            Vector3 outward = new((float)Math.Cos(a), 0, (float)Math.Sin(a));
            (string piece, double natural) = index == 0 ? (PiercedStele, PiercedSteleHeight)
                : (index + site.Variant) % 2 == 0 ? (TallStele, TallSteleHeight) : (BrokenStele, BrokenSteleHeight);
            // The broad face (+Z of the piece) looks into the ring, so the pierced stone's opening does too;
            // the stone then leans a little outward, about the ring's tangent.
            Quaternion face = Facing(-outward);
            Quaternion lean = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, outward)), (float)Math.Atan(SteleLean));
            float scale = (float)(height / natural);
            yield return new(piece, cx + (outward.X * PoiConstants.StoneRingRadius), cz + (outward.Z * PoiConstants.StoneRingRadius),
                false, 0, lean * face, new Vector3(scale));
        }

        float cairn = 1 + (float)((site.Variant % 3) * CairnScaleStep);
        yield return new(Cairn, cx, cz, false, 0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, site.Variant), new Vector3(cairn));
    }

    /// <summary>
    /// The broken curved wall as arc pieces around the ring, each scaled to the wall's height there (gone
    /// where it falls away), and the ribs leaning in from it.
    /// </summary>
    private static IEnumerable<PlacedPart> Ruin(PoiSite site)
    {
        (double cx, double cz) = Centre(site);
        double phase = site.Variant * 0.9;
        for (int wall = 0; wall < RuinWalls; wall++)
        {
            double a = phase + (wall * Math.Tau / RuinWalls);
            double share = RuinHeightShare(a, phase);
            if (share < RuinWallGoneBelow) continue;
            float rise = (float)Math.Clamp(site.Height * share / RuinWallMeanHeight, RuinWallMinimumScale, RuinWallMaximumScale);
            Vector3 outward = new((float)Math.Cos(a), 0, (float)Math.Sin(a));
            // The piece's concave side (+Z) faces the ring's centre; it is centred on its arc's midpoint.
            yield return new(RuinWall, cx + (outward.X * RuinRadius), cz + (outward.Z * RuinRadius), false, 0,
                Facing(-outward), new Vector3(1, rise, 1));
        }

        for (int rib = 0; rib < RuinRibs; rib++)
        {
            double a = phase + (rib * Math.Tau / RuinRibs);
            Vector3 outward = new((float)Math.Cos(a), 0, (float)Math.Sin(a));
            Vector3 foot = outward * (float)RuinRadius;
            Vector3 head = new(outward.X * (float)(RuinRadius * RuinRibInward), (float)(site.Height * RuinRibRise), outward.Z * (float)(RuinRadius * RuinRibInward));
            Vector3 along = head - foot;
            // The rib stands up its +Y and curves toward its +Z: face it inward, then tilt it from upright to its run.
            Quaternion face = Facing(-outward);
            float tilt = (float)Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(along), Vector3.UnitY), -1, 1));
            Quaternion lean = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, -outward)), tilt);
            float scale = (float)(along.Length() / RuinRibLength);
            yield return new(RuinRib, cx + foot.X, cz + foot.Z, false, 0, lean * face, new Vector3(scale));
        }
    }

    /// <summary>The ruin wall's height as a share of the site's, around the ring (the landmark shape's own rise and fall).</summary>
    internal static double RuinHeightShare(double angle, double phase) =>
        Math.Clamp(0.5 + (0.35 * Math.Sin((2 * angle) + phase)) + (0.25 * Math.Sin((5 * angle) - (phase * 1.7))), -0.2, 1.0);

    /// <summary>The arch in the plane of the mouth, a little in front of the recess, its opening looking out of the hill.</summary>
    private static IEnumerable<PlacedPart> Cave(PoiSite site)
    {
        (double cx, double cz) = Centre(site);
        (long forwardX, long forwardZ) = PoiStructures.CardinalOf(site.Aspect);
        Vector3 forward = new(forwardX, 0, forwardZ);
        yield return new(CaveArch, cx - (forward.X * CaveArchSetBack), cz - (forward.Z * CaveArchSetBack), true, 0, Facing(forward), Vector3.One);
    }

    /// <summary>The platform round the shaft, and two arches crossing over it.</summary>
    private static IEnumerable<PlacedPart> Entrance(PoiSite site)
    {
        (double cx, double cz) = Centre(site);
        yield return new(EntrancePlatform, cx, cz, true, 0, Quaternion.Identity, Vector3.One);
        yield return new(EntranceArch, cx, cz, true, 0, Quaternion.Identity, Vector3.One);
        yield return new(EntranceArch, cx, cz, true, 0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Vector3.One);
    }

    /// <summary>The stepped courses, one mesh per height, under the lamp the voxels keep.</summary>
    private static IEnumerable<PlacedPart> Vantage(PoiSite site)
    {
        (double cx, double cz) = Centre(site);
        long courses = Math.Clamp(site.Height, PoiConstants.VantageMinimumSteps, PoiConstants.VantageMinimumSteps + PoiConstants.VantageStepRange - 1);
        yield return new($"{VantagePrefix}{courses}", cx, cz, true, 0, Quaternion.Identity, Vector3.One);
    }

    private static (double X, double Z) Centre(PoiSite site) => (site.X + 0.5, site.Z + 0.5);

    /// <summary>The turn about the vertical that brings a piece's +Z to look along <paramref name="direction"/>.</summary>
    internal static Quaternion Facing(Vector3 direction) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(direction.X, direction.Z));
}
