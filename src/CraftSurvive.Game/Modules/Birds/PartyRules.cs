using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Birds;

/// <summary>What a bird on the ground is doing, which names its clip.</summary>
internal enum PartyClip
{
    Idle,
    Peck,
    Hop,
}

/// <summary>One bird of a party: where it stands or hops, or once flushed, where it set off from and which way it went.</summary>
internal sealed class PartyBird(Vector2 at, double facing, double nextHop)
{
    internal Vector2 At { get; set; } = at;
    internal Vector2 HopFrom { get; set; } = at;
    internal Vector2 HopTo { get; set; } = at;
    internal double Facing { get; set; } = facing;
    internal double NextHop { get; set; } = nextHop;
    internal double HopLeft { get; set; }
    internal PartyClip Clip { get; set; } = PartyClip.Idle;

    /// <summary>The seconds until it goes up, once the party has been flushed.</summary>
    internal double? FlushIn { get; set; }

    internal Vector2 Away { get; set; }
    internal double? Flying { get; set; }
    internal Vector3 Launch { get; set; }
}

/// <summary>
/// A party of birds feeding on the ground (#10639). Like a flight it is drawn, not simulated: it appears out of
/// the player's view, nothing about it is kept or saved, and a bird that leaves is simply gone. A bird on the
/// ground and a bird in the air need not be the same bird, so nothing ever lands in view. Pure: the module hands
/// in its draws and the ground.
/// </summary>
internal static class PartyRules
{
    private const double FullTurn = 2 * Math.PI;
    private const double DegreesToRadians = Math.PI / 180;

    /// <summary>The widest screen allowed for, so a view test holds on any window.</summary>
    private const double WidestAspect = 2.4;

    /// <summary>The kind to send from those that feed here and now, by how much each likes the ground, or null for none.</summary>
    internal static PartyKind? Choose(IReadOnlyList<PartyKind> feeding, MapBiome biome, double draw)
    {
        double left = draw * feeding.Sum(kind => kind.Habitat.GetValueOrDefault(biome));
        foreach (PartyKind kind in feeding)
        {
            left -= kind.Habitat.GetValueOrDefault(biome);
            if (left < 0) return kind;
        }

        return feeding.Count > 0 ? feeding[^1] : null;
    }

    /// <summary>
    /// Whether a point could be on screen: within a cone that holds the whole view at the widest screen, and a margin.
    /// It is deliberately generous, and ignores what stands in the way, so "not in view" can be trusted.
    /// </summary>
    internal static bool InView(Vector3 point, Vector3 eye, Vector3 forward, double fieldOfViewDegrees, double marginDegrees)
    {
        Vector3 to = point - eye;
        if (to.LengthSquared() < 1e-6f) return true;
        double halfTan = Math.Tan(fieldOfViewDegrees * DegreesToRadians / 2);
        double corner = Math.Atan(halfTan * Math.Sqrt(1 + (WidestAspect * WidestAspect)));
        double cone = corner + (marginDegrees * DegreesToRadians);
        return Vector3.Dot(Vector3.Normalize(to), Vector3.Normalize(forward)) >= Math.Cos(cone);
    }

    /// <summary>A spot for a party around the player and out of view, where <paramref name="suits"/> allows, or null if the tries find none.</summary>
    internal static Vector2? Place(PartyCatalog catalog, Vector2 player, Vector3 eye, Vector3 forward, double fieldOfViewDegrees, Func<double> draw, Func<Vector2, double> groundAt, Func<Vector2, bool> suits)
    {
        for (int attempt = 0; attempt < catalog.PlaceTries; attempt++)
        {
            double bearing = draw() * FullTurn;
            Vector2 spot = player + (new Vector2((float)Math.Cos(bearing), (float)Math.Sin(bearing)) * (float)catalog.AppearMetres.At(draw()));
            Vector3 ground = new(spot.X, (float)groundAt(spot), spot.Y);
            if (!InView(ground, eye, forward, fieldOfViewDegrees, catalog.ViewMarginDegrees) && suits(spot)) return spot;
        }

        return null;
    }

