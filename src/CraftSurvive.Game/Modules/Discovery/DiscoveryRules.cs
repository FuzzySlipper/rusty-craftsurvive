using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>How far a place has been taken in.</summary>
internal enum DiscoveryStage : byte
{
    /// <summary>Nothing known.</summary>
    None = 0,

    /// <summary>Visible from somewhere the player has stood: it is on the skyline.</summary>
    Seen = 1,

    /// <summary>The player has stood at it, close enough to know what it is.</summary>
    Visited = 2,
}

/// <summary>
/// The noticing policy: how close counts as seen, how close counts as visited, and what
/// blocks the view in between.
///
/// Sightlines are computed from the generation contract's own ground rather than from the
/// renderer. That is a deliberate choice: "the player has seen this landmark" is a game
/// fact, and the ground it depends on is deterministic and available without a resident
/// chunk - so the fact survives streaming, and it is reproducible in a check lane. The
/// Engine still owns what is actually drawn; this decides what the character has learned.
/// </summary>
internal static class DiscoveryRules
{
    /// <summary>How far the eye is above the feet for sightline purposes.</summary>
    internal const double EyeHeightMetres = 1.6;

    /// <summary>The player must be this close, in metres, before a site can be noticed at all.</summary>
    internal const double NoticeRadiusMetres = PoiConstants.NoticeRadiusMetres;

    /// <summary>Within this distance the player has arrived, not just looked.</summary>
    internal const double VisitRadiusMetres = 24.0;

    /// <summary>
    /// How finely the ground is sampled along a sightline. Four metres is finer than the
    /// terrain's own detail scale, so a ridge that could hide a landmark is not stepped over.
    /// </summary>
    internal const double SightlineStepMetres = 4.0;

    /// <summary>
    /// The stage a site has reached, given where the player is standing. Distance alone is
    /// not enough to claim a sighting: something hidden behind a ridge is not seen from the
    /// near side of it, which is the whole reason a landmark is worth walking to.
    /// </summary>
    internal static DiscoveryStage StageFor(double distance, bool visible)
    {
        if (distance <= VisitRadiusMetres)
        {
            return DiscoveryStage.Visited;
        }

        return visible && distance <= NoticeRadiusMetres ? DiscoveryStage.Seen : DiscoveryStage.None;
    }

    /// <summary>
    /// Whether the ground leaves the line between an eye and a target unobstructed. The
    /// target is its own highest voxel, so a tall structure is visible over ground that
    /// would hide its base.
    /// </summary>
    internal static bool HasSightline(ITerrainColumns columns, long fromX, long fromZ, double fromY,
        long toX, long toZ, double toY)
    {
        ArgumentNullException.ThrowIfNull(columns);
        long dx = toX - fromX;
        long dz = toZ - fromZ;
        double span = Math.Sqrt((dx * (double)dx) + (dz * (double)dz));
        int steps = (int)Math.Ceiling(span / SightlineStepMetres);
        if (steps <= 1)
        {
            return true;
        }

        for (int step = 1; step < steps; step++)
        {
            double fraction = step / (double)steps;
            long x = fromX + (long)Math.Round(dx * fraction, MidpointRounding.AwayFromZero);
            long z = fromZ + (long)Math.Round(dz * fraction, MidpointRounding.AwayFromZero);
            double lineY = fromY + ((toY - fromY) * fraction);

            // A column's surface is its highest ground voxel, so ground reaching the line
            // at or above it blocks the view.
            if (columns.ColumnAt(x, z).Surface >= lineY)
            {
                return false;
            }
        }

        return true;
    }
}
