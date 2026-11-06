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

    /// <summary>The height of the ground it stands on, or is hopping to.</summary>
    internal float Ground { get; set; }

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

    /// <summary>
    /// A spot for a party around the player, where <paramref name="suits"/> allows, and its birds, every one of
    /// them out of view: a spot out of view can still scatter a bird onto the screen. Null if the tries find none.
    /// </summary>
    internal static (Vector2 Spot, PartyBird[] Birds)? Place(PartyCatalog catalog, PartyKind kind, Vector2 player, Vector3 eye, Vector3 forward, double fieldOfViewDegrees, Func<double> draw, Func<Vector2, float> groundAt, Func<Vector2, bool> suits)
    {
        for (int attempt = 0; attempt < catalog.PlaceTries; attempt++)
        {
            double bearing = draw() * FullTurn;
            Vector2 spot = player + (new Vector2((float)Math.Cos(bearing), (float)Math.Sin(bearing)) * (float)catalog.AppearMetres.At(draw()));
            if (!suits(spot)) continue;
            PartyBird[] birds = Scatter(kind, spot, draw, groundAt);
            if (!birds.Any(bird => InView(Standing(bird), eye, forward, fieldOfViewDegrees, catalog.ViewMarginDegrees))) return (spot, birds);
        }

        return null;
    }

    /// <summary>A party's birds, scattered about its spot.</summary>
    internal static PartyBird[] Scatter(PartyKind kind, Vector2 spot, Func<double> draw, Func<Vector2, float> groundAt)
    {
        int count = Math.Min(kind.Most, kind.Least + (int)Math.Floor(draw() * (kind.Most - kind.Least + 1)));
        PartyBird[] birds = new PartyBird[count];
        for (int index = 0; index < count; index++)
        {
            double bearing = draw() * FullTurn;
            // The root of the draw spreads them evenly over the disc rather than bunching them at its middle.
            Vector2 at = spot + (new Vector2((float)Math.Cos(bearing), (float)Math.Sin(bearing)) * (float)(kind.SpreadMetres * Math.Sqrt(draw())));
            birds[index] = new PartyBird(at, draw() * FullTurn, kind.Gait.HopEverySeconds.At(draw())) { Ground = groundAt(at) };
        }

        return birds;
    }

    /// <summary>Where a bird on the ground is standing.</summary>
    internal static Vector3 Standing(PartyBird bird) => new(bird.At.X, bird.Ground, bird.At.Y);

    /// <summary>
    /// Whether a party may go all at once: it has stayed its time or been left behind, and none of it would be seen
    /// to go. A flushed bird leaves on its own, by flying out and fading, so a party with one in the air stays until it has.
    /// </summary>
    /// <param name="seen">Whether a point could be on screen.</param>
    internal static bool Leaves(IEnumerable<PartyBird> birds, double stayLeft, double metresFromSpot, PartyCatalog catalog, Func<Vector3, bool> seen)
    {
        if (stayLeft > 0 && metresFromSpot < catalog.GoneMetres) return false;
        foreach (PartyBird bird in birds)
        {
            if (bird.Flying is not null || bird.FlushIn is not null || seen(Standing(bird))) return false;
        }

        return true;
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

    /// <summary>
    /// How much of a flying bird's size to draw, by how far it is from the player through the air: all of it until the
    /// last of the way out, where a speck would otherwise blink away. It climbs as it goes, so a player running under it still loses it.
    /// </summary>
    internal static float Size(double metres, PartyCatalog catalog) =>
        (float)Math.Clamp((catalog.GoneMetres - metres) / catalog.FadeMetres, 0, 1);
}