    /// <summary>A party's birds, scattered about its spot.</summary>
    internal static PartyBird[] Scatter(PartyKind kind, Vector2 spot, Func<double> draw)
    {
        int count = Math.Min(kind.Most, kind.Least + (int)Math.Floor(draw() * (kind.Most - kind.Least + 1)));
        PartyBird[] birds = new PartyBird[count];
        for (int index = 0; index < count; index++)
        {
            double bearing = draw() * FullTurn;
            // The root of the draw spreads them evenly over the disc rather than bunching them at its middle.
            Vector2 at = spot + (new Vector2((float)Math.Cos(bearing), (float)Math.Sin(bearing)) * (float)(kind.SpreadMetres * Math.Sqrt(draw())));
            birds[index] = new PartyBird(at, draw() * FullTurn, kind.Gait.HopEverySeconds.At(draw()));
        }

        return birds;
    }

    /// <summary>Takes a bird on the ground on by a step: it rests, then hops a little way, keeping near the party's spot.</summary>
    internal static void Feed(PartyBird bird, PartyKind kind, Vector2 spot, double seconds, Func<double> draw)
    {
        PartyGait gait = kind.Gait;
        if (bird.HopLeft > 0)
        {
            bird.HopLeft -= seconds;
            if (bird.HopLeft > 0)
            {
                bird.At = Vector2.Lerp(bird.HopFrom, bird.HopTo, (float)(1 - (bird.HopLeft / gait.HopSeconds)));
                return;
            }

            bird.At = bird.HopTo;
            bird.Clip = draw() < gait.PeckShare ? PartyClip.Peck : PartyClip.Idle;
            bird.NextHop = gait.HopEverySeconds.At(draw());
            return;
        }

        bird.NextHop -= seconds;
        if (bird.NextHop > 0) return;
        double bearing = draw() * FullTurn;
        Vector2 way = new((float)Math.Cos(bearing), (float)Math.Sin(bearing));
        // One that has wandered to the edge of the party's ground turns back in.
        Vector2 home = spot - bird.At;
        if (home.Length() > kind.SpreadMetres) way = Vector2.Normalize(home);
        bird.HopFrom = bird.At;
        bird.HopTo = bird.At + (way * (float)gait.HopMetres);
        bird.HopLeft = gait.HopSeconds;
        bird.Facing = Math.Atan2(way.X, -way.Y);
        bird.Clip = PartyClip.Hop;
    }

    /// <summary>How high a hop has carried a bird above the ground it is crossing.</summary>
    internal static double HopHeight(PartyBird bird, PartyKind kind) =>
        bird.HopLeft <= 0 ? 0 : kind.Gait.HopHeightMetres * Math.Sin(Math.PI * (1 - (bird.HopLeft / kind.Gait.HopSeconds)));

    /// <summary>Whether the player has come near enough to any bird on the ground to flush the party.</summary>
    internal static bool Spooked(IEnumerable<PartyBird> birds, PartyKind kind, Vector2 player) =>
        birds.Any(bird => bird.Flying is null && Vector2.Distance(bird.At, player) <= kind.Burst.FlushMetres);

    /// <summary>Flushes a party: every bird goes up within a moment of the others, fanned about the way away from the player.</summary>
    internal static void Flush(IEnumerable<PartyBird> birds, PartyKind kind, Vector2 player, Func<double> draw)
    {
        foreach (PartyBird bird in birds)
        {
            if (bird.FlushIn is not null || bird.Flying is not null) continue;
            Vector2 from = bird.At - player;
            double way = Math.Atan2(from.Y, from.X) + (((2 * draw()) - 1) * kind.Burst.FanDegrees / 2 * DegreesToRadians);
            bird.Away = new Vector2((float)Math.Cos(way), (float)Math.Sin(way));
            bird.FlushIn = draw() * kind.Burst.StaggerSeconds;
        }
    }

    /// <summary>Where a flushed bird is, and which way it faces.</summary>
    internal static BirdPose Burst(PartyBird bird, PartyKind kind)
    {
        double age = bird.Flying ?? 0;
        Vector2 ground = new Vector2(bird.Launch.X, bird.Launch.Z) + (bird.Away * (float)(kind.Burst.MetresPerSecond * age));
        float height = bird.Launch.Y + (float)(kind.Burst.ClimbMetresPerSecond * age);
        return new BirdPose(new Vector3(ground.X, height, ground.Y), MathF.Atan2(bird.Away.X, -bird.Away.Y), 0);
    }

    /// <summary>How much of a flying bird's size to draw: all of it until the last of the way out, where a speck would otherwise blink away.</summary>
    internal static float Size(double metres, PartyCatalog catalog) =>
        (float)Math.Clamp((catalog.GoneMetres - metres) / catalog.FadeMetres, 0, 1);
}
