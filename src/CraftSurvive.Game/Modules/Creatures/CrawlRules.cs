using System.Numerics;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>What a crawling hunter holds (#10975): the Engine's crawler surface, without the Engine.</summary>
internal enum CrawlHold
{
    /// <summary>Nothing: it falls.</summary>
    None,
    Floor,
    Wall,
    Ceiling,
}

/// <summary>Where a crawling hunter is in its fight (#10975).</summary>
internal enum CrawlPhase
{
    /// <summary>On a surface, running, resting or biting.</summary>
    Clinging,

    /// <summary>Let go (a drop, or knocked loose), falling until it lands.</summary>
    Falling,

    /// <summary>Landed from a fall, dazed: it does nothing for a while.</summary>
    Dazed,
}

/// <summary>
/// The wall runner on walls and ceilings (#10975), as settled with Patch on the wiki's Ruins wildlife page ("The wall
/// runner on walls and ceilings"): it runs on floors, walls and ceilings as one surface; hunting, it makes for the ceiling
/// over the player; on a ceiling nearly over them it drops onto them, double its bite, and lands dazed; it bites from a
/// floor or wall beside them; after a strike it runs back up the walls a while; a blow knocks it loose from a wall.
/// Engine-free.
/// </summary>
internal static class CrawlRules
{
    /// <summary>The Engine's path costs are whole numbers: a place's cost is the kind's share of a floor's times this.</summary>
    internal const uint CostScale = 10;

    /// <summary>A dropped body lands on the player within this much of its bite's reach (its body is longer than its ball).</summary>
    internal const double DropLandSlackMetres = 0.8;

    /// <summary>The player's body, for how near a crawler is to it: their feet up to this height.</summary>
    internal const float PlayerHeightMetres = 1.75f;

    /// <summary>Waypoints on another surface lean the crawler into it while their normals agree with its up less than this.</summary>
    internal const float SameSurface = 0.9f;

    /// <summary>The Engine path cost of entering a place on a surface: a floor's is the scale, a wall's and ceiling's their share of it.</summary>
    internal static uint Cost(double share) => (uint)Math.Max(CostScale, Math.Round(share * CostScale));

    /// <summary>Whether a crawler on a ceiling drops onto the player now: hunting, ready to strike, not running off, nearly over them.</summary>
    internal static bool Drops(WildlifeCrawling crawling, CrawlPhase phase, CrawlHold hold, bool hunting, bool canStrike, bool retreating, double planarMetres)
    {
        ArgumentNullException.ThrowIfNull(crawling);
        return phase == CrawlPhase.Clinging && hold == CrawlHold.Ceiling && hunting && canStrike && !retreating && planarMetres <= crawling.DropMetres;
    }

    /// <summary>Whether a blow that lands knocks a crawler loose: only from a wall (a ceiling is out of the swing's reach, a floor holds it anyway).</summary>
    internal static bool KnockedLoose(CrawlPhase phase, CrawlHold hold) => phase == CrawlPhase.Clinging && hold == CrawlHold.Wall;

    /// <summary>Whether a crawler may bite: clinging to a floor or a wall, with the player's body within its reach.</summary>
    internal static bool Bites(CrawlPhase phase, CrawlHold hold, double metresToBody, double reachMetres) =>
        phase == CrawlPhase.Clinging && hold is CrawlHold.Floor or CrawlHold.Wall && metresToBody <= reachMetres;

    /// <summary>Whether a dropped crawler, landed, lands on the player.</summary>
    internal static bool DropLands(double metresToBody, double reachMetres) => metresToBody <= reachMetres + DropLandSlackMetres;

    /// <summary>How far a point is from the player's body: the nearest point of the line up from their feet.</summary>
    internal static double ToBody(Vector3 point, Vector3 playerFeet)
    {
        float y = Math.Clamp(point.Y, playerFeet.Y, playerFeet.Y + PlayerHeightMetres);
        return Vector3.Distance(point, new Vector3(playerFeet.X, y, playerFeet.Z));
    }

    /// <summary>
    /// Where a hunting crawler makes for: the ceiling over the player where it is low enough to drop from (as high over their
    /// feet as <paramref name="ceilingOverPlayer"/>, null for none), otherwise the player's feet.
    /// </summary>
    internal static Vector3 Goal(WildlifeCrawling crawling, Vector3 playerFeet, float? ceilingOverPlayer)
    {
        ArgumentNullException.ThrowIfNull(crawling);
        return ceilingOverPlayer is float ceiling && ceiling <= crawling.DropCeilingMetres
            ? playerFeet + new Vector3(0, ceiling - (float)crawling.RadiusMetres, 0)
            : playerFeet + new Vector3(0, (float)crawling.RadiusMetres, 0);
    }

    /// <summary>A way laid onto the surface shorter than this goes nowhere along it.</summary>
    internal const float NowhereAlong = 0.1f;

    /// <summary>
    /// Whether a crawler lets go to reach its next place: on a ceiling (its up turned down) over a low slot whose floor is the
    /// next place, the way to it is straight through the ceiling's plane, and no way along the ceiling leads down to it.
    /// </summary>
    internal static bool LetsGoTo(Vector3 up, Vector3 steer, bool nextIsFloor, bool nextIsBelow)
    {
        if (!nextIsFloor || !nextIsBelow || up.Y > -SameSurface) return false;
        Vector3 along = steer - (up * Vector3.Dot(steer, up));
        return along.Length() < NowhereAlong;
    }

    /// <summary>
    /// Which way to drive the crawler toward a waypoint (the Engine's guide, docs/surface-crawlers.md): straight at it, but
    /// leaning into the waypoint's surface when it is another surface that faces back toward the crawler, as the first place
    /// up a wall does from the floor, where the way laid onto the floor would be nothing.
    /// </summary>
    internal static Vector3 Steer(Vector3 position, Vector3 up, Vector3 waypoint, Vector3 waypointNormal)
    {
        Vector3 toward = waypoint - position;
        if (toward.LengthSquared() < 1e-8f) return Vector3.Zero;
        Vector3 direction = Vector3.Normalize(toward);
        if (Vector3.Dot(waypointNormal, up) < SameSurface && Vector3.Dot(waypointNormal, position - waypoint) >= 0) direction -= waypointNormal;
        return direction;
    }
}
